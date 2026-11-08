-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspVerifyLabReportingTimeline
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

uspVerifyLabReportingTimeline

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission result.edit (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspVerifyLabReportingTimeline ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		202-204
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspVerifyLabReportingTimeline
      @personId UNIQUEIDENTIFIER
      , @labResultId UNIQUEIDENTIFIER
      , @finalResultDate DATE = NULL
      , @evidence NVARCHAR (1000)
      , @eligibilityConfirmed BIT
      , @applies BIT = 1
      , @sameDayChecked BIT = 0
      , @lateConfirmed BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspVerifyLabReportingTimeline]')
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
    SET @KeyParameters = N'patient care metadata; no text or file content';

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

        IF (@lateConfirmed=1 AND @sameDayChecked=0) OR @eligibilityConfirmed <> 1 OR (@applies=1 AND (@finalResultDate IS NULL OR @finalResultDate > auth.udfSessionToday ())) OR NULLIF(TRIM(@evidence),N'') IS NULL
        BEGIN
            ;THROW 51422, N'Verify reporting eligibility, an actual final-result date and the original source reference.', 1;
        END;
        UPDATE dbo.labResult SET reportingFinalDateVerified=@finalResultDate,reportingEligibilityVerified=@applies,
         reportingPolicyExcluded=CASE WHEN @applies=1 THEN 0 ELSE 1 END,reportingSameDayChecked=@sameDayChecked,reportingLateConfirmed=@lateConfirmed,
         reportingVerifiedByUserId=auth.udfSessionUserId (),reportingVerifiedUtc=SYSUTCDATETIME ()
         WHERE labResultId=@labResultId AND personId=@personId AND isDeleted=0 AND (@applies=0 OR (collectedDate<=@finalResultDate AND @finalResultDate<=CAST(receivedUtc AT TIME ZONE 'UTC' AT TIME ZONE COALESCE(CAST(SESSION_CONTEXT(N'timeZone') AS VARCHAR(100)),'Eastern Standard Time') AS DATE)))
         AND EXISTS (SELECT 1 FROM dbo.person WHERE personId=@personId AND isDeleted=0);
        IF @@ROWCOUNT <> 1
        BEGIN
            ;THROW 51404, N'The result is unavailable or its final date precedes collection.', 1;
        END;
        UPDATE dbo.workTask SET taskStatus=CASE WHEN @applies=1 THEN 'done' ELSE 'dismissed' END,completedUtc=SYSUTCDATETIME (),completedByUserId=auth.udfSessionUserId (),resolutionNote=@evidence
         WHERE labResultId=@labResultId AND queueCode='labReportingReview' AND taskStatus='open' AND isDeleted=0;
        INSERT dbo.personActivity (personActivityId,rootTenantId,owningTenantId,personId,labResultId,activityType,activityDate,audience,channel,outcome,note,workerUserId)
        SELECT NEWID (),rootTenantId,owningTenantId,personId,labResultId,'note',auth.udfSessionToday (),'internal','internal','completed',
         CONCAT(N'Laboratory reporting eligibility/timeline reviewed; applies=',@applies,N'; final date=',CONVERT(VARCHAR(10),@finalResultDate,23),N'. ',@evidence),auth.udfSessionUserId ()
        FROM dbo.labResult WHERE labResultId=@labResultId AND personId=@personId;
        IF @applies=0 UPDATE dbo.workTask SET resolutionNote=CONCAT(N'Later review: reporting policy not applicable. ',@evidence)
         WHERE labResultId=@labResultId AND queueCode='receiptFinding' AND title=N'Late laboratory report detected' AND isDeleted=0;
        EXEC dbo.uspReconcileReceiptQuality @labResultId=@labResultId,@recordReview=1;

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
    , @ObjectName  = N'uspVerifyLabReportingTimeline'
    , @Description = N'uspVerifyLabReportingTimeline';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspVerifyLabReportingTimeline TO meritWebRole;
END;
GO
