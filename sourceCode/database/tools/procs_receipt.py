"""Receipt-quality findings: completed detection events and evidence-based information receipt, PR #239 review."""
from procs_care import proc
from procs_common import BOTH

PROCS = [
{
 'schema': 'dbo', 'name': 'uspIsManualReceiptSource', 'kind': 'read', 'grants': [], 'ticket': '262',
 'execute_as_owner': True,
 'desc': 'Internal provenance lookup for an already authorized result. Intake batches may be hidden from a clinical profile. Granted to no application role; returns only a channel flag through the checked receipt evaluator.',
 'params': '''@intakeBatchId UNIQUEIDENTIFIER
, @rootTenantId UNIQUEIDENTIFIER
, @manual BIT OUTPUT''',
 'key': "N'receipt source channel only'",
 'body': '''SET @manual=0;
IF EXISTS (SELECT 1 FROM dbo.intakeBatch WHERE intakeBatchId=@intakeBatchId AND rootTenantId=@rootTenantId AND sourceChannel='manual') SET @manual=1;'''
},
proc('uspReconcileReceiptQuality', 'write', '''@labResultId UNIQUEIDENTIFIER = NULL
, @stagingRowId UNIQUEIDENTIFIER = NULL
, @recordReview BIT = 0''', '''
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
 @collected DATE, @final DATE, @state VARCHAR (2), @verified BIT = 0, @excluded BIT = 0, @population VARCHAR (10), @operator VARCHAR (1), @sameDayChecked BIT=0, @lateConfirmed BIT=0, @verifiedPolicy UNIQUEIDENTIFIER,
 @manual BIT = 0, @batch UNIQUEIDENTIFIER;
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
   @state = l.state, @batch=r.intakeBatchId
 FROM dbo.labResult AS r LEFT JOIN dbo.location AS l ON l.locationId = r.reportedLocationId
 WHERE r.labResultId = @labResultId AND r.isDeleted = 0 AND r.resultRowStatus = 'valid' AND r.resultStatus IN ('final','corrected');
END;
IF @root IS NULL
BEGIN
    ;THROW 51404, N'The receipt source was not found or is outside your access.', 1;
END;
-- Authorization above runs as the caller. The internal owner lookup exposes no batch content
-- and cannot be executed directly by application roles, including clinical-only profiles.
IF @labResultId IS NOT NULL AND @stagingRowId IS NULL
 EXEC dbo.uspIsManualReceiptSource @intakeBatchId=@batch,@rootTenantId=@root,@manual=@manual OUTPUT;
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
-- Manual entry captures selected result fields, not an inventory of the original report.
-- Use the intake channel, never a caller-controlled JSON source marker. Keep timeline
-- review and all historical findings; chart demographics are not source evidence.
IF @excluded=1 OR @manual=1 SET @required=NULL;
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
END;''', permission=None, grants=BOTH, desc='Idempotent receipt-quality detection. Completed detection tasks use the actual detection instant; missing-information receipt remains pending for explicit evidence.'),
proc('uspListReceiptWork', 'read', '''@personId UNIQUEIDENTIFIER = NULL
, @pageNumber INT = 1
, @workTaskId UNIQUEIDENTIFIER = NULL''', '''
IF @pageNumber NOT BETWEEN 1 AND 100000
BEGIN
    ;THROW 51422, N'Choose a valid receipt-review page.', 1;
END;
SELECT w.workTaskId,w.personId,w.labResultId,w.stagingRowId,s.intakeBatchId,w.title,w.detail,w.taskStatus,w.createdDateUtc,
 w.completedUtc,w.resolutionNote,w.queueCode,w.dueDate,COUNT (*) OVER () AS totalCount
FROM dbo.workTask AS w LEFT JOIN dbo.stagingRow AS s ON s.stagingRowId=w.stagingRowId
WHERE w.isDeleted=0 AND w.queueCode IN ('receiptFinding','missingInformation','labReportingReview')
 AND (@personId IS NULL OR w.personId=@personId) AND (@workTaskId IS NULL OR w.workTaskId=@workTaskId)
 AND (w.personId IS NULL OR EXISTS (SELECT 1 FROM dbo.person WHERE personId=w.personId AND isDeleted=0))
ORDER BY CASE WHEN w.taskStatus='open' THEN 0 ELSE 1 END,w.createdDateUtc DESC,w.workTaskId
OFFSET (@pageNumber-1)*50 ROWS FETCH NEXT 50 ROWS ONLY;''',permission='task.view'),
proc('uspReceiveMissingInformation', 'write', '''@workTaskId UNIQUEIDENTIFIER
, @receivedDate DATE
, @evidence NVARCHAR (1000)
, @confirmed BIT''', '''
IF auth.udfHasPermission (N'person.edit') = 0 AND auth.udfHasPermission (N'intake.upload') = 0
BEGIN
    ;THROW 51403, N'Clinical intake permission is required.', 1;
END;
IF @confirmed <> 1 OR @receivedDate > auth.udfSessionToday () OR @receivedDate < '1900-01-01' OR NULLIF(TRIM(@evidence),N'') IS NULL
BEGIN
    ;THROW 51422, N'Confirm all requested information was received or verified and record its actual receipt date and source evidence.', 1;
END;
UPDATE dbo.workTask SET taskStatus='done', completedUtc=CAST(CAST(@receivedDate AS DATETIME2) AT TIME ZONE COALESCE(CAST(SESSION_CONTEXT(N'timeZone') AS VARCHAR(100)),'Eastern Standard Time') AT TIME ZONE 'UTC' AS DATETIME2),
 completedByUserId=auth.udfSessionUserId (),resolutionNote=@evidence
WHERE workTaskId=@workTaskId AND queueCode='missingInformation' AND taskStatus='open' AND isDeleted=0
 AND (personId IS NULL OR EXISTS (SELECT 1 FROM dbo.person WHERE personId=dbo.workTask.personId AND isDeleted=0));
IF @@ROWCOUNT <> 1
BEGIN
    ;THROW 51409, N'This information request is unavailable or already completed. Refresh the history.', 1;
END;''',permission=None),
proc('uspListReceiptPolicyResults','read','''@notificationRuleId UNIQUEIDENTIFIER = NULL
, @personId UNIQUEIDENTIFIER = NULL
, @pageNumber INT = 1''','''
IF @notificationRuleId IS NULL AND @personId IS NULL OR @pageNumber NOT BETWEEN 1 AND 100000
BEGIN
    ;THROW 51422, N'Choose a patient or laboratory reporting policy and a valid page.', 1;
END;
SELECT r.labResultId FROM dbo.labResult AS r WHERE r.isDeleted=0 AND r.resultRowStatus='valid' AND r.resultStatus IN ('final','corrected')
 AND (@personId IS NULL OR r.personId=@personId)
 AND (@notificationRuleId IS NULL OR EXISTS (SELECT 1 FROM config.notificationRule WHERE notificationRuleId=@notificationRuleId AND rulePurpose='labReporting' AND isActive=1 AND isDeleted=0 AND analyteSpecimenId=r.analyteSpecimenId))
ORDER BY r.labResultId OFFSET (@pageNumber-1)*200 ROWS FETCH NEXT 200 ROWS ONLY;''',permission=None,grants=BOTH),
proc('uspVerifyLabReportingTimeline','write','''@personId UNIQUEIDENTIFIER
, @labResultId UNIQUEIDENTIFIER
, @finalResultDate DATE = NULL
, @evidence NVARCHAR (1000)
, @eligibilityConfirmed BIT
, @applies BIT = 1
, @sameDayChecked BIT = 0
, @lateConfirmed BIT = 0''','''
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
EXEC dbo.uspReconcileReceiptQuality @labResultId=@labResultId,@recordReview=1;''',permission='result.edit')
]
