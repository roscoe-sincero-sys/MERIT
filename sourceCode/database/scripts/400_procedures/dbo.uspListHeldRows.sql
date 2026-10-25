-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspListHeldRows
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

One page of held rows, optionally for one cause, with their reasons, the reported values and any correction beside them, and how long each has waited. Urgent rows first.

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

exec dbo.uspListHeldRows ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T105
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspListHeldRows
      @reasonCode    VARCHAR (30) = NULL
      , @reportedValue NVARCHAR (200) = NULL
      , @intakeBatchId UNIQUEIDENTIFIER = NULL
      , @pageNumber    INT = 1
      , @pageSize      INT = 50
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspListHeldRows]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'reasonCode=', @reasonCode, N', intakeBatchId=', @intakeBatchId, N', pageNumber=', @pageNumber);

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
               s.correctedJson, s.isUrgent, s.heldSinceUtc, s.assignedToUserId, u.displayName AS assignedToName,
               (SELECT STRING_AGG (CONCAT (h.reasonCode, N': ', h.reasonMessage), N' | ')
                  FROM dbo.stagingHoldReason AS h WHERE h.stagingRowId = s.stagingRowId AND h.isResolved = 0 AND h.isDeleted = 0) AS reasons,
               COUNT (*) OVER () AS totalCount
          FROM dbo.stagingRow AS s
          JOIN dbo.intakeBatch AS b ON b.intakeBatchId = s.intakeBatchId
          LEFT JOIN dbo.organization AS o ON o.organizationId = b.senderOrganizationId
          LEFT JOIN auth.appUser AS u ON u.appUserId = s.assignedToUserId
         WHERE s.rowStatus = 'held' AND s.isDeleted = 0
           AND (@intakeBatchId IS NULL OR s.intakeBatchId = @intakeBatchId)
           AND (@reasonCode IS NULL OR EXISTS (SELECT 1 FROM dbo.stagingHoldReason AS h WHERE h.stagingRowId = s.stagingRowId
                                                  AND h.reasonCode = @reasonCode AND h.isResolved = 0 AND h.isDeleted = 0
                                                  AND (@reportedValue IS NULL OR h.reportedValue = @reportedValue)))
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
    , @ObjectName  = N'uspListHeldRows'
    , @Description = N'One page of held rows, optionally for one cause, with their reasons, the reported values and any correction beside them, and how long each has waited. Urgent rows first.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspListHeldRows TO meritWebRole;
END;
GO
