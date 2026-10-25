-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspGetAblesExtractRows
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Source rows for the NIOSH ABLES annual extract: each adult's highest valid final blood lead result in the year, with demographics, reporting county, the exposure category and employment of their case, the exposure source, and whether they had a result of 10 ug/dL or more the year before (registry status).

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

exec dbo.uspGetAblesExtractRows ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T122
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspGetAblesExtractRows
      @reportYear INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspGetAblesExtractRows]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'reportYear=', @reportYear);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'extract.run') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (extract.run).', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @from DATE = DATEFROMPARTS (@reportYear, 1, 1), @to DATE = DATEFROMPARTS (@reportYear, 12, 31);

        ;WITH ranked AS
        (
            SELECT r.*, ROW_NUMBER () OVER (PARTITION BY r.personId ORDER BY r.valueCanonical DESC, r.collectedDate) AS rn
              FROM dbo.labResult AS r
              JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId = r.analyteSpecimenId AND s.specimen = 'wholeBlood'
              JOIN config.analyte AS a ON a.analyteId = s.analyteId AND a.code = 'PB'
             WHERE r.isDeleted = 0 AND r.resultRowStatus = 'valid' AND r.resultStatus IN ('final', 'corrected')
               AND r.personId IS NOT NULL AND r.collectedDate BETWEEN @from AND @to
               AND DATEDIFF (YEAR, (SELECT dob FROM dbo.person WHERE personId = r.personId), r.collectedDate) >= 16
        )
        SELECT r.labResultId, r.personId, p.dob, p.sex, p.ethnicity,
               (SELECT STRING_AGG (COALESCE (lv.ablesCode, pr.raceCode), ',') FROM dbo.personRace AS pr
                  LEFT JOIN config.lookupValue AS lv ON lv.lookupType = 'race' AND lv.code = pr.raceCode
                 WHERE pr.personId = p.personId AND pr.isDeleted = 0) AS raceCodes,
               r.collectedDate, r.valueCanonical, r.ageAtCollectionMonths,
               COALESCE (res.state, rep.state) AS stateRes, COALESCE (res.countyFips, rep.countyFips) AS countyRes,
               COALESCE (res.country, rep.country) AS countryRes,
               c.exposureCategory,
               COALESCE (e.naicsOverride, o.naicsCode) AS naics, e.occupationCode,
               wl.state AS stateExp, wl.countyFips AS countyExp,
               src.sourceDescription AS processText, srcType.ablesCode AS processCode,
               CAST (CASE WHEN EXISTS (SELECT 1 FROM dbo.labResult AS pr2
                                         JOIN config.analyteSpecimen AS s2 ON s2.analyteSpecimenId = pr2.analyteSpecimenId
                                         JOIN config.analyte AS a2 ON a2.analyteId = s2.analyteId AND a2.code = 'PB'
                                        WHERE pr2.personId = r.personId AND pr2.isDeleted = 0 AND pr2.resultRowStatus = 'valid'
                                          AND YEAR (pr2.collectedDate) = @reportYear - 1 AND pr2.valueCanonical >= 10)
                          THEN 1 ELSE 0 END AS BIT) AS priorYearElevated,
               CAST (CASE WHEN EXISTS (SELECT 1 FROM dbo.labResult AS pr3
                                        WHERE pr3.personId = r.personId AND pr3.isDeleted = 0 AND YEAR (pr3.collectedDate) < @reportYear)
                          THEN 1 ELSE 0 END AS BIT) AS inRegistryBefore
          FROM ranked AS r
          JOIN dbo.person AS p ON p.personId = r.personId
          OUTER APPLY (SELECT TOP (1) l.countyFips, l.zip5, l.state, l.censusTract, l.yearBuilt, l.country
                         FROM dbo.personLocation AS pl
                         JOIN dbo.location AS l ON l.locationId = pl.locationId
                        WHERE pl.personId = r.personId AND pl.role = 'residence' AND pl.isPrimary = 1 AND pl.isDeleted = 0
                          AND pl.startDate <= r.collectedDate AND (pl.endDate IS NULL OR pl.endDate >= r.collectedDate)
                        ORDER BY pl.startDate DESC) AS res
          LEFT JOIN dbo.location AS rep ON rep.locationId = r.reportedLocationId
          OUTER APPLY (SELECT TOP (1) c.exposureCaseId, c.exposureCategory FROM dbo.exposureCase AS c
                        JOIN config.analyte AS a ON a.analyteId = c.analyteId AND a.code = 'PB'
                       WHERE c.personId = r.personId AND c.isDeleted = 0 ORDER BY c.openedDate DESC) AS c
          OUTER APPLY (SELECT TOP (1) e.* FROM dbo.employment AS e
                        LEFT JOIN dbo.investigationCase AS ic ON ic.employmentId = e.employmentId AND ic.exposureCaseId = c.exposureCaseId
                       WHERE e.personId = r.personId AND e.isDeleted = 0
                         AND (e.startDate IS NULL OR e.startDate <= r.collectedDate)
                         AND (e.endDate IS NULL OR e.endDate >= DATEADD (DAY, -90, r.collectedDate))
                       ORDER BY CASE WHEN ic.investigationCaseId IS NOT NULL THEN 0 ELSE 1 END,
                                CASE WHEN e.metalTasks IS NOT NULL THEN 0 ELSE 1 END, e.startDate DESC) AS e
          LEFT JOIN dbo.organization AS o ON o.organizationId = e.organizationId
          LEFT JOIN dbo.location AS wl ON wl.locationId = e.worksiteLocationId
          OUTER APPLY (SELECT TOP (1) es.sourceDescription, es.sourceType FROM dbo.exposureSource AS es
                        WHERE es.personId = r.personId AND es.isDeleted = 0 ORDER BY es.isConfirmed DESC, es.startDate DESC) AS src
          LEFT JOIN config.lookupValue AS srcType ON srcType.lookupType = 'exposureSourceType' AND srcType.code = src.sourceType
         WHERE r.rn = 1
         ORDER BY r.personId;

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
    , @ObjectName  = N'uspGetAblesExtractRows'
    , @Description = N'Source rows for the NIOSH ABLES annual extract: each adult''s highest valid final blood lead result in the year, with demographics, reporting county, the exposure category and employment of their case, the exposure source, and whether they had a result of 10 ug/dL or more the year before (registry status).';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspGetAblesExtractRows TO meritWorkerRole;
END;
GO
