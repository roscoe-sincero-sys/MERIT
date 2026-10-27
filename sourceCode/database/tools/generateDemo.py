"""
Generates scripts/900_demo/900_demoMaryland.sql: a synthetic demo jurisdiction (Maryland is used only as an example, as
the specification does), with its tenant tree, users, organizations, places and a few people whose records exercise the
model -- a shared-custody child with a sibling, a battery-recycling worker, and a pregnant person.

    python sourceCode/database/tools/generateDemo.py

No real data. Every name and address is invented. Demo users share one password, printed in the generated file header
and in README.md; every account must still enroll an authenticator app at first sign-in.

Password hashes are ASP.NET Core Identity V3 format (PBKDF2-HMAC-SHA512, 100,000 iterations, 128-bit salt, 256-bit
subkey), so Merit.Web verifies them with the standard PasswordHasher. Name and address keys are computed with the same
rules as Merit.Domain.Text.MatchKeys.
"""
from __future__ import annotations

import base64
from datetime import date, timedelta
import hashlib
import os
import re
import struct
import uuid
import unicodedata
from pathlib import Path
from boundaries import boundary_sql

OUT = Path(__file__).resolve().parents[1] / "scripts" / "900_demo" / "900_demoMaryland.sql"
DEMO_PASSWORD = "MeritDemo!2026"
ROOT_CODE = "MD-LEAD"

ABBREVIATIONS = {
    "STREET": "ST", "AVENUE": "AVE", "ROAD": "RD", "DRIVE": "DR", "BOULEVARD": "BLVD", "LANE": "LN", "COURT": "CT",
    "PLACE": "PL", "TERRACE": "TER", "CIRCLE": "CIR", "PARKWAY": "PKWY", "HIGHWAY": "HWY", "NORTH": "N", "SOUTH": "S",
    "EAST": "E", "WEST": "W", "APARTMENT": "APT", "SUITE": "STE",
}
UNIT_PREFIXES = {"APT", "UNIT", "STE", "NO", "FL", "RM"}


def identity_v3_hash(password: str, salt: bytes | None = None) -> str:
    salt = salt or os.urandom(16)
    sub = hashlib.pbkdf2_hmac("sha512", password.encode("utf-8"), salt, 100_000, 32)
    blob = b"\x01" + struct.pack(">III", 2, 100_000, 16) + salt + sub
    return base64.b64encode(blob).decode("ascii")


def strip_accents(s: str) -> str:
    return "".join(ch for ch in unicodedata.normalize("NFKD", s) if not unicodedata.combining(ch))


def name_key(s: str | None) -> str | None:
    if not s:
        return None
    k = re.sub(r"[^A-Z]", "", strip_accents(s).upper())
    return k or None


def norm_words(s: str | None) -> str:
    if not s:
        return ""
    words = re.sub(r"[^A-Z0-9]+", " ", strip_accents(s).upper()).split()
    return " ".join(ABBREVIATIONS.get(w, w) for w in words)


def norm_unit(s: str | None) -> str:
    words = norm_words(s).split()
    while words and words[0] in UNIT_PREFIXES:
        words = words[1:]
    return " ".join(words)


def address_key(line1, unit, city, state, zip5) -> str:
    return "|".join([norm_words(line1), norm_unit(unit), norm_words(city), norm_words(state), (zip5 or "")[:5]])


def h(key: str) -> str:
    """The GUID SQL Server produces for CAST (HASHBYTES ('MD5', N'<key>') AS UNIQUEIDENTIFIER), as a literal."""
    return "'" + str(uuid.UUID(bytes_le=hashlib.md5(key.encode("utf-16-le")).digest())) + "'"


def g(name: str) -> str:
    """Deterministic GUID literal for a demo row. EXEC arguments must be constants, so keys are computed here."""
    return h("demo:" + name)


def bare(lit: str) -> str:
    return lit.strip("'")


def q(v) -> str:
    if v is None:
        return "NULL"
    if isinstance(v, bool):
        return "1" if v else "0"
    if isinstance(v, (int, float)):
        return str(v)
    return "N'" + str(v).replace("'", "''") + "'"


COUNTIES = [
    ("001", "Allegany County", "county"), ("003", "Anne Arundel County", "county"), ("005", "Baltimore County", "county"),
    ("009", "Calvert County", "county"), ("011", "Caroline County", "county"), ("013", "Carroll County", "county"),
    ("015", "Cecil County", "county"), ("017", "Charles County", "county"), ("019", "Dorchester County", "county"),
    ("021", "Frederick County", "county"), ("023", "Garrett County", "county"), ("025", "Harford County", "county"),
    ("027", "Howard County", "county"), ("029", "Kent County", "county"), ("031", "Montgomery County", "county"),
    ("033", "Prince George's County", "county"), ("035", "Queen Anne's County", "county"),
    ("037", "St. Mary's County", "county"), ("039", "Somerset County", "county"), ("041", "Talbot County", "county"),
    ("043", "Washington County", "county"), ("045", "Wicomico County", "county"), ("047", "Worcester County", "county"),
    ("510", "Baltimore City", "independentCity"), ("999", "Unknown", "county"),
]

CHILD_TENANTS = [
    ("MD-OCC", "Occupational Health Program", "program", ""),
    ("MD-BCHD", "Baltimore City Health Department", "jurisdiction", "510"),
    ("MD-BCO", "Baltimore County Department of Health", "jurisdiction", "005"),
    ("MD-PGC", "Prince George's County Health Department", "jurisdiction", "033"),
]

PLATFORM_CODE = "PLATFORM"

USERS = [
    # userName, displayName, home tenant code, grants: (role code, tenant code, includeDescendants, access level)
    ("platform", "Pat Platform (platform administrator)", PLATFORM_CODE,
     [("platformAdministrator", PLATFORM_CODE, True, "full")]),
    ("admin", "Avery Admin (state administrator)", ROOT_CODE,
     [("administrator", ROOT_CODE, True, "full"), ("supervisor", ROOT_CODE, True, "full")]),
    ("supervisor", "Sam Supervisor", ROOT_CODE, [("supervisor", ROOT_CODE, True, "full")]),
    ("intake", "Ivy Intake", ROOT_CODE, [("intakeClerk", ROOT_CODE, True, "full")]),
    ("cmbalt", "Casey Morgan (Baltimore City)", "MD-BCHD", [("caseManager", "MD-BCHD", True, "full")]),
    ("cmbaltco", "Jordan Lee (Baltimore County)", "MD-BCO", [("caseManager", "MD-BCO", True, "full")]),
    ("cmboth", "Dana Brooks (City and County)", "MD-BCHD",
     [("caseManager", "MD-BCHD", True, "full"), ("caseManager", "MD-BCO", True, "full")]),
    ("cityadmin", "Erin Cole (Baltimore City administrator)", "MD-BCHD",
     [("userAdministrator", "MD-BCHD", True, "full"), ("caseManager", "MD-BCHD", True, "full")]),
    ("countyadmin", "Frank Diaz (Baltimore County administrator)", "MD-BCO",
     [("userAdministrator", "MD-BCO", True, "full"), ("caseManager", "MD-BCO", True, "full")]),
    ("occinv", "Riley Okoro (Occupational)", "MD-OCC", [("occupationalInvestigator", "MD-OCC", True, "full")]),
    ("epi", "Emery Patel (Epidemiology)", ROOT_CODE, [("epidemiologist", ROOT_CODE, True, "full")]),
]

LOCATIONS = {
    # key: (line1, unit, city, state, zip5, countyFips, yearBuilt, housingType, tenure)
    "motherHome": ("1214 W Lombard Street", "2F", "Baltimore", "MD", "21223", "510", 1910, "multiUnit", "rental"),
    "fatherHome": ("27 Oak Avenue", None, "Towson", "MD", "21204", "005", 1965, "singleFamily", "ownerOccupied"),
    "grandmaHome": ("3302 Edmondson Avenue", None, "Baltimore", "MD", "21229", "510", 1925, "singleFamily", "ownerOccupied"),
    "childCare": ("880 Washington Boulevard", None, "Baltimore", "MD", "21230", "510", 1950, "commercial", None),
    "plant": ("4100 Ridge Road", None, "Baltimore", "MD", "21226", "510", 1972, "commercial", None),
    "carterHome": ("615 Ponca Street", None, "Baltimore", "MD", "21224", "510", 1940, "singleFamily", "rental"),
    "okaforHome": ("9 Calverton Court", "B", "Hyattsville", "MD", "20782", "033", 1968, "multiUnit", "rental"),
    "labSite": ("100 Research Drive", None, "Columbia", "MD", "21046", "027", 1999, "commercial", None),
    "clinicSite": ("2200 Harbor Way", None, "Baltimore", "MD", "21230", "510", 2005, "commercial", None),
}

# Approximate coordinates for the synthetic addresses, so the case map and cluster review have points to draw.
COORDINATES = {
    "motherHome": (39.286900, -76.637200), "fatherHome": (39.401500, -76.601900),
    "grandmaHome": (39.293000, -76.674500), "childCare": (39.279500, -76.630500),
    "plant": (39.223500, -76.560000), "carterHome": (39.292500, -76.557000),
    "okaforHome": (38.965500, -76.955000), "labSite": (39.196000, -76.825000),
    "clinicSite": (39.276000, -76.618000),
}

ORGS = [
    # key, name, types, naics, clia, siteKey
    ("lab", "Chesapeake Reference Laboratory", ["laboratory"], None, "21D0712345", "labSite"),
    ("clinic", "Harbor Pediatrics", ["providerPractice", "laboratory"], None, "21D0654321", "clinicSite"),
    ("abcCare", "ABC Child Care", ["childCare", "employer"], "624410", None, "childCare"),
    ("battery", "Patapsco Battery Recycling", ["employer"], "562920", None, "plant"),
]

PERSONS = [
    # key, last, first, dob, sex, ethnicity, races, language
    ("mia", "Rivera", "Mia", "2023-05-14", "female", "hispanic", ["white"], "es"),
    ("leo", "Rivera", "Leo", "2021-02-02", "male", "hispanic", ["white"], "es"),
    ("ana", "Rivera", "Ana", "1995-11-30", "female", "hispanic", ["white"], "es"),
    ("luis", "Rivera", "Luis", "1992-07-08", "male", "hispanic", ["white"], "es"),
    ("rosa", "Martinez", "Rosa", "1961-04-21", "female", "hispanic", ["white"], "es"),
    ("james", "Carter", "James", "1980-08-19", "male", "notHispanic", ["blackAfricanAmerican"], "en"),
    ("grace", "Okafor", "Grace", "1994-03-03", "female", "notHispanic", ["blackAfricanAmerican"], "en"),
]


def coordinates() -> str:
    """Approximate coordinates for the demo addresses (synthetic), set on every run so the map has points."""
    lines = []
    for key, (lat, lon) in COORDINATES.items():
        lines.append(f"EXEC dbo.uspSetLocationCoordinates @locationId = {g('loc:' + key)}, @latitude = {lat:.6f}, "
                     f"@longitude = {lon:.6f}, @geocodeQuality = 'rooftop';")
    return "\n".join(lines) + "\nGO\n"


def main() -> int:
    pw = identity_v3_hash(DEMO_PASSWORD)
    out: list[str] = []
    w = out.append
    w(f"""SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
GO

/*
    DEMO DATA -- synthetic, never for production. Loaded only by Deploy-MeritDatabase.ps1 -IncludeDemoData.
    Generated by sourceCode/database/tools/generateDemo.py; edit that file and re-generate.

    Demo jurisdiction {ROOT_CODE} ("Maryland" is an example only), its counties, four child tenants and seven users.
    Every demo user's password is:  {DEMO_PASSWORD}
    Each must enroll an authenticator app (TOTP) at first sign-in; MFA cannot be turned off.

    Users: {", ".join(u[0] for u in USERS)}

    Part 1 (tenants, counties, users, settings) converges on every run. Part 2 (people and their records) is loaded once,
    through the same stored procedures the application calls, under a short-lived session scope that is revoked at the
    end; on a later run it is skipped.
*/

-- ======================================================================================== part 1: jurisdiction
EXEC config.uspCreateRootTenant @tenantCode = N'{ROOT_CODE}', @tenantName = N'Maryland Lead and Metals Program (demo)',
     @statePostalCode = 'MD', @stateFips = '24', @tenantType = 'state',
     @timeZone = 'Eastern Standard Time';
GO

DECLARE @root UNIQUEIDENTIFIER = (SELECT tenantId FROM auth.tenant WHERE tenantCode = N'{ROOT_CODE}');

MERGE config.subJurisdiction AS t
USING (VALUES""")
    w(",\n".join(f"    ('{f}', {q(n)}, '{t}')" for f, n, t in COUNTIES))
    w(f""") AS s (countyFips, subJurisdictionName, jurisdictionType)
   ON t.rootTenantId = @root AND t.countyFips = s.countyFips AND t.isDeleted = 0
WHEN NOT MATCHED THEN
    INSERT (subJurisdictionId, rootTenantId, countyFips, subJurisdictionName, jurisdictionType)
    VALUES (CAST (HASHBYTES ('MD5', N'county:{ROOT_CODE}:' + s.countyFips) AS UNIQUEIDENTIFIER), @root, s.countyFips,
            s.subJurisdictionName, s.jurisdictionType);
GO
""")
    # County outlines for the maps' context layer (issue #48), from the Census Bureau.
    w("-- County outlines for the case and cluster maps (U.S. Census Bureau, public domain).")
    w(boundary_sql(Path(__file__).parent / "data" / "mdCountyBoundaries.geojson", ROOT_CODE))
    for code, name, ttype, fips in CHILD_TENANTS:
        w(f"EXEC config.uspCreateChildTenant @tenantCode = N'{code}', @tenantName = {q(name)}, @tenantType = '{ttype}', "
          f"@parentTenantCode = N'{ROOT_CODE}', @countyFipsList = '{fips}';")
    w("GO\n")

    w(f"""-- Demo values for the settings the specification says must be set at install. Only placeholders are replaced.
DECLARE @root UNIQUEIDENTIFIER = (SELECT tenantId FROM auth.tenant WHERE tenantCode = N'{ROOT_CODE}');
UPDATE s SET settingValue = v.settingValue
  FROM config.jurisdictionSetting AS s
  JOIN (VALUES ('investigationLookbackDays', N'180'), ('siteInvestigationMinHours', N'10'),
               ('contactAttemptsBeforeLetter', N'3'), ('clusterWindowDays', N'365'), ('clusterMinCount', N'2'))
       AS v (settingKey, settingValue) ON v.settingKey = s.settingKey
 WHERE s.rootTenantId = @root AND s.isDeleted = 0 AND s.settingValue = N'NOTSET';

UPDATE config.jurisdictionSetting SET settingValue = N'MD-OCC'
 WHERE rootTenantId = @root AND settingKey = 'adultCaseOwnerTenantCode' AND settingValue = N'' AND isDeleted = 0;

-- Demo levels. Adult and other-metal levels must come from the adopting jurisdiction's own regulations; these are
-- illustrative. The NIOSH adult row ships inactive and is switched on here.
UPDATE config.threshold SET isActive = 1
 WHERE thresholdId = CAST (HASHBYTES ('MD5', CONCAT (N'threshold:', N'{ROOT_CODE}', N':', 'pb-adult-case-niosh')) AS UNIQUEIDENTIFIER)
   AND isActive = 0;

DECLARE @spec TABLE (rowKey VARCHAR (60), analyte VARCHAR (8), specimen VARCHAR (20), population VARCHAR (10),
                     speciation VARCHAR (20), thresholdType VARCHAR (12), actionCode VARCHAR (40), dueDays INT,
                     lowerValue DECIMAL (18, 6), unit NVARCHAR (20), authority NVARCHAR (300));
INSERT @spec VALUES
  ('demo-pb-pregnant-case', 'PB', 'wholeBlood', 'pregnant', NULL, 'case', NULL, NULL, 5, N'ug/dL', N'CDC guidelines for pregnant and lactating women (demo row).'),
  ('demo-pb-adult-medical', 'PB', 'wholeBlood', 'adult', NULL, 'action', 'medicalEvaluation', 30, 30, N'ug/dL', N'Demo medical-removal review level.'),
  ('demo-hg-blood-case', 'HG', 'wholeBlood', 'all', NULL, 'case', NULL, NULL, 10, N'ug/L', N'Demo state reportable level.'),
  ('demo-cd-blood-case', 'CD', 'wholeBlood', 'all', NULL, 'case', NULL, NULL, 5, N'ug/L', N'Demo state reportable level.'),
  ('demo-as-urine-total', 'AS', 'urine', 'all', 'total', 'action', 'speciationRecommended', 14, 50, N'ug/L', N'Demo: total urine arsenic above 50 ug/L prompts speciation.'),
  ('demo-as-urine-inorganic', 'AS', 'urine', 'all', 'inorganic', 'case', NULL, NULL, 35, N'ug/L', N'Demo state reportable level for inorganic arsenic.');

INSERT config.threshold (thresholdId, rootTenantId, analyteSpecimenId, population, speciation, thresholdType, actionCodeId,
                         dueIntervalDays, lowerValue, unit, comparator, effectiveFrom, authority, isActive)
SELECT CAST (HASHBYTES ('MD5', CONCAT (N'threshold:', N'{ROOT_CODE}', N':', s.rowKey)) AS UNIQUEIDENTIFIER), @root,
       sp.analyteSpecimenId, s.population, s.speciation, s.thresholdType, ac.actionCodeId, s.dueDays, s.lowerValue, s.unit,
       '>=', '2026-01-01', s.authority, 1
  FROM @spec AS s
  JOIN config.analyte AS a ON a.code = s.analyte
  JOIN config.analyteSpecimen AS sp ON sp.analyteId = a.analyteId AND sp.specimen = s.specimen
  LEFT JOIN config.actionCode AS ac ON ac.code = s.actionCode
 WHERE NOT EXISTS (SELECT 1 FROM config.threshold AS x
                    WHERE x.thresholdId = CAST (HASHBYTES ('MD5', CONCAT (N'threshold:', N'{ROOT_CODE}', N':', s.rowKey)) AS UNIQUEIDENTIFIER));
GO

-- Users. The password hash is ASP.NET Core Identity V3; the plain password is in this file's header. Each user has a home
-- tenant (who manages the account) and one or more grants (their master profile).
DECLARE @users TABLE (userName NVARCHAR (100), displayName NVARCHAR (120), homeTenantCode VARCHAR (30));
INSERT @users VALUES""")
    w(",\n".join(f"  ({q(u)}, {q(d)}, '{h}')" for u, d, h, _ in USERS) + ";")
    grants = []
    for u, _, _, gs in USERS:
        for n, (role, tenant, inc, level) in enumerate(gs):
            key = f"assignment:{u}" if n == 0 else f"assignment:{u}:{n}"
            grants.append(f"  ({q(key)}, {q(u)}, '{role}', '{tenant}', {1 if inc else 0}, '{level}')")
    w(f"""
INSERT auth.appUser (appUserId, rootTenantId, homeTenantId, userName, displayName, email, passwordHash, securityStamp)
SELECT CAST (HASHBYTES ('MD5', N'user:' + u.userName) AS UNIQUEIDENTIFIER), h.rootTenantId, h.tenantId, u.userName,
       u.displayName, u.userName + N'@merit.example', N'{pw}', NEWID ()
  FROM @users AS u
  JOIN auth.tenant AS h ON h.tenantCode = u.homeTenantCode
 WHERE NOT EXISTS (SELECT 1 FROM auth.appUser AS x WHERE x.userName = u.userName AND x.isDeleted = 0);

UPDATE x SET displayName = u.displayName, homeTenantId = h.tenantId, rootTenantId = h.rootTenantId
  FROM auth.appUser AS x
  JOIN @users AS u ON u.userName = x.userName
  JOIN auth.tenant AS h ON h.tenantCode = u.homeTenantCode
 WHERE x.isDeleted = 0
   AND (x.displayName <> u.displayName OR x.homeTenantId IS NULL OR x.homeTenantId <> h.tenantId);

INSERT auth.userProfile (userProfileId, appUserId, profileName, profileDescription, isMaster, isActive)
SELECT CAST (HASHBYTES ('MD5', CONCAT (N'masterProfile:', x.appUserId)) AS UNIQUEIDENTIFIER), x.appUserId, N'Master profile',
       N'Everything you have been given. Managed by your administrator.', 1, 1
  FROM auth.appUser AS x
  JOIN @users AS u ON u.userName = x.userName
 WHERE NOT EXISTS (SELECT 1 FROM auth.userProfile AS p WHERE p.appUserId = x.appUserId AND p.isMaster = 1 AND p.isDeleted = 0);

DECLARE @grants TABLE (grantKey NVARCHAR (200), userName NVARCHAR (100), roleCode VARCHAR (40), tenantCode VARCHAR (30),
                       includeDescendants BIT, accessLevel VARCHAR (10));
INSERT @grants VALUES
""" + ",\n".join(grants) + f""";

-- Roles come from the grant tenant's data root, or from the platform for the platform role.
INSERT auth.userAssignment (userAssignmentId, appUserId, roleId, tenantId, includeDescendants, accessLevel, startDate)
SELECT CAST (HASHBYTES ('MD5', g.grantKey) AS UNIQUEIDENTIFIER), CAST (HASHBYTES ('MD5', N'user:' + g.userName) AS UNIQUEIDENTIFIER),
       r.roleId, t.tenantId, g.includeDescendants, g.accessLevel, '2026-01-01'
  FROM @grants AS g
  JOIN auth.tenant AS t ON t.tenantCode = g.tenantCode
  JOIN auth.role   AS r ON r.rootTenantId = t.rootTenantId AND r.roleCode = g.roleCode AND r.isDeleted = 0
 WHERE NOT EXISTS (SELECT 1 FROM auth.userAssignment AS x
                    WHERE x.userAssignmentId = CAST (HASHBYTES ('MD5', g.grantKey) AS UNIQUEIDENTIFIER));
GO

-- ======================================================================================== part 2: people, loaded once
IF EXISTS (SELECT 1 FROM dbo.person WHERE personId = {g('person:mia')})
BEGIN
    PRINT N'Demo people already loaded; part 2 skipped.';
    SET NOEXEC ON;
END;
GO

-- A short-lived session scope for the supervisor, so every procedure below runs exactly as it does for the application.
DECLARE @scope UNIQUEIDENTIFIER = NEWID ();
DECLARE @user UNIQUEIDENTIFIER = CAST (HASHBYTES ('MD5', N'user:supervisor') AS UNIQUEIDENTIFIER);
DECLARE @expires DATETIME2 (3) = DATEADD (MINUTE, 30, SYSUTCDATETIME ());
DECLARE @r TABLE (a NVARCHAR (MAX) NULL);

EXEC auth.uspCreateSessionScope @sessionScopeId = @scope, @appUserId = @user, @expiresUtc = @expires;
EXEC sys.sp_set_session_context @key = N'scopeKey', @value = @scope;
EXEC sys.sp_set_session_context @key = N'appUserId', @value = @user;
EXEC sys.sp_set_session_context @key = N'AppUser', @value = N'supervisor (demo loader)';
EXEC sys.sp_set_session_context @key = N'actingTenantId', @value = {h('tenant:' + ROOT_CODE)};
GO
""")
    # locations
    for key, (line1, unit, city, state, zip5, fips, yb, ht, ten) in LOCATIONS.items():
        akey = address_key(line1, unit, city, state, zip5)
        bkey = address_key(line1, None, city, state, zip5) if unit else None
        w(f"EXEC dbo.uspSaveLocation @locationId = {g('loc:' + key)}, @addressLine1 = {q(line1)}, @unit = {q(unit)}, "
          f"@city = {q(city)}, @state = '{state}', @zip5 = '{zip5}', @countyFips = '{fips}', @addressKey = {q(akey)}, "
          f"@buildingAddressKey = {q(bkey)}, @yearBuilt = {yb}, @housingType = {q(ht)}, @tenure = {q(ten)};")
    w("GO\n")
    # organizations
    for key, name, types, naics, clia, site in ORGS:
        types_json = "[" + ",".join(f'"{t}"' for t in types) + "]"
        w(f"EXEC dbo.uspSaveOrganization @organizationId = {g('org:' + key)}, @organizationName = {q(name)}, "
          f"@organizationNameKey = {q(norm_words(name))}, @naicsCode = {q(naics)}, "
          f"@naicsVersion = {q('2022' if naics else None)}, @cliaNumber = {q(clia)}, @typesJson = N'{types_json}';")
        w(f"EXEC dbo.uspSaveOrganizationSite @organizationSiteId = {g('site:' + key)}, @organizationId = {g('org:' + key)}, "
          f"@locationId = {g('loc:' + site)}, @siteName = N'Main', @startDate = '2010-01-01';")
    w(f"EXEC dbo.uspSaveProvider @providerId = {g('provider:nguyen')}, @npi = '1234567893', @lastName = N'Nguyen', "
      f"@firstName = N'Linh', @credential = N'MD', @practiceLocationId = {g('loc:clinicSite')};")
    w("GO\n")
    # persons
    for key, last, first, dob, sex, eth, races, lang in PERSONS:
        w(f"EXEC dbo.uspSavePerson @personId = {g('person:' + key)}, @lastName = {q(last)}, @firstName = {q(first)}, "
          f"@lastNameKey = {q(name_key(last))}, @firstNameKey = {q(name_key(first))}, @dob = '{dob}', @sex = '{sex}', "
          f"@ethnicity = '{eth}', @preferredLanguage = '{lang}';")
        races_json = "[" + ",".join(f'"{r}"' for r in races) + "]"
        w(f"EXEC dbo.uspSavePersonRaces @personId = {g('person:' + key)}, @raceCodesJson = N'{races_json}';")
    w(f"EXEC dbo.uspSavePersonContactMethod @personContactMethodId = {g('contact:ana')}, @personId = {g('person:ana')}, "
      f"@contactType = 'phone', @contactValue = N'410-555-0142', @isPreferred = 1;")
    w(f"EXEC dbo.uspSavePersonContactMethod @personContactMethodId = {g('contact:james')}, @personId = {g('person:james')}, "
      f"@contactType = 'phone', @contactValue = N'410-555-0177', @isPreferred = 1;")
    w(f"EXEC dbo.uspSavePersonIdentifier @personIdentifierId = {g('ident:mia')}, @personId = {g('person:mia')}, "
      f"@identifierType = 'mrn', @identifierValue = N'HP-0042117', @assigningFacility = N'Harbor Pediatrics', "
      f"@source = 'labReport', @reportedDate = '2026-08-06';")
    w("GO\n")

    # relationships: (key, person, related, role, primary, custody)
    rels = [
        ("mia-ana", "mia", "ana", "parent", True, True), ("mia-luis", "mia", "luis", "parent", False, True),
        ("leo-ana", "leo", "ana", "parent", True, True), ("leo-luis", "leo", "luis", "parent", False, True),
        ("mia-leo", "mia", "leo", "sibling", False, False), ("mia-rosa", "mia", "rosa", "otherCaregiver", False, False),
    ]
    for key, p, r, role, prim, cust in rels:
        w(f"EXEC dbo.uspSavePersonRelationship @personRelationshipId = {g('rel:' + key)}, @personId = {g('person:' + p)}, "
          f"@relatedPersonId = {g('person:' + r)}, @role = '{role}', @isPrimaryContact = {1 if prim else 0}, "
          f"@hasLegalCustody = {1 if cust else 0}, @source = 'interview', @verifiedDate = '2026-08-12';")
    w("GO\n")

    # places: person -> list of change dicts
    def pl(key, loc, role, primary=False, reason=None, sched=None, hours=None, start="2024-01-01", site=None):
        parts = [f'"personLocationId":"{bare(g(key))}"', f'"locationId":"{bare(g("loc:" + loc))}"', f'"role":"{role}"',
                 f'"isPrimary":{"true" if primary else "false"}', f'"startDate":"{start}"', '"source":"interview"']
        if reason:
            parts.append(f'"primaryReason":"{reason}"')
        if sched:
            parts.append(f'"scheduleType":"{sched}"')
        if hours is not None:
            parts.append(f'"hoursPerWeek":{hours}')
        if site:
            parts.append(f'"organizationSiteId":"{bare(g("site:" + site))}"')
        return "{" + ",".join(parts) + "}"

    places = {
        "mia": [pl("pl:mia-mother", "motherHome", "residence", True, "custodyOrder", "weekdays", 120, "2023-05-14"),
                pl("pl:mia-father", "fatherHome", "residence", False, None, "weekends", 48, "2024-02-01"),
                pl("pl:mia-care", "childCare", "childCare", False, None, "weekdays", 40, "2025-09-01", "abcCare"),
                pl("pl:mia-grandma", "grandmaHome", "relativeHome", False, None, "partTime", 6, "2025-01-01")],
        "leo": [pl("pl:leo-mother", "motherHome", "residence", True, "custodyOrder", "weekdays", 120, "2021-02-02"),
                pl("pl:leo-father", "fatherHome", "residence", False, None, "weekends", 48, "2024-02-01")],
        "ana": [pl("pl:ana-home", "motherHome", "residence", True, None, "fullTime", None, "2020-06-01")],
        "luis": [pl("pl:luis-home", "fatherHome", "residence", True, None, "fullTime", None, "2024-02-01")],
        "rosa": [pl("pl:rosa-home", "grandmaHome", "residence", True, None, "fullTime", None, "1990-01-01")],
        "james": [pl("pl:james-home", "carterHome", "residence", True, None, "fullTime", None, "2015-03-01")],
        "grace": [pl("pl:grace-home", "okaforHome", "residence", True, None, "fullTime", None, "2022-09-01")],
    }
    w("-- Places go through dbo.uspSavePersonLocations, so the primary residence rules are enforced on the demo too.")
    for person, items in places.items():
        w(f"DECLARE @json_{person} NVARCHAR (MAX) = N'[{','.join(items)}]';")
        w(f"EXEC dbo.uspSavePersonLocations @personId = {g('person:' + person)}, @changesJson = @json_{person};")
    w("GO\n")

    # employment
    w(f"EXEC dbo.uspSaveEmployment @employmentId = {g('job:luis')}, @personId = {g('person:luis')}, "
      f"@organizationId = {g('org:battery')}, @worksiteLocationId = {g('loc:plant')}, @jobTitle = N'Furnace operator', "
      f"@industryText = N'Battery recycling', @occupationCode = '8140', @occupationCodeVersion = '2002', "
      f"@metalTasks = N'Breaks and smelts lead-acid batteries; cleans baghouse.', "
      f"@ppeNotes = N'Half-face respirator; no change room; launders work clothes at home.', @hoursPerWeek = 45, "
      f"@startDate = '2022-04-01', @source = 'interview', @analyteIdsJson = N'[]';")
    w(f"EXEC dbo.uspSaveEmployment @employmentId = {g('job:james')}, @personId = {g('person:james')}, "
      f"@organizationId = {g('org:battery')}, @worksiteLocationId = {g('loc:plant')}, @jobTitle = N'Maintenance mechanic', "
      f"@industryText = N'Battery recycling', @occupationCode = '7000', @occupationCodeVersion = '2002', "
      f"@metalTasks = N'Repairs smelting furnace and ventilation ducts.', @ppeNotes = N'Respirator fit-tested 2025.', "
      f"@hoursPerWeek = 40, @startDate = '2019-06-15', @source = 'interview', @analyteIdsJson = N'[]';")
    w(f"EXEC dbo.uspSaveEmployment @employmentId = {g('job:ana')}, @personId = {g('person:ana')}, "
      f"@organizationId = {g('org:abcCare')}, @worksiteLocationId = {g('loc:childCare')}, @jobTitle = N'Teacher assistant', "
      f"@industryText = N'Child care', @occupationCode = '4600', @occupationCodeVersion = '2002', @hoursPerWeek = 32, "
      f"@startDate = '2024-09-01', @source = 'interview', @analyteIdsJson = N'[]';")
    w(f"EXEC dbo.uspSavePregnancyEpisode @pregnancyEpisodeId = {g('preg:grace')}, @personId = {g('person:grace')}, "
      f"@startDate = '2026-03-01', @expectedEndDate = '2026-12-05', @source = 'provider';")
    w(f"EXEC dbo.uspSaveProduct @productId = {g('product:turmeric')}, @brand = N'Golden Valley', "
      f"@productName = N'Ground turmeric', @countryOfOrigin = 'XX', @productType = 'spice', "
      f"@notes = N'Demo product found during an interview.';")
    w(f"EXEC dbo.uspSaveExposureSource @exposureSourceId = {g('src:grace')}, @personId = {g('person:grace')}, "
      f"@sourceType = 'spiceFood', @sourceDescription = N'Ground turmeric brought from abroad, used daily', "
      f"@productId = {g('product:turmeric')}, @startDate = '2026-01-01';")
    w("GO\n")

    # results and cases (values already normalized: canonical = reported for these units)
    def plus(d: str, days: int) -> str:
        return (date.fromisoformat(d) + timedelta(days=days)).isoformat()

    def result(key, person, analyte, specimen, collected, value, unit, sample, method, status="final", op="="):
        snap = ('{"source":"demo","patientName":"' + key + '","value":"' + str(value) + '"}')
        return (f"EXEC dbo.uspSaveLabResult @labResultId = {g('result:' + key)}, @personId = {g('person:' + person)}, "
                f"@analyteSpecimenId = {h('analyteSpecimen:' + analyte + ':' + specimen)}, "
                f"@collectedDate = '{collected}', @reportedDate = '{plus(collected, 3)}', "
                f"@receivedUtc = '{plus(collected, 4)}T09:00:00', @resultStatus = '{status}', "
                f"@valueReported = {value}, @valueText = N'{op if op != '=' else ''}{value}', @operator = '{op}', "
                f"@unitReported = N'{unit}', @valueCanonical = {value}, @unitCanonical = N'{unit}', "
                f"@sampleType = {q(sample)}, @testMethod = {q(method)}, @labOrganizationId = {g('org:lab')}, "
                f"@orderingProviderId = {g('provider:nguyen')}, @orderingFacilityId = {g('org:clinic')}, "
                f"@accessionNumber = N'DEMO-{key.upper()}', @loincReported = NULL, @reportedSnapshotJson = N'{snap}', "
                f"@population = '{{pop}}';")

    w("-- Results and cases, as classification would leave them (see Merit.Domain.Classification).")
    w(result("mia-cap", "mia", "PB", "wholeBlood", "2026-08-05", 7.2, "ug/dL", "capillary", "pointOfCare").replace("{pop}", "child"))
    w(result("mia-ven", "mia", "PB", "wholeBlood", "2026-09-01", 6.1, "ug/dL", "venous", "icpMs").replace("{pop}", "child"))
    w(result("leo-old", "leo", "PB", "wholeBlood", "2024-02-10", 1.2, "ug/dL", "venous", "icpMs").replace("{pop}", "child"))
    w(result("james-1", "james", "PB", "wholeBlood", "2026-07-20", 22.0, "ug/dL", "venous", "graphiteFurnaceAa").replace("{pop}", "adult"))
    w(result("james-0", "james", "PB", "wholeBlood", "2025-07-15", 12.0, "ug/dL", "venous", "graphiteFurnaceAa").replace("{pop}", "adult"))
    w(result("grace-1", "grace", "PB", "wholeBlood", "2026-09-10", 6.0, "ug/dL", "venous", "icpMs").replace("{pop}", "pregnant"))
    w("GO\n")
    w(f"""EXEC dbo.uspOpenExposureCase @exposureCaseId = {g('case:mia')}, @personId = {g('person:mia')},
     @analyteId = {h('analyte:PB')}, @population = 'child',
     @classification = 'suspect', @qualifyingLabResultId = {g('result:mia-cap')}, @workflowTemplate = 'childResidential',
     @openedDate = '2026-08-09';
EXEC dbo.uspCreateWorkTask @workTaskId = {g('task:mia-confirm')}, @queueCode = 'confirmatoryTest',
     @title = N'Confirmatory venous test due', @detail = N'Capillary 7.2 ug/dL on 2026-08-05: confirm within 3 months.',
     @personId = {g('person:mia')}, @exposureCaseId = {g('case:mia')}, @labResultId = {g('result:mia-cap')},
     @dueDate = '2026-11-03';
EXEC dbo.uspUpdateWorkTasks @workTaskIdsJson = N'["{bare(g('task:mia-confirm'))}"]', @action = 'complete', @note = N'Venous result received.';
EXEC dbo.uspAttachResultToCase @exposureCaseId = {g('case:mia')}, @labResultId = {g('result:mia-ven')},
     @computedClassification = 'confirmed', @isHighest = 0;
EXEC dbo.uspCreateCaseFollowUpTasks @exposureCaseId = {g('case:mia')};

EXEC dbo.uspOpenExposureCase @exposureCaseId = {g('case:james')}, @personId = {g('person:james')},
     @analyteId = {h('analyte:PB')}, @population = 'adult',
     @classification = 'confirmed', @qualifyingLabResultId = {g('result:james-1')}, @workflowTemplate = 'adultOccupational',
     @openedDate = '2026-07-25';
EXEC dbo.uspCreateCaseFollowUpTasks @exposureCaseId = {g('case:james')};

EXEC dbo.uspOpenExposureCase @exposureCaseId = {g('case:grace')}, @personId = {g('person:grace')},
     @analyteId = {h('analyte:PB')}, @population = 'pregnant',
     @classification = 'confirmed', @qualifyingLabResultId = {g('result:grace-1')}, @workflowTemplate = 'pregnancy',
     @openedDate = '2026-09-14';
EXEC dbo.uspCreateCaseFollowUpTasks @exposureCaseId = {g('case:grace')};

EXEC dbo.uspCreateInvestigation @investigationId = {g('inv:motherHome')}, @investigationType = 'residential',
     @locationId = {g('loc:motherHome')}, @exposureCaseId = {g('case:mia')}, @requestedDate = '2026-09-03';
EXEC dbo.uspCreateInvestigation @investigationId = {g('inv:plant')}, @investigationType = 'workplace',
     @organizationId = {g('org:battery')}, @worksiteLocationId = {g('loc:plant')}, @exposureCaseId = {g('case:james')},
     @employmentId = {g('job:james')}, @requestedDate = '2026-07-28';
EXEC dbo.uspSaveEnvironmentalSample @environmentalSampleId = {g('sample:1')}, @investigationId = {g('inv:motherHome')},
     @sampleType = 'dustWipe', @roomOrComponent = N'Child bedroom', @surface = N'windowSill', @sampleValue = 112,
     @unit = N'ug/ft2', @sampleDate = '2026-09-20';
EXEC dbo.uspSaveEnvironmentalSample @environmentalSampleId = {g('sample:2')}, @investigationId = {g('inv:motherHome')},
     @sampleType = 'dustWipe', @roomOrComponent = N'Kitchen', @surface = N'floor', @sampleValue = 3.1, @unit = N'ug/ft2',
     @sampleDate = '2026-09-20';
EXEC dbo.uspSaveCaseActivity @caseActivityId = {g('act:1')}, @exposureCaseId = {g('case:mia')},
     @activityType = 'contactAttempt', @activityDate = '2026-08-10', @contactPersonId = {g('person:ana')},
     @outcome = 'reached', @note = N'Spoke with mother; education provided; interview scheduled.';
GO

-- Assign the cases to the demo case workers.
UPDATE dbo.exposureCase SET assignedToUserId = CAST (HASHBYTES ('MD5', N'user:cmbalt') AS UNIQUEIDENTIFIER)
 WHERE exposureCaseId = {g('case:mia')} AND assignedToUserId IS NULL;
UPDATE dbo.exposureCase SET assignedToUserId = CAST (HASHBYTES ('MD5', N'user:occinv') AS UNIQUEIDENTIFIER),
                            exposureCategory = 'occupational'
 WHERE exposureCaseId = {g('case:james')} AND assignedToUserId IS NULL;

GO

UPDATE auth.sessionScope SET revokedUtc = SYSUTCDATETIME ()
 WHERE sessionScopeId = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER) AND revokedUtc IS NULL;
GO

SET NOEXEC OFF;
GO

-- ======================================================================================== part 3: refreshed on every run
DECLARE @scope UNIQUEIDENTIFIER = NEWID ();
DECLARE @user UNIQUEIDENTIFIER = CAST (HASHBYTES ('MD5', N'user:supervisor') AS UNIQUEIDENTIFIER);
DECLARE @expires DATETIME2 (3) = DATEADD (MINUTE, 30, SYSUTCDATETIME ());
EXEC auth.uspCreateSessionScope @sessionScopeId = @scope, @appUserId = @user, @expiresUtc = @expires;
EXEC sys.sp_set_session_context @key = N'scopeKey', @value = @scope;
EXEC sys.sp_set_session_context @key = N'appUserId', @value = @user;
EXEC sys.sp_set_session_context @key = N'AppUser', @value = N'supervisor (demo loader)';
EXEC sys.sp_set_session_context @key = N'actingTenantId', @value = {h('tenant:' + ROOT_CODE)};
GO

""" + coordinates() + f"""
-- A sample intake profile for the CSV in sourceCode/samples.
-- Version 2 maps the Apt column to addressUnit (version 1 sent it to unit, which the result unit then overwrote).
DECLARE @previousProfile UNIQUEIDENTIFIER = (SELECT intakeProfileId FROM dbo.intakeProfile
                                              WHERE intakeProfileId = {g('profile:chesapeake')});
IF NOT EXISTS (SELECT 1 FROM dbo.intakeProfile WHERE intakeProfileId = {g('profile:chesapeake:2')})
EXEC dbo.uspSaveIntakeProfile @intakeProfileId = {g('profile:chesapeake:2')}, @previousProfileId = @previousProfile,
     @senderOrganizationId = {g('org:lab')},
     @profileName = N'Chesapeake Reference Laboratory CSV', @fileType = 'csv', @headerRow = 1,
     @dateFormats = N'MM/dd/yyyy,yyyy-MM-dd',
     @columnMapJson = N'{{"PatientLast":"lastName","PatientFirst":"firstName","DOB":"dob","Sex":"sex","Street":"addressLine1","Apt":"addressUnit","City":"city","State":"state","Zip":"zip5","LOINC":"loinc","Result":"value","Units":"unit","SampleType":"sampleType","Method":"testMethod","Collected":"collectedDate","Reported":"reportedDate","Accession":"accessionNumber","Status":"resultStatus","OrderingNPI":"orderingProviderNpi","Guardian":"guardianName"}}',
     @valueTranslationsJson = N'{{"sex":{{"F":"female","M":"male","U":"unknown"}},"sampleType":{{"V":"venous","C":"capillary","U":"unknown"}},"resultStatus":{{"F":"final","P":"preliminary","C":"corrected","X":"cancelled"}},"testMethod":{{"ICPMS":"icpMs","GFAA":"graphiteFurnaceAa","ASV":"anodicStrippingVoltammetry","LEADCARE":"pointOfCare"}}}}',
     @constantsJson = N'{{"labCliaNumber":"21D0712345"}}',
     @headerSignature = N'PatientLast,PatientFirst,DOB,Sex,Street,Apt,City,State,Zip,LOINC,Result,Units,SampleType,Method,Collected,Reported,Accession,Status,OrderingNPI,Guardian';
GO

UPDATE auth.sessionScope SET revokedUtc = SYSUTCDATETIME ()
 WHERE sessionScopeId = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER) AND revokedUtc IS NULL;
GO
""")
    text = "\n".join(out)
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(text, encoding="utf-8")
    print(f"Wrote {OUT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
