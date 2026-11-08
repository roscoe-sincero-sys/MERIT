"""Patient activities, private supporting documents and approved notification obligations (#202-204)."""
from procs_common import WEB, BOTH, NEWID, not_found

PERSON = """IF auth.udfHasPermission (N'person.view') = 0
BEGIN
    ;THROW 51403, N'Patient view permission is required.', 1;
END;
""" + not_found('@personId', 'dbo.person', 'personId', 'The patient')
CASE_READ = """IF @exposureCaseId IS NOT NULL AND (auth.udfHasPermission (N'case.view') = 0 OR NOT EXISTS
    (SELECT 1 FROM dbo.exposureCase WHERE exposureCaseId = @exposureCaseId AND personId = @personId AND isDeleted = 0))
BEGIN
    ;THROW 51404, N'The case was not found for this patient, or is outside your access.', 1;
END;"""
CASE_WRITE = CASE_READ + """
IF @exposureCaseId IS NOT NULL AND auth.udfHasPermission (N'case.edit') = 0
BEGIN
    ;THROW 51403, N'Case edit permission is required for case-associated records.', 1;
END;"""
OWNER = """DECLARE @root UNIQUEIDENTIFIER, @owner UNIQUEIDENTIFIER;
SELECT @root = rootTenantId, @owner = owningTenantId FROM dbo.person WHERE personId = @personId;
IF @exposureCaseId IS NOT NULL SELECT @owner = owningTenantId FROM dbo.exposureCase WHERE exposureCaseId = @exposureCaseId;
"""
CASE_VISIBLE_ROW = """(a.exposureCaseId IS NULL OR (auth.udfHasPermission (N'case.view') = 1 AND EXISTS
    (SELECT 1 FROM dbo.exposureCase AS c WHERE c.exposureCaseId = a.exposureCaseId AND c.personId = a.personId AND c.isDeleted = 0)))"""

def proc(name, kind, params, body, permission='person.view', grants=WEB, validate='', desc=''):
    return dict(schema='dbo' if not name.startswith('config.') else 'config', name=name.split('.')[-1], kind=kind,
                grants=grants, ticket='202-204', permission=permission, params=params, body=body, validate=validate,
                key="N'patient care metadata; no text or file content'", desc=desc or name)

PROCS = [
proc('uspNotificationComplianceSummary', 'read', '', """
SELECT status, COUNT (*) AS obligationCount FROM (
 SELECT CASE WHEN a.evaluationState = 'notApplicable' THEN 'Not applicable'
             WHEN a.evaluationState = 'needsReview' THEN 'Needs eligibility review'
             WHEN a.completedDate <= a.dueDate THEN 'Completed on time'
             WHEN a.completedDate IS NOT NULL THEN 'Completed late'
             WHEN a.dueDate < CAST (SYSUTCDATETIME () AT TIME ZONE 'UTC' AT TIME ZONE a.timeZoneId AS DATE) THEN 'Overdue'
             ELSE 'Pending / due today' END AS status
 FROM dbo.notificationObligation AS a JOIN dbo.person AS p ON p.personId = a.personId AND p.isDeleted = 0
 WHERE a.isDeleted = 0 AND """ + CASE_VISIBLE_ROW + """
) AS scoped GROUP BY status;""", desc='Full-scope compliance counts, separated from generic first-follow-up metrics and paged lists.'),
proc('config.uspListNotificationRules', 'read', '', """
SELECT n.*, a.code AS analyteCode, s.specimen, s.canonicalUnit, t.tenantName AS scopeName
  FROM config.notificationRule AS n JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId = n.analyteSpecimenId
  JOIN config.analyte AS a ON a.analyteId = s.analyteId JOIN auth.tenant AS t ON t.tenantId = n.owningTenantId
 WHERE n.isDeleted = 0 ORDER BY n.ruleName, n.effectiveFrom DESC;""", permission='config.edit',
    desc='Approved effective-dated notification policies; examples are not seeded as active law.'),
proc('config.uspSaveNotificationRule', 'write', """@notificationRuleId UNIQUEIDENTIFIER
, @ruleName NVARCHAR (120)
, @analyteSpecimenId UNIQUEIDENTIFIER
, @population VARCHAR (10)
, @minimumValue DECIMAL (18,6)
, @recipientKind VARCHAR (12)
, @dueInDays INT
, @dayBasis VARCHAR (12)
, @timeZoneId VARCHAR (100)
, @calendarName NVARCHAR (120)
, @holidayDatesJson NVARCHAR (MAX)
, @effectiveFrom DATE
, @approvalReference NVARCHAR (300)
, @minimumAgeMonths INT = NULL
, @maximumAgeMonths INT = NULL
, @maximumValue DECIMAL (18,6) = NULL
, @effectiveTo DATE = NULL""", """
IF @population NOT IN ('all','child','adult','pregnant','unknown') OR @recipientKind NOT IN ('patient','caregiver')
   OR @dueInDays NOT BETWEEN 1 AND 365 OR @dayBasis NOT IN ('calendar','business')
   OR @minimumValue < 0 OR @maximumValue <= @minimumValue OR @minimumAgeMonths < 0 OR @maximumAgeMonths < @minimumAgeMonths
   OR @effectiveTo < @effectiveFrom OR NULLIF (LTRIM (RTRIM (@ruleName)), N'') IS NULL
   OR NULLIF (LTRIM (RTRIM (@approvalReference)), N'') IS NULL OR NULLIF (LTRIM (RTRIM (@calendarName)), N'') IS NULL
   OR ISJSON (@holidayDatesJson) <> 1
BEGIN
    ;THROW 51422, N'Provide an approved rule, valid thresholds and dates, and an explicit calendar policy.', 1;
END;
IF EXISTS (SELECT 1 FROM OPENJSON (@holidayDatesJson) WHERE [type] <> 1 OR TRY_CONVERT (DATE, [value]) IS NULL)
   OR (SELECT COUNT (*) FROM OPENJSON (@holidayDatesJson)) > 2000
   OR NOT EXISTS (SELECT 1 FROM sys.time_zone_info WHERE name = @timeZoneId)
BEGIN
    ;THROW 51422, N'Use valid explicit holiday dates and a supported policy time zone.', 1;
END;
DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId (), @owner UNIQUEIDENTIFIER = auth.udfSessionActingTenantId ();
IF NOT EXISTS (SELECT 1 FROM auth.tenant WHERE tenantId = @owner AND rootTenantId = @root AND isActive = 1 AND isDeleted = 0)
   OR NOT EXISTS (SELECT 1 FROM config.analyteSpecimen WHERE analyteSpecimenId = @analyteSpecimenId AND isDeleted = 0)
BEGIN
    ;THROW 51422, N'Choose a working agency and an available analyte/specimen.', 1;
END;
IF EXISTS (SELECT 1 FROM config.notificationRule WITH (UPDLOCK, HOLDLOCK) WHERE owningTenantId = @owner AND ruleName = @ruleName
           AND isActive = 1 AND isDeleted = 0 AND effectiveFrom <= COALESCE (@effectiveTo, '9999-12-31')
           AND COALESCE (effectiveTo, '9999-12-31') >= @effectiveFrom)
BEGIN
    ;THROW 51422, N'An active version with this name overlaps the proposed period. Retire it or use a non-overlapping period.', 1;
END;
INSERT config.notificationRule (notificationRuleId, rootTenantId, owningTenantId, ruleName, analyteSpecimenId,
    population, minimumAgeMonths, maximumAgeMonths, minimumValue, maximumValue, recipientKind, triggerEvent, dueInDays,
    dayBasis, timeZoneId, calendarName, holidayDatesJson, effectiveFrom, effectiveTo, approvalReference, approvedByUserId)
VALUES (@notificationRuleId, @root, @owner, @ruleName, @analyteSpecimenId, @population, @minimumAgeMonths, @maximumAgeMonths,
    @minimumValue, @maximumValue, @recipientKind, 'receipt', @dueInDays, @dayBasis, @timeZoneId, @calendarName,
    @holidayDatesJson, @effectiveFrom, @effectiveTo, @approvalReference, auth.udfSessionUserId ());""", permission='config.edit',
    desc='Creates an approved receipt-based policy version; existing snapshots cannot be edited or silently recalculated.'),
proc('config.uspRetireNotificationRule', 'write', '@notificationRuleId UNIQUEIDENTIFIER', """
UPDATE config.notificationRule SET isActive = 0 WHERE notificationRuleId = @notificationRuleId AND isDeleted = 0
 AND EXISTS (SELECT 1 FROM auth.sessionScopeTenant AS st WHERE st.sessionScopeId = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER)
             AND st.tenantId = owningTenantId AND st.accessLevel IN ('write','full'));
IF @@ROWCOUNT <> 1
BEGIN
    ;THROW 51404, N'The rule is unavailable in your writable agency scope.', 1;
END;""", permission='config.edit', desc='Stops generating new obligations; existing deadlines and evidence remain intact.'),
proc('uspReconcileNotifications', 'write', '@labResultId UNIQUEIDENTIFIER = NULL\n, @notificationRuleId UNIQUEIDENTIFIER = NULL\n, @personId UNIQUEIDENTIFIER = NULL',
    """IF auth.udfHasPermission (N'result.edit') = 0 AND auth.udfHasPermission (N'person.edit') = 0 AND auth.udfHasPermission (N'config.edit') = 0
BEGIN
    ;THROW 51403, N'Clinical edit or policy configuration permission is required.', 1;
END;
IF @labResultId IS NULL AND @notificationRuleId IS NULL AND @personId IS NULL
BEGIN
    ;THROW 51422, N'Choose a patient, result or approved rule to evaluate.', 1;
END;
DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId (), @lock INT,
        @resource NVARCHAR (100) = CONCAT (N'MERIT.notifications.', auth.udfSessionRootTenantId ());
EXEC @lock = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
IF @lock < 0
BEGIN
    ;THROW 51409, N'Notification evaluation is busy. Retry shortly.', 1;
END;
SELECT TOP (500) """ + NEWID + """ AS notificationObligationId, """ + NEWID + """ AS workTaskId,
       r.personId, r.labResultId, r.rootTenantId, COALESCE (c.owningTenantId, r.owningTenantId) AS owningTenantId,
       c.exposureCaseId, n.notificationRuleId, n.ruleName, n.recipientKind, n.timeZoneId, n.triggerEvent,
       CAST (r.receivedUtc AT TIME ZONE 'UTC' AT TIME ZONE n.timeZoneId AS DATE) AS anchorDate,
       config.udfNotificationDueDate (CAST (r.receivedUtc AT TIME ZONE 'UTC' AT TIME ZONE n.timeZoneId AS DATE),
           n.dueInDays, n.dayBasis, n.holidayDatesJson) AS dueDate,
       CASE WHEN r.operator = '=' AND (n.population = 'all' OR n.population = r.population)
              AND ((n.minimumAgeMonths IS NULL AND n.maximumAgeMonths IS NULL) OR r.ageAtCollectionMonths IS NOT NULL)
            THEN 'eligible' ELSE 'needsReview' END AS evaluationState,
       (SELECT n.ruleName, n.population, n.minimumAgeMonths, n.maximumAgeMonths, n.minimumValue, n.maximumValue,
               n.recipientKind, n.triggerEvent, n.dueInDays, n.dayBasis, n.timeZoneId, n.calendarName,
               JSON_QUERY (n.holidayDatesJson) AS holidays, n.effectiveFrom, n.effectiveTo, n.approvalReference
          FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) AS policySnapshotJson
  INTO #new FROM dbo.labResult AS r JOIN config.notificationRule AS n ON n.rootTenantId = r.rootTenantId
       AND n.analyteSpecimenId = r.analyteSpecimenId AND n.isActive = 1 AND n.isDeleted = 0
  OUTER APPLY (SELECT TOP (1) ec.exposureCaseId, ec.owningTenantId FROM dbo.exposureCase AS ec
               JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId = r.analyteSpecimenId
              WHERE ec.personId = r.personId AND ec.analyteId = s.analyteId AND ec.isDeleted = 0
                AND (ec.qualifyingLabResultId = r.labResultId OR ec.caseStatus <> 'closed')
              ORDER BY CASE WHEN ec.qualifyingLabResultId = r.labResultId THEN 0 ELSE 1 END, ec.openedDate DESC) AS c
 WHERE r.rootTenantId = @root AND r.isDeleted = 0 AND r.resultRowStatus = 'valid' AND r.resultStatus IN ('final','corrected')
   AND r.personId IS NOT NULL AND r.valueCanonical IS NOT NULL
   AND (@labResultId IS NULL OR r.labResultId = @labResultId) AND (@personId IS NULL OR r.personId = @personId)
   AND (@notificationRuleId IS NULL OR n.notificationRuleId = @notificationRuleId)
   AND EXISTS (SELECT 1 FROM auth.tenantClosure AS tc WHERE tc.ancestorTenantId = n.owningTenantId AND tc.descendantTenantId = r.owningTenantId AND tc.isDeleted = 0)
   AND CAST (r.receivedUtc AT TIME ZONE 'UTC' AT TIME ZONE n.timeZoneId AS DATE) >= n.effectiveFrom
   AND (n.effectiveTo IS NULL OR CAST (r.receivedUtc AT TIME ZONE 'UTC' AT TIME ZONE n.timeZoneId AS DATE) <= n.effectiveTo)
   AND (n.population = 'all' OR n.population = r.population OR r.population = 'unknown')
   AND (n.minimumAgeMonths IS NULL OR r.ageAtCollectionMonths IS NULL OR r.ageAtCollectionMonths >= n.minimumAgeMonths)
   AND (n.maximumAgeMonths IS NULL OR r.ageAtCollectionMonths IS NULL OR r.ageAtCollectionMonths <= n.maximumAgeMonths)
   AND ((r.operator = '=' AND r.valueCanonical >= n.minimumValue AND (n.maximumValue IS NULL OR r.valueCanonical < n.maximumValue))
     OR (r.operator = '<' AND r.valueCanonical > n.minimumValue)
     OR (r.operator = '>' AND (n.maximumValue IS NULL OR r.valueCanonical < n.maximumValue)))
   AND NOT EXISTS (SELECT 1 FROM dbo.notificationObligation AS o WHERE o.notificationRuleId = n.notificationRuleId AND o.labResultId = r.labResultId AND o.isDeleted = 0)
 ORDER BY r.receivedUtc, r.labResultId, n.notificationRuleId;
IF EXISTS (SELECT 1 FROM #new WHERE dueDate IS NULL)
BEGIN
    ;THROW 51422, N'The approved calendar cannot produce a bounded deadline. Correct the calendar before retrying.', 1;
END;
INSERT dbo.workTask (workTaskId, rootTenantId, owningTenantId, queueCode, title, detail, personId, exposureCaseId, labResultId, dueDate, isUrgent)
SELECT workTaskId, rootTenantId, owningTenantId, 'notificationDue',
       CONCAT (CASE WHEN evaluationState = 'needsReview' THEN N'Review potential notification: ' ELSE N'Notify ' END, recipientKind, N' — ', ruleName),
       CONCAT (N'Due ', CONVERT (VARCHAR (10), dueDate, 23), N' (', timeZoneId, N'). Required recipient: ', recipientKind, N'. Open the patient notification workspace to record completed delivery. Attempts do not complete this task.'),
       personId, exposureCaseId, labResultId, dueDate, 1 FROM #new;
INSERT dbo.notificationObligation (notificationObligationId, rootTenantId, owningTenantId, personId, exposureCaseId,
    labResultId, notificationRuleId, workTaskId, policySnapshotJson, ruleName, recipientKind, triggerEvent, anchorDate, dueDate, timeZoneId, evaluationState)
SELECT notificationObligationId, rootTenantId, owningTenantId, personId, exposureCaseId, labResultId, notificationRuleId,
       workTaskId, policySnapshotJson, ruleName, recipientKind, triggerEvent, anchorDate, dueDate, timeZoneId, evaluationState FROM #new;
SELECT COUNT (*) AS createdCount FROM #new;""", permission=None, grants=BOTH,
    desc='Idempotent receipt/threshold rule evaluation in batches. Every matching rule gets its own deadline; censored results require explicit applicability review.'),
proc('uspListNotificationObligations', 'read', '@personId UNIQUEIDENTIFIER = NULL\n, @exposureCaseId UNIQUEIDENTIFIER = NULL\n, @pageNumber INT = 1',
    """IF @pageNumber NOT BETWEEN 1 AND 100000
BEGIN
    ;THROW 51422, N'Choose a valid notification page.', 1;
END;
SELECT a.*, p.lastName, p.firstName, c.caseNumber, r.collectedDate, r.receivedUtc, r.resultRowStatus,
       r.valueCanonical, r.operator, s.canonicalUnit, an.displayName AS analyteName, s.specimen,
       pa.activityDate AS evidenceDate, pa.channel AS evidenceChannel,
       CONCAT (cp.lastName, N', ', cp.firstName) AS recipientName,
       CAST (SYSUTCDATETIME () AT TIME ZONE 'UTC' AT TIME ZONE a.timeZoneId AS DATE) AS policyToday,
       COUNT (*) OVER () AS totalCount
  FROM dbo.notificationObligation AS a JOIN dbo.person AS p ON p.personId = a.personId
  JOIN dbo.labResult AS r ON r.labResultId = a.labResultId
  JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId = r.analyteSpecimenId
  JOIN config.analyte AS an ON an.analyteId = s.analyteId
  LEFT JOIN dbo.exposureCase AS c ON c.exposureCaseId = a.exposureCaseId
  LEFT JOIN dbo.personActivity AS pa ON pa.personActivityId = a.completedActivityId AND pa.isVoided = 0
  LEFT JOIN dbo.person AS cp ON cp.personId = pa.contactPersonId
 WHERE a.isDeleted = 0 AND p.isDeleted = 0 AND (@personId IS NULL OR a.personId = @personId)
   AND (@exposureCaseId IS NULL OR a.exposureCaseId = @exposureCaseId)
   AND """ + CASE_VISIBLE_ROW + """
 ORDER BY CASE WHEN a.completedDate IS NULL AND a.evaluationState <> 'notApplicable' THEN 0 ELSE 1 END,
          a.dueDate, a.notificationObligationId OFFSET (@pageNumber - 1) * 50 ROWS FETCH NEXT 50 ROWS ONLY;""",
    desc='Scoped notification work and evidence, including source-result changes and immutable policy snapshots.'),
proc('uspReviewNotificationObligation', 'write', '@personId UNIQUEIDENTIFIER\n, @notificationObligationId UNIQUEIDENTIFIER\n, @applies BIT\n, @reason NVARCHAR (500)',
    """DECLARE @exposureCaseId UNIQUEIDENTIFIER, @task UNIQUEIDENTIFIER;
SELECT @exposureCaseId = exposureCaseId, @task = workTaskId FROM dbo.notificationObligation WITH (UPDLOCK, HOLDLOCK)
 WHERE notificationObligationId = @notificationObligationId AND personId = @personId AND isDeleted = 0 AND completedActivityId IS NULL;
""" + CASE_WRITE + """
IF @task IS NULL OR NULLIF (LTRIM (RTRIM (@reason)), N'') IS NULL
BEGIN
    ;THROW 51422, N'Choose a pending obligation and record why it applies or does not apply.', 1;
END;
UPDATE dbo.notificationObligation SET evaluationState = CASE WHEN @applies = 1 THEN 'eligible' ELSE 'notApplicable' END,
    reviewReason = @reason WHERE notificationObligationId = @notificationObligationId;
UPDATE dbo.workTask SET taskStatus = CASE WHEN @applies = 1 THEN 'open' ELSE 'dismissed' END,
    completedUtc = CASE WHEN @applies = 0 THEN SYSUTCDATETIME () END,
    completedByUserId = CASE WHEN @applies = 0 THEN auth.udfSessionUserId () END,
    resolutionNote = CASE WHEN @applies = 0 THEN N'Explicit notification applicability review; see obligation reason.' END
 WHERE workTaskId = @task;""", permission='person.edit', validate=PERSON,
    desc='Auditable applicability decision for an uncertain/corrected result; never counts an exclusion as completed notification.'),
proc('uspWithdrawNotificationEvidence', 'write', '@personId UNIQUEIDENTIFIER\n, @notificationObligationId UNIQUEIDENTIFIER\n, @reason NVARCHAR (500)',
    """DECLARE @exposureCaseId UNIQUEIDENTIFIER, @task UNIQUEIDENTIFIER, @activity UNIQUEIDENTIFIER;
SELECT @exposureCaseId = exposureCaseId, @task = workTaskId, @activity = completedActivityId FROM dbo.notificationObligation WITH (UPDLOCK, HOLDLOCK)
 WHERE notificationObligationId = @notificationObligationId AND personId = @personId AND isDeleted = 0;
""" + CASE_WRITE + """
IF @activity IS NULL OR NULLIF (LTRIM (RTRIM (@reason)), N'') IS NULL
BEGIN
    ;THROW 51422, N'Choose completed evidence and explain the correction.', 1;
END;
UPDATE dbo.personActivity SET isVoided = 1, voidReason = @reason WHERE personActivityId = @activity;
UPDATE w SET taskStatus = 'open', completedUtc = NULL, completedByUserId = NULL, resolutionNote = NULL
  FROM dbo.workTask AS w JOIN dbo.notificationObligation AS o ON o.workTaskId = w.workTaskId
 WHERE o.personId = @personId AND o.completedActivityId = @activity AND o.isDeleted = 0;
UPDATE dbo.notificationObligation SET completedActivityId = NULL, completedDate = NULL
 WHERE personId = @personId AND completedActivityId = @activity AND isDeleted = 0;""", permission='person.edit', validate=PERSON,
    desc='Withdraws mistaken evidence with a reason while retaining original event date, recipient and worker; reopens required follow-up.'),
proc('uspListPatientActivity', 'read', '@personId UNIQUEIDENTIFIER\n, @exposureCaseId UNIQUEIDENTIFIER = NULL\n, @pageNumber INT = 1',
    """IF @pageNumber NOT BETWEEN 1 AND 100000
BEGIN
    ;THROW 51422, N'Choose a valid activity page.', 1;
END;
SELECT a.*, u.displayName AS workerName, CONCAT (cp.lastName, N', ', cp.firstName) AS contactName,
       c.caseNumber, COUNT (*) OVER () AS totalCount
  FROM dbo.vwPatientActivityStream AS a
  LEFT JOIN auth.appUser AS u ON u.appUserId = a.workerUserId
  LEFT JOIN dbo.person AS cp ON cp.personId = a.contactPersonId
  LEFT JOIN dbo.exposureCase AS c ON c.exposureCaseId = a.exposureCaseId
 WHERE a.personId = @personId AND (@exposureCaseId IS NULL OR a.exposureCaseId = @exposureCaseId)
 ORDER BY a.activityDate DESC, a.createdDateUtc DESC, a.caseActivityId
 OFFSET (@pageNumber - 1) * 50 ROWS FETCH NEXT 50 ROWS ONLY;""",
    validate=PERSON + CASE_READ, desc='Paged canonical patient/case activity, with actual recipient and worker provenance.'),
proc('uspRecordPatientActivity', 'write', """@personActivityId UNIQUEIDENTIFIER
, @personId UNIQUEIDENTIFIER
, @activityType VARCHAR (30)
, @activityDate DATE
, @audience VARCHAR (12)
, @channel VARCHAR (12)
, @outcome VARCHAR (30)
, @contactPersonId UNIQUEIDENTIFIER = NULL
, @recipientLabel NVARCHAR (200) = NULL
, @note NVARCHAR (2000) = NULL
, @exposureCaseId UNIQUEIDENTIFIER = NULL
, @labResultId UNIQUEIDENTIFIER = NULL
, @notificationObligationId UNIQUEIDENTIFIER = NULL
, @completeMatching BIT = 0""",
    OWNER + """
DECLARE @today DATE = auth.udfSessionToday ();
IF @notificationObligationId IS NOT NULL
BEGIN
    DECLARE @zone VARCHAR (100);
    SELECT @zone = timeZoneId FROM dbo.notificationObligation WHERE notificationObligationId = @notificationObligationId AND personId = @personId AND isDeleted = 0;
    IF @zone IS NOT NULL SET @today = CAST (SYSUTCDATETIME () AT TIME ZONE 'UTC' AT TIME ZONE @zone AS DATE);
END;
IF @activityDate > @today OR @activityDate < '1900-01-01' OR NULLIF (LTRIM (RTRIM (@note)), N'') IS NULL
BEGIN
    ;THROW 51422, N'Enter an actual activity date and a description of what happened.', 1;
END;
IF @audience = 'internal' AND (@channel <> 'internal' OR @contactPersonId IS NOT NULL)
BEGIN
    ;THROW 51422, N'Internal notes cannot name an external contact or delivery channel.', 1;
END;
IF @contactPersonId IS NOT NULL AND (@contactPersonId <> @personId AND NOT EXISTS
    (SELECT 1 FROM dbo.personRelationship AS r WHERE r.isDeleted = 0
      AND ((r.personId = @personId AND r.relatedPersonId = @contactPersonId) OR (r.relatedPersonId = @personId AND r.personId = @contactPersonId))
      AND (r.startDate IS NULL OR r.startDate <= @activityDate) AND (r.endDate IS NULL OR r.endDate >= @activityDate)))
BEGIN
    ;THROW 51422, N'Choose the patient or a relationship valid on the activity date.', 1;
END;
IF @contactPersonId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.person WHERE personId = @contactPersonId AND isDeleted = 0)
BEGIN
    ;THROW 51404, N'The selected contact is not available.', 1;
END;
IF @audience = 'patient' AND (@contactPersonId IS NULL OR @contactPersonId <> @personId)
BEGIN
    ;THROW 51422, N'Choose the patient as recipient.', 1;
END;
IF @audience = 'caregiver' AND (@contactPersonId IS NULL OR @contactPersonId = @personId OR NOT EXISTS
    (SELECT 1 FROM dbo.personRelationship AS r WHERE r.personId = @personId AND r.relatedPersonId = @contactPersonId AND r.isDeleted = 0
       AND (r.startDate IS NULL OR r.startDate <= @activityDate) AND (r.endDate IS NULL OR r.endDate >= @activityDate)
       AND (r.hasLegalCustody = 1 OR r.isPrimaryContact = 1 OR r.role IN ('parent','legalGuardian','fosterParent','caregiver'))))
BEGIN
    ;THROW 51422, N'Choose a recorded caregiver or guardian valid on this date. Add or verify the relationship first.', 1;
END;
IF @audience <> 'internal' AND (@channel = 'internal' OR (@contactPersonId IS NULL AND NULLIF (LTRIM (RTRIM (@recipientLabel)), N'') IS NULL))
BEGIN
    ;THROW 51422, N'Record the external recipient and how contact was attempted or completed.', 1;
END;
IF @labResultId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.labResult WHERE labResultId = @labResultId AND personId = @personId AND isDeleted = 0)
BEGIN
    ;THROW 51404, N'The result is not available for this patient.', 1;
END;
IF @activityType = 'notificationCompleted' AND EXISTS (SELECT 1 FROM dbo.labResult WHERE labResultId = @labResultId AND collectedDate > @activityDate)
BEGIN
    ;THROW 51422, N'Notification cannot precede collection of the blood test.', 1;
END;
IF @activityType = 'notificationCompleted' AND (@outcome <> 'completed' OR @contactPersonId IS NULL OR @labResultId IS NULL OR @audience NOT IN ('patient','caregiver'))
BEGIN
    ;THROW 51422, N'Completed notification requires a result, a recorded recipient, a delivery channel and a completed outcome.', 1;
END;
IF EXISTS (SELECT 1 FROM dbo.caseActivity WHERE caseActivityId = @personActivityId)
BEGIN
    ;THROW 51422, N'That activity identifier already belongs to a case event.', 1;
END;
IF EXISTS (SELECT 1 FROM dbo.personActivity WHERE personActivityId = @personActivityId)
BEGIN
    ;THROW 51409, N'This activity was already submitted. Refresh the history before submitting another event.', 1;
END;
INSERT dbo.personActivity (personActivityId, rootTenantId, owningTenantId, personId, exposureCaseId, labResultId,
    activityType, activityDate, audience, channel, contactPersonId, recipientLabel, outcome, note, workerUserId)
VALUES (@personActivityId, @root, @owner, @personId, @exposureCaseId, @labResultId, @activityType, @activityDate,
    @audience, @channel, @contactPersonId, @recipientLabel, @outcome, @note, auth.udfSessionUserId ());
IF @notificationObligationId IS NOT NULL
BEGIN
    DECLARE @task UNIQUEIDENTIFIER;
    SELECT @task = workTaskId FROM dbo.notificationObligation WITH (UPDLOCK, HOLDLOCK)
     WHERE notificationObligationId = @notificationObligationId AND personId = @personId AND labResultId = @labResultId
       AND recipientKind = @audience AND isDeleted = 0 AND completedActivityId IS NULL AND evaluationState = 'eligible'
       AND (@activityType = 'notificationCompleted' AND @outcome = 'completed')
       AND ((exposureCaseId IS NULL AND @exposureCaseId IS NULL) OR exposureCaseId = @exposureCaseId);
    IF @task IS NULL
    BEGIN
        ;THROW 51422, N'This activity cannot complete that obligation, or completion was already recorded. Nothing was saved.', 1;
    END;
    DECLARE @targets TABLE (id UNIQUEIDENTIFIER PRIMARY KEY, taskId UNIQUEIDENTIFIER);
    INSERT @targets SELECT notificationObligationId, workTaskId FROM dbo.notificationObligation WITH (UPDLOCK, HOLDLOCK)
     WHERE personId = @personId AND labResultId = @labResultId AND recipientKind = @audience
       AND isDeleted = 0 AND completedActivityId IS NULL AND evaluationState = 'eligible' AND anchorDate <= @activityDate
       AND ((exposureCaseId IS NULL AND @exposureCaseId IS NULL) OR exposureCaseId = @exposureCaseId)
       AND (notificationObligationId = @notificationObligationId OR @completeMatching = 1);
    IF NOT EXISTS (SELECT 1 FROM @targets WHERE id = @notificationObligationId)
    BEGIN
        ;THROW 51422, N'Completed notification cannot precede receipt of this blood test. Nothing was saved.', 1;
    END;
    UPDATE o SET completedActivityId = @personActivityId, completedDate = @activityDate
      FROM dbo.notificationObligation AS o JOIN @targets AS t ON t.id = o.notificationObligationId;
    UPDATE w SET taskStatus = 'done', completedUtc = SYSUTCDATETIME (), completedByUserId = auth.udfSessionUserId (),
        resolutionNote = N'Completed notification recorded with recipient and result evidence.'
      FROM dbo.workTask AS w JOIN @targets AS t ON t.taskId = w.workTaskId;
END;
SELECT COUNT (*) AS failedContactAttempts FROM dbo.vwPatientActivityStream
 WHERE exposureCaseId = @exposureCaseId AND activityType = 'contactAttempt' AND isDeleted = 0 AND isVoided = 0
   AND outcome IN ('noAnswer','leftMessage','wrongNumber');""", permission='person.edit', validate=PERSON + CASE_WRITE,
    desc='Records an actual patient activity and, only with explicit matching completion evidence, completes a notification atomically.'),
proc('uspLinkPatientRecord', 'write', """@personId UNIQUEIDENTIFIER
, @exposureCaseId UNIQUEIDENTIFIER
, @recordId UNIQUEIDENTIFIER
, @recordKind VARCHAR (12)""", """
IF @recordKind = 'activity'
    UPDATE dbo.personActivity SET exposureCaseId = @exposureCaseId WHERE personActivityId = @recordId AND personId = @personId AND exposureCaseId IS NULL AND isDeleted = 0
      AND NOT EXISTS (SELECT 1 FROM dbo.notificationObligation WHERE completedActivityId = @recordId AND isDeleted = 0);
ELSE
BEGIN
    ;THROW 51422, N'Choose an unlinked patient activity.', 1;
END;
IF @@ROWCOUNT <> 1
BEGIN
    ;THROW 51409, N'That record is unavailable or already linked. Its original date and provenance were not changed.', 1;
END;""", permission='person.edit', validate=PERSON + CASE_WRITE,
    desc='Links an existing patient record to a later case without copying its event or changing original provenance.'),
proc('uspListPatientDocuments', 'read', '@personId UNIQUEIDENTIFIER\n, @exposureCaseId UNIQUEIDENTIFIER = NULL\n, @pageNumber INT = 1',
    """IF @pageNumber NOT BETWEEN 1 AND 100000
BEGIN
    ;THROW 51422, N'Choose a valid document page.', 1;
END;
SELECT a.personDocumentId, a.personId, a.exposureCaseId, a.supersedesDocumentId, a.title, a.fileName, a.contentType,
       a.category, a.documentDate, a.source, a.description, a.createdDateUtc, DATALENGTH (a.content) AS sizeBytes,
       u.displayName AS uploadedBy, c.caseNumber, COUNT (*) OVER () AS totalCount
  FROM dbo.personDocument AS a LEFT JOIN auth.appUser AS u ON u.appUserId = a.uploadedByUserId
  LEFT JOIN dbo.exposureCase AS c ON c.exposureCaseId = a.exposureCaseId
 WHERE a.personId = @personId AND a.isDeleted = 0 AND (@exposureCaseId IS NULL OR a.exposureCaseId = @exposureCaseId)
   AND """ + CASE_VISIBLE_ROW + """
 ORDER BY a.createdDateUtc DESC, a.personDocumentId OFFSET (@pageNumber - 1) * 50 ROWS FETCH NEXT 50 ROWS ONLY;""",
    permission='document.view', validate=PERSON + CASE_READ, desc='Paged private document metadata, including retained revisions; never reads file bytes for a list.'),
proc('uspGetPatientDocument', 'read', '@personId UNIQUEIDENTIFIER\n, @personDocumentId UNIQUEIDENTIFIER',
    """SELECT a.personDocumentId, a.personId, a.exposureCaseId, a.title, a.fileName, a.contentType, a.content, a.contentHash
  FROM dbo.personDocument AS a WHERE a.personDocumentId = @personDocumentId AND a.personId = @personId AND a.isDeleted = 0
   AND """ + CASE_VISIBLE_ROW + ';', permission='document.view', validate=PERSON,
    desc='Fetches one authorized original revision, with patient RLS and case visibility; application audits download/preview.'),
proc('uspSavePatientDocument', 'write', """@personDocumentId UNIQUEIDENTIFIER
, @personId UNIQUEIDENTIFIER
, @title NVARCHAR (200)
, @fileName NVARCHAR (200)
, @contentType VARCHAR (100)
, @category VARCHAR (30)
, @documentDate DATE
, @source NVARCHAR (200)
, @contentHash BINARY (32)
, @content VARBINARY (MAX)
, @description NVARCHAR (1000) = NULL
, @exposureCaseId UNIQUEIDENTIFIER = NULL
, @supersedesDocumentId UNIQUEIDENTIFIER = NULL""", OWNER + """
IF DATALENGTH (@content) NOT BETWEEN 1 AND 10485760 OR @contentHash <> HASHBYTES ('SHA2_256', @content)
   OR @contentType NOT IN ('application/pdf','application/vnd.openxmlformats-officedocument.spreadsheetml.sheet','image/png','image/jpeg','image/webp')
BEGIN
    ;THROW 51422, N'Choose a supported original file up to 10 MB.', 1;
END;
DECLARE @lock INT, @resource NVARCHAR (100) = CONCAT (N'MERIT.document.', @personDocumentId);
EXEC @lock = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 1000;
IF @lock < 0
BEGIN
    ;THROW 51409, N'This upload is busy. Check the document list before retrying.', 1;
END;
IF EXISTS (SELECT 1 FROM dbo.personDocument WHERE personDocumentId = @personDocumentId)
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.personDocument WHERE personDocumentId = @personDocumentId AND personId = @personId
                    AND contentHash = @contentHash AND isDeleted = 0
                    AND title = @title AND fileName = @fileName AND contentType = @contentType AND category = @category
                    AND documentDate = @documentDate AND source = @source
                    AND description IS NOT DISTINCT FROM @description AND supersedesDocumentId IS NOT DISTINCT FROM @supersedesDocumentId
                    AND ((exposureCaseId IS NULL AND @exposureCaseId IS NULL) OR exposureCaseId = @exposureCaseId))
    BEGIN
        ;THROW 51409, N'This upload was already saved with different content or details. Refresh to check the original before starting another upload.', 1;
    END;
END
ELSE
BEGIN
    IF @supersedesDocumentId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.personDocument
        WHERE personDocumentId = @supersedesDocumentId AND personId = @personId AND isDeleted = 0
          AND ((exposureCaseId IS NULL AND @exposureCaseId IS NULL) OR exposureCaseId = @exposureCaseId))
    BEGIN
        ;THROW 51422, N'Choose an earlier revision of this patient and case document.', 1;
    END;
    INSERT dbo.personDocument (personDocumentId, rootTenantId, owningTenantId, personId, exposureCaseId, supersedesDocumentId,
        title, fileName, contentType, category, documentDate, source, description, uploadedByUserId, contentHash, content)
    VALUES (@personDocumentId, @root, @owner, @personId, @exposureCaseId, @supersedesDocumentId, @title, @fileName,
        @contentType, @category, @documentDate, @source, @description, auth.udfSessionUserId (), @contentHash, @content);
END;""", permission='document.edit', validate=PERSON + CASE_WRITE,
    desc='Idempotent private original-file upload or new revision; retained originals are never overwritten.'),
]
