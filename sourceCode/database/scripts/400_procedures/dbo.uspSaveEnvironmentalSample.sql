-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspSaveEnvironmentalSample
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Adds or removes an environmental sample. Exceedance is computed here from the environmental standard in force on the sample date for the sample type and surface, in the same unit -- it is never typed.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission investigation.edit (auth.udfHasPermission); refuses with 51403 otherwise.

Returns exceedsStandard (NULL when no standard applies).

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspSaveEnvironmentalSample ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T081
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspSaveEnvironmentalSample
      @environmentalSampleId UNIQUEIDENTIFIER
      , @investigationId       UNIQUEIDENTIFIER
      , @sampleType            VARCHAR (30)
      , @roomOrComponent       NVARCHAR (120) = NULL
      , @surface               NVARCHAR (60) = NULL
      , @sampleValue           DECIMAL (18, 6)
      , @unit                  NVARCHAR (20)
      , @sampleDate            DATE
      , @notes                 NVARCHAR (1000) = NULL
      , @isDeleted             BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspSaveEnvironmentalSample]')
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
    SET @KeyParameters = CONCAT (N'environmentalSampleId=', @environmentalSampleId, N', investigationId=', @investigationId);

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        IF auth.udfHasPermission (N'investigation.edit') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (investigation.edit).', 1;
        END;

        IF NOT EXISTS (SELECT 1 FROM dbo.investigation WHERE investigationId = @investigationId AND isDeleted = 0)
        BEGIN
            ;THROW 51404, N'The investigation was not found, or you do not have access to it.', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @standardId UNIQUEIDENTIFIER, @standardValue DECIMAL (18, 6);

        SELECT TOP (1) @standardId = environmentalStandardId, @standardValue = standardValue
          FROM config.environmentalStandard
         WHERE isDeleted = 0 AND sampleType = @sampleType AND unit = @unit
           AND (surface IS NULL OR surface = @surface)
           AND effectiveFrom <= @sampleDate AND (effectiveTo IS NULL OR effectiveTo >= @sampleDate)
         ORDER BY CASE WHEN surface = @surface THEN 0 ELSE 1 END, effectiveFrom DESC;

        IF EXISTS (SELECT 1 FROM dbo.environmentalSample WHERE environmentalSampleId = @environmentalSampleId)
        BEGIN
            UPDATE dbo.environmentalSample SET isDeleted = @isDeleted
             WHERE environmentalSampleId = @environmentalSampleId AND investigationId = @investigationId AND isDeleted = 0;
        END
        ELSE
        BEGIN
            INSERT dbo.environmentalSample (environmentalSampleId, rootTenantId, owningTenantId, investigationId, sampleType,
                                            roomOrComponent, surface, sampleValue, unit, sampleDate, environmentalStandardId,
                                            exceedsStandard, notes)
            SELECT @environmentalSampleId, rootTenantId, owningTenantId, @investigationId, @sampleType, @roomOrComponent,
                   @surface, @sampleValue, @unit, @sampleDate, @standardId,
                   CASE WHEN @standardId IS NULL THEN NULL WHEN @sampleValue >= @standardValue THEN 1 ELSE 0 END, @notes
              FROM dbo.investigation WHERE investigationId = @investigationId;
        END;

        SELECT CASE WHEN @standardId IS NULL THEN NULL WHEN @sampleValue >= @standardValue THEN CAST (1 AS BIT)
                    ELSE CAST (0 AS BIT) END AS exceedsStandard;

        SET @Comments = N'Sample saved.';

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
      @SchemaName  = N'dbo'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspSaveEnvironmentalSample'
    , @Description = N'Adds or removes an environmental sample. Exceedance is computed here from the environmental standard in force on the sample date for the sample type and surface, in the same unit -- it is never typed.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspSaveEnvironmentalSample TO meritWebRole;
END;
GO
