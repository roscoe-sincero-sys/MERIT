-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspCorrectHeldGroup
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Corrects one field of an exact held group, optionally limited to selected rows and a batch.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission result.release_hold (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspCorrectHeldGroup ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T105
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspCorrectHeldGroup
      @reason VARCHAR (30)
      , @reportedValue NVARCHAR (200) = NULL
      , @sender UNIQUEIDENTIFIER = NULL
      , @batch UNIQUEIDENTIFIER = NULL
      , @field VARCHAR (60)
      , @value NVARCHAR (1000) = NULL
      , @selectedIdsJson NVARCHAR (MAX) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspCorrectHeldGroup]')
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
    SET @KeyParameters = N'group correction';

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        IF auth.udfHasPermission (N'result.release_hold') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (result.release_hold).', 1;
        END;

        IF NULLIF (@reason, '') IS NULL OR @field LIKE '%[^a-zA-Z0-9]%' OR @field IN ('matchPersonId', 'matchNewPerson')
            THROW 50000, N'Choose a cause and a reported field.', 1;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @corrected TABLE (stagingRowId UNIQUEIDENTIFIER PRIMARY KEY, owningTenantId UNIQUEIDENTIFIER);
        UPDATE s SET correctedJson = JSON_MODIFY (COALESCE (s.correctedJson, N'{}'), CONCAT (N'$.', @field), @value)
        OUTPUT inserted.stagingRowId, inserted.owningTenantId INTO @corrected
        FROM dbo.stagingRow AS s JOIN dbo.intakeBatch AS b ON b.intakeBatchId = s.intakeBatchId
        WHERE s.rowStatus = 'held' AND s.isDeleted = 0 AND b.isDeleted = 0
          AND b.senderOrganizationId IS NOT DISTINCT FROM @sender
          AND (@batch IS NULL OR s.intakeBatchId = @batch)
          AND (@selectedIdsJson IS NULL OR s.stagingRowId IN (SELECT TRY_CONVERT (UNIQUEIDENTIFIER, [value]) FROM OPENJSON (@selectedIdsJson)))
          AND EXISTS (SELECT 1 FROM dbo.stagingHoldReason AS h WHERE h.stagingRowId = s.stagingRowId
            AND h.reasonCode = @reason AND h.reportedValue IS NOT DISTINCT FROM @reportedValue AND h.isResolved = 0 AND h.isDeleted = 0);
        DECLARE @count INT = (SELECT COUNT (*) FROM @corrected);
        IF @count > 0
        BEGIN
            DECLARE @auditScope UNIQUEIDENTIFIER = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER);
        DECLARE @auditSessionRef UNIQUEIDENTIFIER, @auditActingTenant UNIQUEIDENTIFIER, @auditViewAs UNIQUEIDENTIFIER;
        SELECT @auditSessionRef = sessionReference, @auditActingTenant = actingTenantId, @auditViewAs = viewAsUserId
          FROM auth.sessionScope
         WHERE sessionScopeId = @auditScope AND revokedUtc IS NULL AND expiresUtc > SYSUTCDATETIME () AND isDeleted = 0;
        DECLARE @auditClientAddress VARCHAR (45) = TRY_CAST (SESSION_CONTEXT (N'clientAddress') AS VARCHAR (45));
        DECLARE @auditTraceId VARCHAR (64) = TRY_CAST (SESSION_CONTEXT (N'traceId') AS VARCHAR (64));
            DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();
            DECLARE @singleOwner UNIQUEIDENTIFIER = (SELECT CASE WHEN COUNT (DISTINCT owningTenantId) = 1 THEN MAX (owningTenantId) END FROM @corrected);
            INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId, recordCount, clientAddress, owningTenantId,
                                    sessionReference, traceId, actingTenantId, viewAsUserId)
            VALUES (auth.udfSessionUserId (), @root, 'update', 'stagingRow', NULL, @count, @auditClientAddress, @singleOwner,
                    @auditSessionRef, @auditTraceId, @auditActingTenant, @auditViewAs);
            DECLARE @auditEventId BIGINT = SCOPE_IDENTITY ();
            IF @root IS NOT NULL
                INSERT logs.auditEventRecord (auditEventRecordId, auditEventId, rootTenantId, entityType, entityId)
                SELECT CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), @auditEventId, @root, 'stagingRow', stagingRowId FROM @corrected;
        END;
        SELECT @count AS updated;
        SET @Comments = CONCAT (@count, N' rows corrected.');

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
      @SchemaName  = N'dbo'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspCorrectHeldGroup'
    , @Description = N'Corrects one field of an exact held group, optionally limited to selected rows and a batch.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspCorrectHeldGroup TO meritWebRole;
END;
GO
