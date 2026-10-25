-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspSaveLabResult
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Inserts a lab result with its normalized values. A corrected or cancelled result names the result it supersedes, which is marked superseded. Manual entry with no intake batch gets a manual batch of its own, so every result points to where it came from.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission result.edit (auth.udfHasPermission); refuses with 51403 otherwise.

Value, unit and snapshot are payload and are never written to @KeyParameters. Returns labResultId and intakeBatchId.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspSaveLabResult ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T044
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspSaveLabResult
      @labResultId          UNIQUEIDENTIFIER
      , @personId             UNIQUEIDENTIFIER = NULL
      , @analyteSpecimenId    UNIQUEIDENTIFIER
      , @collectedDate        DATE
      , @analyzedDate         DATE = NULL
      , @reportedDate         DATE = NULL
      , @receivedUtc          DATETIME2 (3)
      , @resultStatus         VARCHAR (12)
      , @supersedesLabResultId UNIQUEIDENTIFIER = NULL
      , @valueReported        DECIMAL (18, 6) = NULL
      , @valueText            NVARCHAR (60) = NULL
      , @operator             VARCHAR (1) = '='
      , @unitReported         NVARCHAR (20)
      , @valueCanonical       DECIMAL (18, 6) = NULL
      , @unitCanonical        NVARCHAR (20)
      , @sampleType           VARCHAR (20) = NULL
      , @testMethod           VARCHAR (30) = NULL
      , @deviceModel          NVARCHAR (60) = NULL
      , @deviceSerial         NVARCHAR (60) = NULL
      , @kitLot               NVARCHAR (60) = NULL
      , @speciation           VARCHAR (20) = NULL
      , @creatinineValue      DECIMAL (18, 6) = NULL
      , @valueCreatinineAdj   DECIMAL (18, 6) = NULL
      , @reasonForTest        VARCHAR (30) = NULL
      , @payerType            VARCHAR (20) = NULL
      , @labOrganizationId    UNIQUEIDENTIFIER
      , @orderingProviderId   UNIQUEIDENTIFIER = NULL
      , @orderingFacilityId   UNIQUEIDENTIFIER = NULL
      , @accessionNumber      NVARCHAR (60) = NULL
      , @loincReported        VARCHAR (10) = NULL
      , @reportedSnapshotJson NVARCHAR (MAX)
      , @reportedLocationId   UNIQUEIDENTIFIER = NULL
      , @ageAtCollectionMonths INT = NULL
      , @population           VARCHAR (10) = 'unknown'
      , @isIndeterminate      BIT = 0
      , @resultRowStatus      VARCHAR (20) = 'valid'
      , @intakeBatchId        UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspSaveLabResult]')
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
    SET @KeyParameters = CONCAT (N'labResultId=', @labResultId, N', personId=', @personId, N', intakeBatchId=', @intakeBatchId);

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        IF auth.udfHasPermission (N'result.edit') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (result.edit).', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();
        DECLARE @acting UNIQUEIDENTIFIER = auth.udfSessionActingTenantId ();
        DECLARE @dob DATE = (SELECT dob FROM dbo.person WHERE personId = @personId);

        IF @dob IS NOT NULL AND @collectedDate < @dob
        BEGIN
            ;THROW 51422, N'The collection date is before the date of birth.', 1;
        END;

        IF @collectedDate > CAST (@receivedUtc AS DATE) OR (@reportedDate IS NOT NULL AND @collectedDate > @reportedDate)
        BEGIN
            ;THROW 51422, N'The collection date must be on or before the reported and received dates.', 1;
        END;

        IF @analyzedDate IS NOT NULL AND (@analyzedDate < @collectedDate OR (@reportedDate IS NOT NULL AND @analyzedDate > @reportedDate))
        BEGIN
            ;THROW 51422, N'The analyzed date must fall between the collected and reported dates.', 1;
        END;

        IF @intakeBatchId IS NULL
        BEGIN
            SET @intakeBatchId = CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER);
            INSERT dbo.intakeBatch (intakeBatchId, rootTenantId, owningTenantId, senderOrganizationId, sourceChannel, fileName,
                                    receivedUtc, batchStatus, receivedCount, loadedCount, submittedByUserId)
            VALUES (@intakeBatchId, @root, @acting, @labOrganizationId, 'manual', N'Manual entry', @receivedUtc, 'loaded', 1, 1,
                    auth.udfSessionUserId ());
        END;

        IF @supersedesLabResultId IS NOT NULL
        BEGIN
            UPDATE dbo.labResult SET resultRowStatus = 'superseded'
             WHERE labResultId = @supersedesLabResultId AND resultRowStatus = 'valid' AND isDeleted = 0;
        END;

        INSERT dbo.labResult (labResultId, rootTenantId, owningTenantId, personId, analyteSpecimenId, collectedDate, analyzedDate,
                              reportedDate, receivedUtc, resultStatus, supersedesLabResultId, valueReported, valueText, operator,
                              unitReported, valueCanonical, unitCanonical, sampleType, testMethod, deviceModel, deviceSerial, kitLot,
                              speciation, creatinineValue, valueCreatinineAdj, reasonForTest, payerType, labOrganizationId,
                              orderingProviderId, orderingFacilityId, accessionNumber, loincReported, reportedSnapshotJson,
                              reportedLocationId, ageAtCollectionMonths, population, isIndeterminate, resultRowStatus,
                              intakeBatchId)
        VALUES (@labResultId, @root, @root, @personId, @analyteSpecimenId, @collectedDate, @analyzedDate, @reportedDate,
                @receivedUtc, @resultStatus, @supersedesLabResultId, @valueReported, @valueText, @operator, @unitReported,
                @valueCanonical, @unitCanonical, @sampleType, @testMethod, @deviceModel, @deviceSerial, @kitLot, @speciation,
                @creatinineValue, @valueCreatinineAdj, @reasonForTest, @payerType, @labOrganizationId, @orderingProviderId,
                @orderingFacilityId, @accessionNumber, @loincReported, @reportedSnapshotJson, @reportedLocationId,
                @ageAtCollectionMonths, @population, @isIndeterminate, @resultRowStatus, @intakeBatchId);

        SELECT @labResultId AS labResultId, @intakeBatchId AS intakeBatchId;

        SET @Comments = N'Lab result saved.';

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
    , @ObjectName  = N'uspSaveLabResult'
    , @Description = N'Inserts a lab result with its normalized values. A corrected or cancelled result names the result it supersedes, which is marked superseded. Manual entry with no intake batch gets a manual batch of its own, so every result points to where it came from.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspSaveLabResult TO meritWebRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspSaveLabResult TO meritWorkerRole;
END;
GO
