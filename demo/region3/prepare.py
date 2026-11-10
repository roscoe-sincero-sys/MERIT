"""Generate synthetic Region 3 setup SQL and three overlapping import datasets."""
from pathlib import Path
import base64
import csv
import hashlib
import json
import os
import struct
import uuid

OUT = Path(__file__).resolve().parent
PASSWORD = 'MeritDemo!2026'

def guid(key):
    return str(uuid.UUID(bytes_le=hashlib.md5(key.encode('utf-16le')).digest()))

def q(value):
    return "N'" + str(value).replace("'", "''") + "'"

def password_hash():
    salt = os.urandom(16)
    return base64.b64encode(b'\x01' + struct.pack('>III', 2, 100000, 16) + salt +
                           hashlib.pbkdf2_hmac('sha512', PASSWORD.encode(), salt, 100000, 32)).decode()

header = ['PatientLast','PatientFirst','DOB','Sex','Street','Apt','City','State','Zip','County',
          'LOINC','Result','Units','SampleType','Method','Collected','Reported','Accession','Status','LabCLIA']
targets = ['lastName','firstName','dob','sex','addressLine1','addressUnit','city','state','zip5','countyFips',
           'loinc','value','unit','sampleType','testMethod','collectedDate','reportedDate','accessionNumber','resultStatus','labCliaNumber']
translations = {'sex': {'F':'female','M':'male'}, 'sampleType':{'V':'venous'},
                'resultStatus':{'F':'final'}, 'testMethod':{'ICPMS':'icpMs'}}
datasets = {'source_a': list(range(1,21)), 'source_b':[1,2,3]+list(range(21,38)),
            'source_c':[4,5,6,21,22,23,24]+list(range(38,51))}
sources = {'source_a': ('POTOMAC_DEMO','Potomac Synthetic Laboratory','21D0900001'),
           'source_b': ('ALLEGHENY_DEMO','Allegheny Synthetic Laboratory','21D0900002'),
           'source_c': ('CHESAPEAKE_DEMO','Chesapeake Synthetic Laboratory','21D0900003')}
firsts = ['Ada','Bennett','Cora','Dorian','Eliza','Felix','Gemma','Hugo','Iris','Jonah',
          'Keira','Leo','Maren','Nolan','Opal','Perry','Quinn','Remy','Sylvie','Theo']

def patient(i):
    return {'id':f'R3DEMO{i:03}', 'first':firsts[(i-1)%20], 'last':f'Demopatient{i:03}',
            'dob':f'{2012+(i%10)}-{1+(i%12):02}-{1+(i%27):02}', 'sex':'F' if i%2 else 'M',
            'street':f'{9000+i} Synthetic Meadow Lane', 'city':'Towson','state':'MD','zip':'21204','county':'005'}

def row(i, source, n):
    p = patient(i)
    return [p['last'],p['first'],p['dob'],p['sex'],p['street'],'',p['city'],p['state'],p['zip'],p['county'],
            '77307-7',str(2+(i%12)),'ug/dL','V','ICPMS','2026-10-20','2026-10-22',
            f'R3-{source[-1].upper()}-{n:03}','F',sources[source][2]]

for source, ids in datasets.items():
    with (OUT/f'{source}.csv').open('w',newline='',encoding='utf-8') as f:
        writer = csv.writer(f)
        writer.writerow(header)
        writer.writerows(row(i,source,n) for n,i in enumerate(ids,1))
    if source == 'source_c':
        continue
    segments = []
    for n,i in enumerate(ids,1):
        p = patient(i)
        accession = f'R3-{source[-1].upper()}-{n:03}'
        segments += [
            f'MSH|^~\\&|{sources[source][0]}|{sources[source][2]}|MERIT|MD-LEAD|20261022120000||ORU^R01^ORU_R01|{accession}|P|2.5.1',
            f'PID|1||{p["id"]}^^^{sources[source][0]}^MR||{p["last"]}^{p["first"]}||{p["dob"].replace("-","")}|{p["sex"]}|||{p["street"]}^^{p["city"]}^MD^{p["zip"]}^USA^H^^24005',
            f'OBR|1||{accession}^{sources[source][0]}|77307-7^Lead [Mass/volume] in Venous blood^LN|||20261020100000|||||||||||||||20261022120000|||F',
            f'OBX|1|NM|77307-7^Lead [Mass/volume] in Venous blood^LN||{2+(i%12)}|ug/dL^Micrograms per deciliter^UCUM|||||F|||20261020100000|||ICPMS^Inductively coupled plasma mass spectrometry^99MERIT',
            'SPM|1|||BLDV^Blood venous^HL70487']
    (OUT/f'{source}.hl7').write_bytes(('\r'.join(segments)+'\r').encode('ascii'))

# Check exact identity intersections, not just shared row numbers.
identities = {s:{(patient(i)['first'],patient(i)['last'],patient(i)['dob']) for i in ids} for s,ids in datasets.items()}
assert all(len(ids)==20 and len(set(ids))==20 for ids in datasets.values())
assert len(identities['source_a'] & identities['source_b'])==3
assert len(identities['source_c'] & identities['source_a'])==3
assert len(identities['source_c'] & identities['source_b'])==4
(OUT/'patient_manifest.json').write_text(json.dumps({s:[patient(i) for i in ids] for s,ids in datasets.items()},indent=2)+'\n')

sql = ['SET QUOTED_IDENTIFIER ON; SET XACT_ABORT ON; SET NOCOUNT ON;',
       "IF DB_NAME() <> 'MERITRegion3Demo' THROW 51000, 'Run only on MERITRegion3Demo.', 1;",
       "EXEC config.uspInstallRootTenant @tenantCode='EPA-R3', @tenantName=N'EPA Region 3 Demo', @tenantType='region', @timeZone='Eastern Standard Time';",
       # Maryland was installed by the shipped demo; attach its whole existing subtree to Region 3.
       """DECLARE @region uniqueidentifier=(SELECT tenantId FROM auth.tenant WHERE tenantCode='EPA-R3');
DECLARE @md uniqueidentifier=(SELECT tenantId FROM auth.tenant WHERE tenantCode='MD-LEAD');
UPDATE auth.tenant SET parentTenantId=@region WHERE tenantId=@md;
INSERT auth.tenantClosure (tenantClosureId,ancestorTenantId,descendantTenantId,depth)
SELECT NEWID(),@region,c.descendantTenantId,c.depth+1 FROM auth.tenantClosure c
WHERE c.ancestorTenantId=@md AND c.isDeleted=0 AND NOT EXISTS
(SELECT 1 FROM auth.tenantClosure x WHERE x.ancestorTenantId=@region AND x.descendantTenantId=c.descendantTenantId AND x.isDeleted=0);
UPDATE c SET depth=2+d.depth FROM auth.tenantClosure c JOIN auth.tenantClosure d ON d.descendantTenantId=c.descendantTenantId AND d.ancestorTenantId=@md
WHERE c.ancestorTenantId=(SELECT tenantId FROM auth.tenant WHERE tenantCode='PLATFORM');
GO"""
]
states = [('R3-DE','Delaware','DE','10','003','New Castle County'),
          ('R3-DC','District of Columbia','DC','11','001','District of Columbia'),
          ('R3-PA','Pennsylvania','PA','42','101','Philadelphia County'),
          ('R3-VA','Virginia','VA','51','059','Fairfax County'),
          ('R3-WV','West Virginia','WV','54','037','Jefferson County')]
for code,name,postal,fips,county,county_name in states:
    counties = json.dumps([{'fips':county,'name':county_name}])
    sql += [f"EXEC config.uspInstallRootTenant @tenantCode='{code}', @tenantName={q(name)}, @tenantType='state', @statePostalCode='{postal}', @stateFips='{fips}', @parentTenantCode='EPA-R3', @countiesJson={q(counties)}, @timeZone='Eastern Standard Time';",
            f"EXEC config.uspInstallChildTenant @tenantCode='{code}-COUNTY', @tenantName={q(county_name+' Demo Health Department')}, @tenantType='jurisdiction', @parentTenantCode='{code}', @countyFipsList='{county}';"]
sql.append('GO')
users = [('region3demo','EPA-R3','EPA-R3','Region 3 demo administrator'),
         ('marylanddemo','MD-LEAD','MD-LEAD','Maryland demo administrator'),
         ('countydemo','MD-BCO','MD-LEAD','Baltimore County demo administrator'),
         ('platformdemo','PLATFORM','PLATFORM','Platform demo administrator')]
for user,home,root,display in users:
    uid = guid('region3demo:user:'+user)
    sql += [f"DECLARE @home uniqueidentifier=(SELECT tenantId FROM auth.tenant WHERE tenantCode='{home}');",
            f"EXEC auth.uspSaveUser @appUserId='{uid}', @userName={q(user)}, @displayName={q(display)}, @email={q(user+'@merit.example')}, @passwordHash={q(password_hash())}, @homeTenantId=@home;"]
    for role in (['platformAdministrator'] if home=='PLATFORM' else ['administrator','supervisor']):
        sql += [f"DECLARE @role_{role} uniqueidentifier=(SELECT roleId FROM auth.role WHERE rootTenantId=(SELECT rootTenantId FROM auth.tenant WHERE tenantCode='{root}') AND roleCode='{role}' AND isDeleted=0);",
                f"EXEC auth.uspSaveUserAssignment @userAssignmentId='{guid('region3demo:grant:'+user+role)}', @appUserId='{uid}', @roleId=@role_{role}, @tenantId=@home, @includeDescendants=1, @accessLevel='full', @startDate='2026-01-01';"]
    sql.append('GO')

# A small patient and queue sample in each other state demonstrates geographic filtering.
for code,name,postal,fips,county,county_name in states:
    sql += [f"DECLARE @tenant uniqueidentifier=(SELECT tenantId FROM auth.tenant WHERE tenantCode='{code}-COUNTY');",
            f"DECLARE @root uniqueidentifier=(SELECT tenantId FROM auth.tenant WHERE tenantCode='{code}');",
            f"IF NOT EXISTS(SELECT 1 FROM dbo.person WHERE personId='{guid('region3demo:person:'+code)}') INSERT dbo.person(personId,rootTenantId,owningTenantId,lastName,firstName,lastNameKey,firstNameKey,dob,sex) VALUES('{guid('region3demo:person:'+code)}',@root,@tenant,N'Demo{postal}',N'Synthetic',N'DEMO{postal}',N'SYNTHETIC','2019-03-15','female');",
            f"IF NOT EXISTS(SELECT 1 FROM dbo.workTask WHERE workTaskId='{guid('region3demo:task:'+code)}') INSERT dbo.workTask(workTaskId,rootTenantId,owningTenantId,queueCode,title,personId) VALUES('{guid('region3demo:task:'+code)}',@root,@tenant,'actionLevel',{q(name+' synthetic follow-up')},'{guid('region3demo:person:'+code)}');", 'GO']

# Configure three sources in Maryland using normal procedure permissions and the new state account.
sql += [f"DECLARE @u uniqueidentifier='{guid('region3demo:user:marylanddemo')}'; DECLARE @s uniqueidentifier=NEWID(); DECLARE @expires datetime2=DATEADD(hour,1,SYSUTCDATETIME());",
        'EXEC auth.uspCreateSessionScope @sessionScopeId=@s,@appUserId=@u,@expiresUtc=@expires;',
        "EXEC sys.sp_set_session_context @key=N'scopeKey',@value=@s; EXEC sys.sp_set_session_context @key=N'appUserId',@value=@u;",
        "DECLARE @md uniqueidentifier=(SELECT tenantId FROM auth.tenant WHERE tenantCode='MD-LEAD'); EXEC sys.sp_set_session_context @key=N'actingTenantId',@value=@md;",'GO']
for source,(app,name,clia) in sources.items():
    oid = guid('region3demo:org:'+source)
    sql += [f"EXEC dbo.uspSaveOrganization @organizationId='{oid}', @organizationName={q(name)}, @organizationNameKey={q(name.upper())}, @cliaNumber='{clia}', @typesJson=N'[\"laboratory\"]';",
            f"EXEC dbo.uspSaveIntakeProfile @intakeProfileId='{guid('region3demo:profile:'+source)}', @senderOrganizationId='{oid}', @profileName={q('Region 3 Demo '+source[-1].upper()+' - '+name)}, @fileType='csv', @headerRow=1, @dateFormats=N'yyyy-MM-dd', @columnMapJson={q(json.dumps(dict(zip(header,targets))))}, @valueTranslationsJson={q(json.dumps(translations))}, @constantsJson=N'{{}}', @headerSignature={q(','.join(header))};",'GO']
sql += ["UPDATE auth.sessionScope SET revokedUtc=SYSUTCDATETIME() WHERE sessionScopeId=TRY_CAST(SESSION_CONTEXT(N'scopeKey') AS uniqueidentifier);",'GO']
(OUT/'seed.sql').write_text('\n'.join(sql)+'\n',encoding='utf-8')
print('Generated Region 3 seed, 2 HL7 files, 3 import CSVs, and manifest. Exact overlaps: A/B=3, C/A=3, C/B=4; 50 unique patients.')

