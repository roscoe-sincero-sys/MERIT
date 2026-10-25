-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   config.uspCreateRootTenant
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Installs a data root (a region, state, tribe or stand-alone jurisdiction) under the platform tenant or under a region: creates the tenant, its closure rows, the shipped roles with their permissions, the jurisdiction settings with their shipped defaults, the default threshold rows (the CDC child blood lead reference value active; federal reference rows inactive) and the EPA dust standards. Re-runnable: rows that exist are left alone.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission platform.administer (auth.udfHasPermission); refuses with 51403 otherwise.

Settings the specification says 'must be set at install' are created with a placeholder value of NOTSET where the application needs a number to run; an administrator replaces them on the settings screen. Returns the root tenant id.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec config.uspCreateRootTenant ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T032
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE config.uspCreateRootTenant
      @tenantCode       VARCHAR (30)
      , @tenantName       NVARCHAR (120)
      , @statePostalCode  CHAR (2) = NULL
      , @stateFips        CHAR (2) = NULL
      , @effectiveFrom    DATE = '2026-01-01'
      , @tenantType       VARCHAR (30) = 'state'
      , @parentTenantCode VARCHAR (30) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[config].[uspCreateRootTenant]')
          , @StartTimeUtc   DATETIME2 (3)  = SYSUTCDATETIME ()
          , @EndTimeUtc     DATETIME2 (3)  = NULL
          , @ExecutionId    BIGINT         = NULL
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @Comments       NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never a credential, a payload, or patient data.
    SET @KeyParameters = CONCAT (N'tenantCode=', @tenantCode);

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        IF auth.udfHasPermission (N'platform.administer') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (platform.administer).', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @root UNIQUEIDENTIFIER = CAST (HASHBYTES ('MD5', CONCAT (N'tenant:', @tenantCode)) AS UNIQUEIDENTIFIER);
        DECLARE @parent UNIQUEIDENTIFIER = COALESCE ((SELECT tenantId FROM auth.tenant WHERE tenantCode = @parentTenantCode
                                                                                        AND isDeleted = 0),
                                                     CAST (HASHBYTES ('MD5', N'tenant:PLATFORM') AS UNIQUEIDENTIFIER));

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
            INSERT auth.tenant (tenantId, parentTenantId, rootTenantId, tenantCode, tenantName, tenantType, statePostalCode)
            VALUES (@root, @parent, @root, @tenantCode, @tenantName, @tenantType, @statePostalCode);
        END;

        -- Closure: self, plus every ancestor of the parent one level further away.
        INSERT auth.tenantClosure (tenantClosureId, ancestorTenantId, descendantTenantId, depth)
        SELECT CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), x.ancestorTenantId, @root, x.depth
          FROM (SELECT @root AS ancestorTenantId, 0 AS depth
                UNION ALL
                SELECT ancestorTenantId, depth + 1 FROM auth.tenantClosure WHERE descendantTenantId = @parent AND isDeleted = 0) AS x
         WHERE NOT EXISTS (SELECT 1 FROM auth.tenantClosure AS c WHERE c.ancestorTenantId = x.ancestorTenantId
                                                                 AND c.descendantTenantId = @root AND c.isDeleted = 0);

        -- ------------------------------------------------------------------------------------------ roles
        DECLARE @roles TABLE (roleCode VARCHAR (40), roleName NVARCHAR (120), isAdministrator BIT, isDeidentified BIT,
                              permissions VARCHAR (1000));
        INSERT @roles VALUES
          ('intakeClerk', N'Intake clerk', 0, 0, 'person.view,person.edit,result.view,result.edit,result.release_hold,intake.upload,task.view,task.edit,case.view,case.open'),
          ('caseManager', N'Case manager', 0, 0, 'person.view,person.edit,result.view,result.edit,case.view,case.open,case.edit,investigation.edit,task.view,task.edit,data.export'),
          ('investigator', N'Investigator', 0, 0, 'person.view,person.edit,result.view,case.view,investigation.edit,task.view,task.edit'),
          ('occupationalInvestigator', N'Occupational investigator', 0, 0, 'person.view,person.edit,result.view,result.edit,case.view,case.open,case.edit,investigation.edit,task.view,task.edit,data.export'),
          ('supervisor', N'Supervisor', 0, 0, 'person.view,person.edit,person.view_identifiers,person.merge,result.view,result.edit,result.release_hold,case.view,case.open,case.edit,investigation.edit,task.view,task.edit,intake.upload,data.export,extract.run,extract.submit,audit.view'),
          ('epidemiologist', N'Epidemiologist (de-identified)', 0, 1, 'person.view,result.view,case.view,task.view,data.export,extract.run'),
          ('administrator', N'Administrator (users and configuration)', 1, 0, 'tenant.administer,config.edit,intake.configure,matching.configure,audit.view,person.view,result.view,case.view,task.view'),
          ('userAdministrator', N'User administrator (accounts and access only)', 1, 0, 'tenant.administer,audit.view');

        INSERT auth.role (roleId, rootTenantId, roleCode, roleName, isAdministrator, isDeidentified)
        SELECT CAST (HASHBYTES ('MD5', CONCAT (N'profile:', @tenantCode, N':', r.roleCode)) AS UNIQUEIDENTIFIER), @root, r.roleCode, r.roleName,
               r.isAdministrator, r.isDeidentified
          FROM @roles AS r
         WHERE NOT EXISTS (SELECT 1 FROM auth.role AS x WHERE x.rootTenantId = @root AND x.roleCode = r.roleCode
                                                         AND x.isDeleted = 0);

        UPDATE x SET roleName = r.roleName
          FROM auth.role AS x JOIN @roles AS r ON r.roleCode = x.roleCode
         WHERE x.rootTenantId = @root AND x.isDeleted = 0 AND x.roleName <> r.roleName;

        INSERT auth.rolePermission (rolePermissionId, roleId, permissionId)
        SELECT CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), ro.roleId, pm.permissionId
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
        SELECT CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), @root, s.settingKey, s.settingValue, @effectiveFrom, s.settingDescription
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
        SELECT CAST (HASHBYTES ('MD5', CONCAT (N'threshold:', @tenantCode, N':', t.rowKey)) AS UNIQUEIDENTIFIER), @root, @pbBlood, t.population, t.sampleType,
               t.thresholdType, t.actionCodeId, t.dueIntervalDays, t.lowerValue, t.upperValue, N'ug/dL', '>=', @effectiveFrom,
               t.authority, t.isActive
          FROM @th AS t
         WHERE @pbBlood IS NOT NULL
           AND NOT EXISTS (SELECT 1 FROM config.threshold AS x
                            WHERE x.thresholdId = CAST (HASHBYTES ('MD5', CONCAT (N'threshold:', @tenantCode, N':', t.rowKey)) AS UNIQUEIDENTIFIER));

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
        SELECT CAST (HASHBYTES ('MD5', CONCAT (N'standard:', @tenantCode, N':', s.rowKey)) AS UNIQUEIDENTIFIER), @root, s.sampleType, s.surface, s.standardValue, s.unit,
               s.authority, '2025-01-13'
          FROM @std AS s
         WHERE NOT EXISTS (SELECT 1 FROM config.environmentalStandard AS x
                            WHERE x.environmentalStandardId = CAST (HASHBYTES ('MD5', CONCAT (N'standard:', @tenantCode, N':', s.rowKey)) AS UNIQUEIDENTIFIER));

        SELECT @root AS rootTenantId;

        SET @Comments = N'Root tenant installed.';

        -- =========================================================================================
        -- ===== End of the procedure's own work. ===================================================
        -- =========================================================================================

        IF @@TRANCOUNT > 0
        BEGIN
            COMMIT TRANSACTION;
        END;

        -- Completion. Deliberately after the COMMIT.
        SET @EndTimeUtc = SYSUTCDATETIME ();

        IF @ExecutionId IS NOT NULL
        BEGIN
            UPDATE logs.ExecutionLog
               SET EndDateUtc          = @EndTimeUtc
                 , ElapsedMilliseconds = CAST (LEAST (DATEDIFF_BIG (MILLISECOND, @StartTimeUtc, @EndTimeUtc)
                                                    , CAST (2147483647 AS BIGINT)) AS INT)
                 , Successful          = 1
                 , Comments            = @Comments
                 , modifiedBy          = ORIGINAL_LOGIN ()
                 , modifiedDateUtc     = @EndTimeUtc
             WHERE ExecutionLogId = @ExecutionId;
        END;

    END TRY
    BEGIN CATCH

        SELECT @ErrorNumber = ERROR_NUMBER ()
             , @ErrorProc   = ERROR_PROCEDURE ()
             , @ErrorLine   = ERROR_LINE ()
             , @ErrorMsg    = ERROR_MESSAGE ()
                            + N' (error '  + CAST (ERROR_NUMBER () AS NVARCHAR (11))
                            + N', line '   + CAST (ERROR_LINE ()   AS NVARCHAR (11)) + N')';

        IF XACT_STATE () <> 0
        BEGIN
            ROLLBACK TRANSACTION;
        END;

        BEGIN TRY
            IF @ExecutionId IS NULL
               OR NOT EXISTS (SELECT 1
                                FROM logs.ExecutionLog
                               WHERE ExecutionLogId = @ExecutionId)
            BEGIN
                EXEC logs.uspStartExecutionLogging
                      @ProcedureName          = @ProcName
                    , @KeyParameters          = @KeyParameters
                    , @StartDateUtc           = @StartTimeUtc
                    , @ReCreatedAfterRollback = 1
                    , @ExecutionLogId         = @ExecutionId OUTPUT;
            END;
        END TRY
        BEGIN CATCH
            SET @ExecutionId = NULL;
        END CATCH;

        EXEC logs.uspRecordExecutionError
              @ProcedureName   = @ProcName
            , @KeyParameters   = @KeyParameters
            , @ExecutionLogId  = @ExecutionId
            , @ErrorMessage    = @ErrorMsg
            , @ErrorProcedure  = @ErrorProc
            , @ErrorNumber     = @ErrorNumber
            , @ErrorLine       = @ErrorLine
            , @DynamicSql      = @DynamicSql
            , @ContextMessage  = @ContextMessage;

        ;THROW;

    END CATCH;

    RETURN 0;
END;
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'config'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspCreateRootTenant'
    , @Description = N'Installs a data root (a region, state, tribe or stand-alone jurisdiction) under the platform tenant or under a region: creates the tenant, its closure rows, the shipped roles with their permissions, the jurisdiction settings with their shipped defaults, the default threshold rows (the CDC child blood lead reference value active; federal reference rows inactive) and the EPA dust standards. Re-runnable: rows that exist are left alone.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON config.uspCreateRootTenant TO meritWebRole;
END;
GO
