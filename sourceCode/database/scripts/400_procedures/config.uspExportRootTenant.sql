-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   config.uspExportRootTenant
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Platform-only, audited per-root-tenant data export covering all 70 mutable tenant/auth tables plus read-only logs.auditEvent (#346, I17). Must be called standalone with IMPLICIT_TRANSACTIONS OFF and no enclosing transaction (returns -100 without rolling back caller work). Validates and canonicalizes @tableName before committing its durable export audit event, requires @maxAuditEventId from the manifest when paging logs.auditEvent, and supports keyset paging via @afterAuditEventId.

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

exec config.uspExportRootTenant ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		346
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE config.uspExportRootTenant
      @rootTenantId      UNIQUEIDENTIFIER
      , @tableName         NVARCHAR (128) = NULL
      , @pageNumber        INT = 1
      , @pageSize          INT = 1000
      , @maxAuditEventId   BIGINT = NULL
      , @afterAuditEventId BIGINT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @@TRANCOUNT > 0 OR (@@OPTIONS & 2) = 2
    BEGIN
        -- Non-error return status (-100) preserves caller transactions even under XACT_ABORT ON + TRY/CATCH
        -- and rejects IMPLICIT_TRANSACTIONS ON before any table write.
        RAISERROR (N'config.uspExportRootTenant must be called standalone with IMPLICIT_TRANSACTIONS OFF and no enclosing transaction.', 10, 1);
        RETURN -100;
    END;

    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[config].[uspExportRootTenant]')
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
    SET @KeyParameters = CONCAT (N'rootTenantId=', @rootTenantId, N', pageNumber=', @pageNumber, N', pageSize=', @pageSize);

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

        IF NOT EXISTS (SELECT 1 FROM auth.tenant WHERE tenantId = @rootTenantId AND tenantId = rootTenantId)
        BEGIN
            ;THROW 51404, N'The root tenant was not found.', 1;
        END;
        DECLARE @canonicalTableName NVARCHAR (128) = N'[manifest]';
        IF @tableName IS NOT NULL
        BEGIN
            SELECT @canonicalTableName = v.tableName
              FROM (VALUES (N'auth.mfaRecoveryCode'), (N'auth.passwordReset'), (N'auth.mfaEmailChallenge'), (N'auth.userProfileGrant'), (N'auth.userProfile'), (N'auth.userAssignment'), (N'auth.sessionScopeTenant'), (N'auth.sessionScopePermission'), (N'auth.sessionScopeAdminTenant'), (N'auth.rolePermission'), (N'auth.tenantJurisdiction'), (N'auth.tenantClosure'), (N'logs.auditEventChange'), (N'logs.auditEventRecord'), (N'logs.securityFinding'), (N'logs.searchLog'), (N'logs.screenVisit'), (N'logs.signInEvent'), (N'dbo.contactCorrection'), (N'dbo.caseResultLink'), (N'dbo.notificationObligation'), (N'dbo.personDocument'), (N'dbo.personActivity'), (N'dbo.matchExclusion'), (N'dbo.extractRun'), (N'dbo.jobQueue'), (N'dbo.workTask'), (N'dbo.caseCluster'), (N'dbo.cluster'), (N'dbo.environmentalSample'), (N'dbo.investigationCase'), (N'dbo.investigation'), (N'dbo.exposureSource'), (N'dbo.caseActivity'), (N'dbo.exposureCase'), (N'dbo.stagingHoldReason'), (N'dbo.stagingRow'), (N'dbo.labResult'), (N'dbo.intakeBatch'), (N'dbo.intakeProfile'), (N'dbo.provider'), (N'dbo.product'), (N'dbo.employmentAnalyte'), (N'dbo.employment'), (N'dbo.personLocation'), (N'dbo.locationOwner'), (N'dbo.organizationSite'), (N'dbo.location'), (N'dbo.organizationType'), (N'dbo.organization'), (N'dbo.personRelationship'), (N'dbo.pregnancyEpisode'), (N'dbo.personIdentifier'), (N'dbo.personContactMethod'), (N'dbo.personRace'), (N'dbo.personAlias'), (N'dbo.personMatchKey'), (N'dbo.personAccess'), (N'dbo.person'), (N'config.workflowDefinition'), (N'config.notificationRule'), (N'config.environmentalStandard'), (N'config.threshold'), (N'config.jurisdictionSetting'), (N'config.subJurisdiction'), (N'auth.sessionScopeRoot'), (N'auth.sessionScope'), (N'auth.appUser'), (N'auth.role'), (N'auth.tenant'), (N'logs.auditEvent')) AS v (tableName)
             WHERE v.tableName = @tableName;
            IF @canonicalTableName = N'[manifest]'
            BEGIN
                ;THROW 51422, N'Unknown or non-tenant table name for root export.', 1;
            END;
            IF @canonicalTableName = N'logs.auditEvent' AND @maxAuditEventId IS NULL
            BEGIN
                ;THROW 51422, N'@maxAuditEventId from the export manifest is required when paging logs.auditEvent.', 1;
            END;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @exportFilterJson NVARCHAR (MAX) = (
            SELECT @rootTenantId AS rootTenantId,
                   @canonicalTableName AS tableName,
                   @pageNumber AS pageNumber,
                   @pageSize AS pageSize,
                   @maxAuditEventId AS maxAuditEventId,
                   @afterAuditEventId AS afterAuditEventId
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
        );
        DECLARE @exportAudit TABLE (auditEventId BIGINT NOT NULL);
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
                                sessionReference, traceId, actingTenantId, viewAsUserId, userAgentHash, filterJson)
        OUTPUT INSERTED.auditEventId INTO @exportAudit (auditEventId)
        VALUES (auth.udfSessionUserId (), @rootTenantId, 'export', 'tenant', @rootTenantId, @auditClientAddress, @rootTenantId,
                @auditSessionRef, @auditTraceId, @auditActingTenant, @auditViewAs, @auditUserAgentHash, @exportFilterJson);
        COMMIT TRANSACTION;

        DECLARE @effectiveMaxAuditEventId BIGINT = COALESCE (@maxAuditEventId, (SELECT TOP (1) auditEventId - 1 FROM @exportAudit));
        DECLARE @internalTableName NVARCHAR (128) = CASE WHEN @tableName IS NULL THEN NULL ELSE @canonicalTableName END;
        EXEC config.uspExportRootTenantInternal @rootTenantId = @rootTenantId, @tableName = @internalTableName,
             @pageNumber = @pageNumber, @pageSize = @pageSize, @maxAuditEventId = @effectiveMaxAuditEventId,
             @afterAuditEventId = @afterAuditEventId;
        SET @Comments = CONCAT (N'Root tenant export completed for ', @rootTenantId);

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
    , @ObjectName  = N'uspExportRootTenant'
    , @Description = N'Platform-only, audited per-root-tenant data export covering all 70 mutable tenant/auth tables plus read-only logs.auditEvent (#346, I17). Must be called standalone with IMPLICIT_TRANSACTIONS OFF and no enclosing transaction (returns -100 without rolling back caller work). Validates and canonicalizes @tableName before committing its durable export audit event, requires @maxAuditEventId from the manifest when paging logs.auditEvent, and supports keyset paging via @afterAuditEventId.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON config.uspExportRootTenant TO meritWebRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON config.uspExportRootTenant TO meritWorkerRole;
END;
GO
