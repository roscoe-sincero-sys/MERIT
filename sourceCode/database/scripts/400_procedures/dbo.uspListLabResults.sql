-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspListLabResults
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

One page of the results line list, filtered and sorted on the server, with the total row count.

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

exec dbo.uspListLabResults ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T106
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspListLabResults
      @analyteId         UNIQUEIDENTIFIER = NULL
      , @collectedFrom     DATE = NULL
      , @collectedTo       DATE = NULL
      , @population        VARCHAR (10) = NULL
      , @minValue          DECIMAL (18, 6) = NULL
      , @labOrganizationId UNIQUEIDENTIFIER = NULL
      , @unmatchedOnly     BIT = 0
      , @sortBy            VARCHAR (30) = 'collectedDate'
      , @sortDescending    BIT = 1
      , @pageNumber        INT = 1
      , @pageSize          INT = 50
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspListLabResults]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'analyteId=', @analyteId, N', pageNumber=', @pageNumber, N', pageSize=', @pageSize, N', sortBy=', @sortBy);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'result.view') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (result.view).', 1;
        END;

        IF @pageNumber < 1 OR @pageSize NOT BETWEEN 1 AND 500
        BEGIN
            ;THROW 50000, N'@pageNumber must be 1 or more and @pageSize between 1 and 500.', 1;
        END;

        IF @sortBy NOT IN ('collectedDate', 'valueCanonical', 'lastName', 'analyteCode', 'receivedUtc')
        BEGIN
            ;THROW 50000, N'@sortBy must be one of collectedDate, valueCanonical, lastName, analyteCode, receivedUtc.', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        SELECT r.labResultId, r.personId, p.lastName, p.firstName, p.dob, a.code AS analyteCode, s.specimen, r.collectedDate,
               r.receivedUtc, r.operator, r.valueCanonical, r.unitCanonical, r.sampleType, r.testMethod, r.population,
               r.resultStatus, r.resultRowStatus, r.isIndeterminate, lab.organizationName AS labName,
               COUNT (*) OVER () AS totalCount
          FROM dbo.labResult AS r
          JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId = r.analyteSpecimenId
          JOIN config.analyte AS a ON a.analyteId = s.analyteId
          LEFT JOIN dbo.person AS p ON p.personId = r.personId
          LEFT JOIN dbo.organization AS lab ON lab.organizationId = r.labOrganizationId
         WHERE r.isDeleted = 0
           AND (@analyteId IS NULL OR a.analyteId = @analyteId)
           AND (@collectedFrom IS NULL OR r.collectedDate >= @collectedFrom)
           AND (@collectedTo IS NULL OR r.collectedDate <= @collectedTo)
           AND (@population IS NULL OR r.population = @population)
           AND (@minValue IS NULL OR r.valueCanonical >= @minValue)
           AND (@labOrganizationId IS NULL OR r.labOrganizationId = @labOrganizationId)
           AND (@unmatchedOnly = 0 OR r.personId IS NULL)
         ORDER BY CASE WHEN @sortBy = 'collectedDate'  AND @sortDescending = 0 THEN r.collectedDate END ASC,
                  CASE WHEN @sortBy = 'collectedDate'  AND @sortDescending = 1 THEN r.collectedDate END DESC,
                  CASE WHEN @sortBy = 'valueCanonical' AND @sortDescending = 0 THEN r.valueCanonical END ASC,
                  CASE WHEN @sortBy = 'valueCanonical' AND @sortDescending = 1 THEN r.valueCanonical END DESC,
                  CASE WHEN @sortBy = 'lastName'       AND @sortDescending = 0 THEN p.lastName END ASC,
                  CASE WHEN @sortBy = 'lastName'       AND @sortDescending = 1 THEN p.lastName END DESC,
                  CASE WHEN @sortBy = 'analyteCode'    AND @sortDescending = 0 THEN a.code END ASC,
                  CASE WHEN @sortBy = 'analyteCode'    AND @sortDescending = 1 THEN a.code END DESC,
                  CASE WHEN @sortBy = 'receivedUtc'    AND @sortDescending = 0 THEN r.receivedUtc END ASC,
                  CASE WHEN @sortBy = 'receivedUtc'    AND @sortDescending = 1 THEN r.receivedUtc END DESC,
                  r.labResultId
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
    , @ObjectName  = N'uspListLabResults'
    , @Description = N'One page of the results line list, filtered and sorted on the server, with the total row count.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspListLabResults TO meritWebRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspListLabResults TO meritWorkerRole;
END;
GO
