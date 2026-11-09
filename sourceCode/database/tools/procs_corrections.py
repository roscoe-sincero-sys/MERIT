"""Tracked caregiver contact corrections without expanded clinical access (#258)."""
from procs_care import proc

# The capability is resolved per tenant from the clipped profile, not from a global permission union.
PATIENT_ACCESS = """EXISTS(SELECT 1 FROM dbo.person p
 JOIN auth.sessionScopeTenant st ON st.sessionScopeId=TRY_CAST(SESSION_CONTEXT(N'scopeKey') AS UNIQUEIDENTIFIER)
 AND st.isDeleted=0 AND st.canReadRelatedContacts=1
 CROSS APPLY auth.tvfPersonPredicate(p.rootTenantId,p.owningTenantId,p.personId) clinical
 WHERE p.personId=r.personId AND p.isDeleted=0 AND (p.owningTenantId=st.tenantId OR EXISTS
 (SELECT 1 FROM dbo.personAccess pa WHERE pa.personId=p.personId AND pa.tenantId=st.tenantId AND pa.isDeleted=0)))"""
OWNER_ACCESS = """EXISTS(SELECT 1 FROM dbo.person g
 JOIN auth.sessionScopeTenant st ON st.sessionScopeId=TRY_CAST(SESSION_CONTEXT(N'scopeKey') AS UNIQUEIDENTIFIER)
 AND st.isDeleted=0 AND st.canReadRelatedContacts=1 AND st.tenantId=g.owningTenantId
 CROSS APPLY auth.tvfPersonWritePredicate(g.rootTenantId,g.owningTenantId,g.personId,0) writable
 WHERE g.personId=r.caregiverId AND g.isDeleted=0)"""
SNAPSHOT = """(SELECT (SELECT contactType,contactValue,isPreferred FROM dbo.personContactMethod WHERE personId=@caregiverId AND isDeleted=0 FOR JSON PATH) AS methods,
 (SELECT l.addressLine1,l.unit,l.city,l.state,l.zip5,pl.isPrimary,pl.startDate,pl.endDate FROM dbo.personLocation pl
 JOIN dbo.location l ON l.locationId=pl.locationId AND l.isDeleted=0 WHERE pl.personId=@caregiverId AND pl.isDeleted=0 FOR JSON PATH) AS addresses FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)"""
PROCS = [
proc('uspListContactCorrections','read','@personId UNIQUEIDENTIFIER = NULL',f"""
SELECT r.*,t.tenantName AS responsibleAgency,CONCAT(p.lastName,N', ',p.firstName) AS patientName,
 r.caregiverName,CAST(CASE WHEN {OWNER_ACCESS} THEN 1 ELSE 0 END AS BIT) AS canReview
FROM dbo.contactCorrection r LEFT JOIN dbo.person p ON p.personId=r.personId
LEFT JOIN dbo.person g ON g.personId=r.caregiverId JOIN auth.tenant t ON t.tenantId=COALESCE(g.owningTenantId,r.responsibleTenantId)
WHERE r.isDeleted=0 AND (@personId IS NULL OR r.personId=@personId) AND ({PATIENT_ACCESS} OR {OWNER_ACCESS})
ORDER BY CASE WHEN r.requestStatus='pending' THEN 0 ELSE 1 END,r.createdDateUtc DESC;
""",permission='person.edit',desc='Scoped correction history and owning-team review inbox; no clinical chart rights are added.'),
proc('uspRequestContactCorrection','write','''@contactCorrectionId UNIQUEIDENTIFIER
, @personId UNIQUEIDENTIFIER
, @caregiverId UNIQUEIDENTIFIER
, @contactType VARCHAR(12)
, @proposedValue NVARCHAR(200)
, @sourceNote NVARCHAR(1000)
, @city NVARCHAR(80) = NULL
, @state VARCHAR(2) = NULL
, @zip5 VARCHAR(5) = NULL
, @unitAction VARCHAR(12) = 'unspecified'
, @unit NVARCHAR(30) = NULL
, @proposedAddressKey NVARCHAR(300) = NULL''',f"""
DECLARE @root UNIQUEIDENTIFIER,@owner UNIQUEIDENTIFIER,@responsible UNIQUEIDENTIFIER,@name NVARCHAR(140);
SELECT @root=p.rootTenantId,@owner=p.owningTenantId,@responsible=g.owningTenantId,@name=CONCAT(g.lastName,N', ',g.firstName) FROM dbo.person p
JOIN dbo.personRelationship rel ON rel.personId=p.personId AND rel.relatedPersonId=@caregiverId AND rel.isDeleted=0
JOIN dbo.person g ON g.personId=rel.relatedPersonId AND g.rootTenantId=p.rootTenantId AND g.isDeleted=0
CROSS APPLY(SELECT p.personId,@caregiverId AS caregiverId) r
WHERE p.personId=@personId AND p.isDeleted=0 AND p.mergedIntoPersonId IS NULL
 AND (rel.startDate IS NULL OR rel.startDate<=auth.udfSessionToday()) AND (rel.endDate IS NULL OR rel.endDate>auth.udfSessionToday())
 AND (rel.isPrimaryContact=1 OR rel.hasLegalCustody=1 OR rel.role IN('caregiver','parent','legalGuardian','fosterParent')) AND {PATIENT_ACCESS};
IF @owner IS NULL
BEGIN
 ;THROW 51403,N'An active caregiver relationship and clinical contact access to this patient are required.',1;
END;
IF @contactType NOT IN('phone','email','address') OR NULLIF(TRIM(@proposedValue),N'') IS NULL OR NULLIF(TRIM(@sourceNote),N'') IS NULL
 OR @unitAction IS NULL OR @unitAction NOT IN('unspecified','set','remove')
 OR (@contactType<>'address' AND (@unitAction<>'unspecified' OR @unit IS NOT NULL OR @proposedAddressKey IS NOT NULL))
 OR (@unitAction='set' AND NULLIF(TRIM(@unit),N'') IS NULL)
 OR (@unitAction<>'set' AND @unit IS NOT NULL)
 OR (@unitAction='unspecified' AND @proposedAddressKey IS NOT NULL)
 OR (@unitAction IN('set','remove') AND NULLIF(@proposedAddressKey,N'') IS NULL)
 OR (@contactType='address' AND (NULLIF(TRIM(@city),N'') IS NULL OR @state IS NULL OR @zip5 IS NULL OR LEN(@state)<>2 OR LEN(@zip5)<>5 OR @zip5 LIKE '%[^0-9]%'))
BEGIN
 ;THROW 51422,N'Provide the proposed contact details and their source; addresses require city, state and ZIP.',1;
END;
INSERT dbo.contactCorrection(contactCorrectionId,rootTenantId,owningTenantId,personId,caregiverId,caregiverName,responsibleTenantId,contactType,proposedValue,sourceNote,city,state,zip5,unitAction,unit,proposedAddressKey,beforeJson)
VALUES(@contactCorrectionId,@root,@owner,@personId,@caregiverId,@name,@responsible,@contactType,@proposedValue,@sourceNote,@city,@state,@zip5,@unitAction,@unit,@proposedAddressKey,{SNAPSHOT});
INSERT logs.auditEvent(appUserId,rootTenantId,eventAction,entityType,entityId)
VALUES(auth.udfSessionUserId(),@root,'create','contactCorrection',@contactCorrectionId);
""",permission='person.edit'),
proc('uspReviewContactCorrection','write','''@contactCorrectionId UNIQUEIDENTIFIER
, @requestStatus VARCHAR(12)
, @reviewNote NVARCHAR(1000)
, @rowVersion BINARY(8)''',f"""
DECLARE @caregiverId UNIQUEIDENTIFIER,@root UNIQUEIDENTIFIER,@type VARCHAR(12),@value NVARCHAR(200),@city NVARCHAR(80),@state VARCHAR(2),@zip VARCHAR(5),@unitAction VARCHAR(12),@addressKey NVARCHAR(300);
SELECT @caregiverId=r.caregiverId,@root=r.rootTenantId,@type=r.contactType,@value=r.proposedValue,@city=r.city,@state=r.state,@zip=r.zip5,@unitAction=r.unitAction,@addressKey=r.proposedAddressKey
FROM dbo.contactCorrection r WITH(UPDLOCK,HOLDLOCK) WHERE r.contactCorrectionId=@contactCorrectionId AND r.isDeleted=0
 AND r.requestStatus='pending' AND r.rowVersion=@rowVersion AND {OWNER_ACCESS};
IF @caregiverId IS NULL
BEGIN
 ;THROW 51409,N'The request changed, was reviewed, or is outside your caregiver contact-maintenance authority.',1;
END;
IF @requestStatus NOT IN('applied','rejected') OR NULLIF(TRIM(@reviewNote),N'') IS NULL
BEGIN
 ;THROW 51422,N'Choose applied or rejected and record review evidence.',1;
END;
IF @requestStatus='applied' AND NOT
 ((@type IN('phone','email') AND EXISTS(SELECT 1 FROM dbo.personContactMethod WHERE personId=@caregiverId AND contactType=@type AND contactValue=@value AND isDeleted=0))
 OR (@type='address' AND EXISTS(SELECT 1 FROM dbo.personLocation pl JOIN dbo.location l ON l.locationId=pl.locationId AND l.isDeleted=0
 WHERE pl.personId=@caregiverId AND pl.role='residence' AND pl.isPrimary=1 AND pl.isDeleted=0 AND pl.startDate<=auth.udfSessionToday()
 AND (pl.endDate IS NULL OR pl.endDate>=auth.udfSessionToday()) AND l.addressLine1=@value AND l.city=@city AND l.state=@state AND l.zip5=@zip
 AND (@unitAction='unspecified' OR (@unitAction IN('set','remove') AND l.addressKey=@addressKey)))))
BEGIN
 ;THROW 51422,N'Apply and verify the proposed contact, including any requested apartment/unit change, in the caregiver workspace before marking this request applied.',1;
END;
UPDATE dbo.contactCorrection SET requestStatus=@requestStatus,reviewNote=@reviewNote,reviewedByUserId=auth.udfSessionUserId(),reviewedUtc=SYSUTCDATETIME(),afterJson={SNAPSHOT}
WHERE contactCorrectionId=@contactCorrectionId;
INSERT logs.auditEvent(appUserId,rootTenantId,eventAction,entityType,entityId)
VALUES(auth.udfSessionUserId(),@root,'update','contactCorrection',@contactCorrectionId);
""",permission='person.edit')]
