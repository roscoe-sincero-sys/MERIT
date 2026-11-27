-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   config.uspPurgeRootTenant
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Platform-only, two-step confirmed, audited per-root-tenant purge (#346, B4, B5, I16, I18). Requires @confirmationCode = N'PURGE:' + tenantCode and refuses the platform root. Fences the root first (deactivates tenants, revokes sessions, fails queued/running jobs), scrubs and soft-deletes all 70 mutable tenant/auth tables in 5,000-row batches (or rehearses inside a rolled-back transaction when @dryRun = 1), verifies zero unscrubbed rows remain, and returns a purge report with backupAgeOutUtc.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec config.uspPurgeRootTenant ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		346
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE config.uspPurgeRootTenant
      @rootTenantId        UNIQUEIDENTIFIER
      , @confirmationCode    NVARCHAR (60)
      , @backupRetentionDays INT = 35
      , @dryRun              BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    IF @@TRANCOUNT > 0
    BEGIN
        RAISERROR (N'config.uspPurgeRootTenant must be called outside an enclosing transaction.', 16, 1);
        RETURN -1;
    END;

    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[config].[uspPurgeRootTenant]')
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
    SET @KeyParameters = CONCAT (N'rootTenantId=', @rootTenantId, N', backupRetentionDays=', @backupRetentionDays, N', dryRun=', @dryRun);

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        IF auth.udfHasPermission (N'security.investigate') = 0 OR auth.udfHasPermission (N'tenant.create') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (security.investigate and tenant.create).', 1;
        END;

        IF @dryRun IS NULL
        BEGIN
            ;THROW 51422, N'@dryRun must be 0 or 1.', 1;
        END;
        DECLARE @targetCode VARCHAR (30), @targetType VARCHAR (30);
        SELECT @targetCode = tenantCode, @targetType = tenantType
          FROM auth.tenant
         WHERE tenantId = @rootTenantId AND tenantId = rootTenantId;

        IF @targetCode IS NULL
        BEGIN
            ;THROW 51404, N'The root tenant was not found.', 1;
        END;
        IF @targetType = 'platform'
        BEGIN
            ;THROW 51422, N'The platform root tenant cannot be purged.', 1;
        END;
        IF EXISTS (
            SELECT 1
              FROM auth.tenant AS child
             WHERE child.rootTenantId <> @rootTenantId
               AND child.isDeleted = 0
               AND (   child.parentTenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId)
                    OR EXISTS (SELECT 1
                                 FROM auth.tenantClosure AS tc
                                 JOIN auth.tenant        AS anc ON anc.tenantId = tc.ancestorTenantId
                                WHERE tc.descendantTenantId = child.tenantId
                                  AND tc.isDeleted = 0
                                  AND anc.rootTenantId = @rootTenantId))
        )
        BEGIN
            ;THROW 51422, N'This root tenant has live descendant data roots. Reparent or purge all child roots before purging this root.', 1;
        END;
        IF COALESCE (@confirmationCode, N'') <> CONCAT (N'PURGE:', @targetCode)
        BEGIN
            ;THROW 51422, N'Confirmation code mismatch. Pass PURGE:<tenantCode> to confirm root tenant purge.', 1;
        END;
        IF @backupRetentionDays IS NULL OR @backupRetentionDays NOT BETWEEN 1 AND 3650
        BEGIN
            ;THROW 51422, N'@backupRetentionDays must be between 1 and 3650.', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        IF @dryRun = 0
        BEGIN
            BEGIN TRANSACTION;
            DECLARE @auditScope UNIQUEIDENTIFIER = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER);
        DECLARE @auditSessionRef UNIQUEIDENTIFIER, @auditActingTenant UNIQUEIDENTIFIER, @auditViewAs UNIQUEIDENTIFIER;
        SELECT @auditSessionRef = sessionReference, @auditActingTenant = actingTenantId, @auditViewAs = viewAsUserId
          FROM auth.sessionScope
         WHERE sessionScopeId = @auditScope AND revokedUtc IS NULL AND expiresUtc > SYSUTCDATETIME () AND isDeleted = 0;
        DECLARE @auditClientAddress VARCHAR (45) = TRY_CAST (SESSION_CONTEXT (N'clientAddress') AS VARCHAR (45));
        DECLARE @auditTraceId VARCHAR (64) = TRY_CAST (SESSION_CONTEXT (N'traceId') AS VARCHAR (64));
        DECLARE @auditUserAgentHash CHAR (16) = TRY_CAST (SESSION_CONTEXT (N'userAgentHash') AS CHAR (16));
            INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId, clientAddress, owningTenantId,
                                    sessionReference, traceId, actingTenantId, viewAsUserId, userAgentHash)
            VALUES (auth.udfSessionUserId (), @rootTenantId, 'delete', 'tenant', @rootTenantId, @auditClientAddress, @rootTenantId,
                    @auditSessionRef, @auditTraceId, @auditActingTenant, @auditViewAs, @auditUserAgentHash);
            COMMIT TRANSACTION;
        END;

        EXEC config.uspPurgeRootTenantInternal @rootTenantId = @rootTenantId, @backupRetentionDays = @backupRetentionDays, @dryRun = @dryRun;
        SET @Comments = CONCAT (CASE WHEN @dryRun = 1 THEN N'Dry-run purge completed for ' ELSE N'Root tenant purge completed for ' END, @rootTenantId);

        -- =========================================================================================
        -- ===== End of the procedure's own work. ===================================================
        -- =========================================================================================

        -- Completion. Deliberately after the COMMIT.
        SET @EndTimeUtc = SYSUTCDATETIME ();

        IF @ExecutionId IS NOT NULL
        BEGIN
            UPDATE logs.ExecutionLog
               SET EndDateUtc          = @EndTimeUtc
                 , ElapsedMilliseconds = CAST (LEAST (DATEDIFF_BIG (MILLISECOND, @StartTimeUtc, @EndTimeUtc)
                                                    , CAST (2147483647 AS BIGINT)) AS INT)
                 , Successful          = 1
                 , Comments            = LEFT (@Comments, 500)
                 , modifiedBy          = COALESCE (NULLIF (CAST (SESSION_CONTEXT (N'AppUser') AS NVARCHAR (255)), N''), ORIGINAL_LOGIN ())
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
    , @ObjectName  = N'uspPurgeRootTenant'
    , @Description = N'Platform-only, two-step confirmed, audited per-root-tenant purge (#346, B4, B5, I16, I18). Requires @confirmationCode = N''PURGE:'' + tenantCode and refuses the platform root. Fences the root first (deactivates tenants, revokes sessions, fails queued/running jobs), scrubs and soft-deletes all 70 mutable tenant/auth tables in 5,000-row batches (or rehearses inside a rolled-back transaction when @dryRun = 1), verifies zero unscrubbed rows remain, and returns a purge report with backupAgeOutUtc.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON config.uspPurgeRootTenant TO meritWebRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON config.uspPurgeRootTenant TO meritWorkerRole;
END;
GO
