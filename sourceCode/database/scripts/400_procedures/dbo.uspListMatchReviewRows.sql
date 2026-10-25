-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspListMatchReviewRows
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

The person matching review queue: held rows whose identity needs a reviewer (an uncertain match or a placeholder name), urgent first, then oldest. Rows a reviewer marked Not sure are flagged as escalated to a supervisor.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission result.release_hold (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspListMatchReviewRows ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T108
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspListMatchReviewRows
      @escalatedOnly BIT = 0
      , @stagingRowId  UNIQUEIDENTIFIER = NULL
      , @pageNumber    INT = 1
      , @pageSize      INT = 100
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspListMatchReviewRows]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'escalatedOnly=', @escalatedOnly, N', pageNumber=', @pageNumber);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'result.release_hold') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (result.release_hold).', 1;
        END;

        IF @pageNumber < 1 OR @pageSize NOT BETWEEN 1 AND 500
        BEGIN
            ;THROW 50000, N'@pageNumber must be 1 or more and @pageSize between 1 and 500.', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        SELECT s.stagingRowId, s.intakeBatchId, b.fileName, o.organizationName AS senderName, s.rowNumber, s.parsedJson,
               s.correctedJson, s.isUrgent, s.heldSinceUtc, h.reasonMessage,
               CAST (CASE WHEN w.workTaskId IS NULL THEN 0 ELSE 1 END AS BIT) AS isEscalated, w.detail AS escalationNote,
               COUNT (*) OVER () AS totalCount
          FROM dbo.stagingRow AS s
          JOIN dbo.intakeBatch AS b ON b.intakeBatchId = s.intakeBatchId
          LEFT JOIN dbo.organization AS o ON o.organizationId = b.senderOrganizationId
         CROSS APPLY (SELECT TOP (1) x.reasonMessage FROM dbo.stagingHoldReason AS x
                       WHERE x.stagingRowId = s.stagingRowId AND x.reasonCode = 'uncertainMatch' AND x.isResolved = 0
                         AND x.isDeleted = 0
                       ORDER BY x.createdDateUtc) AS h
         OUTER APPLY (SELECT TOP (1) t.workTaskId, t.detail FROM dbo.workTask AS t
                       WHERE t.stagingRowId = s.stagingRowId AND t.queueCode = 'matchReview' AND t.taskStatus = 'open'
                         AND t.isDeleted = 0) AS w
         WHERE s.rowStatus = 'held' AND s.isDeleted = 0
           AND (@escalatedOnly = 0 OR w.workTaskId IS NOT NULL)
           AND (@stagingRowId IS NULL OR s.stagingRowId = @stagingRowId)
         ORDER BY s.isUrgent DESC, s.heldSinceUtc, s.stagingRowId
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
    , @ObjectName  = N'uspListMatchReviewRows'
    , @Description = N'The person matching review queue: held rows whose identity needs a reviewer (an uncertain match or a placeholder name), urgent first, then oldest. Rows a reviewer marked Not sure are flagged as escalated to a supervisor.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspListMatchReviewRows TO meritWebRole;
END;
GO
