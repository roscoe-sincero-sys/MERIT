-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   logs.uspRecordAuditEvent
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Writes one event to the tamper-evident audit ledger: a view, change, merge, export, search or permission denial. The user, root tenant, session reference, acting tenant and view-as user come from the session, never from a parameter. Optional manifest record IDs are stored in logs.auditEventRecord (#326, #333).

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

exec logs.uspRecordAuditEvent ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T051
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE logs.uspRecordAuditEvent
      @eventAction   VARCHAR (40)
      , @entityType    VARCHAR (30)
      , @entityId      UNIQUEIDENTIFIER = NULL
      , @recordCount   INT = NULL
      , @clientAddress VARCHAR (45) = NULL
      , @viewKind      VARCHAR (16) = NULL
      , @filterJson    NVARCHAR (MAX) = NULL
      , @recordIdsJson NVARCHAR (MAX) = NULL
      , @traceId       VARCHAR (64) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[logs].[uspRecordAuditEvent]')
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
    SET @KeyParameters = CONCAT (N'eventAction=', @eventAction, N', entityType=', @entityType, N', entityId=', @entityId);

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        -- Issue 270: stamp the owner the actor's own session can see now, so a tenant-scoped reviewer later sees events on
        -- that tenant's records without needing row access to the records themselves.
        DECLARE @owner UNIQUEIDENTIFIER;
        IF @entityId IS NOT NULL
        BEGIN
            SET @owner = CASE @entityType
                WHEN 'person'                 THEN dbo.udfPatientWorkOwner (@entityId)
                WHEN 'patientCare'            THEN dbo.udfPatientWorkOwner (@entityId)
                WHEN 'labResult'              THEN dbo.udfPatientWorkOwner ((SELECT personId FROM dbo.labResult WHERE labResultId = @entityId))
                WHEN 'exposureCase'           THEN (SELECT owningTenantId FROM dbo.exposureCase WHERE exposureCaseId = @entityId)
                WHEN 'investigation'          THEN (SELECT owningTenantId FROM dbo.investigation WHERE investigationId = @entityId)
                WHEN 'workTask'               THEN (SELECT owningTenantId FROM dbo.workTask WHERE workTaskId = @entityId)
                WHEN 'testingPlan'            THEN (SELECT owningTenantId FROM dbo.workTask WHERE workTaskId = @entityId)
                WHEN 'notificationObligation' THEN (SELECT owningTenantId FROM dbo.notificationObligation WHERE notificationObligationId = @entityId)
                WHEN 'stagingRow'             THEN (SELECT owningTenantId FROM dbo.stagingRow WHERE stagingRowId = @entityId)
            END;
        END;

        DECLARE @auditScope UNIQUEIDENTIFIER = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER);
        DECLARE @auditSessionRef UNIQUEIDENTIFIER, @auditActingTenant UNIQUEIDENTIFIER, @auditViewAs UNIQUEIDENTIFIER;
        SELECT @auditSessionRef = sessionReference, @auditActingTenant = actingTenantId, @auditViewAs = viewAsUserId
          FROM auth.sessionScope
         WHERE sessionScopeId = @auditScope AND revokedUtc IS NULL AND expiresUtc > SYSUTCDATETIME () AND isDeleted = 0;
        DECLARE @auditClientAddress VARCHAR (45) = TRY_CAST (SESSION_CONTEXT (N'clientAddress') AS VARCHAR (45));
        DECLARE @auditTraceId VARCHAR (64) = TRY_CAST (SESSION_CONTEXT (N'traceId') AS VARCHAR (64));
        DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();
        DECLARE @resolvedAddress VARCHAR (45) = COALESCE (NULLIF (@clientAddress, ''), @auditClientAddress);
        DECLARE @resolvedTrace VARCHAR (64) = COALESCE (NULLIF (@traceId, ''), @auditTraceId);

        INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId, recordCount, clientAddress, owningTenantId,
                                sessionReference, traceId, actingTenantId, viewAsUserId, viewKind, filterJson)
        VALUES (auth.udfSessionUserId (), @root, @eventAction, @entityType, @entityId, @recordCount,
                @resolvedAddress, @owner, @auditSessionRef, @resolvedTrace, @auditActingTenant, @auditViewAs, @viewKind, @filterJson);

        DECLARE @auditEventId BIGINT = SCOPE_IDENTITY ();

        IF @recordIdsJson IS NOT NULL AND ISJSON (@recordIdsJson) = 1 AND @root IS NOT NULL
        BEGIN
            INSERT logs.auditEventRecord (auditEventRecordId, auditEventId, rootTenantId, entityType, entityId)
            SELECT CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), @auditEventId, @root, x.entityType, x.entityId
              FROM (SELECT DISTINCT
                           COALESCE (NULLIF (JSON_VALUE (j.[value], '$.entityType'), ''), @entityType) AS entityType,
                           COALESCE (TRY_CAST (JSON_VALUE (j.[value], '$.entityId') AS UNIQUEIDENTIFIER),
                                     TRY_CAST (j.[value] AS UNIQUEIDENTIFIER)) AS entityId
                      FROM OPENJSON (@recordIdsJson) AS j) AS x
             WHERE x.entityId IS NOT NULL;
        END;

        SELECT @auditEventId AS auditEventId;

        SET @Comments = N'Audit event recorded.';

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
    , @ObjectName  = N'uspRecordAuditEvent'
    , @Description = N'Writes one event to the tamper-evident audit ledger: a view, change, merge, export, search or permission denial. The user, root tenant, session reference, acting tenant and view-as user come from the session, never from a parameter. Optional manifest record IDs are stored in logs.auditEventRecord (#326, #333).';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspRecordAuditEvent TO meritWebRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspRecordAuditEvent TO meritWorkerRole;
END;
GO
