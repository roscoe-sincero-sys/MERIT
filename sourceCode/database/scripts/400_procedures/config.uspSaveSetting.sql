-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   config.uspSaveSetting
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Changes a jurisdiction setting from a date: the row in force is ended the day before and a new row starts, so every change is effective-dated and kept. The key and value must satisfy config.tvfSettingCatalogue; versions dated after the effective date are replaced only when the caller confirms it, and the audit event names them.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission config.edit (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec config.uspSaveSetting ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T050
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE config.uspSaveSetting
      @settingKey    VARCHAR (80)
      , @settingValue  NVARCHAR (2000)
      , @effectiveFrom DATE
      , @replaceLaterVersions BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[config].[uspSaveSetting]')
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
    SET @KeyParameters = CONCAT (N'settingKey=', @settingKey, N', effectiveFrom=', @effectiveFrom);

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        IF auth.udfHasPermission (N'config.edit') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (config.edit).', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();
        IF @settingKey='testingPolicyVersion' AND USER_NAME()<>N'dbo'
           AND NOT EXISTS(
             SELECT 1 FROM auth.sessionScope ss CROSS APPLY auth.tvfUserGrantPermissions(ss.appUserId) m
             WHERE ss.sessionScopeId=TRY_CAST(SESSION_CONTEXT(N'scopeKey') AS UNIQUEIDENTIFIER)
               AND ss.isDeleted=0 AND ss.revokedUtc IS NULL AND ss.expiresUtc>SYSUTCDATETIME()
               AND m.tenantId=@root AND m.permissionCode='config.edit' AND m.grantRank>=2
               AND ((ss.viewAsUserId IS NOT NULL
                     AND EXISTS(SELECT 1 FROM auth.tvfUserGrantPermissions(ss.viewAsUserId) v WHERE v.tenantId=@root AND v.permissionCode='config.edit' AND v.grantRank>=2)
                     AND EXISTS(SELECT 1 FROM auth.tvfUserGrantPermissions(ss.appUserId) a WHERE a.tenantId=@root AND a.permissionCode='tenant.administer'))
                 OR (ss.viewAsUserId IS NULL AND ss.userProfileId IS NOT NULL
                     AND EXISTS(SELECT 1 FROM auth.tvfProfileGrantPermissions(ss.userProfileId) p WHERE p.tenantId=@root AND p.permissionCode='config.edit' AND p.grantRank>=2))
                 OR (ss.viewAsUserId IS NULL AND ss.userProfileId IS NULL)))
        BEGIN
            ;THROW 51403,N'Only a configuration administrator at the data root can adopt or disable its testing policy.',1;
        END;
        -- Issue 289: only catalogued settings that are in use, with a value inside their definition.
        DECLARE @problem NVARCHAR (400) = config.udfSettingProblem (@settingKey, @settingValue, @root);
        IF @problem IS NOT NULL AND NOT (@settingKey = 'testingPolicyVersion' AND @problem LIKE N'Choose one of%')
        BEGIN
            ;THROW 51422, @problem, 1;
        END;
        IF @effectiveFrom IS NULL OR @effectiveFrom < '1900-01-01' OR @effectiveFrom > DATEADD (YEAR, 10, CAST (SYSUTCDATETIME () AS DATE))
        BEGIN
            ;THROW 51422, N'Choose an effective date from 1900 to ten years from now.', 1;
        END;
        IF @settingKey='testingPolicyVersion' AND (@settingValue NOT IN (N'none',N'MDH-2025-03')
         OR (@settingValue=N'MDH-2025-03' AND NOT EXISTS(SELECT 1 FROM auth.tenant WHERE tenantId=@root AND statePostalCode='MD')))
        BEGIN
         ;THROW 51422,N'Choose no testing policy, or the supported MDH March 2025 policy for a Maryland data root.',1;
        END;

        DECLARE @settingId UNIQUEIDENTIFIER=CAST(CAST(NEWID() AS BINARY(10))+CAST(DATEDIFF_BIG(MILLISECOND,'19700101',SYSUTCDATETIME()) AS BINARY(6)) AS UNIQUEIDENTIFIER),
         @oldId UNIQUEIDENTIFIER,@oldValue NVARCHAR(2000);
        SELECT TOP(1) @oldId=jurisdictionSettingId,@oldValue=settingValue
         FROM config.jurisdictionSetting WITH(UPDLOCK,HOLDLOCK)
         WHERE rootTenantId=@root AND settingKey=@settingKey AND isDeleted=0 AND effectiveFrom<=@effectiveFrom
           AND (effectiveTo IS NULL OR effectiveTo>=@effectiveFrom)
         ORDER BY effectiveFrom DESC,jurisdictionSettingId;

        -- Issue 290: versions dated after the effective date are replaced by this save. That needs the caller's explicit
        -- confirmation, and the audit event below names every version removed.
        DECLARE @laterCount INT, @laterFirst DATE, @laterLast DATE, @removedJson NVARCHAR (MAX);
        SELECT @laterCount = COUNT (*), @laterFirst = MIN (effectiveFrom), @laterLast = MAX (effectiveFrom)
          FROM config.jurisdictionSetting WITH (UPDLOCK, HOLDLOCK)
         WHERE rootTenantId = @root AND settingKey = @settingKey AND isDeleted = 0 AND effectiveFrom > @effectiveFrom;
        IF @laterCount > 0 AND COALESCE (@replaceLaterVersions, 0) = 0
        BEGIN
            DECLARE @laterMessage NVARCHAR (400) = CONCAT (N'This setting has ', @laterCount, CASE WHEN @laterCount = 1 THEN N' version' ELSE N' versions' END,
                N' dated after ', CONVERT (NVARCHAR (10), @effectiveFrom, 23), N' (effective ', CONVERT (NVARCHAR (10), @laterFirst, 23),
                CASE WHEN @laterCount > 1 THEN CONCAT (N' to ', CONVERT (NVARCHAR (10), @laterLast, 23)) ELSE N'' END,
                N'). Saving from this date would remove ', CASE WHEN @laterCount = 1 THEN N'it' ELSE N'them' END,
                N'. Tick "Replace later-dated versions" to confirm, or choose a later effective date.');
            ;THROW 51422, @laterMessage, 1;
        END;
        SET @removedJson = (SELECT s.jurisdictionSettingId AS settingId, s.effectiveFrom, s.effectiveTo
                                 , config.udfAuditSettingValue (@settingKey, s.settingValue) AS [value]
                                 , CAST (CASE WHEN config.udfAuditSettingValue (@settingKey, s.settingValue) IS NULL THEN 1 ELSE 0 END AS BIT) AS valueRedacted
                              FROM config.jurisdictionSetting AS s
                             WHERE s.rootTenantId = @root AND s.settingKey = @settingKey AND s.isDeleted = 0 AND s.effectiveFrom > @effectiveFrom
                             ORDER BY s.effectiveFrom
                               FOR JSON PATH, INCLUDE_NULL_VALUES);

        DECLARE @desc NVARCHAR (500);

        SELECT TOP (1) @desc = settingDescription
          FROM config.jurisdictionSetting WITH (UPDLOCK, HOLDLOCK)
         WHERE rootTenantId = @root AND settingKey = @settingKey AND isDeleted = 0
         ORDER BY effectiveFrom DESC;

        -- A same-day change replaces the value; a later change ends the current row.
        UPDATE config.jurisdictionSetting
           SET isDeleted = 1
         WHERE rootTenantId = @root AND settingKey = @settingKey AND isDeleted = 0 AND effectiveFrom >= @effectiveFrom;

        UPDATE config.jurisdictionSetting
           SET effectiveTo = DATEADD (DAY, -1, @effectiveFrom)
         WHERE rootTenantId = @root AND settingKey = @settingKey AND isDeleted = 0 AND effectiveFrom < @effectiveFrom
           AND (effectiveTo IS NULL OR effectiveTo >= @effectiveFrom);

        INSERT config.jurisdictionSetting (jurisdictionSettingId, rootTenantId, settingKey, settingValue, effectiveFrom,
                                           settingDescription)
        VALUES (@settingId, @root, @settingKey, @settingValue, @effectiveFrom, @desc);

        DECLARE @safeKey VARCHAR(80)=config.udfAuditSettingKey(@settingKey),
         @safeOld NVARCHAR(2000)=config.udfAuditSettingValue(@settingKey,@oldValue),
         @safeNew NVARCHAR(2000)=config.udfAuditSettingValue(@settingKey,@settingValue);
        INSERT logs.auditEvent(appUserId,rootTenantId,owningTenantId,eventAction,entityType,entityId,administrationJson)
        VALUES(auth.udfSessionUserId(),@root,@root,CASE WHEN @oldId IS NULL AND @laterCount = 0 THEN 'create' ELSE 'update' END,'jurisdictionSetting',@settingId,
         (SELECT @safeKey AS settingKey,@oldId AS oldSettingId,@effectiveFrom AS effectiveFrom,
         @safeOld AS oldValue,@safeNew AS newValue,
         CAST(CASE WHEN @safeKey IS NULL THEN 1 ELSE 0 END AS BIT) AS keyRedacted,
         CAST(CASE WHEN @oldValue IS NOT NULL AND @safeOld IS NULL THEN 1 ELSE 0 END AS BIT) AS oldValueRedacted,
         CAST(CASE WHEN @settingValue IS NOT NULL AND @safeNew IS NULL THEN 1 ELSE 0 END AS BIT) AS newValueRedacted,
         @laterCount AS removedVersionCount,JSON_QUERY(COALESCE(@removedJson,N'[]')) AS removedVersions
         FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER));

        SET @Comments = N'Setting changed.';

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
    , @ObjectName  = N'uspSaveSetting'
    , @Description = N'Changes a jurisdiction setting from a date: the row in force is ended the day before and a new row starts, so every change is effective-dated and kept. The key and value must satisfy config.tvfSettingCatalogue; versions dated after the effective date are replaced only when the caller confirms it, and the audit event names them.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON config.uspSaveSetting TO meritWebRole;
END;
GO
