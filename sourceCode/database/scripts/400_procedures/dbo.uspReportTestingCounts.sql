-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspReportTestingCounts
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Testing counts and rates by collection month and population: valid final results, people tested, and results at or above the case level in force on the collection date.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission result.view (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspReportTestingCounts ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T110
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspReportTestingCounts
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
                                                    , N'[dbo].[uspReportTestingCounts]')
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

        IF auth.udfHasPermission (N'result.view') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (result.view).', 1;
        END;

        IF @fromDate IS NULL OR @toDate IS NULL OR @toDate < @fromDate OR DATEDIFF (DAY, @fromDate, @toDate) > 3660
        BEGIN
            ;THROW 50000, N'@fromDate and @toDate are required, in order, and at most ten years apart.', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        SELECT CONVERT (CHAR (7), x.monthStart, 126) AS periodMonth, x.population, COUNT (*) AS testCount,
               COUNT (DISTINCT x.personId) AS personCount,
               SUM (CASE WHEN x.caseLevel IS NOT NULL AND x.operator <> '<' AND x.valueCanonical >= x.caseLevel THEN 1 ELSE 0 END)
                   AS elevatedCount
          FROM (SELECT DATEFROMPARTS (YEAR (r.collectedDate), MONTH (r.collectedDate), 1) AS monthStart, r.population,
                       r.personId, r.operator, r.valueCanonical,
                       (SELECT MIN (t.lowerValue) FROM config.threshold AS t
                  WHERE t.analyteSpecimenId = r.analyteSpecimenId AND t.thresholdType = 'case' AND t.isActive = 1
                    AND t.isDeleted = 0 AND t.population IN (r.population, 'all') AND t.effectiveFrom <= r.collectedDate
                    AND (t.effectiveTo IS NULL OR t.effectiveTo >= r.collectedDate)) AS caseLevel
                  FROM dbo.labResult AS r
                  JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId = r.analyteSpecimenId
                 WHERE r.isDeleted = 0 AND r.resultRowStatus = 'valid' AND r.resultStatus IN ('final', 'corrected')
                   AND r.collectedDate BETWEEN @fromDate AND @toDate
                   AND (@analyteId IS NULL OR s.analyteId = @analyteId)) AS x
         GROUP BY x.monthStart, x.population
         ORDER BY x.monthStart, x.population;

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
    , @ObjectName  = N'uspReportTestingCounts'
    , @Description = N'Testing counts and rates by collection month and population: valid final results, people tested, and results at or above the case level in force on the collection date.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspReportTestingCounts TO meritWebRole;
END;
GO
