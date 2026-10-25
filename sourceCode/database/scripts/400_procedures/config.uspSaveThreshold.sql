-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   config.uspSaveThreshold
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Creates or updates a threshold row. Rows for the same analyte specimen, population, age band, speciation, sample type, type and action code may not overlap in effective dates, so tiered action levels run side by side while a changed level is a new effective-dated row.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission config.edit (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec config.uspSaveThreshold ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T050
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE config.uspSaveThreshold
      @thresholdId       UNIQUEIDENTIFIER
      , @analyteSpecimenId UNIQUEIDENTIFIER
      , @population        VARCHAR (10)
      , @ageMinMonths      INT = NULL
      , @ageMaxMonths      INT = NULL
      , @speciation        VARCHAR (20) = NULL
      , @sampleType        VARCHAR (20) = NULL
      , @thresholdType     VARCHAR (12)
      , @actionCodeId      UNIQUEIDENTIFIER = NULL
      , @dueIntervalDays   INT = NULL
      , @lowerValue        DECIMAL (18, 6)
      , @upperValue        DECIMAL (18, 6) = NULL
      , @unit              NVARCHAR (20)
      , @comparator        VARCHAR (2) = '>='
      , @effectiveFrom     DATE
      , @effectiveTo       DATE = NULL
      , @authority         NVARCHAR (300) = NULL
      , @isActive          BIT = 1
      , @rowVersion        BINARY (8) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[config].[uspSaveThreshold]')
          , @StartTimeUtc   DATETIME2 (3)  = SYSUTCDATETIME ()
          , @EndTimeUtc     DATETIME2 (3)  = NULL
          , @ExecutionId    BIGINT         = NULL
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @Comments       NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never a credential, a payload, or patient data.
    SET @KeyParameters = CONCAT (N'thresholdId=', @thresholdId);

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        IF auth.udfHasPermission (N'config.edit') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (config.edit).', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();

        IF @isActive = 1 AND EXISTS (
            SELECT 1 FROM config.threshold AS t WITH (UPDLOCK, HOLDLOCK)
             WHERE t.isDeleted = 0 AND t.isActive = 1 AND t.thresholdId <> @thresholdId
               AND t.analyteSpecimenId = @analyteSpecimenId AND t.population = @population
               AND t.thresholdType = @thresholdType
               AND t.ageMinMonths IS NOT DISTINCT FROM @ageMinMonths AND t.ageMaxMonths IS NOT DISTINCT FROM @ageMaxMonths
               AND t.speciation IS NOT DISTINCT FROM @speciation AND t.sampleType IS NOT DISTINCT FROM @sampleType
               AND t.actionCodeId IS NOT DISTINCT FROM @actionCodeId
               AND t.lowerValue = @lowerValue
               AND t.effectiveFrom <= COALESCE (@effectiveTo, '9999-12-31')
               AND COALESCE (t.effectiveTo, '9999-12-31') >= @effectiveFrom)
        BEGIN
            ;THROW 51422, N'An active row with the same analyte, population, band, type and action already covers part of those dates. End that row first.', 1;
        END;

        IF EXISTS (SELECT 1 FROM config.threshold WHERE thresholdId = @thresholdId)
        BEGIN
            UPDATE config.threshold
               SET population = @population, ageMinMonths = @ageMinMonths, ageMaxMonths = @ageMaxMonths,
                   speciation = @speciation, sampleType = @sampleType, thresholdType = @thresholdType,
                   actionCodeId = @actionCodeId, dueIntervalDays = @dueIntervalDays, lowerValue = @lowerValue,
                   upperValue = @upperValue, unit = @unit, comparator = @comparator, effectiveFrom = @effectiveFrom,
                   effectiveTo = @effectiveTo, authority = @authority, isActive = @isActive
             WHERE thresholdId = @thresholdId AND isDeleted = 0 AND rowVersion = @rowVersion;
            IF @@ROWCOUNT = 0
            BEGIN
                ;THROW 51409, N'This record was changed by someone else, or is no longer available. Reload it and try again.', 1;
            END;
        END
        ELSE
        BEGIN
            INSERT config.threshold (thresholdId, rootTenantId, analyteSpecimenId, population, ageMinMonths, ageMaxMonths,
                                     speciation, sampleType, thresholdType, actionCodeId, dueIntervalDays, lowerValue, upperValue,
                                     unit, comparator, effectiveFrom, effectiveTo, authority, isActive)
            VALUES (@thresholdId, @root, @analyteSpecimenId, @population, @ageMinMonths, @ageMaxMonths, @speciation, @sampleType,
                    @thresholdType, @actionCodeId, @dueIntervalDays, @lowerValue, @upperValue, @unit, @comparator, @effectiveFrom,
                    @effectiveTo, @authority, @isActive);
        END;

        SET @Comments = N'Threshold saved.';

        -- =========================================================================================
        -- ===== End of the procedure's own work. ===================================================
        -- =========================================================================================

        IF @@TRANCOUNT > 0
        BEGIN
            COMMIT TRANSACTION;
        END;

        -- Completion. Deliberately after the COMMIT.
        SET @EndTimeUtc = SYSUTCDATETIME ();

        IF @ExecutionId IS NOT NULL
        BEGIN
            UPDATE logs.ExecutionLog
               SET EndDateUtc          = @EndTimeUtc
                 , ElapsedMilliseconds = CAST (LEAST (DATEDIFF_BIG (MILLISECOND, @StartTimeUtc, @EndTimeUtc)
                                                    , CAST (2147483647 AS BIGINT)) AS INT)
                 , Successful          = 1
                 , Comments            = @Comments
                 , modifiedBy          = ORIGINAL_LOGIN ()
                 , modifiedDateUtc     = @EndTimeUtc
             WHERE ExecutionLogId = @ExecutionId;
        END;

    END TRY
    BEGIN CATCH

        SELECT @ErrorNumber = ERROR_NUMBER ()
             , @ErrorProc   = ERROR_PROCEDURE ()
             , @ErrorLine   = ERROR_LINE ()
             , @ErrorMsg    = ERROR_MESSAGE ()
                            + N' (error '  + CAST (ERROR_NUMBER () AS NVARCHAR (11))
                            + N', line '   + CAST (ERROR_LINE ()   AS NVARCHAR (11)) + N')';

        IF XACT_STATE () <> 0
        BEGIN
            ROLLBACK TRANSACTION;
        END;

        BEGIN TRY
            IF @ExecutionId IS NULL
               OR NOT EXISTS (SELECT 1
                                FROM logs.ExecutionLog
                               WHERE ExecutionLogId = @ExecutionId)
            BEGIN
                EXEC logs.uspStartExecutionLogging
                      @ProcedureName          = @ProcName
                    , @KeyParameters          = @KeyParameters
                    , @StartDateUtc           = @StartTimeUtc
                    , @ReCreatedAfterRollback = 1
                    , @ExecutionLogId         = @ExecutionId OUTPUT;
            END;
        END TRY
        BEGIN CATCH
            SET @ExecutionId = NULL;
        END CATCH;

        EXEC logs.uspRecordExecutionError
              @ProcedureName   = @ProcName
            , @KeyParameters   = @KeyParameters
            , @ExecutionLogId  = @ExecutionId
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
    , @ObjectName  = N'uspSaveThreshold'
    , @Description = N'Creates or updates a threshold row. Rows for the same analyte specimen, population, age band, speciation, sample type, type and action code may not overlap in effective dates, so tiered action levels run side by side while a changed level is a new effective-dated row.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON config.uspSaveThreshold TO meritWebRole;
END;
GO
