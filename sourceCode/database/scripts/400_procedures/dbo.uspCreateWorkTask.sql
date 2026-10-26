-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspCreateWorkTask
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Creates a task in a work queue. Automation proposes and staff decide, so every automatic consequence -- an address review, a confirmatory test due, a cross-link -- arrives here. An identical open task is not created twice.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

The owner is the case's owner when a case is given, else @owningTenantId, else the caller's acting tenant. Every linked record must be one the caller can see: the checks run under row-level security, so another tenant's person, case, result, investigation or staging row is refused as not found and its id is never stored on a task in the caller's queue (issue #29).

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspCreateWorkTask ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T047
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspCreateWorkTask
      @workTaskId     UNIQUEIDENTIFIER
      , @queueCode      VARCHAR (40)
      , @title          NVARCHAR (200)
      , @detail         NVARCHAR (2000) = NULL
      , @personId       UNIQUEIDENTIFIER = NULL
      , @exposureCaseId UNIQUEIDENTIFIER = NULL
      , @labResultId    UNIQUEIDENTIFIER = NULL
      , @investigationId UNIQUEIDENTIFIER = NULL
      , @stagingRowId   UNIQUEIDENTIFIER = NULL
      , @isUrgent       BIT = 0
      , @dueDate        DATE = NULL
      , @owningTenantId UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspCreateWorkTask]')
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
    SET @KeyParameters = CONCAT (N'workTaskId=', @workTaskId, N', queueCode=', @queueCode, N', exposureCaseId=', @exposureCaseId);

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

        IF (@personId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.person WHERE personId = @personId))
           OR (@exposureCaseId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.exposureCase WHERE exposureCaseId = @exposureCaseId))
           OR (@labResultId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.labResult WHERE labResultId = @labResultId))
           OR (@investigationId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.investigation WHERE investigationId = @investigationId))
           OR (@stagingRowId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.stagingRow WHERE stagingRowId = @stagingRowId))
        BEGIN
            ;THROW 51404, N'A record linked to the task was not found.', 1;
        END;

        DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();
        DECLARE @owner UNIQUEIDENTIFIER = COALESCE ((SELECT owningTenantId FROM dbo.exposureCase WHERE exposureCaseId = @exposureCaseId),
                                                    @owningTenantId, auth.udfSessionActingTenantId ());

        IF NOT EXISTS (SELECT 1 FROM dbo.workTask
                        WHERE queueCode = @queueCode AND taskStatus = 'open' AND isDeleted = 0 AND title = @title
                          AND personId IS NOT DISTINCT FROM @personId AND exposureCaseId IS NOT DISTINCT FROM @exposureCaseId
                          AND labResultId IS NOT DISTINCT FROM @labResultId)
        BEGIN
            INSERT dbo.workTask (workTaskId, rootTenantId, owningTenantId, queueCode, title, detail, personId, exposureCaseId,
                                 labResultId, investigationId, stagingRowId, isUrgent, dueDate)
            VALUES (@workTaskId, @root, @owner, @queueCode, @title, @detail, @personId, @exposureCaseId, @labResultId,
                    @investigationId, @stagingRowId, @isUrgent, @dueDate);
            SET @Comments = N'Task created.';
        END
        ELSE
            SET @Comments = N'Identical open task exists; nothing created.';

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
      @SchemaName  = N'dbo'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspCreateWorkTask'
    , @Description = N'Creates a task in a work queue. Automation proposes and staff decide, so every automatic consequence -- an address review, a confirmatory test due, a cross-link -- arrives here. An identical open task is not created twice.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspCreateWorkTask TO meritWebRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspCreateWorkTask TO meritWorkerRole;
END;
GO
