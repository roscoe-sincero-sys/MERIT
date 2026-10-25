-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspReportReporterTimeliness
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Reporter timeliness and completeness by performing laboratory: results received, median days from collection to receipt, the share received within five days, and how often key fields were missing (blood sample type, test method, ordering provider, patient address). Held rows count what could not load.

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

exec dbo.uspReportReporterTimeliness ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T110
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspReportReporterTimeliness
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
                                                    , N'[dbo].[uspReportReporterTimeliness]')
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

        WITH r AS (
            SELECT r.labOrganizationId, DATEDIFF (DAY, r.collectedDate, CAST (r.receivedUtc AS DATE)) AS daysToReceipt,
                   CASE WHEN s.usesSampleType = 1 AND COALESCE (r.sampleType, 'unknown') = 'unknown' THEN 1 ELSE 0 END AS noSampleType,
                   CASE WHEN r.testMethod IS NULL OR r.testMethod = 'unknown' THEN 1 ELSE 0 END AS noMethod,
                   CASE WHEN r.orderingProviderId IS NULL THEN 1 ELSE 0 END AS noProvider,
                   CASE WHEN r.reportedLocationId IS NULL THEN 1 ELSE 0 END AS noAddress
              FROM dbo.labResult AS r
              JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId = r.analyteSpecimenId
             WHERE r.isDeleted = 0 AND r.resultRowStatus IN ('valid', 'duplicate')
               AND CAST (r.receivedUtc AS DATE) BETWEEN @fromDate AND @toDate
               AND (@analyteId IS NULL OR s.analyteId = @analyteId)),
        agg AS (
            SELECT DISTINCT labOrganizationId,
                   COUNT (*) OVER (PARTITION BY labOrganizationId) AS resultCount,
                   CAST (PERCENTILE_CONT (0.5) WITHIN GROUP (ORDER BY daysToReceipt) OVER (PARTITION BY labOrganizationId)
                         AS DECIMAL (9, 1)) AS medianDaysToReceipt,
                   SUM (CASE WHEN daysToReceipt <= 5 THEN 1 ELSE 0 END) OVER (PARTITION BY labOrganizationId) AS within5Days,
                   SUM (noSampleType) OVER (PARTITION BY labOrganizationId) AS missingSampleType,
                   SUM (noMethod) OVER (PARTITION BY labOrganizationId) AS missingMethod,
                   SUM (noProvider) OVER (PARTITION BY labOrganizationId) AS missingProvider,
                   SUM (noAddress) OVER (PARTITION BY labOrganizationId) AS missingAddress
              FROM r)
        SELECT o.organizationName AS labName, a.resultCount, a.medianDaysToReceipt,
               CAST (100.0 * a.within5Days / a.resultCount AS DECIMAL (5, 1)) AS pctWithin5Days,
               CAST (100.0 * a.missingSampleType / a.resultCount AS DECIMAL (5, 1)) AS pctMissingSampleType,
               CAST (100.0 * a.missingMethod / a.resultCount AS DECIMAL (5, 1)) AS pctMissingMethod,
               CAST (100.0 * a.missingProvider / a.resultCount AS DECIMAL (5, 1)) AS pctMissingProvider,
               CAST (100.0 * a.missingAddress / a.resultCount AS DECIMAL (5, 1)) AS pctMissingAddress,
               (SELECT COUNT (*) FROM dbo.stagingRow AS sr
                  JOIN dbo.intakeBatch AS b ON b.intakeBatchId = sr.intakeBatchId
                 WHERE b.senderOrganizationId = a.labOrganizationId AND sr.rowStatus IN ('held', 'rejected') AND sr.isDeleted = 0
                   AND CAST (b.receivedUtc AS DATE) BETWEEN @fromDate AND @toDate) AS heldOrRejectedRows
          FROM agg AS a
          JOIN dbo.organization AS o ON o.organizationId = a.labOrganizationId
         ORDER BY o.organizationName;

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
    , @ObjectName  = N'uspReportReporterTimeliness'
    , @Description = N'Reporter timeliness and completeness by performing laboratory: results received, median days from collection to receipt, the share received within five days, and how often key fields were missing (blood sample type, test method, ordering provider, patient address). Held rows count what could not load.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspReportReporterTimeliness TO meritWebRole;
END;
GO
