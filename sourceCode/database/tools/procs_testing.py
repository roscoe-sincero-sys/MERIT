"""Reviewed Maryland clinical testing plans, distinct from statutory notification clocks."""
from procs_care import proc, PERSON, CASE_WRITE, OWNER
from procs_common import BOTH

RESULTS = """
SELECT r.labResultId,r.collectedDate,r.sampleType,r.operator,r.valueCanonical,r.resultRowStatus,r.resultStatus
FROM dbo.labResult AS r JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId=r.analyteSpecimenId
JOIN config.analyte AS a ON a.analyteId=s.analyteId
WHERE r.personId=@personId AND r.isDeleted=0 AND a.code='PB' AND s.specimen='wholeBlood' AND s.canonicalUnit=N'ug/dL'
 AND r.resultRowStatus='valid' AND r.resultStatus IN ('final','corrected')
"""

PROCS = [
proc('uspListTestingPlans','read','@personId UNIQUEIDENTIFIER', '''
SELECT w.workTaskId,w.personId,w.exposureCaseId,w.labResultId,w.title,w.detail,w.queueCode,w.taskStatus,w.dueDate,
 w.isUrgent,w.createdDateUtc,w.completedUtc,w.resolutionNote,w.testingPlanJson
FROM dbo.workTask AS w WHERE w.personId=@personId AND w.isDeleted=0 AND w.queueCode IN ('testingReview','testingFollowUp','testingDischarge')
 AND (w.exposureCaseId IS NULL OR (auth.udfHasPermission(N'case.view')=1 AND EXISTS
 (SELECT 1 FROM dbo.exposureCase WHERE exposureCaseId=w.exposureCaseId AND isDeleted=0)))
ORDER BY CASE WHEN w.taskStatus='open' THEN 0 ELSE 1 END,w.isUrgent DESC,w.dueDate,w.createdDateUtc DESC;''', validate=PERSON),
proc('uspSaveTestingPlan','write','''@workTaskId UNIQUEIDENTIFIER
, @personId UNIQUEIDENTIFIER
, @planKind VARCHAR (20)
, @evidence NVARCHAR (1000)
, @clinicianConfirmed BIT
, @exposureCaseId UNIQUEIDENTIFIER = NULL
, @labResultId UNIQUEIDENTIFIER = NULL
, @targetDate DATE = NULL
, @remediationConfirmed BIT = 0''', PERSON + CASE_WRITE + OWNER + '''
IF auth.udfHasPermission(N'task.edit')=0 OR @clinicianConfirmed<>1 OR NULLIF(TRIM(@evidence),N'') IS NULL
BEGIN
    ;THROW 51422, N'Confirm Maryland applicability, source history and the clinician-approved plan; include its evidence.', 1;
END;
IF @planKind NOT IN ('screen12','screen24','confirmation','early','later','discharge')
BEGIN
    ;THROW 51422, N'Choose screening, confirmation, follow-up or discharge review.', 1;
END;
IF EXISTS(SELECT 1 FROM dbo.workTask WHERE workTaskId=@workTaskId)
BEGIN
    ;THROW 51409, N'This plan was already recorded. Review the existing history before submitting again.', 1;
END;
DECLARE @lock INT;
DECLARE @resource NVARCHAR(255)=CONCAT(N'MERIT.testing.',@personId);
EXEC @lock=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction';
IF @lock<0
BEGIN
    ;THROW 51409, N'Testing plan is busy. Try again.', 1;
END;
DECLARE @today DATE=auth.udfSessionToday (),@dob DATE=(SELECT dob FROM dbo.person WHERE personId=@personId),
 @draw DATE,@sample VARCHAR(20),@value DECIMAL(18,6),@op VARCHAR(4),@latest DATE,@due DATE,@urgent BIT=0,@advice NVARCHAR(600),@second UNIQUEIDENTIFIER;
IF @dob IS NULL OR @dob> @today
BEGIN
    ;THROW 51422, N'Verify the patient birth date before creating a childhood testing plan.', 1;
END;
DECLARE @r TABLE(labResultId UNIQUEIDENTIFIER,collectedDate DATE,sampleType VARCHAR(20),operator VARCHAR(4),valueCanonical DECIMAL(18,6),resultRowStatus VARCHAR(20),resultStatus VARCHAR(20));
INSERT @r
''' + RESULTS.lstrip() + ''';
IF @planKind IN ('screen12','screen24')
BEGIN
    IF @today>=DATEADD(MONTH,72,@dob)
    BEGIN
        ;THROW 51422, N'The childhood catch-up screening window ends before 72 months. Obtain an individual clinical plan.', 1;
    END;
    SET @labResultId=NULL;
    SET @latest=DATEADD(MONTH,CASE WHEN @planKind='screen12' THEN 12 ELSE 24 END,@dob);
    IF EXISTS(SELECT 1 FROM @r WHERE sampleType IN ('venous','capillary') AND collectedDate>=@latest
      AND collectedDate<DATEADD(MONTH,72,@dob) AND collectedDate<=@today)
    BEGIN
        ;THROW 51422, N'An age-appropriate screening result is already recorded. Verify the history instead of creating a duplicate missed-screening plan.', 1;
    END;
    SET @advice=N'Provider screening at the 12- or 24-month visit; if missed, arrange promptly. Verify outside records, residence, exemptions and missed-screening history. Certification is not tracked by MDE.';
END
ELSE
BEGIN
    SELECT @draw=collectedDate,@sample=sampleType,@value=valueCanonical,@op=operator FROM @r WHERE labResultId=@labResultId;
    IF @draw IS NULL OR @draw>@today OR @draw>=DATEADD(MONTH,72,@dob) OR @draw<DATEADD(MONTH,6,@dob)
    BEGIN
        ;THROW 51422, N'Choose a valid final blood lead result collected at age 6 to under 72 months. Other ages require an individual clinical plan.', 1;
    END;
    IF @planKind='confirmation'
    BEGIN
        IF @sample<>'capillary' OR @value<3.5 OR @op<>'='
        BEGIN
            ;THROW 51422, N'Confirmation starts at an elevated capillary draw; uncertain results need source clarification.', 1;
        END;
        IF EXISTS(SELECT 1 FROM @r WHERE sampleType='venous' AND collectedDate>=@draw)
        BEGIN
            ;THROW 51422, N'A venous result is already recorded on or after this draw. Review it before planning confirmation.', 1;
        END;
        IF EXISTS(SELECT 1 FROM @r AS r WHERE r.sampleType='capillary' AND r.valueCanonical>=3.5 AND r.operator IN ('=','>') AND r.collectedDate<@draw
          AND r.collectedDate>COALESCE((SELECT MAX(collectedDate) FROM @r WHERE sampleType='venous' AND collectedDate<@draw),'00010101'))
        BEGIN
            ;THROW 51422, N'An earlier unresolved elevated capillary draw exists. Use that initial draw; later capillary tests do not restart confirmation.', 1;
        END;
        SET @latest=CASE WHEN @value>=45 THEN @draw WHEN @value>=20 THEN DATEADD(DAY,14,@draw) WHEN @value>=10 THEN DATEADD(DAY,28,@draw) ELSE DATEADD(DAY,84,@draw) END;
        SET @urgent=CASE WHEN @value>=45 THEN 1 ELSE 0 END;
        SET @advice=CASE WHEN @urgent=1 THEN N'Urgent clinician coordination now: confirmation within 48 hours of the actual initial draw timestamp. Date-only data cannot establish the exact 48-hour cutoff; record the timestamp and clinician plan in evidence.'
          ELSE N'MDH March 2025 Table 2: confirmation within 12 weeks for 3.5 to under 10, 4 weeks for 10 to under 20, 2 weeks for 20 to under 45. Greater elevations require faster clinician assessment.' END;
    END
    ELSE IF @planKind IN ('early','later')
    BEGIN
        IF EXISTS(SELECT 1 FROM @r WHERE collectedDate>@draw)
        BEGIN
            ;THROW 51422, N'A newer blood lead test exists. Review the latest test before choosing the next follow-up anchor.', 1;
        END;
        IF @sample<>'venous' OR @op<>'='
        BEGIN
            ;THROW 51422, N'Follow-up intervals require an exact venous result; use clinical/source review for censored or capillary results.', 1;
        END;
        SET @urgent=CASE WHEN @value>=45 THEN 1 ELSE 0 END;
        SET @latest=CASE WHEN @value>=45 THEN @draw WHEN @value<3.5 THEN DATEADD(MONTH,3,@draw)
          WHEN @value>=20 THEN DATEADD(MONTH,CASE WHEN @planKind='early' THEN 1 ELSE 3 END,@draw)
          WHEN @value>=10 THEN DATEADD(MONTH,CASE WHEN @planKind='early' THEN 3 ELSE 6 END,@draw)
          ELSE DATEADD(MONTH,CASE WHEN @planKind='early' THEN 3 ELSE 9 END,@draw) END;
        SET @advice=CASE WHEN @urgent=1 THEN N'As soon as possible: urgent clinician coordination and individualized treatment plan; do not wait for a routine retest.'
          WHEN @value<3.5 THEN N'Below-reference monitoring: repeat every 3 months toward reviewed closure; remediation and two consecutive low results are required.'
          ELSE N'MDH March 2025 Table 4. Early: 3 months / 1-3 months / 2 weeks-1 month. Later, only after clinician-reviewed decline: 6-9 / 3-6 / 1-3 months. Select an earlier target within the approved clinical plan; never infer decline from test count alone.' END;
    END
    ELSE
    BEGIN
        IF @exposureCaseId IS NULL OR @remediationConfirmed<>1
        BEGIN
            ;THROW 51422, N'Choose the active case and attest completed environmental remediation with source evidence.', 1;
        END;
        DECLARE @recent TABLE(id UNIQUEIDENTIFIER,draw DATE,value DECIMAL(18,6),op VARCHAR(4));
        INSERT @recent SELECT TOP(2) labResultId,collectedDate,valueCanonical,operator FROM @r
          WHERE collectedDate<=@today AND collectedDate>=(SELECT r.collectedDate FROM dbo.exposureCase AS c JOIN dbo.labResult AS r ON r.labResultId=c.qualifyingLabResultId WHERE c.exposureCaseId=@exposureCaseId)
          ORDER BY collectedDate DESC,labResultId;
        IF (SELECT COUNT(*) FROM @recent)<>2 OR EXISTS(SELECT 1 FROM @recent WHERE value IS NULL OR op IS NULL OR NOT ((op='=' AND value<3.5) OR (op='<' AND value<=3.5)))
          OR (SELECT MAX(draw) FROM @recent)<DATEADD(MONTH,3,(SELECT MIN(draw) FROM @recent))
          OR @draw<>(SELECT MAX(draw) FROM @recent)
        BEGIN
            ;THROW 51422, N'Closure needs the latest two consecutive valid blood lead tests below 3.5, at least three calendar months apart, and completed environmental remediation. Select the latest result.', 1;
        END;
        SELECT @second=id FROM @recent WHERE id<>@labResultId;
        SET @latest=@today;
        SET @advice=N'MDH March 2025 Table 6: remediation reviewed and two consecutive below-reference tests at least three calendar months apart. This records readiness evidence; it does not close the case.';
    END;
END;
IF EXISTS(SELECT 1 FROM dbo.exposureCase WHERE exposureCaseId=@exposureCaseId AND caseStatus='closed')
BEGIN
    ;THROW 51422, N'Use an active case or a patient-only testing plan.', 1;
END;
SET @due=COALESCE(@targetDate,@latest);
IF @due>@latest OR (@draw IS NOT NULL AND @due<@draw) OR @due<@dob
BEGIN
    ;THROW 51422, N'The target cannot extend the guideline window or precede the source draw/birth date. Urgent plans require coordination now.', 1;
END;
IF EXISTS(SELECT 1 FROM dbo.workTask WHERE personId=@personId AND isDeleted=0 AND taskStatus='open' AND queueCode='testingFollowUp'
 AND (JSON_VALUE(testingPlanJson,'$.kind')=@planKind OR (@planKind IN ('early','later') AND JSON_VALUE(testingPlanJson,'$.kind') IN ('early','later'))))
BEGIN
    ;THROW 51422, N'An open plan of this type exists. Complete it with a real test or dismiss it with a documented clinical reason before replacing it.', 1;
END;
DECLARE @json NVARCHAR(MAX)=(SELECT 'MDH-2025-03' AS policyVersion,@planKind AS kind,@labResultId AS anchorResultId,@draw AS anchorDate,
 @second AS previousLowResultId,@remediationConfirmed AS remediationConfirmed,@evidence AS clinicalEvidence FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
INSERT dbo.workTask(workTaskId,rootTenantId,owningTenantId,personId,exposureCaseId,labResultId,queueCode,title,detail,dueDate,isUrgent,testingPlanJson,taskStatus,completedUtc,completedByUserId,resolutionNote)
VALUES(@workTaskId,@root,@owner,@personId,@exposureCaseId,@labResultId,CASE WHEN @planKind='discharge' THEN 'testingDischarge' ELSE 'testingFollowUp' END,
 CONCAT(N'Maryland testing: ',@planKind),CONCAT(@advice,N' Source review: ',@evidence),@due,@urgent,@json,
 CASE WHEN @planKind='discharge' THEN 'done' ELSE 'open' END,CASE WHEN @planKind='discharge' THEN SYSUTCDATETIME() END,
 CASE WHEN @planKind='discharge' THEN auth.udfSessionUserId() END,CASE WHEN @planKind='discharge' THEN @evidence END);
UPDATE dbo.workTask SET taskStatus='done',completedUtc=SYSUTCDATETIME(),completedByUserId=auth.udfSessionUserId(),resolutionNote=N'Clinician-approved testing plan recorded.'
 WHERE personId=@personId AND queueCode='testingReview' AND taskStatus='open' AND isDeleted=0;
''',permission='person.edit'),
proc('uspResolveTestingPlan','write','''@personId UNIQUEIDENTIFIER
, @workTaskId UNIQUEIDENTIFIER
, @evidence NVARCHAR(1000)
, @confirmed BIT
, @completedLabResultId UNIQUEIDENTIFIER = NULL
, @dismiss BIT = 0''', PERSON + '''
IF auth.udfHasPermission(N'person.edit')=0 OR auth.udfHasPermission(N'task.edit')=0 OR @confirmed<>1 OR NULLIF(TRIM(@evidence),N'') IS NULL
BEGIN
    ;THROW 51422, N'Confirm the actual test or clinical reason and provide source evidence.', 1;
END;
DECLARE @case UNIQUEIDENTIFIER,@json NVARCHAR(MAX),@anchor DATE,@kind VARCHAR(20),@dob DATE=(SELECT dob FROM dbo.person WHERE personId=@personId);
SELECT @case=exposureCaseId,@json=testingPlanJson FROM dbo.workTask WITH(UPDLOCK,HOLDLOCK)
 WHERE workTaskId=@workTaskId AND personId=@personId AND queueCode='testingFollowUp' AND taskStatus='open' AND isDeleted=0;
IF @json IS NULL OR (@case IS NOT NULL AND (auth.udfHasPermission(N'case.edit')=0 OR NOT EXISTS(SELECT 1 FROM dbo.exposureCase WHERE exposureCaseId=@case AND personId=@personId AND isDeleted=0)))
BEGIN
    ;THROW 51404, N'The open testing plan is unavailable.', 1;
END;
SET @kind=JSON_VALUE(@json,'$.kind');
SET @anchor=TRY_CONVERT(DATE,JSON_VALUE(@json,'$.anchorDate'));
IF @dismiss=0 AND NOT EXISTS(SELECT 1 FROM dbo.labResult AS r JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId=r.analyteSpecimenId
 JOIN config.analyte AS a ON a.analyteId=s.analyteId WHERE r.labResultId=@completedLabResultId AND r.personId=@personId AND r.isDeleted=0
 AND r.resultRowStatus='valid' AND r.resultStatus IN ('final','corrected') AND a.code='PB' AND s.specimen='wholeBlood' AND s.canonicalUnit=N'ug/dL'
 AND r.collectedDate<=auth.udfSessionToday() AND ((@kind IN ('screen12','screen24') AND r.sampleType IN ('venous','capillary')
 AND r.collectedDate>=DATEADD(MONTH,CASE WHEN @kind='screen12' THEN 12 ELSE 24 END,@dob)
 AND r.collectedDate<DATEADD(MONTH,72,@dob))
 OR (@kind NOT IN ('screen12','screen24') AND r.sampleType='venous' AND r.collectedDate>=@anchor AND r.labResultId<>TRY_CONVERT(UNIQUEIDENTIFIER,JSON_VALUE(@json,'$.anchorResultId')))))
BEGIN
    ;THROW 51422, N'Choose a valid subsequent venous result for this patient, or an age-appropriate screening result. A later capillary test cannot complete confirmation.', 1;
END;
UPDATE dbo.workTask SET taskStatus=CASE WHEN @dismiss=1 THEN 'dismissed' ELSE 'done' END,completedUtc=SYSUTCDATETIME(),completedByUserId=auth.udfSessionUserId(),resolutionNote=@evidence,
 testingPlanJson=JSON_MODIFY(testingPlanJson,'$.completedResultId',CASE WHEN @dismiss=0 THEN CONVERT(VARCHAR(36),@completedLabResultId) END)
 WHERE workTaskId=@workTaskId;
''',permission='person.edit'),
proc('uspReconcileTestingReview','write','@labResultId UNIQUEIDENTIFIER', '''
IF auth.udfHasPermission(N'result.edit')=0 AND auth.udfHasPermission(N'intake.upload')=0 AND auth.udfHasPermission(N'config.edit')=0
BEGIN
    ;THROW 51403, N'Result editing, intake or configuration permission is required.', 1;
END;
DECLARE @person UNIQUEIDENTIFIER,@root UNIQUEIDENTIFIER,@owner UNIQUEIDENTIFIER,@value DECIMAL(18,6),@md BIT;
SELECT @person=r.personId,@root=r.rootTenantId,@owner=r.owningTenantId,@value=r.valueCanonical,
 @md=CASE WHEN UPPER(config.udfReceiptField(r.reportedSnapshotJson,NULL,'state'))='MD' THEN 1 ELSE 0 END
FROM dbo.labResult AS r JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId=r.analyteSpecimenId JOIN config.analyte AS a ON a.analyteId=s.analyteId
WHERE r.labResultId=@labResultId AND r.resultRowStatus='valid' AND r.resultStatus IN ('final','corrected') AND r.isDeleted=0
 AND a.code='PB' AND s.specimen='wholeBlood' AND s.canonicalUnit=N'ug/dL' AND r.ageAtCollectionMonths BETWEEN 6 AND 71;
IF @person IS NOT NULL AND (@md=1 OR EXISTS(SELECT 1 FROM dbo.workTask WHERE personId=@person AND testingPlanJson IS NOT NULL AND isDeleted=0))
BEGIN
    DECLARE @lock INT;
    DECLARE @resource NVARCHAR(255)=CONCAT(N'MERIT.testing.',@person);
    EXEC @lock=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction';
    IF @lock<0
    BEGIN
        ;THROW 51409, N'Testing review is busy. Try again.', 1;
    END;
    UPDATE dbo.workTask SET taskStatus='dismissed',completedUtc=SYSUTCDATETIME(),completedByUserId=auth.udfSessionUserId(),
     resolutionNote=N'Maryland clinical timing requires the reviewed testing-plan workflow; generic confirmatory intervals are not used.'
     WHERE personId=@person AND queueCode='confirmatoryTest' AND taskStatus='open' AND isDeleted=0;
    UPDATE dbo.workTask SET isUrgent=CASE WHEN @value>=45 THEN 1 ELSE isUrgent END
     WHERE personId=@person AND queueCode='testingReview' AND taskStatus='open' AND isDeleted=0;
    IF @@ROWCOUNT=0 INSERT dbo.workTask(workTaskId,rootTenantId,owningTenantId,personId,labResultId,queueCode,title,detail,isUrgent)
    VALUES(NEWID(),@root,@owner,@person,@labResultId,'testingReview',N'Review Maryland blood lead testing plan',
     N'New result: verify Maryland applicability and original history. Review screening, unresolved initial capillary confirmation, clinician-selected early/later venous retesting and remediation/closure readiness. Existing plan anchors are retained. Values at least 45 require urgent clinician coordination; do not wait for routine follow-up.',CASE WHEN @value>=45 THEN 1 ELSE 0 END);
END;
''',permission=None,grants=BOTH)
]
