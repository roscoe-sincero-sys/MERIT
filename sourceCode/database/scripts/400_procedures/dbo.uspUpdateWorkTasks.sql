-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspUpdateWorkTasks
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Bulk action on work tasks from the grid: assign to me, assign to a user, complete or dismiss, with an optional note. Tasks outside the caller's scope are invisible and so untouched.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission task.edit (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspUpdateWorkTasks ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T104
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspUpdateWorkTasks
      @workTaskIdsJson NVARCHAR (MAX)
      , @action          VARCHAR (20)
      , @assignToUserId  UNIQUEIDENTIFIER = NULL
      , @note            NVARCHAR (1000) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspUpdateWorkTasks]')
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
    SET @KeyParameters = CONCAT (N'action=', @action);

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        IF auth.udfHasPermission (N'task.edit') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (task.edit).', 1;
        END;

        IF ISJSON (@workTaskIdsJson) = 0
        BEGIN
            ;THROW 50000, N'@workTaskIdsJson must be a JSON array of ids.', 1;
        END;

        IF @action NOT IN ('assignToMe', 'assign', 'complete', 'dismiss', 'reopen')
        BEGIN
            ;THROW 50000, N'@action must be assignToMe, assign, complete, dismiss or reopen.', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @me UNIQUEIDENTIFIER = auth.udfSessionUserId ();
        DECLARE @ids TABLE (workTaskId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @ids SELECT DISTINCT TRY_CAST ([value] AS UNIQUEIDENTIFIER) FROM OPENJSON (@workTaskIdsJson)
                     WHERE TRY_CAST ([value] AS UNIQUEIDENTIFIER) IS NOT NULL;

        UPDATE w
           SET assignedToUserId  = CASE @action WHEN 'assignToMe' THEN @me WHEN 'assign' THEN @assignToUserId
                                                ELSE w.assignedToUserId END
             , taskStatus        = CASE @action WHEN 'complete' THEN 'done' WHEN 'dismiss' THEN 'dismissed'
                                                WHEN 'reopen' THEN 'open' ELSE w.taskStatus END
             , completedUtc      = CASE WHEN @action IN ('complete', 'dismiss') THEN SYSUTCDATETIME ()
                                        WHEN @action = 'reopen' THEN NULL ELSE w.completedUtc END
             , completedByUserId = CASE WHEN @action IN ('complete', 'dismiss') THEN @me
                                        WHEN @action = 'reopen' THEN NULL ELSE w.completedByUserId END
             , resolutionNote    = CASE WHEN @action IN ('complete', 'dismiss') THEN COALESCE (@note, w.resolutionNote)
                                        ELSE w.resolutionNote END
          FROM dbo.workTask AS w
          JOIN @ids AS i ON i.workTaskId = w.workTaskId
         WHERE w.isDeleted = 0
           AND ((@action IN ('complete', 'dismiss') AND w.taskStatus = 'open')
                OR (@action = 'reopen' AND w.taskStatus <> 'open')
                OR (@action IN ('assign', 'assignToMe') AND w.taskStatus = 'open'
                    AND (w.assignedToUserId IS NULL OR w.assignedToUserId <> CASE WHEN @action = 'assignToMe' THEN @me ELSE @assignToUserId END)));

        DECLARE @updatedCount INT = @@ROWCOUNT;
        SET @Comments = CONCAT (@updatedCount, N' task(s) updated.');
        SELECT @updatedCount AS updatedCount;

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
    , @ObjectName  = N'uspUpdateWorkTasks'
    , @Description = N'Bulk action on work tasks from the grid: assign to me, assign to a user, complete or dismiss, with an optional note. Tasks outside the caller''s scope are invisible and so untouched.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspUpdateWorkTasks TO meritWebRole;
END;
GO
