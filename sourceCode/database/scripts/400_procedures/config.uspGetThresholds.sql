-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   config.uspGetThresholds
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Threshold rows of the caller's root tenant. With @asOfDate, only active rows in force on that collection date (what classification compares against); without it, every row for the configuration screen.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec config.uspGetThresholds ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T050
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE config.uspGetThresholds
      @analyteSpecimenId UNIQUEIDENTIFIER = NULL
      , @asOfDate          DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[config].[uspGetThresholds]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'analyteSpecimenId=', @analyteSpecimenId, N', asOfDate=', @asOfDate);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        SELECT th.thresholdId, th.analyteSpecimenId, a.code AS analyteCode, a.displayName AS analyteName, s.specimen,
               th.population, th.ageMinMonths, th.ageMaxMonths, th.speciation, th.sampleType, th.thresholdType,
               th.actionCodeId, ac.code AS actionCode, ac.displayName AS actionName, ac.queueCode, th.dueIntervalDays,
               th.lowerValue, th.upperValue, th.unit, th.comparator, th.effectiveFrom, th.effectiveTo, th.authority, th.isActive,
               th.rowVersion
          FROM config.threshold       AS th
          JOIN config.analyteSpecimen AS s  ON s.analyteSpecimenId = th.analyteSpecimenId
          JOIN config.analyte         AS a  ON a.analyteId = s.analyteId
          LEFT JOIN config.actionCode AS ac ON ac.actionCodeId = th.actionCodeId
         WHERE th.isDeleted = 0
           AND (@analyteSpecimenId IS NULL OR th.analyteSpecimenId = @analyteSpecimenId)
           AND (@asOfDate IS NULL OR (th.isActive = 1 AND th.effectiveFrom <= @asOfDate
                                      AND (th.effectiveTo IS NULL OR th.effectiveTo >= @asOfDate)))
         ORDER BY a.sortOrder, s.specimen, th.population, th.thresholdType, th.lowerValue;

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
      @SchemaName  = N'config'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspGetThresholds'
    , @Description = N'Threshold rows of the caller''s root tenant. With @asOfDate, only active rows in force on that collection date (what classification compares against); without it, every row for the configuration screen.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON config.uspGetThresholds TO meritWebRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON config.uspGetThresholds TO meritWorkerRole;
END;
GO
