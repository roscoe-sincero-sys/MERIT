"""Versioned program tasks and evidenced completion (#205-207)."""
from procs_care import proc, PERSON, CASE_WRITE, OWNER
from procs_common import BOTH

PROCS = [
proc('uspGetWorkTask','read','@workTaskId UNIQUEIDENTIFIER','''
SELECT w.*,COALESCE(w.personId,c.personId) AS patientId,c.caseNumber,p.firstName,p.lastName
FROM dbo.workTask AS w LEFT JOIN dbo.exposureCase AS c ON c.exposureCaseId=w.exposureCaseId AND c.isDeleted=0
JOIN dbo.person AS p ON p.personId=COALESCE(w.personId,c.personId) AND p.isDeleted=0
WHERE w.workTaskId=@workTaskId AND w.isDeleted=0 AND auth.udfHasPermission(N'person.view')=1
 AND (w.exposureCaseId IS NULL OR (auth.udfHasPermission(N'case.view')=1 AND c.exposureCaseId IS NOT NULL));
''',permission='task.view'),
proc('config.uspListWorkflowDefinitions','read','', '''
SELECT d.*,CONVERT(VARCHAR(16),CAST(d.rowVersion AS BINARY(8)),2) AS versionStamp,t.tenantName AS scopeName
FROM config.workflowDefinition AS d JOIN auth.tenant AS t ON t.tenantId=d.owningTenantId WHERE d.isDeleted=0 ORDER BY d.name,d.version DESC;
''',permission='config.edit'),
proc('config.uspSaveWorkflowDefinition','write','''@workflowDefinitionId UNIQUEIDENTIFIER
, @name NVARCHAR(120)
, @version INT
, @description NVARCHAR(1000)
, @triggerEvent VARCHAR(20)
, @population VARCHAR(10)
, @completionKind VARCHAR(12)
, @effectiveFrom DATE
, @timeZoneId VARCHAR(100)
, @evidenceFieldsJson NVARCHAR(MAX) = N'[]'
, @analyteSpecimenId UNIQUEIDENTIFIER = NULL
, @minimumAgeMonths INT = NULL
, @maximumAgeMonths INT = NULL
, @minimumValue DECIMAL(18,6) = NULL
, @maximumValue DECIMAL(18,6) = NULL
, @requiresEligibilityReview BIT = 0
, @assignmentRoleCode VARCHAR(40) = NULL
, @dueInDays INT = NULL
, @recurrenceDays INT = NULL
, @dayBasis VARCHAR(12) = 'calendar'
, @holidayDatesJson NVARCHAR(MAX) = N'[]'
, @blocksClosure BIT = 0
, @closureReason VARCHAR(30) = 'all'
, @minimumContactAttempts INT = 0
, @allowWaiver BIT = 0
, @effectiveTo DATE = NULL
, @versionStamp VARCHAR(16) = NULL''', '''
IF NULLIF(TRIM(@name),N'') IS NULL OR NULLIF(TRIM(@description),N'') IS NULL OR @version<1
 OR @triggerEvent NOT IN ('patientReview','receivedResult','caseOpened') OR @population NOT IN ('all','child','adult','pregnant')
 OR @completionKind NOT IN ('internal','contact','delivery') OR @dayBasis NOT IN ('calendar','business')
 OR @minimumContactAttempts NOT BETWEEN 0 AND 20 OR @closureReason NOT IN ('all','belowLevelOnRetest','lostToFollowUp','movedOutOfState','declined','deceased','duplicate','other')
 OR (@dueInDays IS NOT NULL AND @dueInDays NOT BETWEEN 0 AND 365) OR (@recurrenceDays IS NOT NULL AND @recurrenceDays NOT BETWEEN 1 AND 365)
 OR @minimumAgeMonths<0 OR @maximumAgeMonths<0 OR @maximumAgeMonths<@minimumAgeMonths OR @minimumValue<0 OR @maximumValue<=@minimumValue
 OR @effectiveTo<@effectiveFrom OR NOT EXISTS(SELECT 1 FROM sys.time_zone_info WHERE name=@timeZoneId)
 OR ((@minimumValue IS NOT NULL OR @maximumValue IS NOT NULL) AND @analyteSpecimenId IS NULL)
 OR (@analyteSpecimenId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM config.analyteSpecimen WHERE analyteSpecimenId=@analyteSpecimenId AND isDeleted=0))
 OR (@assignmentRoleCode IS NOT NULL AND NOT EXISTS(SELECT 1 FROM auth.role WHERE roleCode=@assignmentRoleCode AND isDeleted=0))
BEGIN
    ;THROW 51422, N'Check the task name, version, conditions, role, dates, timing and completion rules.', 1;
END;
IF ISJSON(@evidenceFieldsJson)<>1 OR LEFT(TRIM(@evidenceFieldsJson),1)<>'[' OR LEN(@evidenceFieldsJson)>4000
 OR ISJSON(@holidayDatesJson)<>1 OR LEFT(TRIM(@holidayDatesJson),1)<>'['
BEGIN
    ;THROW 51422, N'Evidence fields and holidays must be bounded JSON arrays.', 1;
END;
IF (SELECT COUNT(*) FROM OPENJSON(@evidenceFieldsJson))>10 OR EXISTS
 (SELECT 1 FROM OPENJSON(@evidenceFieldsJson) WITH([key] NVARCHAR(100),label NVARCHAR(200),type VARCHAR(20),required BIT)
 WHERE NULLIF(TRIM([key]),N'') IS NULL OR LEN([key])>30 OR [key] COLLATE Latin1_General_100_BIN2 LIKE N'%[^a-zA-Z0-9_]%'
 OR NULLIF(TRIM(label),N'') IS NULL OR LEN(label)>80 OR type IS NULL OR type NOT IN ('text','date','number','yesNo') OR required IS NULL)
 OR EXISTS(SELECT 1 FROM OPENJSON(@evidenceFieldsJson) WITH([key] NVARCHAR(100)) GROUP BY [key] HAVING COUNT(*)>1)
 OR (SELECT COUNT(*) FROM OPENJSON(@holidayDatesJson))>2000 OR EXISTS(SELECT 1 FROM OPENJSON(@holidayDatesJson) WHERE type<>1 OR TRY_CONVERT(DATE,[value],23) IS NULL)
BEGIN
    ;THROW 51422, N'Use up to ten unique evidence fields (text/date/number/yesNo) and valid explicit holiday dates.', 1;
END;
IF EXISTS(SELECT 1 FROM config.workflowDefinition WHERE workflowDefinitionId=@workflowDefinitionId AND isDeleted=0)
BEGIN
    UPDATE config.workflowDefinition SET name=@name,version=@version,description=@description,triggerEvent=@triggerEvent,population=@population,
     completionKind=@completionKind,effectiveFrom=@effectiveFrom,effectiveTo=@effectiveTo,timeZoneId=@timeZoneId,evidenceFieldsJson=@evidenceFieldsJson,
     analyteSpecimenId=@analyteSpecimenId,minimumAgeMonths=@minimumAgeMonths,maximumAgeMonths=@maximumAgeMonths,minimumValue=@minimumValue,maximumValue=@maximumValue,
     requiresEligibilityReview=@requiresEligibilityReview,assignmentRoleCode=@assignmentRoleCode,dueInDays=@dueInDays,recurrenceDays=@recurrenceDays,dayBasis=@dayBasis,
     holidayDatesJson=@holidayDatesJson,blocksClosure=@blocksClosure,closureReason=@closureReason,minimumContactAttempts=@minimumContactAttempts,allowWaiver=@allowWaiver
    WHERE workflowDefinitionId=@workflowDefinitionId AND definitionStatus='draft' AND owningTenantId=auth.udfSessionActingTenantId()
     AND CONVERT(VARCHAR(16),CAST(rowVersion AS BINARY(8)),2)=@versionStamp;
    IF @@ROWCOUNT<>1
    BEGIN
        ;THROW 51409, N'Only an unchanged draft owned by the working agency can be edited. Published versions are immutable.', 1;
    END;
END
ELSE
    INSERT config.workflowDefinition(workflowDefinitionId,rootTenantId,owningTenantId,name,version,description,triggerEvent,population,completionKind,effectiveFrom,effectiveTo,
     timeZoneId,evidenceFieldsJson,analyteSpecimenId,minimumAgeMonths,maximumAgeMonths,minimumValue,maximumValue,requiresEligibilityReview,assignmentRoleCode,
     dueInDays,recurrenceDays,dayBasis,holidayDatesJson,blocksClosure,closureReason,minimumContactAttempts,allowWaiver)
    VALUES(@workflowDefinitionId,auth.udfSessionRootTenantId(),auth.udfSessionActingTenantId(),@name,@version,@description,@triggerEvent,@population,@completionKind,@effectiveFrom,@effectiveTo,
     @timeZoneId,@evidenceFieldsJson,@analyteSpecimenId,@minimumAgeMonths,@maximumAgeMonths,@minimumValue,@maximumValue,@requiresEligibilityReview,@assignmentRoleCode,
     @dueInDays,@recurrenceDays,@dayBasis,@holidayDatesJson,@blocksClosure,@closureReason,@minimumContactAttempts,@allowWaiver);
''',permission='config.edit'),
proc('config.uspPreviewWorkflowDefinition','read','''@workflowDefinitionId UNIQUEIDENTIFIER
, @personId UNIQUEIDENTIFIER
, @exposureCaseId UNIQUEIDENTIFIER = NULL''','''
IF NOT EXISTS(SELECT 1 FROM config.workflowDefinition WHERE workflowDefinitionId=@workflowDefinitionId AND isDeleted=0)
 OR NOT EXISTS(SELECT 1 FROM dbo.person WHERE personId=@personId AND isDeleted=0)
BEGIN
    ;THROW 51404, N'The draft or sample patient is not accessible.', 1;
END;
SELECT personId,exposureCaseId,labResultId,anchorDate,evaluationState,explanation FROM config.tvfWorkflowEligibility(@workflowDefinitionId,@personId,@exposureCaseId,NULL);
''',permission='config.edit'),
proc('config.uspPublishWorkflowDefinition','write','''@workflowDefinitionId UNIQUEIDENTIFIER
, @versionStamp VARCHAR(16)
, @approvalReference NVARCHAR(300)
, @approved BIT
, @samplePersonId UNIQUEIDENTIFIER
, @secondSamplePersonId UNIQUEIDENTIFIER
, @sampleCaseId UNIQUEIDENTIFIER = NULL
, @secondSampleCaseId UNIQUEIDENTIFIER = NULL''','''
IF @approved<>1 OR NULLIF(TRIM(@approvalReference),N'') IS NULL OR @samplePersonId=@secondSamplePersonId
 OR NOT EXISTS(SELECT 1 FROM config.tvfWorkflowEligibility(@workflowDefinitionId,@samplePersonId,@sampleCaseId,NULL))
 OR NOT EXISTS(SELECT 1 FROM config.tvfWorkflowEligibility(@workflowDefinitionId,@secondSamplePersonId,@secondSampleCaseId,NULL))
BEGIN
    ;THROW 51422, N'Preview two accessible sample patients and explicitly acknowledge program approval before publication.', 1;
END;
IF EXISTS(SELECT 1 FROM config.workflowDefinition AS d WITH(UPDLOCK,HOLDLOCK) JOIN config.workflowDefinition AS n ON n.workflowDefinitionId=@workflowDefinitionId
 WHERE d.workflowDefinitionId<>n.workflowDefinitionId AND d.owningTenantId=n.owningTenantId AND d.name=n.name AND d.definitionStatus='published' AND d.isDeleted=0
 AND d.effectiveFrom<=COALESCE(n.effectiveTo,'99991231') AND COALESCE(d.effectiveTo,'99991231')>=n.effectiveFrom)
BEGIN
    ;THROW 51422, N'Retire the overlapping published version or use nonoverlapping effective dates.', 1;
END;
UPDATE config.workflowDefinition SET definitionStatus='published',approvalReference=@approvalReference,publishedByUserId=auth.udfSessionUserId(),publishedUtc=SYSUTCDATETIME()
 WHERE workflowDefinitionId=@workflowDefinitionId AND definitionStatus='draft' AND owningTenantId=auth.udfSessionActingTenantId() AND isDeleted=0
 AND CONVERT(VARCHAR(16),CAST(rowVersion AS BINARY(8)),2)=@versionStamp;
IF @@ROWCOUNT<>1
BEGIN
    ;THROW 51409, N'The draft changed or is not owned by the working agency. Reload and preview it again.', 1;
END;
''',permission='config.edit'),
proc('config.uspRetireWorkflowDefinition','write','@workflowDefinitionId UNIQUEIDENTIFIER','''
UPDATE config.workflowDefinition SET definitionStatus='retired' WHERE workflowDefinitionId=@workflowDefinitionId
 AND owningTenantId=auth.udfSessionActingTenantId() AND definitionStatus='published' AND isDeleted=0;
IF @@ROWCOUNT<>1
BEGIN
    ;THROW 51404, N'The published version is not available to retire in this working agency.', 1;
END;
''',permission='config.edit'),
proc('uspReconcileWorkflowTasks','write','''@personId UNIQUEIDENTIFIER
, @labResultId UNIQUEIDENTIFIER = NULL
, @exposureCaseId UNIQUEIDENTIFIER = NULL
, @workflowDefinitionId UNIQUEIDENTIFIER = NULL''', '''
IF auth.udfHasPermission(N'person.edit')=0 AND auth.udfHasPermission(N'intake.upload')=0 AND auth.udfHasPermission(N'config.edit')=0
BEGIN
    ;THROW 51403, N'Patient editing, intake or program configuration is required.', 1;
END;
IF NOT EXISTS(SELECT 1 FROM dbo.person WHERE personId=@personId AND isDeleted=0)
 OR (@labResultId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM dbo.labResult WHERE labResultId=@labResultId AND personId=@personId AND isDeleted=0))
 OR (@exposureCaseId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM dbo.exposureCase WHERE exposureCaseId=@exposureCaseId AND personId=@personId AND caseStatus<>'closed' AND isDeleted=0))
BEGIN
    ;THROW 51404, N'The patient, source result or active case is not accessible.', 1;
END;
DECLARE @lock INT,@resource NVARCHAR(255)=CONCAT(N'MERIT.workflow.',@personId);
EXEC @lock=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction';
IF @lock<0
BEGIN
    ;THROW 51409, N'Workflow evaluation is busy. Try again.', 1;
END;
INSERT dbo.workTask(workTaskId,rootTenantId,owningTenantId,personId,exposureCaseId,labResultId,queueCode,title,detail,dueDate,workflowDefinitionId,workflowSnapshotJson,workflowOccurrenceKey)
SELECT NEWID(),d.rootTenantId,e.targetOwner,@personId,@exposureCaseId,e.labResultId,
 CASE WHEN e.evaluationState='needsReview' THEN 'workflowReview' ELSE 'programTask' END,d.name,
 CONCAT(d.description,N' Eligibility: ',e.explanation,N' Suggested role: ',COALESCE(d.assignmentRoleCode,N'assign within the team')),
 CASE WHEN d.dueInDays IS NULL THEN NULL WHEN d.dueInDays=0 THEN e.anchorDate ELSE config.udfNotificationDueDate(e.anchorDate,d.dueInDays,d.dayBasis,d.holidayDatesJson) END,
 d.workflowDefinitionId,(SELECT d.*,e.anchorDate,e.evaluationState FOR JSON PATH,WITHOUT_ARRAY_WRAPPER),
 CONCAT(d.workflowDefinitionId,N'/',COALESCE(@exposureCaseId,@personId),N'/',CASE WHEN d.triggerEvent='receivedResult' THEN CONVERT(VARCHAR(36),e.labResultId) ELSE 'once' END)
FROM config.workflowDefinition AS d CROSS APPLY config.tvfWorkflowEligibility(d.workflowDefinitionId,@personId,@exposureCaseId,@labResultId) AS e
WHERE d.definitionStatus='published' AND d.isDeleted=0 AND e.evaluationState IN ('eligible','needsReview')
 AND EXISTS(SELECT 1 FROM auth.tvfTenantWritePredicate(d.rootTenantId,e.targetOwner,0))
 AND (@workflowDefinitionId IS NULL OR d.workflowDefinitionId=@workflowDefinitionId)
 AND (@exposureCaseId IS NULL OR ((auth.udfHasPermission(N'case.view')=1 OR auth.udfHasPermission(N'config.edit')=1) AND EXISTS(SELECT 1 FROM dbo.exposureCase WHERE exposureCaseId=@exposureCaseId)))
 AND ((d.triggerEvent='receivedResult' AND @labResultId IS NOT NULL) OR (d.triggerEvent='patientReview' AND @exposureCaseId IS NULL AND @labResultId IS NULL) OR (d.triggerEvent='caseOpened' AND @exposureCaseId IS NOT NULL))
 AND NOT EXISTS(SELECT 1 FROM dbo.workTask AS w WHERE w.isDeleted=0 AND w.workflowOccurrenceKey=
 CONCAT(d.workflowDefinitionId,N'/',COALESCE(@exposureCaseId,@personId),N'/',CASE WHEN d.triggerEvent='receivedResult' THEN CONVERT(VARCHAR(36),e.labResultId) ELSE 'once' END));
SELECT @@ROWCOUNT AS createdCount;
''',permission=None,grants=BOTH),
proc('uspListWorkflowPatients','read','''@workflowDefinitionId UNIQUEIDENTIFIER
, @pageNumber INT = 1''','''
IF @pageNumber<1
BEGIN
    ;THROW 51422, N'Page number must be positive.', 1;
END;
SELECT p.personId FROM dbo.person AS p WHERE p.isDeleted=0 AND p.mergedIntoPersonId IS NULL AND EXISTS
 (SELECT 1 FROM config.workflowDefinition AS d JOIN auth.tenantClosure AS t ON t.ancestorTenantId=d.owningTenantId
 WHERE d.workflowDefinitionId=@workflowDefinitionId AND d.isDeleted=0 AND t.isDeleted=0 AND (t.descendantTenantId=p.owningTenantId
 OR EXISTS(SELECT 1 FROM dbo.exposureCase WHERE personId=p.personId AND owningTenantId=t.descendantTenantId AND isDeleted=0)))
ORDER BY p.personId OFFSET (@pageNumber-1)*200 ROWS FETCH NEXT 200 ROWS ONLY;
''',permission='config.edit',grants=BOTH),
proc('uspListWorkflowSources','read','@personId UNIQUEIDENTIFIER','''
IF auth.udfHasPermission(N'person.edit')=0 AND auth.udfHasPermission(N'config.edit')=0
BEGIN
    ;THROW 51403, N'Patient editing or program configuration is required.', 1;
END;
SELECT CAST(NULL AS UNIQUEIDENTIFIER) AS labResultId,c.exposureCaseId FROM dbo.exposureCase AS c WHERE c.personId=@personId AND c.caseStatus<>'closed' AND c.isDeleted=0
UNION ALL
SELECT r.labResultId,c.exposureCaseId FROM dbo.labResult AS r JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId=r.analyteSpecimenId
OUTER APPLY(SELECT TOP(1) exposureCaseId FROM dbo.exposureCase WHERE personId=@personId AND analyteId=s.analyteId AND caseStatus<>'closed' AND isDeleted=0 ORDER BY openedDate DESC) AS c
WHERE r.personId=@personId AND r.resultRowStatus='valid' AND r.resultStatus IN ('final','corrected') AND r.isDeleted=0;
''',permission=None,grants=BOTH),
proc('uspListPatientWork','read','@personId UNIQUEIDENTIFIER', '''
SELECT w.*,c.caseNumber,COALESCE(c.personId,w.personId) AS patientId FROM dbo.workTask AS w LEFT JOIN dbo.exposureCase AS c ON c.exposureCaseId=w.exposureCaseId AND c.isDeleted=0
WHERE (w.personId=@personId OR c.personId=@personId) AND w.isDeleted=0
 AND (w.exposureCaseId IS NULL OR (auth.udfHasPermission(N'case.view')=1 AND c.exposureCaseId IS NOT NULL))
ORDER BY CASE WHEN w.taskStatus='open' THEN 0 ELSE 1 END,w.isUrgent DESC,w.dueDate,w.createdDateUtc DESC;
''',permission='task.view',validate=PERSON),
proc('uspCreateAdHocWork','write','''@workTaskId UNIQUEIDENTIFIER
, @personId UNIQUEIDENTIFIER
, @title NVARCHAR(200)
, @description NVARCHAR(1000)
, @completionKind VARCHAR(12)
, @exposureCaseId UNIQUEIDENTIFIER = NULL
, @dueDate DATE = NULL''', PERSON+CASE_WRITE+OWNER+'''
IF auth.udfHasPermission(N'task.edit')=0 OR @completionKind NOT IN ('internal','contact','delivery') OR NULLIF(TRIM(@title),N'') IS NULL OR NULLIF(TRIM(@description),N'') IS NULL
 OR EXISTS(SELECT 1 FROM dbo.person WHERE personId=@personId AND mergedIntoPersonId IS NOT NULL)
BEGIN
    ;THROW 51422, N'Enter a task, instructions and the type of completion evidence.', 1;
END;
DECLARE @snapshot NVARCHAR(MAX)=(SELECT @completionKind AS completionKind,'[]' AS evidenceFieldsJson,CAST(0 AS BIT) AS blocksClosure,CAST(0 AS BIT) AS allowWaiver,'eligible' AS evaluationState FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
INSERT dbo.workTask(workTaskId,rootTenantId,owningTenantId,personId,exposureCaseId,queueCode,title,detail,dueDate,workflowSnapshotJson)
VALUES(@workTaskId,@root,@owner,@personId,@exposureCaseId,'programTask',@title,@description,@dueDate,@snapshot);
''',permission='person.edit'),
]

PROCS += [proc('uspResolveWorkWithEvidence','write','''@workTaskId UNIQUEIDENTIFIER
, @personActivityId UNIQUEIDENTIFIER
, @action VARCHAR(12)
, @activityDate DATE
, @note NVARCHAR(1000)
, @outcome VARCHAR(30) = 'completed'
, @audience VARCHAR(12) = 'internal'
, @channel VARCHAR(12) = 'internal'
, @contactPersonId UNIQUEIDENTIFIER = NULL
, @recipientLabel NVARCHAR(200) = NULL
, @evidenceJson NVARCHAR(MAX) = N'{}'
, @transferCaseId UNIQUEIDENTIFIER = NULL''', '''
DECLARE @personId UNIQUEIDENTIFIER,@exposureCaseId UNIQUEIDENTIFIER,@root UNIQUEIDENTIFIER,@owner UNIQUEIDENTIFIER,
 @snapshot NVARCHAR(MAX),@title NVARCHAR(200),@queue VARCHAR(40),@definition UNIQUEIDENTIFIER,@sourceResult UNIQUEIDENTIFIER;
SELECT @personId=COALESCE(w.personId,c.personId),@exposureCaseId=w.exposureCaseId,@root=w.rootTenantId,@owner=w.owningTenantId,
 @snapshot=w.workflowSnapshotJson,@title=w.title,@queue=w.queueCode,@definition=w.workflowDefinitionId,@sourceResult=w.labResultId
 FROM dbo.workTask AS w WITH(UPDLOCK,HOLDLOCK) LEFT JOIN dbo.exposureCase AS c ON c.exposureCaseId=w.exposureCaseId AND c.isDeleted=0
 WHERE w.workTaskId=@workTaskId AND w.taskStatus='open' AND w.isDeleted=0
 AND (w.exposureCaseId IS NULL OR (c.exposureCaseId IS NOT NULL AND (w.personId IS NULL OR w.personId=c.personId)));
IF @personId IS NULL
BEGIN
    ;THROW 51404, N'The open patient task is not available. Work without a patient is handled in the queue.', 1;
END;
''' + PERSON + CASE_WRITE + '''
IF @queue IN ('notificationDue','receiptFinding','missingInformation','labReportingReview','testingFollowUp','testingDischarge')
BEGIN
    ;THROW 51422, N'This task has a dedicated notification, receipt, testing or closure-letter evidence workflow. Use its original workspace.', 1;
END;
IF @action NOT IN ('complete','attempt','apply','exclude','waive','transfer') OR NULLIF(TRIM(@note),N'') IS NULL
 OR @activityDate>auth.udfSessionToday() OR @activityDate<'19000101'
BEGIN
    ;THROW 51422, N'Choose an action and record the actual event date and source evidence.', 1;
END;
IF EXISTS(SELECT 1 FROM dbo.personActivity WHERE personActivityId=@personActivityId) OR EXISTS(SELECT 1 FROM dbo.caseActivity WHERE caseActivityId=@personActivityId)
BEGIN
    ;THROW 51409, N'This event was already recorded. Reload the task history before resubmitting.', 1;
END;
DECLARE @kind VARCHAR(12)=COALESCE(JSON_VALUE(@snapshot,'$.completionKind'),
 CASE WHEN @queue IN ('letterDue','prenatalNotification') THEN 'delivery'
 WHEN @queue IN ('confirmatoryTest','newbornTesting','actionLevel','crossLink','investigationRequest','assessmentContact') THEN 'contact' ELSE 'internal' END);
IF JSON_VALUE(@snapshot,'$.evaluationState')='needsReview' AND @action NOT IN ('apply','exclude','transfer')
BEGIN
    ;THROW 51422, N'Record an explicit applicability decision before completing this conditional task.', 1;
END;
IF @action IN ('apply','exclude') AND COALESCE(JSON_VALUE(@snapshot,'$.evaluationState'),'eligible')<>'needsReview'
BEGIN
    ;THROW 51422, N'This task is not awaiting an applicability decision.', 1;
END;
IF @action='waive' AND (auth.udfHasPermission(N'workflow.waive')=0 OR COALESCE(JSON_VALUE(@snapshot,'$.allowWaiver'),'false')<>'true')
BEGIN
    ;THROW 51403, N'This policy does not allow a waiver, or the active profile lacks workflow waiver permission.', 1;
END;
IF @action='complete'
BEGIN
    IF @kind='internal' AND (@audience<>'internal' OR @outcome<>'completed' OR @channel<>'internal')
     OR @kind='contact' AND (@audience='internal' OR @channel='internal' OR @outcome<>'reached')
     OR @kind='delivery' AND (@audience='internal' OR @channel='internal' OR @outcome<>'completed')
    BEGIN
        ;THROW 51422, N'Use the required completion outcome, recipient and channel. Unsuccessful contact is an attempt and leaves the task open.', 1;
    END;
    IF ISJSON(@evidenceJson)<>1 OR LEFT(TRIM(@evidenceJson),1)<>'{' OR LEN(@evidenceJson)>1200
    BEGIN
        ;THROW 51422, N'Provide a bounded object containing the required evidence fields.', 1;
    END;
    DECLARE @fields NVARCHAR(MAX)=COALESCE(JSON_VALUE(@snapshot,'$.evidenceFieldsJson'),N'[]');
    IF EXISTS(SELECT 1 FROM OPENJSON(@fields) WITH([key] NVARCHAR(30),type VARCHAR(20),required BIT) AS f
     OUTER APPLY(SELECT [value] FROM OPENJSON(@evidenceJson) WHERE [key]=f.[key]) AS e
     WHERE (f.required=1 AND NULLIF(TRIM(e.[value]),N'') IS NULL) OR (NULLIF(TRIM(e.[value]),N'') IS NOT NULL AND
     ((f.type='date' AND (LEN(e.[value])<>10 OR TRY_CONVERT(DATE,e.[value],23) IS NULL)) OR (f.type='number' AND TRY_CONVERT(DECIMAL(18,6),e.[value]) IS NULL)
     OR (f.type='yesNo' AND e.[value] NOT IN ('yes','no')))))
     OR EXISTS(SELECT 1 FROM OPENJSON(@evidenceJson) AS e WHERE e.type NOT IN (0,1) OR NOT EXISTS(SELECT 1 FROM OPENJSON(@fields) WITH([key] NVARCHAR(30)) WHERE [key]=e.[key]))
    BEGIN
        ;THROW 51422, N'Complete each required evidence field using the configured text, date, number or yes/no format.', 1;
    END;
    IF COALESCE(TRY_CONVERT(INT,JSON_VALUE(@snapshot,'$.minimumContactAttempts')),0)>
     (SELECT COUNT(*) FROM dbo.vwPatientActivityStream WHERE exposureCaseId=@exposureCaseId AND activityType='contactAttempt' AND isDeleted=0 AND isVoided=0
      AND outcome IN ('noAnswer','leftMessage','wrongNumber') AND (contactPersonId IS NOT NULL OR NULLIF(TRIM(recipientLabel),N'') IS NOT NULL))
    BEGIN
        ;THROW 51422, N'The required number of evidenced unsuccessful contact attempts has not been recorded.', 1;
    END;
END;
IF @action='attempt' AND (@audience='internal' OR @channel='internal' OR @outcome NOT IN ('reached','noAnswer','leftMessage','wrongNumber'))
BEGIN
    ;THROW 51422, N'Record the external recipient, channel and actual contact outcome. An attempt does not complete required delivery.', 1;
END;
IF @action='transfer' AND (@transferCaseId IS NULL OR @transferCaseId=@exposureCaseId OR NOT EXISTS
 (SELECT 1 FROM dbo.exposureCase AS c WHERE c.exposureCaseId=@transferCaseId AND c.personId=@personId AND c.caseStatus<>'closed' AND c.isDeleted=0
 AND EXISTS(SELECT 1 FROM auth.tvfTenantWritePredicate(c.rootTenantId,c.owningTenantId,c.isDeleted))))
BEGIN
    ;THROW 51422, N'Choose a different active case for this patient within your write scope. The receiving task will remain open.', 1;
END;
DECLARE @activityType VARCHAR(30)=CASE WHEN @action='attempt' OR (@action='complete' AND @kind='contact') THEN 'contactAttempt'
 WHEN @action='complete' AND @kind='delivery' THEN 'letter' ELSE 'note' END;
IF @action NOT IN ('complete','attempt')
BEGIN
    SET @audience='internal'; SET @channel='internal'; SET @outcome='completed'; SET @contactPersonId=NULL; SET @recipientLabel=NULL;
END;
DECLARE @fullNote NVARCHAR(MAX)=CONCAT(N'Task: ',@title,N'. Action: ',@action,N'. ',@note,CASE WHEN @action='complete' AND @evidenceJson<>N'{}' THEN N' Evidence: '+@evidenceJson ELSE N'' END);
IF LEN(@fullNote)>2000
BEGIN
    ;THROW 51422, N'Shorten the note/evidence so the complete activity remains within 2,000 characters.', 1;
END;
DECLARE @activityNote NVARCHAR(2000)=@fullNote;
EXEC dbo.uspRecordPatientActivity @personActivityId=@personActivityId,@personId=@personId,@activityType=@activityType,@activityDate=@activityDate,
 @audience=@audience,@channel=@channel,@outcome=@outcome,@contactPersonId=@contactPersonId,@recipientLabel=@recipientLabel,@note=@activityNote,@exposureCaseId=@exposureCaseId,@returnCount=0;
IF @action='apply'
    UPDATE dbo.workTask SET queueCode='programTask',workflowSnapshotJson=JSON_MODIFY(workflowSnapshotJson,'$.evaluationState','eligible'),resolutionNote=@note WHERE workTaskId=@workTaskId;
IF @action IN ('complete','waive','exclude','transfer')
    UPDATE dbo.workTask SET taskStatus=CASE WHEN @action='complete' THEN 'done' ELSE 'dismissed' END,completedUtc=SYSUTCDATETIME(),completedByUserId=auth.udfSessionUserId(),
     resolutionKind=CASE @action WHEN 'complete' THEN 'completed' WHEN 'waive' THEN 'waived' WHEN 'exclude' THEN 'notApplicable' ELSE 'transferred' END,
     completionActivityId=@personActivityId,resolutionNote=@note WHERE workTaskId=@workTaskId;
IF @action='transfer'
BEGIN
    DECLARE @next UNIQUEIDENTIFIER=NEWID();
    INSERT dbo.workTask(workTaskId,rootTenantId,owningTenantId,personId,exposureCaseId,labResultId,queueCode,title,detail,dueDate,isUrgent,
     workflowDefinitionId,workflowSnapshotJson,workflowOccurrenceKey)
    SELECT @next,w.rootTenantId,c.owningTenantId,@personId,@transferCaseId,w.labResultId,w.queueCode,w.title,w.detail,w.dueDate,w.isUrgent,
     w.workflowDefinitionId,w.workflowSnapshotJson,CONCAT(N'transfer/',@workTaskId,N'/',@next)
    FROM dbo.workTask AS w JOIN dbo.exposureCase AS c ON c.exposureCaseId=@transferCaseId WHERE w.workTaskId=@workTaskId;
    UPDATE dbo.workTask SET resolutionNote=CONCAT(@note,N' Transferred to task ',@next,N' on case ',@transferCaseId,N'.') WHERE workTaskId=@workTaskId;
END;
DECLARE @repeat INT=TRY_CONVERT(INT,JSON_VALUE(@snapshot,'$.recurrenceDays'));
IF @action='complete' AND @repeat>0 AND EXISTS(SELECT 1 FROM config.workflowDefinition WHERE workflowDefinitionId=@definition AND definitionStatus='published' AND isDeleted=0)
BEGIN
    DECLARE @frozenHolidays NVARCHAR(MAX)=(SELECT holidayDatesJson FROM OPENJSON(@snapshot) WITH(holidayDatesJson NVARCHAR(MAX)));
    DECLARE @nextDue DATE=config.udfNotificationDueDate(@activityDate,@repeat,JSON_VALUE(@snapshot,'$.dayBasis'),@frozenHolidays);
    IF @nextDue<=COALESCE(TRY_CONVERT(DATE,JSON_VALUE(@snapshot,'$.effectiveTo')),'99991231')
        INSERT dbo.workTask(workTaskId,rootTenantId,owningTenantId,personId,exposureCaseId,queueCode,title,detail,dueDate,workflowDefinitionId,workflowSnapshotJson,workflowOccurrenceKey)
        VALUES(NEWID(),@root,@owner,@personId,@exposureCaseId,'programTask',@title,N'Recurring task from actual prior completion; original definition retained.',@nextDue,@definition,@snapshot,CONCAT(N'repeat/',@workTaskId,N'/',@personActivityId));
END;
IF @queue='assessmentOwnerClosure' EXEC dbo.uspRefreshPersonAccess @personId=@personId;
''',permission='task.edit')]

PROCS += [proc('uspCorrectPatientActivity','write','''@personId UNIQUEIDENTIFIER
, @activityId UNIQUEIDENTIFIER
, @sourceKind VARCHAR(12)
, @reason NVARCHAR(500)''', PERSON+'''
DECLARE @case UNIQUEIDENTIFIER,@found BIT=0;
IF @sourceKind='patient' SELECT @case=exposureCaseId,@found=1 FROM dbo.personActivity WITH(UPDLOCK,HOLDLOCK) WHERE personActivityId=@activityId AND personId=@personId AND isDeleted=0 AND isVoided=0;
IF @sourceKind='case' SELECT @case=a.exposureCaseId,@found=1 FROM dbo.caseActivity AS a WITH(UPDLOCK,HOLDLOCK) JOIN dbo.exposureCase AS c ON c.exposureCaseId=a.exposureCaseId
 WHERE a.caseActivityId=@activityId AND c.personId=@personId AND a.isDeleted=0 AND a.isVoided=0 AND a.activityType IN ('contactAttempt','education','referral','medicalManagement','letter','note');
IF @found=0 OR NULLIF(TRIM(@reason),N'') IS NULL
BEGIN
    ;THROW 51422, N'The correctable activity is unavailable or the correction reason is missing.', 1;
END;
IF @case IS NOT NULL AND auth.udfHasPermission(N'case.edit')=0
BEGIN
    ;THROW 51403, N'Case editing is required for this activity.', 1;
END;
IF EXISTS(SELECT 1 FROM dbo.exposureCase WHERE exposureCaseId=@case AND caseStatus='closed' AND isDeleted=0)
BEGIN
    ;THROW 51422, N'Reopen the case before correcting evidence that could affect its closure requirements.', 1;
END;
IF EXISTS(SELECT 1 FROM dbo.workTask WHERE completionActivityId=@activityId AND isDeleted=0 AND (resolutionKind='transferred' OR auth.udfHasPermission(N'task.edit')=0))
BEGIN
    ;THROW 51422, N'Task editing is required; transferred work must be reviewed with the receiving case before evidence is corrected.', 1;
END;
IF EXISTS(SELECT 1 FROM dbo.notificationObligation WHERE completedActivityId=@activityId AND isDeleted=0)
BEGIN
    ;THROW 51422, N'Withdraw notification evidence in the notification workspace so all obligations reopen together.', 1;
END;
IF EXISTS(SELECT 1 FROM dbo.workTask AS w JOIN dbo.workTask AS next ON next.workflowOccurrenceKey LIKE CONCAT(N'repeat/',w.workTaskId,N'/%')
 WHERE w.completionActivityId=@activityId AND next.isDeleted=0 AND next.taskStatus<>'open' AND COALESCE(next.resolutionKind,'')<>'autoDismissed')
BEGIN
    ;THROW 51422, N'A later recurrence was already resolved. Review the dependent task history before correcting this evidence.', 1;
END;
UPDATE next SET taskStatus='dismissed',resolutionKind='autoDismissed',completedUtc=SYSUTCDATETIME(),completedByUserId=auth.udfSessionUserId(),resolutionNote=CONCAT(N'Prior completion evidence corrected: ',@reason)
 FROM dbo.workTask AS next JOIN dbo.workTask AS w ON next.workflowOccurrenceKey LIKE CONCAT(N'repeat/',w.workTaskId,N'/%') WHERE w.completionActivityId=@activityId AND next.taskStatus='open' AND next.isDeleted=0;
UPDATE dbo.workTask SET taskStatus='open',resolutionKind=NULL,completedUtc=NULL,completedByUserId=NULL,completionActivityId=NULL,resolutionNote=CONCAT(N'Completion evidence corrected: ',@reason)
 WHERE completionActivityId=@activityId AND isDeleted=0;
IF @sourceKind='patient' UPDATE dbo.personActivity SET isVoided=1,voidReason=@reason WHERE personActivityId=@activityId;
IF @sourceKind='case' UPDATE dbo.caseActivity SET isVoided=1,voidReason=@reason WHERE caseActivityId=@activityId;
''',permission='person.edit')]
