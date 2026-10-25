-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspGetClassificationContext
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Everything case classification needs about a person for one analyte: DOB and sex, pregnancy episodes, the open case, and the earlier valid results. Population and classification are computed in Merit.Domain from these rows.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission result.view (auth.udfHasPermission); refuses with 51403 otherwise.

Four result sets: person, pregnancy episodes, open case (zero or one row), prior results. Preliminary, superseded, duplicate and entered-in-error results are excluded because they never count toward classification.

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspGetClassificationContext ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T044
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspGetClassificationContext
      @personId  UNIQUEIDENTIFIER
      , @analyteId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspGetClassificationContext]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'personId=', @personId, N', analyteId=', @analyteId);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'result.view') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (result.view).', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        SELECT personId, dob, sex, deceasedDate, mergedIntoPersonId FROM dbo.person WHERE personId = @personId AND isDeleted = 0;

        SELECT pregnancyEpisodeId, startDate, expectedEndDate, actualEndDate
          FROM dbo.pregnancyEpisode WHERE personId = @personId AND isDeleted = 0;

        SELECT c.exposureCaseId, c.caseNumber, c.classification, c.computedClassification, c.overrideReason, c.population,
               c.caseStatus, c.highestLabResultId, h.valueCanonical AS highestValue, c.openedDate, c.owningTenantId
          FROM dbo.exposureCase AS c
          LEFT JOIN dbo.labResult AS h ON h.labResultId = c.highestLabResultId
         WHERE c.personId = @personId AND c.analyteId = @analyteId AND c.isDeleted = 0 AND c.caseStatus <> 'closed';

        SELECT r.labResultId, r.analyteSpecimenId, s.specimen, r.collectedDate, r.sampleType, r.testMethod, r.speciation,
               r.operator, r.valueCanonical, r.population, r.isIndeterminate
          FROM dbo.labResult AS r
          JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId = r.analyteSpecimenId
         WHERE r.personId = @personId AND s.analyteId = @analyteId AND r.isDeleted = 0
           AND r.resultRowStatus = 'valid' AND r.resultStatus IN ('final', 'corrected')
         ORDER BY r.collectedDate;

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
    , @ObjectName  = N'uspGetClassificationContext'
    , @Description = N'Everything case classification needs about a person for one analyte: DOB and sex, pregnancy episodes, the open case, and the earlier valid results. Population and classification are computed in Merit.Domain from these rows.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspGetClassificationContext TO meritWebRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspGetClassificationContext TO meritWorkerRole;
END;
GO
