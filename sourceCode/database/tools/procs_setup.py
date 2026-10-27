"""Installation procedures: create a data root (region, state, tribe or stand-alone jurisdiction) and child tenants."""
from procs_common import NEWID, WEB

# Deterministic keys for shipped rows, so a re-run converges instead of adding duplicates.
def k(expr: str) -> str:
    return f"CAST (HASHBYTES ('MD5', {expr}) AS UNIQUEIDENTIFIER)"


PROCS = [
    {
        "schema": "config", "name": "uspInstallRootTenant", "kind": "write", "grants": [], "ticket": "T032",
        "execute_as_owner": True,
        "desc": "Installs a data root (a region, state, tribe or stand-alone jurisdiction) under the platform tenant or "
                "under a region: creates the tenant, its closure rows, the shipped roles with their permissions, the jurisdiction settings with their shipped defaults, the default threshold rows (the CDC "
                "child blood lead reference value active; federal reference rows inactive) and the EPA dust standards. "
                "Re-runnable: rows that exist are left alone.",
        "params": """@tenantCode       VARCHAR (30)
, @tenantName       NVARCHAR (120)
, @statePostalCode  CHAR (2) = NULL
, @stateFips        CHAR (2) = NULL
, @effectiveFrom    DATE = '2026-01-01'
, @tenantType       VARCHAR (30) = 'state'
, @parentTenantCode VARCHAR (30) = NULL
, @countiesJson     NVARCHAR (MAX) = N'[]'
, @timeZone         VARCHAR (64) = NULL""",
        "key": "CONCAT (N'tenantCode=', @tenantCode)",
        "notes": "Core installer: runs as its owner and is granted to no role, because it seeds the new root's shared "
                 "rows, which row-level security would hide from a platform administrator. Reached only through "
                 "config.uspCreateRootTenant, which checks who may create the tenant. Settings the specification says "
                 "'must be set at install' are created with a placeholder value of NOTSET where the application needs "
                 "a number to run; an administrator replaces them on the settings screen. Returns the root tenant id.",
        "body": f"""
DECLARE @root UNIQUEIDENTIFIER = {k("CONCAT (N'tenant:', @tenantCode)")};
DECLARE @parent UNIQUEIDENTIFIER = COALESCE ((SELECT tenantId FROM auth.tenant WHERE tenantCode = @parentTenantCode
                                                                                AND isDeleted = 0),
                                             {k("N'tenant:PLATFORM'")});

IF @tenantType NOT IN ('region', 'state', 'tribe', 'jurisdiction')
BEGIN
    ;THROW 51422, N'A data root is a region, state, tribe or stand-alone jurisdiction.', 1;
END;

IF NOT EXISTS (SELECT 1 FROM auth.tenant WHERE tenantId = @parent AND tenantType IN ('platform', 'region'))
BEGIN
    ;THROW 51422, N'A data root goes directly under the platform or under a region.', 1;
END;

IF NOT EXISTS (SELECT 1 FROM auth.tenant WHERE tenantId = @root)
BEGIN
    INSERT auth.tenant (tenantId, parentTenantId, rootTenantId, tenantCode, tenantName, tenantType, statePostalCode, timeZone)
    VALUES (@root, @parent, @root, @tenantCode, @tenantName, @tenantType, @statePostalCode, @timeZone);
END
ELSE IF @timeZone IS NOT NULL
BEGIN
    -- A re-run fills in a zone the root does not have yet (issue #36); one already chosen is kept.
    UPDATE auth.tenant SET timeZone = @timeZone WHERE tenantId = @root AND timeZone IS NULL;
END;

-- Closure: self, plus every ancestor of the parent one level further away.
INSERT auth.tenantClosure (tenantClosureId, ancestorTenantId, descendantTenantId, depth)
SELECT {NEWID}, x.ancestorTenantId, @root, x.depth
  FROM (SELECT @root AS ancestorTenantId, 0 AS depth
        UNION ALL
        SELECT ancestorTenantId, depth + 1 FROM auth.tenantClosure WHERE descendantTenantId = @parent AND isDeleted = 0) AS x
 WHERE NOT EXISTS (SELECT 1 FROM auth.tenantClosure AS c WHERE c.ancestorTenantId = x.ancestorTenantId
                                                         AND c.descendantTenantId = @root AND c.isDeleted = 0);

-- ------------------------------------------------------------------------------------------ roles
DECLARE @roles TABLE (roleCode VARCHAR (40), roleName NVARCHAR (120), isAdministrator BIT, isDeidentified BIT,
                      permissions VARCHAR (1000));
INSERT @roles VALUES
  ('intakeClerk', N'Intake clerk', 0, 0, 'person.view,person.edit,result.view,result.edit,result.release_hold,intake.upload,registry.edit,task.view,task.edit,case.view,case.open'),
  ('caseManager', N'Case manager', 0, 0, 'person.view,person.edit,result.view,result.edit,case.view,case.open,case.edit,investigation.edit,task.view,task.edit,data.export'),
  ('investigator', N'Investigator', 0, 0, 'person.view,person.edit,result.view,case.view,investigation.edit,task.view,task.edit'),
  ('occupationalInvestigator', N'Occupational investigator', 0, 0, 'person.view,person.edit,result.view,result.edit,case.view,case.open,case.edit,investigation.edit,task.view,task.edit,data.export'),
  ('supervisor', N'Supervisor', 0, 0, 'person.view,person.edit,person.view_identifiers,person.merge,result.view,result.edit,result.release_hold,case.view,case.open,case.edit,investigation.edit,task.view,task.edit,intake.upload,registry.edit,data.export,extract.run,extract.submit,audit.view'),
  ('epidemiologist', N'Epidemiologist (de-identified)', 0, 1, 'person.view,result.view,case.view,task.view,data.export,extract.run'),
  ('administrator', N'Administrator (users and configuration)', 1, 0, 'tenant.administer,tenant.create,config.edit,registry.edit,intake.configure,matching.configure,audit.view,person.view,result.view,case.view,task.view'),
  ('userAdministrator', N'User administrator (accounts and access only)', 1, 0, 'tenant.administer,audit.view');

INSERT auth.role (roleId, rootTenantId, roleCode, roleName, isAdministrator, isDeidentified)
SELECT {k("CONCAT (N'profile:', @tenantCode, N':', r.roleCode)")}, @root, r.roleCode, r.roleName,
       r.isAdministrator, r.isDeidentified
  FROM @roles AS r
 WHERE NOT EXISTS (SELECT 1 FROM auth.role AS x WHERE x.rootTenantId = @root AND x.roleCode = r.roleCode
                                                 AND x.isDeleted = 0);

UPDATE x SET roleName = r.roleName
  FROM auth.role AS x JOIN @roles AS r ON r.roleCode = x.roleCode
 WHERE x.rootTenantId = @root AND x.isDeleted = 0 AND x.roleName <> r.roleName;

INSERT auth.rolePermission (rolePermissionId, roleId, permissionId)
SELECT {NEWID}, ro.roleId, pm.permissionId
  FROM @roles AS r
 CROSS APPLY STRING_SPLIT (r.permissions, ',') AS s
  JOIN auth.role AS ro ON ro.rootTenantId = @root AND ro.roleCode = r.roleCode AND ro.isDeleted = 0
  JOIN auth.permission AS pm ON pm.permissionCode = s.[value] AND pm.isDeleted = 0
 WHERE NOT EXISTS (SELECT 1 FROM auth.rolePermission AS x WHERE x.roleId = ro.roleId
                                                           AND x.permissionId = pm.permissionId AND x.isDeleted = 0);

-- ------------------------------------------------------------------------------------------ settings
DECLARE @settings TABLE (settingKey VARCHAR (80), settingValue NVARCHAR (2000), settingDescription NVARCHAR (500));
INSERT @settings VALUES
  ('statePostalCode', COALESCE (@statePostalCode, N''), N'Jurisdiction identity: two-letter postal code (ABLES StateRep).'),
  ('stateFips', COALESCE (@stateFips, N''), N'Jurisdiction identity: state FIPS code.'),
  ('agencyName', @tenantName, N'Agency name used on letters.'),
  ('adultAgeCutoffYears', N'16', N'Age at which population becomes adult. 16 matches ABLES and the national case definition; some state laws use 18.'),
  ('pregnancyMaxWeeks', N'42', N'How long a pregnancy episode with no end date stays open, in weeks from its start.'),
  ('primaryResidencePolicy', N'review', N'review: a new lab address creates an address review task; move: it becomes the primary.'),
  ('investigationLookbackDays', N'NOTSET', N'How far back before the qualifying collection date past addresses and jobs count. Must be set at install.'),
  ('siteInvestigationMinHours', N'NOTSET', N'Weekly hours at a relative or sitter home or other site that trigger an investigation request. Must be set at install.'),
  ('contactAttemptsBeforeLetter', N'NOTSET', N'Failed contact attempts before a letter is queued. Must be set at install.'),
  ('localNotification', N'false', N'Whether opening a case notifies the local health department.'),
  ('arsenicHandling', N'task', N'task: a high total urine arsenic creates a speciation recommended task; case: it opens a case.'),
  ('occupationalReferralAgency', N'Federal OSHA', N'Federal OSHA area office or the state-plan agency.'),
  ('confirmingMethods', N'graphiteFurnaceAa,icpMs', N'Test methods whose venous results confirm a case.'),
  ('unreportedMethodConfirms', N'true', N'Whether a venous result with no reported method confirms.'),
  ('matchingPreset', N'strict', N'strict, balanced or lenient. Strict links only when name, DOB and sex agree exactly.'),
  ('cblsEnabled', N'true', N'Produce the quarterly CDC CBLS extract.'),
  ('ablesEnabled', N'true', N'Produce the annual NIOSH ABLES extract.'),
  ('providerLookups', N'live', N'live NPPES and CMS lookups, or file loads.'),
  ('emailCodesEnabled', N'false', N'Email one-time codes. NIST SP 800-63B-4 does not allow email for out-of-band authentication.'),
  ('smsCodesEnabled', N'false', N'Text-message codes. NIST SP 800-63B-4 treats SMS as a restricted authenticator.'),
  ('adultCaseOwnerTenantCode', N'', N'Tenant code that owns adult cases (case ownership rule); empty = the root tenant.'),
  ('clusterWindowDays', N'NOTSET', N'Time window for cluster detection rules. Must be set at install.'),
  ('clusterMinCount', N'NOTSET', N'Minimum number of cases for a cluster. Must be set at install.'),
  ('passwordMinLength', N'8', N'Minimum password length; NIST permits 8 because every account has a second factor.'),
  ('sessionTimeoutMinutes', N'20', N'Sessions time out after this many minutes of inactivity.');

INSERT config.jurisdictionSetting (jurisdictionSettingId, rootTenantId, settingKey, settingValue, effectiveFrom,
                                   settingDescription)
SELECT {NEWID}, @root, s.settingKey, s.settingValue, @effectiveFrom, s.settingDescription
  FROM @settings AS s
 WHERE NOT EXISTS (SELECT 1 FROM config.jurisdictionSetting AS x WHERE x.rootTenantId = @root
                                                                AND x.settingKey = s.settingKey AND x.isDeleted = 0);

-- ------------------------------------------------------------------------------------------ thresholds
DECLARE @pbBlood UNIQUEIDENTIFIER = (SELECT s.analyteSpecimenId FROM config.analyteSpecimen AS s
                                       JOIN config.analyte AS a ON a.analyteId = s.analyteId
                                      WHERE a.code = 'PB' AND s.specimen = 'wholeBlood');
DECLARE @confirm UNIQUEIDENTIFIER = (SELECT actionCodeId FROM config.actionCode WHERE code = 'confirmatoryVenousDue');

DECLARE @th TABLE (rowKey VARCHAR (60), population VARCHAR (10), sampleType VARCHAR (20), thresholdType VARCHAR (12),
                   actionCodeId UNIQUEIDENTIFIER, dueIntervalDays INT, lowerValue DECIMAL (18, 6),
                   upperValue DECIMAL (18, 6), authority NVARCHAR (300), isActive BIT);
INSERT @th VALUES
  ('pb-child-reportable', 'child', NULL, 'reportable', NULL, NULL, 0, NULL, N'Blood lead reportable at any value (shipped default).', 1),
  ('pb-child-case', 'child', NULL, 'case', NULL, NULL, 3.5, NULL, N'CDC blood lead reference value for children, 3.5 ug/dL (2021).', 1),
  ('pb-child-confirm-1', 'child', 'capillary', 'action', @confirm, 90, 3.5, 10, N'CDC: confirmatory venous test within 3 months at 3.5-9 ug/dL.', 1),
  ('pb-child-confirm-2', 'child', 'capillary', 'action', @confirm, 30, 10, 20, N'CDC: confirmatory venous test within 1 month at 10-19 ug/dL.', 1),
  ('pb-child-confirm-3', 'child', 'capillary', 'action', @confirm, 14, 20, 45, N'CDC: confirmatory venous test within 2 weeks at 20-44 ug/dL.', 1),
  ('pb-child-confirm-4', 'child', 'capillary', 'action', @confirm, 2, 45, NULL, N'CDC: confirmatory venous test within 48 hours at 45 ug/dL or more.', 1),
  ('pb-all-case-csteref', 'all', 'venous', 'case', NULL, NULL, 3.5, NULL, N'CSTE 22-EH-01 Lead in Blood 2023 national case definition (reference row, inactive).', 0),
  ('pb-adult-case-niosh', 'adult', NULL, 'case', NULL, NULL, 5, NULL, N'NIOSH adult elevated blood lead level, 5 ug/dL (reference row, inactive).', 0);

INSERT config.threshold (thresholdId, rootTenantId, analyteSpecimenId, population, sampleType, thresholdType, actionCodeId,
                         dueIntervalDays, lowerValue, upperValue, unit, comparator, effectiveFrom, authority, isActive)
SELECT {k("CONCAT (N'threshold:', @tenantCode, N':', t.rowKey)")}, @root, @pbBlood, t.population, t.sampleType,
       t.thresholdType, t.actionCodeId, t.dueIntervalDays, t.lowerValue, t.upperValue, N'ug/dL', '>=', @effectiveFrom,
       t.authority, t.isActive
  FROM @th AS t
 WHERE @pbBlood IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM config.threshold AS x
                    WHERE x.thresholdId = {k("CONCAT (N'threshold:', @tenantCode, N':', t.rowKey)")});

-- ------------------------------------------------------------------------------------------ environmental standards
DECLARE @std TABLE (rowKey VARCHAR (40), sampleType VARCHAR (30), surface NVARCHAR (60), standardValue DECIMAL (18, 6),
                    unit NVARCHAR (20), authority NVARCHAR (300));
INSERT @std VALUES
  ('dust-floor', 'dustWipe', N'floor', 5, N'ug/ft2', N'EPA 2024 dust-lead action level after abatement: floors 5 ug/ft2.'),
  ('dust-sill', 'dustWipe', N'windowSill', 40, N'ug/ft2', N'EPA 2024 dust-lead action level after abatement: window sills 40 ug/ft2.'),
  ('dust-trough', 'dustWipe', N'windowTrough', 100, N'ug/ft2', N'EPA 2024 dust-lead action level after abatement: window troughs 100 ug/ft2.'),
  ('xrf-paint', 'xrfPaint', NULL, 1.0, N'mg/cm2', N'HUD/EPA lead-based paint definition: 1.0 mg/cm2 by XRF.');

INSERT config.environmentalStandard (environmentalStandardId, rootTenantId, sampleType, surface, standardValue, unit,
                                     authority, effectiveFrom)
SELECT {k("CONCAT (N'standard:', @tenantCode, N':', s.rowKey)")}, @root, s.sampleType, s.surface, s.standardValue, s.unit,
       s.authority, '2025-01-13'
  FROM @std AS s
 WHERE NOT EXISTS (SELECT 1 FROM config.environmentalStandard AS x
                    WHERE x.environmentalStandardId = {k("CONCAT (N'standard:', @tenantCode, N':', s.rowKey)")});

-- ------------------------------------------------------------------------------------------ counties
INSERT config.subJurisdiction (subJurisdictionId, rootTenantId, countyFips, subJurisdictionName, jurisdictionType)
SELECT {k("CONCAT (N'county:', @tenantCode, N':', j.fips)")}, @root, j.fips, j.countyName, COALESCE (j.countyType, 'county')
  FROM OPENJSON (COALESCE (@countiesJson, N'[]'))
  WITH (fips CHAR (3) '$.fips', countyName NVARCHAR (80) '$.name', countyType VARCHAR (20) '$.type') AS j
 WHERE j.fips LIKE '[0-9][0-9][0-9]' AND LEN (LTRIM (j.countyName)) > 0
   AND NOT EXISTS (SELECT 1 FROM config.subJurisdiction AS x WHERE x.rootTenantId = @root AND x.countyFips = j.fips
                                                              AND x.isDeleted = 0);

SELECT @root AS rootTenantId;

SET @Comments = N'Root tenant installed.';
""",
    },
    {
        "schema": "config", "name": "uspInstallChildTenant", "kind": "write", "grants": [], "ticket": "T032",
        "execute_as_owner": True,
        "desc": "Core installer for a program or local jurisdiction: the tenant, its closure rows, any counties new to "
                "its data root, and the counties it serves. Below the data root the tenant serves only counties its "
                "parent serves, and new counties are refused. Runs as its owner, granted to no role; reached only "
                "through config.uspCreateChildTenant, which checks who may create it. Re-runnable.",
        "params": """@tenantCode       VARCHAR (30)
, @tenantName       NVARCHAR (120)
, @tenantType       VARCHAR (30)
, @parentTenantCode VARCHAR (30)
, @countyFipsList   VARCHAR (400) = NULL
, @countiesJson     NVARCHAR (MAX) = N'[]'""",
        "key": "CONCAT (N'tenantCode=', @tenantCode, N', parentTenantCode=', @parentTenantCode)",
        "body": f"""
DECLARE @parent UNIQUEIDENTIFIER, @root UNIQUEIDENTIFIER, @state CHAR (2);
SELECT @parent = tenantId, @root = rootTenantId, @state = statePostalCode
  FROM auth.tenant WHERE tenantCode = @parentTenantCode AND isDeleted = 0;

IF @parent IS NULL
BEGIN
    ;THROW 51404, N'The parent tenant was not found.', 1;
END;

IF @tenantType NOT IN ('program', 'jurisdiction')
BEGIN
    ;THROW 51422, N'A child tenant is a program or a jurisdiction; states, tribes and regions are created as data roots.', 1;
END;

IF EXISTS (SELECT 1 FROM auth.tenant WHERE tenantId = @parent AND tenantType = 'platform')
BEGIN
    ;THROW 51422, N'The platform holds data roots only.', 1;
END;

-- Counties give residence access to the people who live there (dbo.uspRefreshPersonAccess). Below the data root a
-- tenant serves only counties its parent serves, and counties new to the data root come only through the root itself:
-- otherwise an administrator of one county could map a child to another county and see the people who live there.
-- This is a rule of the tree, so it holds for every caller, platform administrators and deployment scripts included.
IF @parent <> @root
BEGIN
    IF EXISTS (SELECT 1 FROM OPENJSON (COALESCE (@countiesJson, N'[]')))
    BEGIN
        ;THROW 51403, N'New counties are added under the state, tribe or other data root, not under one of its tenants.', 1;
    END;

    IF EXISTS (SELECT 1
                 FROM STRING_SPLIT (COALESCE (@countyFipsList, ''), ',') AS s
                WHERE LTRIM (RTRIM (s.[value])) <> ''
                  AND NOT EXISTS (SELECT 1
                                    FROM auth.tenantJurisdiction AS tj
                                    JOIN config.subJurisdiction AS sj ON sj.subJurisdictionId = tj.subJurisdictionId
                                                                     AND sj.isDeleted = 0
                                   WHERE tj.tenantId = @parent AND tj.isDeleted = 0
                                     AND sj.countyFips = LTRIM (RTRIM (s.[value]))))
    BEGIN
        ;THROW 51403, N'A tenant can serve only counties its parent serves.', 1;
    END;
END;

DECLARE @id UNIQUEIDENTIFIER = {k("CONCAT (N'tenant:', @tenantCode)")};

IF NOT EXISTS (SELECT 1 FROM auth.tenant WHERE tenantId = @id)
BEGIN
    INSERT auth.tenant (tenantId, parentTenantId, rootTenantId, tenantCode, tenantName, tenantType, statePostalCode)
    VALUES (@id, @parent, @root, @tenantCode, @tenantName, @tenantType, @state);
END;

-- Closure: self, plus every ancestor of the parent one level further away.
INSERT auth.tenantClosure (tenantClosureId, ancestorTenantId, descendantTenantId, depth)
SELECT {NEWID}, x.ancestorTenantId, @id, x.depth
  FROM (SELECT @id AS ancestorTenantId, 0 AS depth
        UNION ALL
        SELECT ancestorTenantId, depth + 1 FROM auth.tenantClosure WHERE descendantTenantId = @parent AND isDeleted = 0) AS x
 WHERE NOT EXISTS (SELECT 1 FROM auth.tenantClosure AS c WHERE c.ancestorTenantId = x.ancestorTenantId
                                                         AND c.descendantTenantId = @id AND c.isDeleted = 0);

-- Counties new to the data root are added first, then the tenant is mapped to every county it serves.
INSERT config.subJurisdiction (subJurisdictionId, rootTenantId, countyFips, subJurisdictionName, jurisdictionType)
SELECT {k("CONCAT (N'county:', (SELECT tenantCode FROM auth.tenant WHERE tenantId = @root), N':', j.fips)")}, @root, j.fips,
       j.countyName, COALESCE (j.countyType, 'county')
  FROM OPENJSON (COALESCE (@countiesJson, N'[]'))
  WITH (fips CHAR (3) '$.fips', countyName NVARCHAR (80) '$.name', countyType VARCHAR (20) '$.type') AS j
 WHERE j.fips LIKE '[0-9][0-9][0-9]' AND LEN (LTRIM (j.countyName)) > 0
   AND NOT EXISTS (SELECT 1 FROM config.subJurisdiction AS x WHERE x.rootTenantId = @root AND x.countyFips = j.fips
                                                              AND x.isDeleted = 0);

INSERT auth.tenantJurisdiction (tenantJurisdictionId, tenantId, subJurisdictionId)
SELECT {NEWID}, @id, sj.subJurisdictionId
  FROM (SELECT LTRIM (RTRIM ([value])) AS fips FROM STRING_SPLIT (COALESCE (@countyFipsList, ''), ',')
        UNION
        SELECT fips FROM OPENJSON (COALESCE (@countiesJson, N'[]')) WITH (fips CHAR (3) '$.fips')) AS s
  JOIN config.subJurisdiction AS sj ON sj.rootTenantId = @root AND sj.countyFips = s.fips
 WHERE NOT EXISTS (SELECT 1 FROM auth.tenantJurisdiction AS tj WHERE tj.tenantId = @id
                                                              AND tj.subJurisdictionId = sj.subJurisdictionId
                                                              AND tj.isDeleted = 0);

SELECT @id AS tenantId;

SET @Comments = N'Child tenant installed.';
""",
    },
]

CODE_RULE = """IF @tenantCode IS NULL OR @tenantCode NOT LIKE '[A-Z0-9]%' OR @tenantCode LIKE '%[^A-Z0-9-]%' COLLATE Latin1_General_BIN
   OR LEN (@tenantCode) > 30
BEGIN
    ;THROW 51422, N'The tenant code uses capital letters, digits and hyphens, up to 30 characters (for example WV-JEFFERSON).', 1;
END;
IF LEN (LTRIM (RTRIM (COALESCE (@tenantName, N'')))) = 0
BEGIN
    ;THROW 51422, N'Give the tenant a name.', 1;
END;
IF @countiesJson IS NOT NULL AND ISJSON (@countiesJson) = 0
BEGIN
    ;THROW 50000, N'@countiesJson must be a JSON array of {fips, name, type}.', 1;
END;"""

PROCS += [
    {
        "schema": "config", "name": "uspCreateRootTenant", "kind": "write", "grants": WEB, "ticket": "T162",
        "desc": "Creates a data root: a region, state, tribe or stand-alone jurisdiction, with its roles, settings, "
                "default levels and counties. A platform administrator can create one anywhere; an administrator holding "
                "tenant.create at a region can add a state or tribe under that region. Re-runnable for the same parent.",
        "params": """@tenantCode       VARCHAR (30)
, @tenantName       NVARCHAR (120)
, @statePostalCode  CHAR (2) = NULL
, @stateFips        CHAR (2) = NULL
, @effectiveFrom    DATE = '2026-01-01'
, @tenantType       VARCHAR (30) = 'state'
, @parentTenantCode VARCHAR (30) = NULL
, @countiesJson     NVARCHAR (MAX) = N'[]'
, @timeZone         VARCHAR (64) = NULL""",
        "key": "CONCAT (N'tenantCode=', @tenantCode, N', parentTenantCode=', @parentTenantCode, N', tenantType=', @tenantType)",
        "notes": "Returns the root tenant id. The inserts run in config.uspInstallRootTenant.",
        "validate": CODE_RULE + """
DECLARE @parentId UNIQUEIDENTIFIER = COALESCE ((SELECT tenantId FROM auth.tenant WHERE tenantCode = @parentTenantCode AND isDeleted = 0),
                                               """ + k("N'tenant:PLATFORM'") + """);
DECLARE @parentType VARCHAR (30) = (SELECT tenantType FROM auth.tenant WHERE tenantId = @parentId);

IF auth.udfIsPlatformAdmin () = 0
   AND NOT (auth.udfHasPermission ('tenant.create') = 1 AND @parentType = 'region' AND @tenantType IN ('state', 'tribe')
            AND EXISTS (SELECT 1 FROM auth.tvfAdminTenants () WHERE tenantId = @parentId))
BEGIN
    ;THROW 51403, N'Only a platform administrator can create a region or a top-level tenant; a region administrator can add states and tribes to the region.', 1;
END;

IF EXISTS (SELECT 1 FROM auth.tenant WHERE tenantCode = @tenantCode AND isDeleted = 0
                                       AND COALESCE (parentTenantId, '00000000-0000-0000-0000-000000000000') <> @parentId)
BEGIN
    ;THROW 51422, N'That tenant code is already used elsewhere in the tree.', 1;
END;

IF @tenantType IN ('state', 'tribe') AND (@statePostalCode IS NULL OR @statePostalCode NOT LIKE '[A-Z][A-Z]')
BEGIN
    ;THROW 51422, N'A state or tribe needs its two-letter postal code.', 1;
END;
IF @timeZone IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.time_zone_info WHERE name = @timeZone)
BEGIN
    ;THROW 51422, N'Choose the time zone from the list.', 1;
END;""",
        "body": """
EXEC config.uspInstallRootTenant @tenantCode = @tenantCode, @tenantName = @tenantName, @statePostalCode = @statePostalCode,
     @stateFips = @stateFips, @effectiveFrom = @effectiveFrom, @tenantType = @tenantType,
     @parentTenantCode = @parentTenantCode, @countiesJson = @countiesJson, @timeZone = @timeZone;

SET @Comments = N'Root tenant created.';
""",
    },
    {
        "schema": "config", "name": "uspCreateChildTenant", "kind": "write", "grants": WEB, "ticket": "T162",
        "permission": "tenant.create",
        "desc": "Adds a program or a local jurisdiction under a tenant the caller administers (any tenant for a platform "
                "administrator), with the counties it serves. Needs tenant.create. Below the data root the new tenant serves "
                "only counties its parent serves, and counties new to the root are added only under the root "
                "(config.uspInstallChildTenant enforces both). Re-runnable for the same parent.",
        "params": """@tenantCode       VARCHAR (30)
, @tenantName       NVARCHAR (120)
, @tenantType       VARCHAR (30)
, @parentTenantCode VARCHAR (30)
, @countyFipsList   VARCHAR (400) = NULL
, @countiesJson     NVARCHAR (MAX) = N'[]'""",
        "key": "CONCAT (N'tenantCode=', @tenantCode, N', parentTenantCode=', @parentTenantCode, N', tenantType=', @tenantType)",
        "notes": "Returns the new tenant id. The inserts run in config.uspInstallChildTenant.",
        "validate": CODE_RULE + """
DECLARE @parentId UNIQUEIDENTIFIER = (SELECT tenantId FROM auth.tenant WHERE tenantCode = @parentTenantCode AND isDeleted = 0);

IF @parentId IS NULL
BEGIN
    ;THROW 51404, N'The parent tenant was not found.', 1;
END;

IF NOT EXISTS (SELECT 1 FROM auth.tvfAdminTenants () WHERE tenantId = @parentId)
BEGIN
    ;THROW 51403, N'You can add tenants only under tenants you administer.', 1;
END;

IF @tenantType NOT IN ('program', 'jurisdiction')
BEGIN
    ;THROW 51422, N'A tenant added under another is a program or a jurisdiction; states, tribes and regions are data roots.', 1;
END;

IF EXISTS (SELECT 1 FROM auth.tenant WHERE tenantId = @parentId AND tenantType = 'platform')
BEGIN
    ;THROW 51422, N'The platform holds data roots only: create a region, state, tribe or stand-alone jurisdiction.', 1;
END;

IF EXISTS (SELECT 1 FROM auth.tenant WHERE tenantCode = @tenantCode AND isDeleted = 0 AND parentTenantId <> @parentId)
BEGIN
    ;THROW 51422, N'That tenant code is already used elsewhere in the tree.', 1;
END;""",
        "body": """
EXEC config.uspInstallChildTenant @tenantCode = @tenantCode, @tenantName = @tenantName, @tenantType = @tenantType,
     @parentTenantCode = @parentTenantCode, @countyFipsList = @countyFipsList, @countiesJson = @countiesJson;

SET @Comments = N'Child tenant created.';
""",
    },
]
