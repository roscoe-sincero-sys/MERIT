-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspReportCaseTimeliness
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Case timeliness by month opened: median days from collection to receipt of the qualifying result, from receipt to the case opening, and from opening to the first follow-up activity, with the number of cases followed up within seven days.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission case.view (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspReportCaseTimeliness ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T110
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspReportCaseTimeliness
      @fromDate  DATE
      , @toDate    DATE
      , @analyteId UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspReportCaseTimeliness]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'fromDate=', @fromDate, N', toDate=', @toDate, N', analyteId=', @analyteId);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'case.view') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (case.view).', 1;
        END;

        IF @fromDate IS NULL OR @toDate IS NULL OR @toDate < @fromDate OR DATEDIFF (DAY, @fromDate, @toDate) > 3660
        BEGIN
            ;THROW 50000, N'@fromDate and @toDate are required, in order, and at most ten years apart.', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        WITH perCase AS (
            SELECT DATEFROMPARTS (YEAR (c.openedDate), MONTH (c.openedDate), 1) AS monthStart,
                   DATEDIFF (DAY, q.collectedDate, CAST (q.receivedUtc AS DATE)) AS daysToReceipt,
                   DATEDIFF (DAY, CAST (q.receivedUtc AS DATE), c.openedDate) AS daysToOpen,
                   DATEDIFF (DAY, c.openedDate, (SELECT MIN (ca.activityDate) FROM dbo.caseActivity AS ca
                                                  WHERE ca.exposureCaseId = c.exposureCaseId AND ca.isDeleted = 0)) AS daysToFollowUp
              FROM dbo.exposureCase AS c
              JOIN dbo.labResult AS q ON q.labResultId = c.qualifyingLabResultId
             WHERE c.isDeleted = 0 AND c.openedDate BETWEEN @fromDate AND @toDate
               AND (@analyteId IS NULL OR c.analyteId = @analyteId))
        SELECT DISTINCT CONVERT (CHAR (7), monthStart, 126) AS periodMonth,
               COUNT (*) OVER (PARTITION BY monthStart) AS caseCount,
               CAST (PERCENTILE_CONT (0.5) WITHIN GROUP (ORDER BY daysToReceipt) OVER (PARTITION BY monthStart) AS DECIMAL (9, 1))
                   AS medianDaysToReceipt,
               CAST (PERCENTILE_CONT (0.5) WITHIN GROUP (ORDER BY daysToOpen) OVER (PARTITION BY monthStart) AS DECIMAL (9, 1))
                   AS medianDaysToOpen,
               CAST (PERCENTILE_CONT (0.5) WITHIN GROUP (ORDER BY daysToFollowUp) OVER (PARTITION BY monthStart) AS DECIMAL (9, 1))
                   AS medianDaysToFollowUp,
               SUM (CASE WHEN daysToFollowUp <= 7 THEN 1 ELSE 0 END) OVER (PARTITION BY monthStart) AS followedUpWithin7
          FROM perCase
         ORDER BY periodMonth;

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
    , @ObjectName  = N'uspReportCaseTimeliness'
    , @Description = N'Case timeliness by month opened: median days from collection to receipt of the qualifying result, from receipt to the case opening, and from opening to the first follow-up activity, with the number of cases followed up within seven days.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspReportCaseTimeliness TO meritWebRole;
END;
GO
