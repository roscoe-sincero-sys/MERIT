-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   logs.uspUpdateSecurityFindingStatus
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Updates the review status of a persisted security finding (requires tenant.administer or security.investigate, not read-only security.review) and audits the transition in the same transaction (#327, Codex #4).

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission tenant.administer or security.investigate (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec logs.uspUpdateSecurityFindingStatus ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		327
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE logs.uspUpdateSecurityFindingStatus
      @securityFindingId UNIQUEIDENTIFIER
      , @status            VARCHAR (20)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[logs].[uspUpdateSecurityFindingStatus]')
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
    SET @KeyParameters = CONCAT (N'securityFindingId=', @securityFindingId);

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        IF auth.udfHasPermission (N'tenant.administer') = 0 AND auth.udfHasPermission (N'security.investigate') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (tenant.administer or security.investigate).', 1;
        END;

        IF @status NOT IN ('open', 'acknowledged', 'resolved', 'falsePositive')
        BEGIN
            ;THROW 51422, N'Choose a valid finding status (open, acknowledged, resolved, falsePositive).', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @all BIT = auth.udfHasPermission (N'security.investigate');
        DECLARE @adminTenants TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @adminTenants SELECT tenantId FROM auth.tvfSessionWritePermission ('tenant.administer');
        DECLARE @wholeAdminRoots TABLE (rootTenantId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @wholeAdminRoots SELECT t.tenantId FROM auth.tenant AS t JOIN @adminTenants AS a ON a.tenantId = t.tenantId WHERE t.tenantId = t.rootTenantId;

        -- Codex #1: Enforce owner/root scope atomically in the UPDATE statement itself and capture post-update root/owner via OUTPUT.
        DECLARE @updated TABLE (rootTenantId UNIQUEIDENTIFIER NOT NULL, owningTenantId UNIQUEIDENTIFIER NULL);

        UPDATE logs.securityFinding
           SET status = @status,
               acknowledgedByUserId = auth.udfSessionUserId (),
               acknowledgedUtc = SYSUTCDATETIME ()
        OUTPUT INSERTED.rootTenantId, INSERTED.owningTenantId INTO @updated
         WHERE securityFindingId = @securityFindingId AND isDeleted = 0
           AND (@all = 1
                OR owningTenantId IN (SELECT tenantId FROM @adminTenants)
                OR (owningTenantId IS NULL AND rootTenantId IN (SELECT rootTenantId FROM @wholeAdminRoots)));

        IF NOT EXISTS (SELECT 1 FROM @updated)
        BEGIN
            ;THROW 51404, N'The security finding was not found, or you do not have permission to update it.', 1;
        END;

        DECLARE @root UNIQUEIDENTIFIER, @owner UNIQUEIDENTIFIER;
        SELECT TOP (1) @root = rootTenantId, @owner = owningTenantId FROM @updated;

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
        VALUES (auth.udfSessionUserId (), @root, 'update', 'securityFinding', @securityFindingId, @auditClientAddress,
                @owner, @auditSessionRef, @auditTraceId, @auditActingTenant, @auditViewAs, @auditUserAgentHash);

        SET @Comments = CONCAT (N'Finding status updated to ', @status, N'.');

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
      @SchemaName  = N'logs'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspUpdateSecurityFindingStatus'
    , @Description = N'Updates the review status of a persisted security finding (requires tenant.administer or security.investigate, not read-only security.review) and audits the transition in the same transaction (#327, Codex #4).';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspUpdateSecurityFindingStatus TO meritWebRole;
END;
GO
