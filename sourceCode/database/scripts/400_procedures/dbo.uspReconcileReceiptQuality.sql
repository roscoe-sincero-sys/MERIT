-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspReconcileReceiptQuality
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Idempotent receipt-quality detection. Completed detection tasks use the actual detection instant; missing-information receipt remains pending for explicit evidence.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspReconcileReceiptQuality ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		202-204
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspReconcileReceiptQuality
      @labResultId UNIQUEIDENTIFIER = NULL
      , @stagingRowId UNIQUEIDENTIFIER = NULL
      , @recordReview BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspReconcileReceiptQuality]')
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

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        IF auth.udfHasPermission (N'result.edit') = 0 AND auth.udfHasPermission (N'intake.upload') = 0 AND auth.udfHasPermission (N'config.edit') = 0
        BEGIN
            ;THROW 51403, N'Receipt quality evaluation requires clinical intake or policy permission.', 1;
        END;
        IF @stagingRowId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.stagingRow WHERE stagingRowId=@stagingRowId AND isDeleted=0
            AND (@labResultId IS NULL OR labResultId=@labResultId))
        BEGIN
            ;THROW 51404, N'The staging row is unavailable or does not belong to this result.', 1;
        END;
        DECLARE @root UNIQUEIDENTIFIER, @owner UNIQUEIDENTIFIER, @person UNIQUEIDENTIFIER, @received DATETIME2 (3),
         @parsed NVARCHAR (MAX), @patch NVARCHAR (MAX), @specimen UNIQUEIDENTIFIER, @value DECIMAL (18,6), @age INT,
         @collected DATE, @final DATE, @state VARCHAR (2), @verified BIT = 0, @excluded BIT = 0, @population VARCHAR (10), @operator VARCHAR (1), @sameDayChecked BIT=0, @lateConfirmed BIT=0, @verifiedPolicy UNIQUEIDENTIFIER;
        IF @stagingRowId IS NOT NULL
        BEGIN
         SELECT @labResultId = COALESCE (@labResultId, s.labResultId), @root = s.rootTenantId, @owner = s.owningTenantId,
           @parsed = s.parsedJson, @patch = s.correctedJson, @received = b.receivedUtc
         FROM dbo.stagingRow AS s JOIN dbo.intakeBatch AS b ON b.intakeBatchId = s.intakeBatchId
         WHERE s.stagingRowId = @stagingRowId AND s.isDeleted = 0 AND (@labResultId IS NULL OR s.labResultId = @labResultId);
        END;
        IF @labResultId IS NOT NULL
        BEGIN
         SET @root = NULL;
         SELECT @root = r.rootTenantId, @owner = r.owningTenantId, @person = r.personId, @received = r.receivedUtc,
           @specimen = r.analyteSpecimenId, @value = r.valueCanonical, @operator=r.operator,
           @collected = r.collectedDate, @age = r.ageAtCollectionMonths, @population=r.population, @verifiedPolicy=r.reportingVerifiedPolicyId,
           @excluded=CASE WHEN r.reportingPolicyExcluded=1 AND r.reportingVerifiedByUserId IS NOT NULL THEN 1 ELSE 0 END,
           @sameDayChecked=CASE WHEN r.reportingSameDayChecked=1 AND r.reportingVerifiedByUserId IS NOT NULL THEN 1 ELSE 0 END,
           @lateConfirmed=CASE WHEN r.reportingLateConfirmed=1 AND r.reportingVerifiedByUserId IS NOT NULL THEN 1 ELSE 0 END,
           @parsed = CASE WHEN @stagingRowId IS NULL THEN r.reportedSnapshotJson ELSE @parsed END,
           @final = COALESCE (r.reportingFinalDateVerified,
                             CASE WHEN EXISTS (SELECT 1 FROM OPENJSON(r.reportedSnapshotJson) WHERE [key]='finalResultDateCanonical') THEN TRY_CONVERT(DATE,JSON_VALUE(r.reportedSnapshotJson,'$.finalResultDateCanonical')) ELSE TRY_CONVERT (DATE, NULLIF(TRIM(JSON_VALUE (r.reportedSnapshotJson, '$.finalResultDate')),N'')) END),
           @verified = CASE WHEN r.reportingEligibilityVerified=1 AND r.reportingVerifiedByUserId IS NOT NULL THEN 1 ELSE 0 END,
           @state = l.state
         FROM dbo.labResult AS r LEFT JOIN dbo.location AS l ON l.locationId = r.reportedLocationId
         WHERE r.labResultId = @labResultId AND r.isDeleted = 0 AND r.resultRowStatus = 'valid' AND r.resultStatus IN ('final','corrected');
        END;
        IF @root IS NULL
        BEGIN
            ;THROW 51404, N'The receipt source was not found or is outside your access.', 1;
        END;
        IF @specimen IS NULL SELECT @specimen = analyteSpecimenId FROM config.loincCode
         WHERE loincCode = config.udfReceiptField (@parsed,@patch,'loinc') AND isDeleted = 0;
        SET @final = COALESCE (@final, TRY_CONVERT (DATE, config.udfReceiptField (@parsed,@patch,'finalResultDate')));
        SET @collected = COALESCE (@collected, TRY_CONVERT (DATE, config.udfReceiptField (@parsed,@patch,'collectedDate')));
        SET @value = COALESCE (@value, CASE WHEN config.udfReceiptField (@parsed,@patch,'unit') IN ('ug/dL','mcg/dL')
         THEN TRY_CONVERT (DECIMAL (18,6), config.udfReceiptField (@parsed,@patch,'value')) END);
        SET @state = COALESCE (@state, config.udfReceiptField (@parsed,@patch,'state'));
        DECLARE @rule UNIQUEIDENTIFIER, @name NVARCHAR (120), @zone VARCHAR (100), @required NVARCHAR (1000), @mode VARCHAR (20),
         @due DATE, @receiptDate DATE, @minAge INT, @maxAge INT, @certain BIT = 0;
        SELECT TOP (1) @rule = n.notificationRuleId, @name = n.ruleName, @zone = n.timeZoneId, @required = n.requiredFieldsCsv,
         @mode = n.eligibilityMode, @minAge = n.minimumAgeMonths, @maxAge = n.maximumAgeMonths,
         @certain=CASE WHEN @operator='=' OR (@operator='>' AND @value>=n.minimumValue AND n.maximumValue IS NULL) OR (@operator='<' AND n.minimumValue=0 AND @value<=n.maximumValue) THEN 1 ELSE 0 END,
         @due = config.udfNotificationDueDate (@final,n.dueInDays,n.dayBasis,n.holidayDatesJson)
        FROM config.notificationRule AS n WHERE n.rulePurpose = 'labReporting' AND n.isActive = 1 AND n.isDeleted = 0
         AND n.rootTenantId = @root AND n.analyteSpecimenId = @specimen
         AND (n.population='all' OR n.population=@population OR @population IS NULL OR @population='unknown')
         AND (n.eligibilityMode <> 'mdReporting' OR @state IS NULL OR @state='MD' OR @verified=1)
         AND EXISTS (SELECT 1 FROM auth.tenantClosure WHERE ancestorTenantId = n.owningTenantId AND descendantTenantId = @owner AND isDeleted = 0)
         AND COALESCE (CASE WHEN @final>=@collected AND @final<=CAST(@received AT TIME ZONE 'UTC' AT TIME ZONE n.timeZoneId AS DATE) THEN @final END, CAST (@received AT TIME ZONE 'UTC' AT TIME ZONE n.timeZoneId AS DATE)) >= n.effectiveFrom
         AND (n.effectiveTo IS NULL OR COALESCE (CASE WHEN @final>=@collected AND @final<=CAST(@received AT TIME ZONE 'UTC' AT TIME ZONE n.timeZoneId AS DATE) THEN @final END, CAST (@received AT TIME ZONE 'UTC' AT TIME ZONE n.timeZoneId AS DATE)) <= n.effectiveTo)
         AND (@value IS NULL OR (@operator='=' AND @value>=n.minimumValue AND (n.maximumValue IS NULL OR @value<n.maximumValue)) OR (@operator='>' AND (n.maximumValue IS NULL OR @value<n.maximumValue)) OR (@operator='<' AND @value>n.minimumValue))
         AND (@age IS NULL OR ((n.minimumAgeMonths IS NULL OR @age >= n.minimumAgeMonths) AND (n.maximumAgeMonths IS NULL OR @age <= n.maximumAgeMonths)))
         ORDER BY config.udfNotificationDueDate (@final,n.dueInDays,n.dayBasis,n.holidayDatesJson), n.effectiveFrom DESC, n.notificationRuleId;
        SET @receiptDate = CAST (@received AT TIME ZONE 'UTC' AT TIME ZONE COALESCE (@zone,'Eastern Standard Time') AS DATE);
        DECLARE @lock INT, @resource NVARCHAR (100) = CONCAT (N'MERIT.receipt.',COALESCE (@labResultId,@stagingRowId));
        EXEC @lock = sys.sp_getapplock @Resource=@resource, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000;
        IF @lock < 0
        BEGIN
            ;THROW 51409, N'Receipt review is busy. Retry shortly.', 1;
        END;
        -- Preserve the original detected/completed dates when a previously unmatched held row becomes a patient result.
        UPDATE dbo.workTask SET personId = COALESCE (personId,@person), labResultId = COALESCE (labResultId,@labResultId)
         WHERE stagingRowId = @stagingRowId AND isDeleted = 0 AND queueCode IN ('receiptFinding','missingInformation','labReportingReview');
        IF @recordReview=1 AND @labResultId IS NOT NULL
        BEGIN
            UPDATE dbo.labResult SET reportingVerifiedPolicyId=@rule WHERE labResultId=@labResultId;
            SET @verifiedPolicy=@rule;
        END;
        IF @verifiedPolicy IS NULL OR @rule IS NULL OR @verifiedPolicy<>@rule
        BEGIN
            SET @verified=0; SET @excluded=0; SET @sameDayChecked=0; SET @lateConfirmed=0;
        END;
        IF @excluded=1 SET @required=NULL;
        DECLARE @missing NVARCHAR (1000);
        SELECT @missing = LEFT (STRING_AGG (CONVERT (NVARCHAR (MAX), f.fieldName), N', '),1000) FROM
         (SELECT DISTINCT TRIM ([value]) AS fieldName,0 AS knownInvalid FROM STRING_SPLIT (@required, ',')
          UNION SELECT h.fieldName,1 FROM dbo.stagingHoldReason AS h WHERE h.stagingRowId = @stagingRowId AND h.isDeleted = 0 AND h.isResolved = 0
            AND h.reasonCode IN ('missingDob','missingResultValue')
          UNION SELECT 'lastName',0 WHERE @stagingRowId IS NOT NULL AND config.udfReceiptField (@parsed,@patch,'lastName') IS NULL) AS f
         WHERE f.knownInvalid=1 OR config.udfReceiptField (@parsed,@patch,f.fieldName) IS NULL OR config.udfReceiptField (@parsed,@patch,f.fieldName) = 'unknown';
        IF NULLIF (@missing,N'') IS NOT NULL
        BEGIN
         IF NOT EXISTS (SELECT 1 FROM dbo.workTask WHERE queueCode='receiptFinding' AND title=N'Missing information detected' AND isDeleted=0
             AND ((@labResultId IS NOT NULL AND labResultId=@labResultId) OR (@stagingRowId IS NOT NULL AND stagingRowId=@stagingRowId)))
            INSERT dbo.workTask (workTaskId,rootTenantId,owningTenantId,queueCode,title,detail,personId,labResultId,stagingRowId,taskStatus,completedUtc,completedByUserId)
            VALUES (NEWID (),@root,@owner,'receiptFinding',N'Missing information detected',CONCAT(N'Missing/unusable mapped report fields: ',@missing,N'. Verify the original source; unmapped data is not proof the laboratory omitted it.'),
              @person,@labResultId,@stagingRowId,'done',SYSUTCDATETIME (),auth.udfSessionUserId ());
         IF NOT EXISTS (SELECT 1 FROM dbo.workTask WHERE queueCode='missingInformation' AND isDeleted=0
             AND ((@labResultId IS NOT NULL AND labResultId=@labResultId) OR (@stagingRowId IS NOT NULL AND stagingRowId=@stagingRowId)))
            INSERT dbo.workTask (workTaskId,rootTenantId,owningTenantId,queueCode,title,detail,personId,labResultId,stagingRowId)
            VALUES (NEWID (),@root,@owner,'missingInformation',N'Missing Information Received',CONCAT(N'Obtain/verify the missing mapped fields: ',@missing,N'. Record the actual receipt date and source evidence; leave pending until all requested information is received or verified in the original.'),@person,@labResultId,@stagingRowId);
        END;
        IF @rule IS NOT NULL AND @excluded=0
        BEGIN
         IF @due IS NULL OR (@receiptDate=@due AND @sameDayChecked=0) OR @labResultId IS NULL OR @certain=0 OR @final IS NULL OR @final < @collected OR @final > @receiptDate OR @value IS NULL
            OR (@verified = 0 AND ((@mode = 'mdReporting' AND COALESCE (@state,'') <> 'MD') OR (@age IS NULL AND (@minAge IS NOT NULL OR @maxAge IS NOT NULL))))
         BEGIN
            UPDATE dbo.workTask SET taskStatus='open',completedUtc=NULL,completedByUserId=NULL
             WHERE queueCode='labReportingReview' AND isDeleted=0 AND taskStatus<>'open'
              AND ((@labResultId IS NOT NULL AND labResultId=@labResultId) OR (@stagingRowId IS NOT NULL AND stagingRowId=@stagingRowId));
            IF NOT EXISTS (SELECT 1 FROM dbo.workTask WHERE queueCode='labReportingReview' AND isDeleted=0
              AND ((@labResultId IS NOT NULL AND labResultId=@labResultId) OR (@stagingRowId IS NOT NULL AND stagingRowId=@stagingRowId)))
              INSERT dbo.workTask (workTaskId,rootTenantId,owningTenantId,queueCode,title,detail,personId,labResultId,stagingRowId)
              VALUES (NEWID (),@root,@owner,'labReportingReview',N'Verify laboratory reporting timeline',N'An explicit final-result date, verified eligibility and any same-day close-of-business cutoff are needed. Collection, analysis, message time and Department receipt do not establish the laboratory final-result date. Check the original report and approved policy.',@person,@labResultId,@stagingRowId);
         END
         ELSE IF @receiptDate > @due OR (@receiptDate=@due AND @lateConfirmed=1)
         BEGIN
            IF NOT EXISTS (SELECT 1 FROM dbo.workTask WHERE queueCode='receiptFinding' AND title=N'Late laboratory report detected' AND isDeleted=0
              AND ((@labResultId IS NOT NULL AND labResultId=@labResultId) OR (@stagingRowId IS NOT NULL AND stagingRowId=@stagingRowId)))
              INSERT dbo.workTask (workTaskId,rootTenantId,owningTenantId,queueCode,title,detail,personId,labResultId,stagingRowId,dueDate,taskStatus,completedUtc,completedByUserId)
              VALUES (NEWID (),@root,@owner,'receiptFinding',N'Late laboratory report detected',CONCAT(@name,N'; policy ',@rule,N'; final result ',CONVERT(VARCHAR(10),@final,23),N'; due ',CONVERT(VARCHAR(10),@due,23),N'; Department receipt ',CONVERT(VARCHAR(10),@receiptDate,23),N' (',@zone,N'). Detection date is recorded separately from these source dates.'),@person,@labResultId,@stagingRowId,@due,'done',SYSUTCDATETIME (),auth.udfSessionUserId ());
         END;
        END;

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
    , @ObjectName  = N'uspReconcileReceiptQuality'
    , @Description = N'Idempotent receipt-quality detection. Completed detection tasks use the actual detection instant; missing-information receipt remains pending for explicit evidence.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspReconcileReceiptQuality TO meritWebRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspReconcileReceiptQuality TO meritWorkerRole;
END;
GO
