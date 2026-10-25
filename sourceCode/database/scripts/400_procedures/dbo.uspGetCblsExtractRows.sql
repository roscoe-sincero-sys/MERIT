-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspGetCblsExtractRows
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Source rows for the CDC CBLS quarterly extract: every valid final blood lead result for a child collected in the period, with the child's surrogate id, demographics and reporting county. No names, street addresses, phone numbers or record numbers are returned.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission extract.run (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspGetCblsExtractRows ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T122
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspGetCblsExtractRows
      @periodStart DATE
      , @periodEnd   DATE
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspGetCblsExtractRows]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'periodStart=', @periodStart, N', periodEnd=', @periodEnd);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'extract.run') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (extract.run).', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        SELECT r.labResultId, r.personId, p.dob, p.sex, p.ethnicity,
               (SELECT STRING_AGG (COALESCE (lv.cblsCode, pr.raceCode), ',') FROM dbo.personRace AS pr
                  LEFT JOIN config.lookupValue AS lv ON lv.lookupType = 'race' AND lv.code = pr.raceCode
                 WHERE pr.personId = p.personId AND pr.isDeleted = 0) AS raceCodes,
               r.collectedDate, r.sampleType, r.testMethod, r.operator, r.valueCanonical, r.reasonForTest, r.payerType,
               r.ageAtCollectionMonths, COALESCE (res.countyFips, rep.countyFips) AS countyFips,
               COALESCE (res.zip5, rep.zip5) AS zip5, COALESCE (res.censusTract, rep.censusTract) AS censusTract,
               res.yearBuilt, lab.cliaNumber AS labClia, s.specimen
          FROM dbo.labResult AS r
          JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId = r.analyteSpecimenId AND s.specimen = 'wholeBlood'
          JOIN config.analyte AS a ON a.analyteId = s.analyteId AND a.code = 'PB'
          JOIN dbo.person AS p ON p.personId = r.personId
          LEFT JOIN dbo.organization AS lab ON lab.organizationId = r.labOrganizationId
          OUTER APPLY (SELECT TOP (1) l.countyFips, l.zip5, l.state, l.censusTract, l.yearBuilt, l.country
                         FROM dbo.personLocation AS pl
                         JOIN dbo.location AS l ON l.locationId = pl.locationId
                        WHERE pl.personId = r.personId AND pl.role = 'residence' AND pl.isPrimary = 1 AND pl.isDeleted = 0
                          AND pl.startDate <= r.collectedDate AND (pl.endDate IS NULL OR pl.endDate >= r.collectedDate)
                        ORDER BY pl.startDate DESC) AS res
          LEFT JOIN dbo.location AS rep ON rep.locationId = r.reportedLocationId
         WHERE r.isDeleted = 0 AND r.resultRowStatus = 'valid' AND r.resultStatus IN ('final', 'corrected')
           AND r.population IN ('child')
           AND r.collectedDate BETWEEN @periodStart AND @periodEnd
         ORDER BY r.personId, r.collectedDate;

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
    , @ObjectName  = N'uspGetCblsExtractRows'
    , @Description = N'Source rows for the CDC CBLS quarterly extract: every valid final blood lead result for a child collected in the period, with the child''s surrogate id, demographics and reporting county. No names, street addresses, phone numbers or record numbers are returned.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspGetCblsExtractRows TO meritWorkerRole;
END;
GO
