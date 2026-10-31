"""Case, case activity, cross-link task and investigation procedures."""
from procs_common import NEWID, WEB, BOTH, CONCURRENCY, not_found

CASE_VISIBLE = not_found("@exposureCaseId", "dbo.exposureCase", "exposureCaseId", "The case")
INV_VISIBLE = not_found("@investigationId", "dbo.investigation", "investigationId", "The investigation")

PROCS = [
    {
        "schema": "dbo", "name": "uspReopenCase", "kind": "write", "grants": WEB, "ticket": "T081",
        "permission": "case.edit", "desc": "Reopens a closed case with a reason and a review task, retaining the closure activity history.",
        "params": "@exposureCaseId UNIQUEIDENTIFIER\n, @reason NVARCHAR (2000)\n, @rowVersion BINARY (8)",
        "key": "CONCAT (N'exposureCaseId=', @exposureCaseId)", "validate": CASE_VISIBLE,
        "body": f"""
DECLARE @person UNIQUEIDENTIFIER, @analyte UNIQUEIDENTIFIER, @root UNIQUEIDENTIFIER, @owner UNIQUEIDENTIFIER;
SELECT @person = personId, @analyte = analyteId, @root = rootTenantId, @owner = owningTenantId FROM dbo.exposureCase WITH (UPDLOCK, HOLDLOCK) WHERE exposureCaseId = @exposureCaseId;
IF EXISTS (SELECT 1 FROM dbo.exposureCase WITH (UPDLOCK, HOLDLOCK) WHERE personId = @person AND analyteId = @analyte AND caseStatus <> 'closed' AND isDeleted = 0)
BEGIN
    ;THROW 51422, N'An open case already exists for this person and analyte.', 1;
END;
UPDATE dbo.exposureCase SET caseStatus = 'open', closedDate = NULL, closureReason = NULL
 WHERE exposureCaseId = @exposureCaseId AND caseStatus = 'closed' AND rowVersion = @rowVersion AND isDeleted = 0;
{CONCURRENCY}
INSERT dbo.caseActivity (caseActivityId, rootTenantId, owningTenantId, exposureCaseId, activityType, activityDate, workerUserId, note)
VALUES ({NEWID}, @root, @owner, @exposureCaseId, 'statusChange', auth.udfSessionToday (), auth.udfSessionUserId (), CONCAT (N'Reopened: ', @reason));
INSERT dbo.workTask (workTaskId, rootTenantId, owningTenantId, queueCode, title, exposureCaseId, personId)
VALUES ({NEWID}, @root, @owner, 'caseFollowUp', N'Review reopened case', @exposureCaseId, @person);
SET @Comments = N'Case reopened with review task.';
""",
    },
    {
        "schema": "dbo", "name": "uspOpenExposureCase", "kind": "write", "grants": BOTH, "ticket": "T045",
        "permission": "case.open",
        "desc": "Opens a case for a person and analyte from a qualifying result. The owning tenant comes from the case "
                "ownership rules: child and pregnancy cases go to the local health department serving the county of the "
                "current primary residence; adult cases go to the tenant named by setting adultCaseOwnerTenantCode; "
                "otherwise the root tenant. Gives the owner access to the person.",
        "params": """@exposureCaseId        UNIQUEIDENTIFIER
, @personId              UNIQUEIDENTIFIER
, @analyteId             UNIQUEIDENTIFIER
, @population            VARCHAR (10)
, @classification        VARCHAR (12)
, @qualifyingLabResultId UNIQUEIDENTIFIER
, @workflowTemplate      VARCHAR (30)
, @openedDate            DATE""",
        "key": "CONCAT (N'exposureCaseId=', @exposureCaseId, N', personId=', @personId, N', analyteId=', @analyteId)",
        "notes": "One open case per person per analyte is enforced by UX_dbo_exposureCase_OneOpenPerAnalyte as well as by "
                 "the check here, which gives the user a readable message. Returns caseNumber, owningTenantId and "
                 "owningTenantName.",
        "body": f"""
DECLARE @root UNIQUEIDENTIFIER = (SELECT rootTenantId FROM dbo.person WHERE personId = @personId);
DECLARE @today DATE = auth.udfSessionToday ();
DECLARE @owner UNIQUEIDENTIFIER;

IF EXISTS (SELECT 1 FROM dbo.exposureCase WITH (UPDLOCK, HOLDLOCK)
            WHERE personId = @personId AND analyteId = @analyteId AND caseStatus <> 'closed' AND isDeleted = 0)
BEGIN
    ;THROW 51422, N'This person already has an open case for this analyte; the result attaches to it.', 1;
END;

IF @population IN ('child', 'pregnant')
BEGIN
    SELECT TOP (1) @owner = tj.tenantId
      FROM dbo.personLocation AS pl
      JOIN dbo.location AS l ON l.locationId = pl.locationId
      JOIN config.subJurisdiction AS sj ON sj.rootTenantId = l.rootTenantId AND sj.countyFips = l.countyFips
      JOIN auth.tenantJurisdiction AS tj ON tj.subJurisdictionId = sj.subJurisdictionId AND tj.isDeleted = 0
      JOIN auth.tenant AS t ON t.tenantId = tj.tenantId AND t.tenantType = 'localHealthDepartment' AND t.isActive = 1
     WHERE pl.personId = @personId AND pl.isDeleted = 0 AND pl.role = 'residence' AND pl.isPrimary = 1
       AND pl.startDate <= @today AND (pl.endDate IS NULL OR pl.endDate >= @today);
END
ELSE IF @population = 'adult'
BEGIN
    SELECT TOP (1) @owner = t.tenantId
      FROM config.jurisdictionSetting AS s
      JOIN auth.tenant AS t ON t.tenantCode = s.settingValue AND t.rootTenantId = @root AND t.isActive = 1
     WHERE s.rootTenantId = @root AND s.settingKey = 'adultCaseOwnerTenantCode' AND s.isDeleted = 0
       AND s.effectiveFrom <= @today AND (s.effectiveTo IS NULL OR s.effectiveTo >= @today);
END;

SET @owner = COALESCE (@owner, @root);

DECLARE @code VARCHAR (8) = (SELECT code FROM config.analyte WHERE analyteId = @analyteId);
DECLARE @number VARCHAR (20) = CONCAT (@code, '-', YEAR (@openedDate), '-',
                                       RIGHT (CONCAT ('000000', NEXT VALUE FOR dbo.caseNumberSequence), 6));

INSERT dbo.exposureCase (exposureCaseId, rootTenantId, owningTenantId, caseNumber, personId, analyteId, population,
                         classification, computedClassification, caseStatus, qualifyingLabResultId, highestLabResultId,
                         workflowTemplate, openedDate)
VALUES (@exposureCaseId, @root, @owner, @number, @personId, @analyteId, @population, @classification, @classification,
        'open', @qualifyingLabResultId, @qualifyingLabResultId, @workflowTemplate, @openedDate);

INSERT dbo.caseActivity (caseActivityId, rootTenantId, owningTenantId, exposureCaseId, activityType, activityDate,
                         workerUserId, note)
VALUES ({NEWID}, @root, @owner, @exposureCaseId, 'statusChange', @openedDate, auth.udfSessionUserId (),
        CONCAT (N'Case opened as ', @classification, N' from a qualifying result.'));

EXEC dbo.uspRefreshPersonAccess @personId = @personId;

SELECT @number AS caseNumber, @owner AS owningTenantId, t.tenantName AS owningTenantName
  FROM auth.tenant AS t WHERE t.tenantId = @owner;

SET @Comments = N'Case opened.';
""",
    },
    {
        "schema": "dbo", "name": "uspAttachResultToCase", "kind": "write", "grants": BOTH, "ticket": "T045",
        "permission": "case.open",
        "desc": "Attaches a new qualifying result to the open case: updates the highest result and, when the rules compute "
                "a stronger classification, upgrades it (suspect to confirmed). A staff override is kept; the computed "
                "value is stored beside it.",
        "params": """@exposureCaseId         UNIQUEIDENTIFIER
, @labResultId            UNIQUEIDENTIFIER
, @computedClassification VARCHAR (12)
, @isHighest              BIT""",
        "key": "CONCAT (N'exposureCaseId=', @exposureCaseId, N', labResultId=', @labResultId)",
        "body": f"""
DECLARE @before VARCHAR (12), @after VARCHAR (12), @root UNIQUEIDENTIFIER, @owner UNIQUEIDENTIFIER;

SELECT @before = classification, @root = rootTenantId, @owner = owningTenantId
  FROM dbo.exposureCase WITH (UPDLOCK) WHERE exposureCaseId = @exposureCaseId AND isDeleted = 0;

UPDATE dbo.exposureCase
   SET highestLabResultId     = IIF (@isHighest = 1, @labResultId, highestLabResultId)
     , computedClassification = @computedClassification
     , classification         = IIF (overrideReason IS NULL, @computedClassification, classification)
     , @after                 = IIF (overrideReason IS NULL, @computedClassification, classification)
 WHERE exposureCaseId = @exposureCaseId AND isDeleted = 0;

IF @before <> @after
BEGIN
    INSERT dbo.caseActivity (caseActivityId, rootTenantId, owningTenantId, exposureCaseId, activityType, activityDate,
                             workerUserId, note)
    VALUES ({NEWID}, @root, @owner, @exposureCaseId, 'statusChange', auth.udfSessionToday (),
            auth.udfSessionUserId (), CONCAT (N'Classification changed from ', @before, N' to ', @after,
                                               N' by a new result.'));
END;

SELECT @before AS classificationBefore, @after AS classificationAfter;

SET @Comments = N'Result attached to case.';
""",
    },
    {
        "schema": "dbo", "name": "uspGetExposureCase", "kind": "read", "grants": BOTH, "ticket": "T080",
        "permission": "case.view",
        "desc": "The case page: the case with its person and owner, the person's results for the analyte, activities, "
                "linked investigations, open tasks, and the person's primary contact.",
        "params": "@exposureCaseId UNIQUEIDENTIFIER",
        "key": "CONCAT (N'exposureCaseId=', @exposureCaseId)",
        "validate": CASE_VISIBLE,
        "body": """
DECLARE @personId UNIQUEIDENTIFIER, @analyteId UNIQUEIDENTIFIER;
SELECT @personId = personId, @analyteId = analyteId FROM dbo.exposureCase WHERE exposureCaseId = @exposureCaseId;

SELECT c.exposureCaseId, c.caseNumber, c.personId, p.lastName, p.firstName, p.dob, p.sex, c.analyteId,
       a.code AS analyteCode, a.displayName AS analyteName, c.population, c.classification, c.computedClassification,
       c.overrideReason, c.caseStatus, c.exposureCategory, c.workflowTemplate, c.openedDate, c.closedDate,
       c.closureReason, c.assignedToUserId, u.displayName AS assignedToName, c.owningTenantId,
       t.tenantName AS owningTenantName, c.qualifyingLabResultId, c.highestLabResultId,
       h.valueCanonical AS highestValue, h.unitCanonical AS highestUnit, h.collectedDate AS highestCollectedDate,
       c.rowVersion
  FROM dbo.exposureCase AS c
  JOIN dbo.person AS p ON p.personId = c.personId
  JOIN config.analyte AS a ON a.analyteId = c.analyteId
  LEFT JOIN auth.appUser AS u ON u.appUserId = c.assignedToUserId
  LEFT JOIN auth.tenant AS t ON t.tenantId = c.owningTenantId
  LEFT JOIN dbo.labResult AS h ON h.labResultId = c.highestLabResultId
 WHERE c.exposureCaseId = @exposureCaseId;

SELECT r.labResultId, r.collectedDate, r.operator, r.valueCanonical, r.unitCanonical, r.sampleType, r.testMethod,
       r.resultStatus, r.resultRowStatus, s.specimen, lab.organizationName AS labName
  FROM dbo.labResult AS r
  JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId = r.analyteSpecimenId
  LEFT JOIN dbo.organization AS lab ON lab.organizationId = r.labOrganizationId
 WHERE r.personId = @personId AND s.analyteId = @analyteId AND r.isDeleted = 0
 ORDER BY r.collectedDate DESC;

SELECT ca.caseActivityId, ca.activityType, ca.activityDate, ca.outcome, ca.note, u.displayName AS workerName,
       cp.lastName AS contactLastName, cp.firstName AS contactFirstName, ca.createdDateUtc
  FROM dbo.caseActivity AS ca
  LEFT JOIN auth.appUser AS u ON u.appUserId = ca.workerUserId
  LEFT JOIN dbo.person AS cp ON cp.personId = ca.contactPersonId
 WHERE ca.exposureCaseId = @exposureCaseId AND ca.isDeleted = 0
 ORDER BY ca.activityDate DESC, ca.createdDateUtc DESC;

SELECT i.investigationId, i.investigationNumber, i.investigationType, i.investigationStatus, i.requestedDate,
       i.completedDate, COALESCE (l.addressLine1, o.organizationName, es.sourceDescription) AS targetLabel,
       l.city AS targetCity
  FROM dbo.investigationCase AS ic
  JOIN dbo.investigation AS i ON i.investigationId = ic.investigationId AND i.isDeleted = 0
  LEFT JOIN dbo.location AS l ON l.locationId = i.locationId
  LEFT JOIN dbo.organization AS o ON o.organizationId = i.organizationId
  LEFT JOIN dbo.exposureSource AS es ON es.exposureSourceId = i.exposureSourceId
 WHERE ic.exposureCaseId = @exposureCaseId AND ic.isDeleted = 0
 ORDER BY i.requestedDate DESC;

SELECT w.workTaskId, w.queueCode, w.title, w.detail, w.dueDate, w.isUrgent, w.taskStatus, u.displayName AS assignedToName,
       CAST (CASE WHEN auth.udfHasPermission (N'task.edit') = 1 AND EXISTS (SELECT 1 FROM auth.tvfTenantWritePredicate (w.rootTenantId, w.owningTenantId, w.isDeleted)) THEN 1 ELSE 0 END AS BIT) AS canEdit
  FROM dbo.workTask AS w
  LEFT JOIN auth.appUser AS u ON u.appUserId = w.assignedToUserId
 WHERE w.exposureCaseId = @exposureCaseId AND w.isDeleted = 0 AND w.taskStatus = 'open'
 ORDER BY w.isUrgent DESC, w.dueDate;

SELECT TOP (1) r.relatedPersonId AS contactPersonId, cp.lastName, cp.firstName, r.role,
       (SELECT TOP (1) contactValue FROM dbo.personContactMethod AS m
         WHERE m.personId = r.relatedPersonId AND m.contactType = 'phone' AND m.isDeleted = 0
         ORDER BY m.isPreferred DESC) AS phone
  FROM dbo.personRelationship AS r
  JOIN dbo.person AS cp ON cp.personId = r.relatedPersonId
 WHERE r.personId = @personId AND r.isDeleted = 0 AND r.isPrimaryContact = 1
   AND (r.endDate IS NULL OR r.endDate >= auth.udfSessionToday ());
""",
    },
    {
        "schema": "dbo", "name": "uspListExposureCases", "kind": "read", "grants": BOTH, "ticket": "T080",
        "permission": "case.view",
        "desc": "One page of cases visible to the caller, filtered by status, analyte, classification, population, owning "
                "tenant, assignee (me, a person, or nobody) and a case number or last name, sorted by opened date, case "
                "number, classification or person.",
        "params": """@openOnly         BIT = 1
, @assignedToMe     BIT = 0
, @analyteId        UNIQUEIDENTIFIER = NULL
, @classification   VARCHAR (12) = NULL
, @population       VARCHAR (10) = NULL
, @owningTenantId   UNIQUEIDENTIFIER = NULL
, @assignedToUserId UNIQUEIDENTIFIER = NULL
, @unassignedOnly   BIT = 0
, @search           NVARCHAR (60) = NULL
, @sort             VARCHAR (20) = 'opened'
, @descending       BIT = 1
, @pageNumber       INT = 1
, @pageSize         INT = 25""",
        # The search text is a name: it is never logged.
        "key": "CONCAT (N'openOnly=', @openOnly, N', assignedToMe=', @assignedToMe, N', unassignedOnly=', @unassignedOnly, "
               "N', classification=', @classification, N', population=', @population, N', sort=', @sort, N', pageNumber=', @pageNumber)",
        "validate": """IF @pageNumber < 1 OR @pageSize NOT BETWEEN 1 AND 500
BEGIN
    ;THROW 50000, N'@pageNumber must be 1 or more and @pageSize between 1 and 500.', 1;
END;
IF @sort NOT IN ('opened', 'case', 'classification', 'person')
BEGIN
    ;THROW 50000, N'@sort must be opened, case, classification or person.', 1;
END;""",
        "body": """
DECLARE @me UNIQUEIDENTIFIER = auth.udfSessionUserId ();
-- The search text matches literally: LIKE wildcards in it are escaped.
DECLARE @like NVARCHAR (200) = REPLACE (REPLACE (REPLACE (NULLIF (LTRIM (RTRIM (@search)), N''), N'[', N'[[]'), N'%', N'[%]'), N'_', N'[_]');

SELECT c.exposureCaseId, c.caseNumber, c.personId, p.lastName, p.firstName, p.dob, a.code AS analyteCode,
       c.population, c.classification, c.caseStatus, c.openedDate, u.displayName AS assignedToName,
       t.tenantName AS owningTenantName, COUNT (*) OVER () AS totalCount
  FROM dbo.exposureCase AS c
  JOIN dbo.person AS p ON p.personId = c.personId
  JOIN config.analyte AS a ON a.analyteId = c.analyteId
  LEFT JOIN auth.appUser AS u ON u.appUserId = c.assignedToUserId
  LEFT JOIN auth.tenant AS t ON t.tenantId = c.owningTenantId
 WHERE c.isDeleted = 0
   AND (@openOnly = 0 OR c.caseStatus <> 'closed')
   AND (@assignedToMe = 0 OR c.assignedToUserId = @me)
   AND (@unassignedOnly = 0 OR c.assignedToUserId IS NULL)
   AND (@assignedToUserId IS NULL OR c.assignedToUserId = @assignedToUserId)
   AND (@analyteId IS NULL OR c.analyteId = @analyteId)
   AND (@classification IS NULL OR c.classification = @classification)
   AND (@population IS NULL OR c.population = @population)
   AND (@owningTenantId IS NULL OR c.owningTenantId = @owningTenantId)
   AND (@like IS NULL OR c.caseNumber LIKE N'%' + @like + N'%' OR p.lastName LIKE @like + N'%')
 ORDER BY CASE WHEN @sort = 'opened' AND @descending = 0 THEN c.openedDate END ASC,
          CASE WHEN @sort = 'opened' AND @descending = 1 THEN c.openedDate END DESC,
          CASE WHEN @sort = 'case' AND @descending = 0 THEN c.caseNumber END ASC,
          CASE WHEN @sort = 'case' AND @descending = 1 THEN c.caseNumber END DESC,
          -- Most serious first when ascending.
          CASE WHEN @sort = 'classification' AND @descending = 0
               THEN CASE c.classification WHEN 'confirmed' THEN 1 WHEN 'suspect' THEN 2 ELSE 3 END END ASC,
          CASE WHEN @sort = 'classification' AND @descending = 1
               THEN CASE c.classification WHEN 'confirmed' THEN 1 WHEN 'suspect' THEN 2 ELSE 3 END END DESC,
          CASE WHEN @sort = 'person' AND @descending = 0 THEN p.lastName END ASC,
          CASE WHEN @sort = 'person' AND @descending = 0 THEN p.firstName END ASC,
          CASE WHEN @sort = 'person' AND @descending = 1 THEN p.lastName END DESC,
          CASE WHEN @sort = 'person' AND @descending = 1 THEN p.firstName END DESC,
          c.openedDate DESC, c.exposureCaseId
OFFSET (@pageNumber - 1) * @pageSize ROWS
 FETCH NEXT @pageSize ROWS ONLY
OPTION (RECOMPILE);
""",
    },
    {
        "schema": "dbo", "name": "uspListCaseOwners", "kind": "read", "grants": WEB, "ticket": "T080",
        "permission": "case.view",
        "desc": "The tenants that own a case the caller can see, by name, for the case list's owner filter.",
        "params": "",
        "key": "NULL",
        "body": """
SELECT t.tenantId, t.tenantName
  FROM auth.tenant AS t
 WHERE EXISTS (SELECT 1 FROM dbo.exposureCase AS c WHERE c.owningTenantId = t.tenantId AND c.isDeleted = 0)
 ORDER BY t.tenantName, t.tenantId;
""",
    },
    {
        "schema": "dbo", "name": "uspUpdateExposureCase", "kind": "write", "grants": WEB, "ticket": "T080",
        "permission": "case.edit",
        "desc": "Assigns a case, sets its exposure category, or moves it between open and monitoring.",
        "params": """@exposureCaseId   UNIQUEIDENTIFIER
, @assignedToUserId UNIQUEIDENTIFIER = NULL
, @exposureCategory VARCHAR (20)
, @caseStatus       VARCHAR (12)
, @rowVersion       BINARY (8)""",
        "key": "CONCAT (N'exposureCaseId=', @exposureCaseId)",
        "validate": CASE_VISIBLE,
        "body": f"""
IF @caseStatus NOT IN ('open', 'monitoring')
BEGIN
    ;THROW 51422, N'Use Close case to close a case, so a closure reason is recorded.', 1;
END;

UPDATE dbo.exposureCase
   SET assignedToUserId = @assignedToUserId, exposureCategory = @exposureCategory, caseStatus = @caseStatus
 WHERE exposureCaseId = @exposureCaseId AND isDeleted = 0 AND caseStatus <> 'closed' AND rowVersion = @rowVersion;
{CONCURRENCY}

SET @Comments = N'Case updated.';
""",
    },
    {
        "schema": "dbo", "name": "uspOverrideCaseClassification", "kind": "write", "grants": WEB, "ticket": "T080",
        "permission": "case.edit",
        "desc": "Staff override of a case's classification, or of its population, with a required reason. The computed "
                "classification is kept beside it.",
        "params": """@exposureCaseId UNIQUEIDENTIFIER
, @classification VARCHAR (12)
, @population     VARCHAR (10)
, @reason         NVARCHAR (500)
, @rowVersion     BINARY (8)""",
        "key": "CONCAT (N'exposureCaseId=', @exposureCaseId, N', classification=', @classification)",
        "validate": CASE_VISIBLE + """

IF NULLIF (LTRIM (RTRIM (@reason)), N'') IS NULL
BEGIN
    ;THROW 51422, N'A reason is required to override a classification or population.', 1;
END;""",
        "body": f"""
DECLARE @root UNIQUEIDENTIFIER, @owner UNIQUEIDENTIFIER, @before VARCHAR (12), @beforePop VARCHAR (10);
SELECT @root = rootTenantId, @owner = owningTenantId, @before = classification, @beforePop = population
  FROM dbo.exposureCase WHERE exposureCaseId = @exposureCaseId;

UPDATE dbo.exposureCase
   SET classification = @classification, population = @population, overrideReason = @reason
 WHERE exposureCaseId = @exposureCaseId AND isDeleted = 0 AND rowVersion = @rowVersion;
{CONCURRENCY}

INSERT dbo.caseActivity (caseActivityId, rootTenantId, owningTenantId, exposureCaseId, activityType, activityDate,
                         workerUserId, note)
VALUES ({NEWID}, @root, @owner, @exposureCaseId, 'statusChange', auth.udfSessionToday (),
        auth.udfSessionUserId (),
        CONCAT (N'Override: ', @before, N'/', @beforePop, N' to ', @classification, N'/', @population, N'. Reason: ', @reason));

SET @Comments = N'Classification overridden.';
""",
    },
    {
        "schema": "dbo", "name": "uspSaveCaseActivity", "kind": "write", "grants": WEB, "ticket": "T080",
        "permission": "case.edit",
        "desc": "Logs a contact attempt, education, referral, medical management note or letter on a case.",
        "params": """@caseActivityId  UNIQUEIDENTIFIER
, @exposureCaseId  UNIQUEIDENTIFIER
, @activityType    VARCHAR (30)
, @activityDate    DATE
, @contactPersonId UNIQUEIDENTIFIER = NULL
, @outcome         VARCHAR (30) = NULL
, @note            NVARCHAR (2000) = NULL""",
        "key": "CONCAT (N'caseActivityId=', @caseActivityId, N', exposureCaseId=', @exposureCaseId)",
        "validate": CASE_VISIBLE,
        "notes": "Returns failedContactAttempts: the count of contact attempts on the case that did not reach anyone, so "
                 "the application can queue a letter once the jurisdiction's limit is reached.",
        "body": """
INSERT dbo.caseActivity (caseActivityId, rootTenantId, owningTenantId, exposureCaseId, activityType, activityDate,
                         workerUserId, contactPersonId, outcome, note)
SELECT @caseActivityId, rootTenantId, owningTenantId, @exposureCaseId, @activityType, @activityDate,
       auth.udfSessionUserId (), @contactPersonId, @outcome, @note
  FROM dbo.exposureCase WHERE exposureCaseId = @exposureCaseId;

SELECT COUNT (*) AS failedContactAttempts
  FROM dbo.caseActivity
 WHERE exposureCaseId = @exposureCaseId AND activityType = 'contactAttempt' AND isDeleted = 0
   AND outcome IN ('noAnswer', 'leftMessage', 'wrongNumber');

SET @Comments = N'Activity logged.';
""",
    },
    {
        "schema": "dbo", "name": "uspCloseExposureCase", "kind": "write", "grants": WEB, "ticket": "T080",
        "permission": "case.edit",
        "desc": "Closes a case with a required reason and date; open tasks on it are dismissed and person access is "
                "refreshed.",
        "params": """@exposureCaseId UNIQUEIDENTIFIER
, @closureReason  VARCHAR (30)
, @closedDate     DATE
, @note           NVARCHAR (2000) = NULL
, @rowVersion     BINARY (8)""",
        "key": "CONCAT (N'exposureCaseId=', @exposureCaseId, N', closureReason=', @closureReason)",
        "validate": CASE_VISIBLE,
        "body": f"""
DECLARE @personId UNIQUEIDENTIFIER, @root UNIQUEIDENTIFIER, @owner UNIQUEIDENTIFIER, @opened DATE;
SELECT @personId = personId, @root = rootTenantId, @owner = owningTenantId, @opened = openedDate
  FROM dbo.exposureCase WHERE exposureCaseId = @exposureCaseId;

IF @closedDate < @opened
BEGIN
    ;THROW 51422, N'The closed date cannot be before the opened date.', 1;
END;

UPDATE dbo.exposureCase
   SET caseStatus = 'closed', closureReason = @closureReason, closedDate = @closedDate
 WHERE exposureCaseId = @exposureCaseId AND isDeleted = 0 AND caseStatus <> 'closed' AND rowVersion = @rowVersion;
{CONCURRENCY}

INSERT dbo.caseActivity (caseActivityId, rootTenantId, owningTenantId, exposureCaseId, activityType, activityDate,
                         workerUserId, note)
VALUES ({NEWID}, @root, @owner, @exposureCaseId, 'statusChange', @closedDate, auth.udfSessionUserId (),
        CONCAT (N'Case closed: ', @closureReason, IIF (@note IS NULL, N'', N'. ' + @note)));

UPDATE dbo.workTask
   SET taskStatus = 'dismissed', completedUtc = SYSUTCDATETIME (), completedByUserId = auth.udfSessionUserId (),
       resolutionNote = N'Case closed.'
 WHERE exposureCaseId = @exposureCaseId AND taskStatus = 'open' AND isDeleted = 0;

EXEC dbo.uspRefreshPersonAccess @personId = @personId;

SET @Comments = N'Case closed.';
""",
    },
    {
        "schema": "dbo", "name": "uspListAssignableUsers", "kind": "read", "grants": WEB, "ticket": "T080",
        "desc": "Active users assigned anywhere in the caller's tenant set, for assignment drop-downs.",
        "params": "",
        "key": "NULL",
        "body": """
DECLARE @scope UNIQUEIDENTIFIER = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER);

SELECT DISTINCT u.appUserId, u.displayName
  FROM auth.appUser AS u
  JOIN auth.userAssignment AS ua ON ua.appUserId = u.appUserId AND ua.isDeleted = 0
                                AND (ua.endDate IS NULL OR ua.endDate >= auth.udfSessionToday ())
  JOIN auth.sessionScopeTenant AS st ON st.tenantId = ua.tenantId AND st.sessionScopeId = @scope
 WHERE u.isActive = 1 AND u.isDeleted = 0
 ORDER BY u.displayName;
""",
    },
    {
        "schema": "dbo", "name": "uspCreateCaseFollowUpTasks", "kind": "write", "grants": BOTH, "ticket": "T065",
        "permission": "case.open",
        "desc": "Creates the cross-link and follow-up tasks a new case triggers -- the links separate child and adult "
                "systems cannot make. Each is a task for staff to review, never an automatic case: testing for other "
                "children who share the home or are siblings; an occupational inquiry for household adults whose jobs "
                "list metal tasks; testing for children and pregnant people in an adult worker's household; prenatal "
                "provider notification and newborn testing for a pregnant person.",
        "params": """@exposureCaseId  UNIQUEIDENTIFIER
, @adultAgeCutoffYears INT = 16""",
        "key": "CONCAT (N'exposureCaseId=', @exposureCaseId)",
        "validate": CASE_VISIBLE,
        "notes": "Idempotent: a task is not created twice for the same case, queue and person.",
        "body": f"""
DECLARE @personId UNIQUEIDENTIFIER, @root UNIQUEIDENTIFIER, @owner UNIQUEIDENTIFIER, @population VARCHAR (10),
        @category VARCHAR (20), @code VARCHAR (8), @caseNumber VARCHAR (20);
DECLARE @today DATE = auth.udfSessionToday ();

SELECT @personId = c.personId, @root = c.rootTenantId, @owner = c.owningTenantId, @population = c.population,
       @category = c.exposureCategory, @code = a.code, @caseNumber = c.caseNumber
  FROM dbo.exposureCase AS c JOIN config.analyte AS a ON a.analyteId = c.analyteId
 WHERE c.exposureCaseId = @exposureCaseId;

-- Household: everyone sharing the case person's current primary residence, plus relationship links.
DECLARE @household TABLE (personId UNIQUEIDENTIFIER PRIMARY KEY, viaRelationship BIT, role VARCHAR (30));

INSERT @household (personId, viaRelationship, role)
SELECT DISTINCT other.personId, 0, NULL
  FROM dbo.personLocation AS mine
  JOIN dbo.personLocation AS other ON other.locationId = mine.locationId AND other.personId <> mine.personId
                                  AND other.role = 'residence' AND other.isDeleted = 0
                                  AND other.startDate <= @today AND (other.endDate IS NULL OR other.endDate >= @today)
 WHERE mine.personId = @personId AND mine.role = 'residence' AND mine.isPrimary = 1 AND mine.isDeleted = 0
   AND mine.startDate <= @today AND (mine.endDate IS NULL OR mine.endDate >= @today);

MERGE @household AS h
USING (SELECT relatedPersonId AS personId, role FROM dbo.personRelationship
        WHERE personId = @personId AND isDeleted = 0 AND (endDate IS NULL OR endDate >= @today)
       UNION
       SELECT personId, role FROM dbo.personRelationship
        WHERE relatedPersonId = @personId AND isDeleted = 0 AND (endDate IS NULL OR endDate >= @today)) AS r
   ON h.personId = r.personId
WHEN MATCHED THEN UPDATE SET viaRelationship = 1, role = r.role
WHEN NOT MATCHED THEN INSERT (personId, viaRelationship, role) VALUES (r.personId, 1, r.role);

DECLARE @tasks TABLE (queueCode VARCHAR (40), title NVARCHAR (200), detail NVARCHAR (2000), personId UNIQUEIDENTIFIER,
                      dueDate DATE);

-- Child case: other children in the home or siblings -> testing.
IF @population = 'child'
BEGIN
    INSERT @tasks
    SELECT 'crossLink', CONCAT (N'Recommend testing ', p.firstName, N' ', p.lastName),
           CONCAT (N'Shares the home or is a sibling of the child on case ', @caseNumber, N'.'), p.personId, NULL
      FROM @household AS h JOIN dbo.person AS p ON p.personId = h.personId
     WHERE p.dob IS NOT NULL AND DATEDIFF (YEAR, p.dob, @today) < @adultAgeCutoffYears
       AND NOT EXISTS (SELECT 1 FROM dbo.exposureCase AS c WHERE c.personId = p.personId AND c.caseStatus <> 'closed'
                                                            AND c.isDeleted = 0);

    -- Household adults whose jobs list metal tasks -> occupational inquiry, even when contact-only.
    INSERT @tasks
    SELECT 'crossLink', CONCAT (N'Occupational inquiry for ', p.firstName, N' ', p.lastName),
           CONCAT (N'Household member of the child on case ', @caseNumber, N'; a current job lists work with metals: ',
                   LEFT (e.metalTasks, 200)), p.personId, NULL
      FROM @household AS h
      JOIN dbo.person AS p ON p.personId = h.personId
      JOIN dbo.employment AS e ON e.personId = p.personId AND e.isDeleted = 0 AND e.metalTasks IS NOT NULL
                              AND (e.endDate IS NULL OR e.endDate >= @today)
     WHERE p.dob IS NULL OR DATEDIFF (YEAR, p.dob, @today) >= @adultAgeCutoffYears;
END;

-- Adult case: children or a pregnant person in the household -> testing.
IF @population = 'adult'
BEGIN
    INSERT @tasks
    SELECT 'crossLink', CONCAT (N'Recommend testing ', p.firstName, N' ', p.lastName),
           CONCAT (N'Child or pregnant household member of the adult on case ', @caseNumber,
                   N'; take-home exposure is possible.'), p.personId, NULL
      FROM @household AS h JOIN dbo.person AS p ON p.personId = h.personId
     WHERE (p.dob IS NOT NULL AND DATEDIFF (YEAR, p.dob, @today) < @adultAgeCutoffYears)
        OR EXISTS (SELECT 1 FROM dbo.pregnancyEpisode AS e WHERE e.personId = p.personId AND e.isDeleted = 0
                      AND e.startDate <= @today AND COALESCE (e.actualEndDate, e.expectedEndDate, DATEADD (WEEK, 42, e.startDate)) >= @today);
END;

-- Pregnant case: prenatal provider notification and newborn testing follow-up.
IF @population = 'pregnant'
BEGIN
    INSERT @tasks VALUES ('prenatalNotification', N'Notify the prenatal care provider',
                          CONCAT (N'Case ', @caseNumber, N' is for a pregnant person.'), @personId, DATEADD (DAY, 7, @today));

    INSERT @tasks
    SELECT TOP (1) 'newbornTesting', N'Newborn testing follow-up',
           CONCAT (N'Arrange testing of the newborn after delivery (case ', @caseNumber, N').'), @personId,
           COALESCE (e.expectedEndDate, DATEADD (WEEK, 40, e.startDate))
      FROM dbo.pregnancyEpisode AS e
     WHERE e.personId = @personId AND e.isDeleted = 0 AND e.actualEndDate IS NULL
     ORDER BY e.startDate DESC;
END;

INSERT dbo.workTask (workTaskId, rootTenantId, owningTenantId, queueCode, title, detail, personId, exposureCaseId, dueDate)
SELECT {NEWID}, @root, @owner, t.queueCode, t.title, t.detail, t.personId, @exposureCaseId, t.dueDate
  FROM @tasks AS t
 WHERE NOT EXISTS (SELECT 1 FROM dbo.workTask AS w
                    WHERE w.exposureCaseId = @exposureCaseId AND w.queueCode = t.queueCode
                      AND w.personId IS NOT DISTINCT FROM t.personId AND w.title = t.title AND w.isDeleted = 0);

SET @Comments = CONCAT (@@ROWCOUNT, N' follow-up task(s) created.');
""",
    },
    # -------------------------------------------------------------------------------------------- investigations
    {
        "schema": "dbo", "name": "uspFindOpenInvestigation", "kind": "read", "grants": BOTH, "ticket": "T046",
        "permission": "case.view",
        "desc": "Open investigations of the same target, offered before a new one is created, so siblings in one home and "
                "co-workers at one worksite share one investigation.",
        "params": """@investigationType  VARCHAR (20)
, @locationId         UNIQUEIDENTIFIER = NULL
, @organizationId     UNIQUEIDENTIFIER = NULL
, @worksiteLocationId UNIQUEIDENTIFIER = NULL
, @exposureSourceId   UNIQUEIDENTIFIER = NULL""",
        "key": "CONCAT (N'investigationType=', @investigationType, N', locationId=', @locationId, N', organizationId=', @organizationId)",
        "body": """
SELECT i.investigationId, i.investigationNumber, i.investigationStatus, i.requestedDate,
       (SELECT COUNT (*) FROM dbo.investigationCase AS ic WHERE ic.investigationId = i.investigationId
           AND ic.isDeleted = 0) AS linkedCases
  FROM dbo.investigation AS i
 WHERE i.isDeleted = 0 AND i.investigationStatus NOT IN ('cleared', 'closed')
   AND i.investigationType = @investigationType
   AND i.locationId IS NOT DISTINCT FROM @locationId
   AND i.organizationId IS NOT DISTINCT FROM @organizationId
   AND (@worksiteLocationId IS NULL OR i.worksiteLocationId = @worksiteLocationId)
   AND i.exposureSourceId IS NOT DISTINCT FROM @exposureSourceId
 ORDER BY i.requestedDate;
""",
    },
    {
        "schema": "dbo", "name": "uspCreateInvestigation", "kind": "write", "grants": BOTH, "ticket": "T046",
        "permission": "investigation.edit",
        "desc": "Creates an investigation of exactly one target and links it to the case (and employment) it serves. Owned "
                "by the case's owning tenant, or the caller's tenant for an investigation started from a location or "
                "cluster with no case.",
        "params": """@investigationId    UNIQUEIDENTIFIER
, @investigationType  VARCHAR (20)
, @locationId         UNIQUEIDENTIFIER = NULL
, @organizationId     UNIQUEIDENTIFIER = NULL
, @worksiteLocationId UNIQUEIDENTIFIER = NULL
, @exposureSourceId   UNIQUEIDENTIFIER = NULL
, @exposureCaseId     UNIQUEIDENTIFIER = NULL
, @employmentId       UNIQUEIDENTIFIER = NULL
, @requestedDate      DATE""",
        "key": "CONCAT (N'investigationId=', @investigationId, N', exposureCaseId=', @exposureCaseId)",
        "notes": "Returns investigationNumber.",
        "body": f"""
DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();
DECLARE @owner UNIQUEIDENTIFIER = COALESCE ((SELECT owningTenantId FROM dbo.exposureCase WHERE exposureCaseId = @exposureCaseId),
                                            auth.udfSessionActingTenantId ());
DECLARE @number VARCHAR (20) = CONCAT ('INV-', YEAR (@requestedDate), '-',
                                       RIGHT (CONCAT ('000000', NEXT VALUE FOR dbo.investigationNumberSequence), 6));

INSERT dbo.investigation (investigationId, rootTenantId, owningTenantId, investigationNumber, investigationType, locationId,
                          organizationId, worksiteLocationId, exposureSourceId, requestedDate)
VALUES (@investigationId, @root, @owner, @number, @investigationType, @locationId, @organizationId, @worksiteLocationId,
        @exposureSourceId, @requestedDate);

IF @exposureCaseId IS NOT NULL OR @employmentId IS NOT NULL
BEGIN
    INSERT dbo.investigationCase (investigationCaseId, rootTenantId, owningTenantId, investigationId, exposureCaseId,
                                  employmentId)
    VALUES ({NEWID}, @root, @owner, @investigationId, @exposureCaseId, @employmentId);
END;

SELECT @number AS investigationNumber;

SET @Comments = N'Investigation created.';
""",
    },
    {
        "schema": "dbo", "name": "uspLinkInvestigationCase", "kind": "write", "grants": BOTH, "ticket": "T046",
        "permission": "investigation.edit",
        "desc": "Links an existing investigation to another case or employment it serves. A second link of the same pair "
                "is a no-op.",
        "params": """@investigationId UNIQUEIDENTIFIER
, @exposureCaseId  UNIQUEIDENTIFIER = NULL
, @employmentId    UNIQUEIDENTIFIER = NULL""",
        "key": "CONCAT (N'investigationId=', @investigationId, N', exposureCaseId=', @exposureCaseId)",
        "validate": INV_VISIBLE,
        "body": f"""
INSERT dbo.investigationCase (investigationCaseId, rootTenantId, owningTenantId, investigationId, exposureCaseId,
                              employmentId)
SELECT {NEWID}, i.rootTenantId, i.owningTenantId, @investigationId, @exposureCaseId, @employmentId
  FROM dbo.investigation AS i
 WHERE i.investigationId = @investigationId
   AND NOT EXISTS (SELECT 1 FROM dbo.investigationCase AS ic WHERE ic.investigationId = @investigationId
                      AND ic.exposureCaseId IS NOT DISTINCT FROM @exposureCaseId
                      AND ic.employmentId IS NOT DISTINCT FROM @employmentId AND ic.isDeleted = 0);

SET @Comments = CONCAT (@@ROWCOUNT, N' link(s) added.');
""",
    },
    {
        "schema": "dbo", "name": "uspGetInvestigation", "kind": "read", "grants": BOTH, "ticket": "T081",
        "permission": "case.view",
        "desc": "The investigation page: the investigation and its target, the cases it serves, and its environmental "
                "samples with computed exceedance.",
        "params": "@investigationId UNIQUEIDENTIFIER",
        "key": "CONCAT (N'investigationId=', @investigationId)",
        "validate": INV_VISIBLE,
        "body": """
SELECT i.investigationId, i.investigationNumber, i.investigationType, i.locationId, i.organizationId,
       i.worksiteLocationId, i.exposureSourceId, i.investigationStatus, i.assignedToUserId, u.displayName AS assignedToName,
       i.requestedDate, i.scheduledDate, i.completedDate, i.findings, i.regulatoryAction, i.referralDate,
       i.referralOutcome, i.rowVersion, t.tenantName AS owningTenantName,
       l.addressLine1, l.unit, l.city, l.state, l.zip5, l.yearBuilt, o.organizationName,
       w.addressLine1 AS worksiteAddress, w.city AS worksiteCity, es.sourceType, es.sourceDescription
  FROM dbo.investigation AS i
  LEFT JOIN auth.appUser AS u ON u.appUserId = i.assignedToUserId
  LEFT JOIN auth.tenant AS t ON t.tenantId = i.owningTenantId
  LEFT JOIN dbo.location AS l ON l.locationId = i.locationId
  LEFT JOIN dbo.organization AS o ON o.organizationId = i.organizationId
  LEFT JOIN dbo.location AS w ON w.locationId = i.worksiteLocationId
  LEFT JOIN dbo.exposureSource AS es ON es.exposureSourceId = i.exposureSourceId
 WHERE i.investigationId = @investigationId;

SELECT c.exposureCaseId, c.caseNumber, c.personId, p.lastName, p.firstName, a.code AS analyteCode, c.classification,
       c.caseStatus, e.jobTitle
  FROM dbo.investigationCase AS ic
  LEFT JOIN dbo.exposureCase AS c ON c.exposureCaseId = ic.exposureCaseId
  LEFT JOIN dbo.person AS p ON p.personId = c.personId
  LEFT JOIN config.analyte AS a ON a.analyteId = c.analyteId
  LEFT JOIN dbo.employment AS e ON e.employmentId = ic.employmentId
 WHERE ic.investigationId = @investigationId AND ic.isDeleted = 0;

SELECT es.environmentalSampleId, es.sampleType, es.roomOrComponent, es.surface, es.sampleValue, es.unit, es.sampleDate,
       es.exceedsStandard, st.standardValue, st.unit AS standardUnit, st.authority, es.notes, es.modifiedDateUtc
  FROM dbo.environmentalSample AS es
  LEFT JOIN config.environmentalStandard AS st ON st.environmentalStandardId = es.environmentalStandardId
 WHERE es.investigationId = @investigationId AND es.isDeleted = 0
 ORDER BY es.sampleDate DESC, es.sampleType;
""",
    },
    {
        "schema": "dbo", "name": "uspSaveInvestigation", "kind": "write", "grants": WEB, "ticket": "T081",
        "permission": "investigation.edit",
        "desc": "Updates an investigation's status, assignment, dates, findings and regulatory action. Dates must be in "
                "order.",
        "params": """@investigationId     UNIQUEIDENTIFIER
, @investigationStatus VARCHAR (20)
, @assignedToUserId    UNIQUEIDENTIFIER = NULL
, @scheduledDate       DATE = NULL
, @completedDate       DATE = NULL
, @findings            NVARCHAR (MAX) = NULL
, @regulatoryAction    VARCHAR (30) = NULL
, @referralDate        DATE = NULL
, @referralOutcome     NVARCHAR (1000) = NULL
, @rowVersion          BINARY (8)""",
        "key": "CONCAT (N'investigationId=', @investigationId, N', status=', @investigationStatus)",
        "validate": INV_VISIBLE,
        "body": f"""
UPDATE dbo.investigation
   SET investigationStatus = @investigationStatus, assignedToUserId = @assignedToUserId, scheduledDate = @scheduledDate,
       completedDate = @completedDate, findings = @findings, regulatoryAction = @regulatoryAction,
       referralDate = @referralDate, referralOutcome = @referralOutcome
 WHERE investigationId = @investigationId AND isDeleted = 0 AND rowVersion = @rowVersion;
{CONCURRENCY}

SET @Comments = N'Investigation saved.';
""",
    },
    {
        "schema": "dbo", "name": "uspSaveEnvironmentalSample", "kind": "write", "grants": WEB, "ticket": "T081",
        "permission": "investigation.edit",
        "desc": "Adds or removes an environmental sample. Exceedance is computed here from the environmental standard in "
                "force on the sample date for the sample type and surface, in the same unit -- it is never typed.",
        "params": """@environmentalSampleId UNIQUEIDENTIFIER
, @investigationId       UNIQUEIDENTIFIER
, @sampleType            VARCHAR (30)
, @roomOrComponent       NVARCHAR (120) = NULL
, @surface               NVARCHAR (60) = NULL
, @sampleValue           DECIMAL (18, 6)
, @unit                  NVARCHAR (20)
, @sampleDate            DATE
, @notes                 NVARCHAR (1000) = NULL
, @isDeleted             BIT = 0
, @originalModifiedUtc DATETIME2 (3) = NULL""",
        "key": "CONCAT (N'environmentalSampleId=', @environmentalSampleId, N', investigationId=', @investigationId)",
        "validate": INV_VISIBLE,
        "notes": "Returns exceedsStandard (NULL when no standard applies).",
        "body": """
DECLARE @standardId UNIQUEIDENTIFIER, @standardValue DECIMAL (18, 6);

SELECT TOP (1) @standardId = environmentalStandardId, @standardValue = standardValue
  FROM config.environmentalStandard
 WHERE isDeleted = 0 AND sampleType = @sampleType AND unit = @unit
   AND (surface IS NULL OR surface = @surface)
   AND effectiveFrom <= @sampleDate AND (effectiveTo IS NULL OR effectiveTo >= @sampleDate)
 ORDER BY CASE WHEN surface = @surface THEN 0 ELSE 1 END, effectiveFrom DESC;

IF EXISTS (SELECT 1 FROM dbo.environmentalSample WHERE environmentalSampleId = @environmentalSampleId)
BEGIN
    UPDATE dbo.environmentalSample SET isDeleted = @isDeleted, sampleType = @sampleType,
        roomOrComponent = @roomOrComponent, surface = @surface, sampleValue = @sampleValue, unit = @unit,
        sampleDate = @sampleDate, environmentalStandardId = @standardId, notes = @notes,
        exceedsStandard = CASE WHEN @standardId IS NULL THEN NULL WHEN @sampleValue >= @standardValue THEN 1 ELSE 0 END
     WHERE environmentalSampleId = @environmentalSampleId AND investigationId = @investigationId AND isDeleted = 0
       AND modifiedDateUtc = @originalModifiedUtc;
    IF @@ROWCOUNT <> 1 THROW 51409, N'This reading changed. Reload before editing.', 1;
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
""",
    },
    {
        "schema": "dbo", "name": "uspImportEnvironmentalSamples", "kind": "write", "grants": WEB, "ticket": "T081",
        "permission": "investigation.edit",
        "desc": "Atomically imports validated CSV readings. Stable reading ids make repeated submissions idempotent.",
        "params": "@investigationId UNIQUEIDENTIFIER\n, @samplesJson NVARCHAR (MAX)",
        "key": "CONCAT (N'investigationId=', @investigationId)",
        "validate": INV_VISIBLE,
        "body": """
IF ISJSON (@samplesJson, ARRAY) <> 1 OR (SELECT COUNT (*) FROM OPENJSON (@samplesJson)) NOT BETWEEN 1 AND 500
    THROW 50000, N'Expected 1 to 500 readings.', 1;
INSERT dbo.environmentalSample (environmentalSampleId, rootTenantId, owningTenantId, investigationId, sampleType,
    roomOrComponent, surface, sampleValue, unit, sampleDate, environmentalStandardId, exceedsStandard, notes)
SELECT r.id, i.rootTenantId, i.owningTenantId, i.investigationId, r.sampleType,
    r.roomOrComponent, r.surface, r.sampleValue, r.unit, r.sampleDate, st.environmentalStandardId,
    CASE WHEN st.environmentalStandardId IS NULL THEN NULL WHEN r.sampleValue >= st.standardValue THEN 1 ELSE 0 END, r.notes
FROM dbo.investigation AS i
CROSS APPLY OPENJSON (@samplesJson) WITH (
    id UNIQUEIDENTIFIER '$.environmentalSampleId', sampleType VARCHAR (30), roomOrComponent NVARCHAR (120),
    surface NVARCHAR (60), sampleValue DECIMAL (18, 6), unit NVARCHAR (20), sampleDate DATE, notes NVARCHAR (1000)) AS r
OUTER APPLY (SELECT TOP (1) s.environmentalStandardId, s.standardValue FROM config.environmentalStandard AS s
    WHERE s.isDeleted = 0 AND s.sampleType = r.sampleType AND s.unit = r.unit
      AND (s.surface IS NULL OR s.surface = r.surface) AND s.effectiveFrom <= r.sampleDate
      AND (s.effectiveTo IS NULL OR s.effectiveTo >= r.sampleDate)
    ORDER BY CASE WHEN s.surface = r.surface THEN 0 ELSE 1 END, s.effectiveFrom DESC) AS st
WHERE i.investigationId = @investigationId AND NOT EXISTS (
    SELECT 1 FROM dbo.environmentalSample WITH (UPDLOCK, HOLDLOCK) WHERE environmentalSampleId = r.id);
DECLARE @count INT = @@ROWCOUNT;
SELECT @count AS importedCount;
SET @Comments = CONCAT (@count, N' readings imported.');
""",
    },
    {
        "schema": "dbo", "name": "uspListInvestigations", "kind": "read", "grants": WEB, "ticket": "T081",
        "permission": "case.view",
        "desc": "One page of investigations visible to the caller, optionally only open ones or only theirs.",
        "params": """@openOnly     BIT = 1
, @assignedToMe BIT = 0
, @pageNumber   INT = 1
, @pageSize     INT = 25
, @searchText NVARCHAR (200) = NULL
, @assignedFilter VARCHAR (40) = NULL
, @statusFilter VARCHAR (20) = NULL
, @typeFilter VARCHAR (30) = NULL
, @sortBy VARCHAR (20) = 'requested'
, @sortDescending BIT = 1""",
        "key": "CONCAT (N'openOnly=', @openOnly, N', pageNumber=', @pageNumber)",
        "validate": """IF @pageNumber < 1 OR @pageSize NOT BETWEEN 1 AND 500
BEGIN
    ;THROW 50000, N'@pageNumber must be 1 or more and @pageSize between 1 and 500.', 1;
END;""",
        "body": """
DECLARE @me UNIQUEIDENTIFIER = auth.udfSessionUserId ();

SELECT i.investigationId, i.investigationNumber, i.investigationType, i.investigationStatus, i.requestedDate,
       i.scheduledDate, COALESCE (l.addressLine1, o.organizationName, es.sourceDescription) AS targetLabel,
       u.displayName AS assignedToName, COUNT (*) OVER () AS totalCount
  FROM dbo.investigation AS i
  LEFT JOIN dbo.location AS l ON l.locationId = i.locationId
  LEFT JOIN dbo.organization AS o ON o.organizationId = i.organizationId
  LEFT JOIN dbo.exposureSource AS es ON es.exposureSourceId = i.exposureSourceId
  LEFT JOIN auth.appUser AS u ON u.appUserId = i.assignedToUserId
 WHERE i.isDeleted = 0
   AND (@openOnly = 0 OR i.investigationStatus NOT IN ('cleared', 'closed'))
   AND (@assignedToMe = 0 OR i.assignedToUserId = @me)
   AND (NULLIF (@searchText, N'') IS NULL OR i.investigationNumber LIKE N'%' + @searchText + N'%'
        OR l.addressLine1 LIKE N'%' + @searchText + N'%' OR o.organizationName LIKE N'%' + @searchText + N'%')
   AND (NULLIF (@statusFilter, '') IS NULL OR i.investigationStatus = @statusFilter)
   AND (NULLIF (@typeFilter, '') IS NULL OR i.investigationType = @typeFilter)
   AND (NULLIF (@assignedFilter, '') IS NULL OR (@assignedFilter = 'me' AND i.assignedToUserId = @me)
        OR (@assignedFilter = 'unassigned' AND i.assignedToUserId IS NULL)
        OR i.assignedToUserId = TRY_CAST (@assignedFilter AS UNIQUEIDENTIFIER))
 ORDER BY CASE WHEN @sortBy = 'number' AND @sortDescending = 0 THEN i.investigationNumber END ASC,
          CASE WHEN @sortBy = 'number' AND @sortDescending = 1 THEN i.investigationNumber END DESC,
          CASE WHEN @sortBy = 'status' AND @sortDescending = 0 THEN i.investigationStatus END ASC,
          CASE WHEN @sortBy = 'status' AND @sortDescending = 1 THEN i.investigationStatus END DESC,
          CASE WHEN @sortBy = 'requested' AND @sortDescending = 0 THEN i.requestedDate END ASC,
          CASE WHEN @sortBy = 'requested' AND @sortDescending = 1 THEN i.requestedDate END DESC, i.investigationId
OFFSET (@pageNumber - 1) * @pageSize ROWS
 FETCH NEXT @pageSize ROWS ONLY;
""",
    },
]
