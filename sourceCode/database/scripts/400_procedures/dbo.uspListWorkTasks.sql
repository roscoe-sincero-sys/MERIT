-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspListWorkTasks
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

One page of work tasks for the shared data grid: filtered by queue, status and assignment, sorted on the server, with the total count.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission task.view (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspListWorkTasks ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T104
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspListWorkTasks
      @queueCode      VARCHAR (40) = NULL
      , @taskStatus     VARCHAR (12) = 'open'
      , @assignedToMe   BIT = 0
      , @urgentOnly     BIT = 0
      , @sortBy         VARCHAR (30) = 'dueDate'
      , @sortDescending BIT = 0
      , @pageNumber     INT = 1
      , @pageSize       INT = 50
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspListWorkTasks]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'queueCode=', @queueCode, N', pageNumber=', @pageNumber, N', pageSize=', @pageSize, N', sortBy=', @sortBy);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'task.view') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (task.view).', 1;
        END;

        IF @pageNumber < 1 OR @pageSize NOT BETWEEN 1 AND 500
        BEGIN
            ;THROW 50000, N'@pageNumber must be 1 or more and @pageSize between 1 and 500.', 1;
        END;

        IF @sortBy NOT IN ('dueDate', 'createdDateUtc', 'title', 'queueCode')
        BEGIN
            ;THROW 50000, N'@sortBy must be one of dueDate, createdDateUtc, title, queueCode.', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @me UNIQUEIDENTIFIER = auth.udfSessionUserId ();

        SELECT w.workTaskId, w.queueCode, w.title, w.detail, w.isUrgent, w.dueDate, w.taskStatus, w.createdDateUtc,
               w.personId, p.lastName, p.firstName, w.exposureCaseId, c.caseNumber, w.labResultId, w.investigationId,
               w.stagingRowId, w.assignedToUserId, u.displayName AS assignedToName, w.rowVersion,
               COUNT (*) OVER () AS totalCount
          FROM dbo.workTask AS w
          LEFT JOIN dbo.person AS p ON p.personId = w.personId
          LEFT JOIN dbo.exposureCase AS c ON c.exposureCaseId = w.exposureCaseId
          LEFT JOIN auth.appUser AS u ON u.appUserId = w.assignedToUserId
         WHERE w.isDeleted = 0
           AND (@queueCode IS NULL OR w.queueCode = @queueCode)
           AND (@taskStatus IS NULL OR w.taskStatus = @taskStatus)
           AND (@assignedToMe = 0 OR w.assignedToUserId = @me)
           AND (@urgentOnly = 0 OR w.isUrgent = 1)
         ORDER BY w.isUrgent DESC,
                  CASE WHEN @sortBy = 'dueDate'        AND @sortDescending = 0 THEN COALESCE (w.dueDate, '9999-12-31') END ASC,
                  CASE WHEN @sortBy = 'dueDate'        AND @sortDescending = 1 THEN w.dueDate END DESC,
                  CASE WHEN @sortBy = 'createdDateUtc' AND @sortDescending = 0 THEN w.createdDateUtc END ASC,
                  CASE WHEN @sortBy = 'createdDateUtc' AND @sortDescending = 1 THEN w.createdDateUtc END DESC,
                  CASE WHEN @sortBy = 'title'          AND @sortDescending = 0 THEN w.title END ASC,
                  CASE WHEN @sortBy = 'title'          AND @sortDescending = 1 THEN w.title END DESC,
                  CASE WHEN @sortBy = 'queueCode'      AND @sortDescending = 0 THEN w.queueCode END ASC,
                  CASE WHEN @sortBy = 'queueCode'      AND @sortDescending = 1 THEN w.queueCode END DESC,
                  w.workTaskId
        OFFSET (@pageNumber - 1) * @pageSize ROWS
         FETCH NEXT @pageSize ROWS ONLY;

        -- =========================================================================================
        -- ===== End of the procedure's own work. ===================================================
        -- =========================================================================================

    END TRY
    BEGIN CATCH

        SELECT @ErrorNumber = ERROR_NUMBER ()
             , @ErrorProc   = ERROR_PROCEDURE ()
             , @ErrorLine   = ERROR_LINE ()
             , @ErrorMsg    = ERROR_MESSAGE ()
                            + N' (error '  + CAST (ERROR_NUMBER () AS NVARCHAR (11))
                            + N', line '   + CAST (ERROR_LINE ()   AS NVARCHAR (11)) + N')';

        EXEC logs.uspRecordExecutionError
              @ProcedureName   = @ProcName
            , @KeyParameters   = @KeyParameters
            , @ExecutionLogId  = NULL
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
    , @ObjectName  = N'uspListWorkTasks'
    , @Description = N'One page of work tasks for the shared data grid: filtered by queue, status and assignment, sorted on the server, with the total count.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspListWorkTasks TO meritWebRole;
END;
GO
