"""Work queues, tasks, dashboard, background job queue, cluster detection and the audit ledger."""
from procs_common import NEWID, WEB, WORKER, BOTH, CONCURRENCY, JOB_SCOPE, JOB_SCOPE_COLUMNS, JOB_SCOPE_VALUES

QUEUE_NAMES = """('unmatchedResult', N'Unmatched results'), ('unknownCode', N'Unknown LOINC codes or units'),
        ('pausedBatch', N'Paused intake batches'), ('addressReview', N'Address review'),
        ('unverifiedPrimary', N'Unverified primary residences'), ('detectionLimit', N'Detection limit above level'),
        ('pregnancyPastEnd', N'Pregnancies past expected end'), ('overdueStep', N'Overdue case steps'),
        ('crossLink', N'Cross-link tasks'), ('cluster', N'Cluster alerts'), ('extractMissing', N'Missing extract fields'),
        ('confirmatoryTest', N'Confirmatory tests due'), ('speciationRecommended', N'Speciation recommended'),
        ('populationReview', N'Population unknown (no DOB)'), ('deceasedReview', N'Deceased: close open cases'),
        ('siteReview', N'Other sites to review'), ('investigationRequest', N'Investigation requests'),
        ('prenatalNotification', N'Prenatal provider notifications'), ('newbornTesting', N'Newborn testing'),
        ('letterDue', N'Letters to send'), ('actionLevel', N'Action level follow-up'),
        ('matchReview', N'Person match review (supervisor)')"""

PROCS = [
    {
        "schema": "dbo", "name": "uspCreateWorkTask", "kind": "write", "grants": BOTH, "ticket": "T047",
        "desc": "Creates a task in a work queue. Automation proposes and staff decide, so every automatic consequence -- "
                "an address review, a confirmatory test due, a cross-link -- arrives here. An identical open task is not "
                "created twice.",
        "params": """@workTaskId     UNIQUEIDENTIFIER
, @queueCode      VARCHAR (40)
, @title          NVARCHAR (200)
, @detail         NVARCHAR (2000) = NULL
, @personId       UNIQUEIDENTIFIER = NULL
, @exposureCaseId UNIQUEIDENTIFIER = NULL
, @labResultId    UNIQUEIDENTIFIER = NULL
, @investigationId UNIQUEIDENTIFIER = NULL
, @stagingRowId   UNIQUEIDENTIFIER = NULL
, @isUrgent       BIT = 0
, @dueDate        DATE = NULL
, @owningTenantId UNIQUEIDENTIFIER = NULL""",
        "key": "CONCAT (N'workTaskId=', @workTaskId, N', queueCode=', @queueCode, N', exposureCaseId=', @exposureCaseId)",
        "notes": "The owner is the case's owner when a case is given, else @owningTenantId, else the caller's acting "
                 "tenant. Every linked record must be one the caller can see: the checks run under row-level security, "
                 "so another tenant's person, case, result, investigation or staging row is refused as not found and "
                 "its id is never stored on a task in the caller's queue (issue #29).",
        "body": """
IF (@personId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.person WHERE personId = @personId))
   OR (@exposureCaseId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.exposureCase WHERE exposureCaseId = @exposureCaseId))
   OR (@labResultId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.labResult WHERE labResultId = @labResultId))
   OR (@investigationId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.investigation WHERE investigationId = @investigationId))
   OR (@stagingRowId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.stagingRow WHERE stagingRowId = @stagingRowId))
BEGIN
    ;THROW 51404, N'A record linked to the task was not found.', 1;
END;

DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();
DECLARE @owner UNIQUEIDENTIFIER = COALESCE ((SELECT owningTenantId FROM dbo.exposureCase WHERE exposureCaseId = @exposureCaseId),
                                            @owningTenantId, auth.udfSessionActingTenantId ());

IF NOT EXISTS (SELECT 1 FROM dbo.workTask
                WHERE queueCode = @queueCode AND taskStatus = 'open' AND isDeleted = 0 AND title = @title
                  AND personId IS NOT DISTINCT FROM @personId AND exposureCaseId IS NOT DISTINCT FROM @exposureCaseId
                  AND labResultId IS NOT DISTINCT FROM @labResultId)
BEGIN
    INSERT dbo.workTask (workTaskId, rootTenantId, owningTenantId, queueCode, title, detail, personId, exposureCaseId,
                         labResultId, investigationId, stagingRowId, isUrgent, dueDate)
    VALUES (@workTaskId, @root, @owner, @queueCode, @title, @detail, @personId, @exposureCaseId, @labResultId,
            @investigationId, @stagingRowId, @isUrgent, @dueDate);
    SET @Comments = N'Task created.';
END
ELSE
    SET @Comments = N'Identical open task exists; nothing created.';
""",
    },
    {
        "schema": "dbo", "name": "uspGetQueueCounts", "kind": "read", "grants": WEB, "ticket": "T074",
        "permission": "task.view",
        "desc": "Open task counts for every work queue the caller can see, with urgent and overdue counts, plus held intake "
                "rows. Feeds the home dashboard tiles, which poll it.",
        "params": "",
        "key": "NULL",
        "body": f"""
DECLARE @today DATE = auth.udfSessionToday ();
DECLARE @me UNIQUEIDENTIFIER = auth.udfSessionUserId ();

SELECT q.queueCode, q.queueName,
       COUNT (w.workTaskId) AS openCount,
       SUM (CASE WHEN w.isUrgent = 1 THEN 1 ELSE 0 END) AS urgentCount,
       SUM (CASE WHEN w.dueDate < @today THEN 1 ELSE 0 END) AS overdueCount,
       SUM (CASE WHEN w.assignedToUserId = @me THEN 1 ELSE 0 END) AS mineCount
  FROM (VALUES {QUEUE_NAMES}) AS q (queueCode, queueName)
  LEFT JOIN dbo.workTask AS w ON w.queueCode = q.queueCode AND w.taskStatus = 'open' AND w.isDeleted = 0
 GROUP BY q.queueCode, q.queueName
UNION ALL
SELECT 'heldRecords', N'Held intake records', COUNT (*), SUM (CASE WHEN isUrgent = 1 THEN 1 ELSE 0 END), 0,
       SUM (CASE WHEN assignedToUserId = @me THEN 1 ELSE 0 END)
  FROM dbo.stagingRow WHERE rowStatus = 'held' AND isDeleted = 0
 ORDER BY urgentCount DESC, openCount DESC;
""",
    },
    {
        "schema": "dbo", "name": "uspGetDashboard", "kind": "read", "grants": WEB, "ticket": "T074",
        "permission": "task.view",
        "desc": "The home dashboard: the caller's counts, urgent items (elevated held results first, because a result "
                "sitting in a queue delays follow-up) and their most recent open cases.",
        "params": "",
        "key": "NULL",
        "body": """
DECLARE @today DATE = auth.udfSessionToday ();
DECLARE @me UNIQUEIDENTIFIER = auth.udfSessionUserId ();

SELECT (SELECT COUNT (*) FROM dbo.exposureCase WHERE assignedToUserId = @me AND caseStatus <> 'closed' AND isDeleted = 0) AS myOpenCases,
       (SELECT COUNT (*) FROM dbo.workTask WHERE assignedToUserId = @me AND taskStatus = 'open' AND isDeleted = 0) AS myOpenTasks,
       (SELECT COUNT (*) FROM dbo.workTask WHERE taskStatus = 'open' AND dueDate < @today AND isDeleted = 0) AS overdueTasks,
       (SELECT COUNT (*) FROM dbo.workTask WHERE taskStatus = 'open' AND isUrgent = 1 AND isDeleted = 0) AS urgentTasks,
       (SELECT COUNT (*) FROM dbo.stagingRow WHERE rowStatus = 'held' AND isUrgent = 1 AND isDeleted = 0) AS urgentHeld,
       (SELECT COUNT (*) FROM dbo.exposureCase WHERE caseStatus <> 'closed' AND isDeleted = 0) AS openCasesInScope;

-- A held result is named by the patient and the result as reported, corrections applied, so each row says which record
-- it is (issue #40); its batch and sender narrow the held records it links to.
SELECT TOP (10) x.itemType, x.itemId, x.title, x.detail, x.dueDate, x.waitingSinceUtc, x.personId, x.exposureCaseId,
       x.intakeBatchId, x.senderName
  FROM (SELECT 'heldRecord' AS itemType, s.stagingRowId AS itemId,
               COALESCE (NULLIF (CONCAT_WS (N': ', NULLIF (CONCAT_WS (N', ', f.lastName, f.firstName), N''),
                                            NULLIF (CONCAT_WS (N' ', a.displayName, f.resultValue, f.resultUnit), N'')), N''),
                         N'Elevated result held at intake') AS title,
               (SELECT TOP (1) reasonMessage FROM dbo.stagingHoldReason AS h WHERE h.stagingRowId = s.stagingRowId
                  AND h.isDeleted = 0 AND h.isResolved = 0) AS detail, CAST (NULL AS DATE) AS dueDate,
               s.heldSinceUtc AS waitingSinceUtc, CAST (NULL AS UNIQUEIDENTIFIER) AS personId,
               CAST (NULL AS UNIQUEIDENTIFIER) AS exposureCaseId, s.intakeBatchId, o.organizationName AS senderName,
               0 AS sortKey
          FROM dbo.stagingRow AS s
          JOIN dbo.intakeBatch AS b ON b.intakeBatchId = s.intakeBatchId
          LEFT JOIN dbo.organization AS o ON o.organizationId = b.senderOrganizationId
         CROSS APPLY (SELECT COALESCE (JSON_VALUE (s.correctedJson, '$.lastName'), JSON_VALUE (s.parsedJson, '$.lastName')) AS lastName,
                             COALESCE (JSON_VALUE (s.correctedJson, '$.firstName'), JSON_VALUE (s.parsedJson, '$.firstName')) AS firstName,
                             COALESCE (JSON_VALUE (s.correctedJson, '$.loinc'), JSON_VALUE (s.parsedJson, '$.loinc')) AS loinc,
                             COALESCE (JSON_VALUE (s.correctedJson, '$.value'), JSON_VALUE (s.parsedJson, '$.value')) AS resultValue,
                             COALESCE (JSON_VALUE (s.correctedJson, '$.unit'), JSON_VALUE (s.parsedJson, '$.unit')) AS resultUnit) AS f
          LEFT JOIN config.loincCode AS l ON l.loincCode = f.loinc AND l.isDeleted = 0
          LEFT JOIN config.analyteSpecimen AS sp ON sp.analyteSpecimenId = l.analyteSpecimenId
          LEFT JOIN config.analyte AS a ON a.analyteId = sp.analyteId
         WHERE s.rowStatus = 'held' AND s.isUrgent = 1 AND s.isDeleted = 0
        UNION ALL
        SELECT 'task', w.workTaskId, w.title, w.detail, w.dueDate, w.createdDateUtc, w.personId, w.exposureCaseId,
               CAST (NULL AS UNIQUEIDENTIFIER), CAST (NULL AS NVARCHAR (120)), 1
          FROM dbo.workTask AS w
         WHERE w.taskStatus = 'open' AND w.isDeleted = 0 AND (w.isUrgent = 1 OR w.dueDate < @today)) AS x
 ORDER BY x.sortKey, x.waitingSinceUtc;

SELECT TOP (10) c.exposureCaseId, c.caseNumber, c.personId, p.lastName, p.firstName, a.code AS analyteCode,
       c.classification, c.openedDate
  FROM dbo.exposureCase AS c
  JOIN dbo.person AS p ON p.personId = c.personId
  JOIN config.analyte AS a ON a.analyteId = c.analyteId
 WHERE c.assignedToUserId = @me AND c.caseStatus <> 'closed' AND c.isDeleted = 0
 ORDER BY c.openedDate DESC;
""",
    },
    {
        "schema": "dbo", "name": "uspListWorkTasks", "kind": "read", "grants": WEB, "ticket": "T104",
        "permission": "task.view",
        "desc": "One page of work tasks for the shared data grid: filtered by queue, status and assignment, sorted on the "
                "server, with the total count.",
        "params": """@queueCode      VARCHAR (40) = NULL
, @taskStatus     VARCHAR (12) = 'open'
, @assignedToMe   BIT = 0
, @urgentOnly     BIT = 0
, @sortBy         VARCHAR (30) = 'dueDate'
, @sortDescending BIT = 0
, @pageNumber     INT = 1
, @pageSize       INT = 50""",
        "key": "CONCAT (N'queueCode=', @queueCode, N', pageNumber=', @pageNumber, N', pageSize=', @pageSize, N', sortBy=', @sortBy)",
        "validate": """IF @pageNumber < 1 OR @pageSize NOT BETWEEN 1 AND 500
BEGIN
    ;THROW 50000, N'@pageNumber must be 1 or more and @pageSize between 1 and 500.', 1;
END;

IF @sortBy NOT IN ('dueDate', 'createdDateUtc', 'title', 'queueCode')
BEGIN
    ;THROW 50000, N'@sortBy must be one of dueDate, createdDateUtc, title, queueCode.', 1;
END;""",
        "body": """
DECLARE @me UNIQUEIDENTIFIER = auth.udfSessionUserId ();

SELECT w.workTaskId, w.queueCode, w.title, w.detail, w.isUrgent, w.dueDate, w.taskStatus, w.createdDateUtc,
       w.personId, p.lastName, p.firstName, w.exposureCaseId, c.caseNumber, w.labResultId, w.investigationId,
       w.stagingRowId, w.assignedToUserId, u.displayName AS assignedToName, w.rowVersion,
       CAST (CASE WHEN EXISTS (SELECT 1 FROM auth.tvfTenantWritePredicate (w.rootTenantId, w.owningTenantId, w.isDeleted)) THEN 1 ELSE 0 END AS BIT) AS canEdit,
       COUNT (*) OVER () AS totalCount
  FROM dbo.workTask AS w
  LEFT JOIN dbo.person AS p ON p.personId = w.personId
  LEFT JOIN dbo.exposureCase AS c ON c.exposureCaseId = w.exposureCaseId
  LEFT JOIN auth.appUser AS u ON u.appUserId = w.assignedToUserId
 WHERE w.isDeleted = 0
   AND (@queueCode IS NULL OR w.queueCode = @queueCode)
   AND (@taskStatus IS NULL OR w.taskStatus = @taskStatus)
   AND (@assignedToMe = 0 OR w.assignedToUserId = @me)
   AND (@urgentOnly = 0 OR w.isUrgent = 1)
 ORDER BY w.isUrgent DESC,
          CASE WHEN @sortBy = 'dueDate'        AND @sortDescending = 0 THEN COALESCE (w.dueDate, '9999-12-31') END ASC,
          CASE WHEN @sortBy = 'dueDate'        AND @sortDescending = 1 THEN w.dueDate END DESC,
          CASE WHEN @sortBy = 'createdDateUtc' AND @sortDescending = 0 THEN w.createdDateUtc END ASC,
          CASE WHEN @sortBy = 'createdDateUtc' AND @sortDescending = 1 THEN w.createdDateUtc END DESC,
          CASE WHEN @sortBy = 'title'          AND @sortDescending = 0 THEN w.title END ASC,
          CASE WHEN @sortBy = 'title'          AND @sortDescending = 1 THEN w.title END DESC,
          CASE WHEN @sortBy = 'queueCode'      AND @sortDescending = 0 THEN w.queueCode END ASC,
          CASE WHEN @sortBy = 'queueCode'      AND @sortDescending = 1 THEN w.queueCode END DESC,
          w.workTaskId
OFFSET (@pageNumber - 1) * @pageSize ROWS
 FETCH NEXT @pageSize ROWS ONLY;
""",
    },
    {
        "schema": "dbo", "name": "uspUpdateWorkTasks", "kind": "write", "grants": WEB, "ticket": "T104",
        "permission": "task.edit",
        "desc": "Bulk action on work tasks from the grid: assign to me, assign to a user, complete or dismiss, with an "
                "optional note. Tasks outside the caller's scope are invisible and so untouched.",
        "params": """@workTaskIdsJson NVARCHAR (MAX)
, @action          VARCHAR (20)
, @assignToUserId  UNIQUEIDENTIFIER = NULL
, @note            NVARCHAR (1000) = NULL""",
        "key": "CONCAT (N'action=', @action)",
        "validate": """IF ISJSON (@workTaskIdsJson) = 0
BEGIN
    ;THROW 50000, N'@workTaskIdsJson must be a JSON array of ids.', 1;
END;

IF @action NOT IN ('assignToMe', 'assign', 'complete', 'dismiss', 'reopen')
BEGIN
    ;THROW 50000, N'@action must be assignToMe, assign, complete, dismiss or reopen.', 1;
END;""",
        "body": """
DECLARE @me UNIQUEIDENTIFIER = auth.udfSessionUserId ();
DECLARE @ids TABLE (workTaskId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @ids SELECT DISTINCT TRY_CAST ([value] AS UNIQUEIDENTIFIER) FROM OPENJSON (@workTaskIdsJson)
             WHERE TRY_CAST ([value] AS UNIQUEIDENTIFIER) IS NOT NULL;

UPDATE w
   SET assignedToUserId  = CASE @action WHEN 'assignToMe' THEN @me WHEN 'assign' THEN @assignToUserId
                                        ELSE w.assignedToUserId END
     , taskStatus        = CASE @action WHEN 'complete' THEN 'done' WHEN 'dismiss' THEN 'dismissed'
                                        WHEN 'reopen' THEN 'open' ELSE w.taskStatus END
     , completedUtc      = CASE WHEN @action IN ('complete', 'dismiss') THEN SYSUTCDATETIME ()
                                WHEN @action = 'reopen' THEN NULL ELSE w.completedUtc END
     , completedByUserId = CASE WHEN @action IN ('complete', 'dismiss') THEN @me
                                WHEN @action = 'reopen' THEN NULL ELSE w.completedByUserId END
     , resolutionNote    = CASE WHEN @action IN ('complete', 'dismiss') THEN COALESCE (@note, w.resolutionNote)
                                ELSE w.resolutionNote END
  FROM dbo.workTask AS w
  JOIN @ids AS i ON i.workTaskId = w.workTaskId
 WHERE w.isDeleted = 0
   AND ((@action IN ('complete', 'dismiss') AND w.taskStatus = 'open')
        OR (@action = 'reopen' AND w.taskStatus <> 'open')
        OR (@action IN ('assign', 'assignToMe') AND w.taskStatus = 'open'
            AND (w.assignedToUserId IS NULL OR w.assignedToUserId <> CASE WHEN @action = 'assignToMe' THEN @me ELSE @assignToUserId END)));

DECLARE @updatedCount INT = @@ROWCOUNT;
SET @Comments = CONCAT (@updatedCount, N' task(s) updated.');
SELECT @updatedCount AS updatedCount;
""",
    },
    # -------------------------------------------------------------------------------------------- job queue
    {
        "schema": "dbo", "name": "uspEnqueueJob", "kind": "write", "grants": BOTH, "ticket": "T049",
        "desc": "Records a background job for the worker and returns at once. The job carries the tenant and user that "
                "started it; the worker runs it under that user's session scope.",
        "params": """@jobId          UNIQUEIDENTIFIER
, @jobType        VARCHAR (40)
, @payloadJson    NVARCHAR (MAX)
, @payloadVersion INT = 1
, @availableUtc   DATETIME2 (3) = NULL""",
        "key": "CONCAT (N'jobId=', @jobId, N', jobType=', @jobType)",
        "validate": """IF ISJSON (@payloadJson) = 0
BEGIN
    ;THROW 50000, N'@payloadJson must be JSON.', 1;
END;""",
        "body": f"""
DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();
DECLARE @user UNIQUEIDENTIFIER = auth.udfSessionUserId ();
{JOB_SCOPE}

IF @root IS NULL OR @user IS NULL
BEGIN
    ;THROW 51403, N'A job can only be started from a signed-in session.', 1;
END;

INSERT dbo.jobQueue (jobId, rootTenantId, owningTenantId, jobType, payloadJson, payloadVersion, availableUtc,
                     requestedByUserId, {JOB_SCOPE_COLUMNS})
VALUES (@jobId, @root, auth.udfSessionActingTenantId (), @jobType, @payloadJson, @payloadVersion,
        COALESCE (@availableUtc, SYSUTCDATETIME ()), @user, {JOB_SCOPE_VALUES});

SET @Comments = N'Job queued.';
""",
    },
    {
        "schema": "dbo", "name": "uspClaimNextJob", "kind": "write", "grants": WORKER, "ticket": "T049",
        "desc": "Claims the next job for a worker copy. Row locks with READPAST mean two copies never take the same job; "
                "tenants with the fewest running jobs go first, so one state's large file cannot starve the others.",
        "params": """@workerName VARCHAR (120)
, @maxRunningPerTenant INT = 2""",
        "key": "CONCAT (N'workerName=', @workerName)",
        "notes": "Returns zero rows when nothing is waiting. A job left running past 30 minutes by a worker that died is "
                 "returned to the queue first.",
        "body": """
UPDATE dbo.jobQueue
   SET jobStatus = 'queued', claimedBy = NULL, claimedUtc = NULL, lastError = N'Returned to queue: worker stopped responding.'
 WHERE jobStatus = 'running' AND claimedUtc < DATEADD (MINUTE, -30, SYSUTCDATETIME ()) AND isDeleted = 0;

DECLARE @claimed TABLE (jobId UNIQUEIDENTIFIER);

;WITH running AS
(
    SELECT rootTenantId, COUNT (*) AS n FROM dbo.jobQueue WHERE jobStatus = 'running' AND isDeleted = 0
     GROUP BY rootTenantId
),
candidate AS
(
    SELECT TOP (1) j.jobId
      FROM dbo.jobQueue AS j WITH (UPDLOCK, READPAST, ROWLOCK)
      LEFT JOIN running AS r ON r.rootTenantId = j.rootTenantId
     WHERE j.jobStatus = 'queued' AND j.isDeleted = 0 AND j.availableUtc <= SYSUTCDATETIME ()
       AND COALESCE (r.n, 0) < @maxRunningPerTenant
     ORDER BY COALESCE (r.n, 0), j.availableUtc
)
UPDATE j
   SET jobStatus = 'running', claimedBy = @workerName, claimedUtc = SYSUTCDATETIME (), attempts = j.attempts + 1
OUTPUT inserted.jobId INTO @claimed
  FROM dbo.jobQueue AS j
  JOIN candidate AS n ON n.jobId = j.jobId;

SELECT j.jobId, j.rootTenantId, j.owningTenantId, j.jobType, j.payloadJson, j.payloadVersion, j.attempts, j.maxAttempts,
       j.requestedByUserId, j.requestedProfileId, j.requestedViewAsUserId, j.requestedActingTenantId
  FROM dbo.jobQueue AS j JOIN @claimed AS c ON c.jobId = j.jobId;

SET @Comments = CONCAT ((SELECT COUNT (*) FROM @claimed), N' job(s) claimed.');
""",
    },
    {
        "schema": "dbo", "name": "uspCompleteJob", "kind": "write", "grants": WORKER, "ticket": "T049",
        "desc": "Marks a job succeeded with a result summary the polling screen shows.",
        "params": """@jobId      UNIQUEIDENTIFIER
, @resultJson NVARCHAR (MAX) = NULL""",
        "key": "CONCAT (N'jobId=', @jobId)",
        "body": """
UPDATE dbo.jobQueue
   SET jobStatus = 'succeeded', completedUtc = SYSUTCDATETIME (), resultJson = @resultJson, lastError = NULL
 WHERE jobId = @jobId AND isDeleted = 0;

SET @Comments = CONCAT (@@ROWCOUNT, N' job(s) completed.');
""",
    },
    {
        "schema": "dbo", "name": "uspFailJob", "kind": "write", "grants": WORKER, "ticket": "T049",
        "desc": "Records a job failure. The job is retried with exponential back-off until its attempts run out, then "
                "marked failed for the administration screen. It is never dropped.",
        "params": """@jobId      UNIQUEIDENTIFIER
, @errorText  NVARCHAR (2000)""",
        "key": "CONCAT (N'jobId=', @jobId)",
        "notes": "@errorText must carry record IDs only, never patient data; the worker passes exception type and ids.",
        "body": """
UPDATE dbo.jobQueue
   SET jobStatus    = CASE WHEN attempts >= maxAttempts THEN 'failed' ELSE 'queued' END
     , completedUtc = CASE WHEN attempts >= maxAttempts THEN SYSUTCDATETIME () ELSE NULL END
     , availableUtc = DATEADD (SECOND, 30 * POWER (2, LEAST (attempts, 8)), SYSUTCDATETIME ())
     , claimedBy    = NULL
     , lastError    = LEFT (@errorText, 2000)
 WHERE jobId = @jobId AND isDeleted = 0;

SET @Comments = N'Job failure recorded.';
""",
    },
    {
        "schema": "dbo", "name": "uspGetJob", "kind": "read", "grants": BOTH, "ticket": "T049",
        "desc": "A job's status for a polling screen. Callers see only jobs of their own root tenant.",
        "params": "@jobId UNIQUEIDENTIFIER",
        "key": "CONCAT (N'jobId=', @jobId)",
        "body": """
SELECT jobId, jobType, jobStatus, attempts, maxAttempts, availableUtc, claimedUtc, completedUtc, lastError, resultJson,
       createdDateUtc
  FROM dbo.jobQueue
 WHERE jobId = @jobId AND isDeleted = 0
   AND (USER_NAME () = N'dbo' OR rootTenantId = auth.udfSessionRootTenantId ()
        OR IS_ROLEMEMBER (N'meritWorkerRole') = 1);
""",
    },
    {
        "schema": "dbo", "name": "uspGetQueueDepth", "kind": "read", "grants": BOTH, "ticket": "T120",
        "desc": "Queue depth and the oldest waiting job per tenant and job type, for the worker health endpoint and the "
                "administration screen.",
        "params": "",
        "key": "NULL",
        "body": """
SELECT j.rootTenantId, t.tenantName, j.jobType,
       SUM (CASE WHEN j.jobStatus = 'queued' THEN 1 ELSE 0 END) AS queued,
       SUM (CASE WHEN j.jobStatus = 'running' THEN 1 ELSE 0 END) AS running,
       SUM (CASE WHEN j.jobStatus = 'failed' THEN 1 ELSE 0 END) AS failed,
       MIN (CASE WHEN j.jobStatus = 'queued' THEN j.availableUtc END) AS oldestWaitingUtc
  FROM dbo.jobQueue AS j
  JOIN auth.tenant AS t ON t.tenantId = j.rootTenantId
 WHERE j.isDeleted = 0 AND j.jobStatus IN ('queued', 'running', 'failed')
 GROUP BY j.rootTenantId, t.tenantName, j.jobType;
""",
    },
    # -------------------------------------------------------------------------------------------- clusters
    {
        "schema": "dbo", "name": "uspDetectClusters", "kind": "write", "grants": WORKER, "ticket": "T123",
        "permission": "case.open",
        "desc": "Cluster detection for the caller's root tenant: two or more cases at one primary residence location or "
                "building, or at one employer worksite, opened within the window. Each new group becomes a cluster "
                "record and an alert task; nothing is merged and no case changes.",
        "params": """@windowDays INT = 365
, @minCount   INT = 2""",
        "key": "CONCAT (N'windowDays=', @windowDays, N', minCount=', @minCount)",
        "body": f"""
DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();
DECLARE @from DATE = DATEADD (DAY, -@windowDays, auth.udfSessionToday ());
DECLARE @to DATE = auth.udfSessionToday ();
DECLARE @groups TABLE (clusterType VARCHAR (20), sharedKey UNIQUEIDENTIFIER, title NVARCHAR (200), caseCount INT,
                       owner UNIQUEIDENTIFIER);
DECLARE @members TABLE (clusterType VARCHAR (20), sharedKey UNIQUEIDENTIFIER, exposureCaseId UNIQUEIDENTIFIER);

-- Shared building (or the location itself when it has no building record), from primary residences on the open date.
INSERT @members
SELECT DISTINCT 'building', COALESCE (l.buildingLocationId, l.locationId), c.exposureCaseId
  FROM dbo.exposureCase AS c
  JOIN dbo.personLocation AS pl ON pl.personId = c.personId AND pl.role IN ('residence', 'childCare') AND pl.isDeleted = 0
                               AND pl.startDate <= c.openedDate AND (pl.endDate IS NULL OR pl.endDate >= c.openedDate)
  JOIN dbo.location AS l ON l.locationId = pl.locationId
 WHERE c.isDeleted = 0 AND c.openedDate BETWEEN @from AND @to AND c.rootTenantId = @root;

-- Shared employer worksite.
INSERT @members
SELECT DISTINCT 'worksite', COALESCE (e.worksiteLocationId, e.organizationId), c.exposureCaseId
  FROM dbo.exposureCase AS c
  JOIN dbo.employment AS e ON e.personId = c.personId AND e.isDeleted = 0
                          AND (e.startDate IS NULL OR e.startDate <= c.openedDate)
                          AND (e.endDate IS NULL OR e.endDate >= DATEADD (DAY, -90, c.openedDate))
 WHERE c.isDeleted = 0 AND c.openedDate BETWEEN @from AND @to AND c.rootTenantId = @root;

INSERT @groups
SELECT m.clusterType, m.sharedKey,
       CASE m.clusterType WHEN 'building' THEN CONCAT (N'Cases at ', MAX (l.addressLine1), N', ', MAX (l.city))
            ELSE CONCAT (N'Cases at worksite ', COALESCE (MAX (wl.addressLine1), MAX (o.organizationName))) END,
       COUNT (DISTINCT m.exposureCaseId), MIN (c.owningTenantId)
  FROM @members AS m
  JOIN dbo.exposureCase AS c ON c.exposureCaseId = m.exposureCaseId
  LEFT JOIN dbo.location AS l ON m.clusterType = 'building' AND l.locationId = m.sharedKey
  LEFT JOIN dbo.location AS wl ON m.clusterType = 'worksite' AND wl.locationId = m.sharedKey
  LEFT JOIN dbo.organization AS o ON m.clusterType = 'worksite' AND o.organizationId = m.sharedKey
 GROUP BY m.clusterType, m.sharedKey
HAVING COUNT (DISTINCT m.exposureCaseId) >= @minCount;

-- A cluster that spans tenants goes to the root (the nearest common ancestor in a one-level tree).
UPDATE g SET owner = @root
  FROM @groups AS g
 WHERE (SELECT COUNT (DISTINCT c.owningTenantId) FROM @members AS m JOIN dbo.exposureCase AS c ON c.exposureCaseId = m.exposureCaseId
         WHERE m.clusterType = g.clusterType AND m.sharedKey = g.sharedKey) > 1;

DECLARE @new TABLE (clusterId UNIQUEIDENTIFIER, clusterType VARCHAR (20), sharedKey UNIQUEIDENTIFIER, title NVARCHAR (200),
                    owner UNIQUEIDENTIFIER);

INSERT dbo.cluster (clusterId, rootTenantId, owningTenantId, clusterType, sharedKey, title, windowStart, windowEnd)
OUTPUT inserted.clusterId, inserted.clusterType, inserted.sharedKey, inserted.title, inserted.owningTenantId INTO @new
SELECT {NEWID}, @root, g.owner, g.clusterType, g.sharedKey, g.title, @from, @to
  FROM @groups AS g
 WHERE NOT EXISTS (SELECT 1 FROM dbo.cluster AS x WHERE x.clusterType = g.clusterType AND x.sharedKey = g.sharedKey
                                                    AND x.clusterStatus <> 'closed' AND x.isDeleted = 0);

-- Membership for new and existing open clusters.
INSERT dbo.caseCluster (caseClusterId, rootTenantId, owningTenantId, clusterId, exposureCaseId, joinedDate)
SELECT {NEWID}, @root, cl.owningTenantId, cl.clusterId, m.exposureCaseId, @to
  FROM @members AS m
  JOIN dbo.cluster AS cl ON cl.clusterType = m.clusterType AND cl.sharedKey = m.sharedKey AND cl.clusterStatus <> 'closed'
                        AND cl.isDeleted = 0
 WHERE EXISTS (SELECT 1 FROM @groups AS g WHERE g.clusterType = m.clusterType AND g.sharedKey = m.sharedKey)
   AND NOT EXISTS (SELECT 1 FROM dbo.caseCluster AS cc WHERE cc.clusterId = cl.clusterId
                      AND cc.exposureCaseId = m.exposureCaseId AND cc.isDeleted = 0);

INSERT dbo.workTask (workTaskId, rootTenantId, owningTenantId, queueCode, title, detail, clusterId, isUrgent)
SELECT {NEWID}, @root, n.owner, 'cluster', n.title,
       CONCAT (N'Cluster alert for the ', CASE n.clusterType WHEN 'building' THEN N'residential' ELSE N'occupational' END,
               N' team: review the linked cases.'), n.clusterId, 0
  FROM @new AS n;

SET @Comments = CONCAT ((SELECT COUNT (*) FROM @new), N' new cluster(s).');
""",
    },
    # -------------------------------------------------------------------------------------------- audit
    {
        "schema": "logs", "name": "uspRecordAuditEvent", "kind": "write", "grants": BOTH, "ticket": "T051",
        "desc": "Writes one event to the tamper-evident audit ledger: a view, change, merge or export of a person, result "
                "or case. The user and root tenant come from the session, never from a parameter.",
        "params": """@eventAction   VARCHAR (20)
, @entityType    VARCHAR (30)
, @entityId      UNIQUEIDENTIFIER = NULL
, @recordCount   INT = NULL
, @clientAddress VARCHAR (45) = NULL""",
        "key": "CONCAT (N'eventAction=', @eventAction, N', entityType=', @entityType, N', entityId=', @entityId)",
        "body": """
INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId, recordCount, clientAddress)
VALUES (auth.udfSessionUserId (), auth.udfSessionRootTenantId (), @eventAction, @entityType, @entityId, @recordCount,
        @clientAddress);

SET @Comments = N'Audit event recorded.';
""",
    },
    {
        "schema": "logs", "name": "uspSearchAuditEvents", "kind": "read", "grants": WEB, "ticket": "T051",
        "permission": "audit.view",
        "desc": "Searches the audit ledger within the caller's root tenant by user, record, action, record type and time, "
                "newest first, one page at a time with the total. Record type 'report' covers every report.",
        "params": """@appUserId   UNIQUEIDENTIFIER = NULL
, @entityId    UNIQUEIDENTIFIER = NULL
, @eventAction VARCHAR (20) = NULL
, @entityType  VARCHAR (30) = NULL
, @fromUtc     DATETIME2 (3) = NULL
, @toUtc       DATETIME2 (3) = NULL
, @pageNumber  INT = 1
, @pageSize    INT = 100""",
        "key": "CONCAT (N'appUserId=', @appUserId, N', entityId=', @entityId, N', eventAction=', @eventAction, "
               "N', entityType=', @entityType, N', pageNumber=', @pageNumber, N', pageSize=', @pageSize)",
        "validate": """IF @pageNumber < 1 OR @pageSize NOT BETWEEN 1 AND 10000
BEGIN
    ;THROW 50000, N'@pageNumber must be 1 or more and @pageSize between 1 and 10000.', 1;
END;""",
        "body": """
SELECT e.auditEventId, e.eventUtc, e.appUserId, u.displayName, e.eventAction, e.entityType, e.entityId,
       e.recordCount, e.clientAddress, COUNT (*) OVER () AS totalCount
  FROM logs.auditEvent AS e
  LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
 WHERE e.rootTenantId = auth.udfSessionRootTenantId ()
   AND (@appUserId IS NULL OR e.appUserId = @appUserId)
   AND (@entityId IS NULL OR e.entityId = @entityId)
   AND (@eventAction IS NULL OR e.eventAction = @eventAction)
   AND (@entityType IS NULL OR e.entityType = @entityType OR (@entityType = 'report' AND e.entityType LIKE 'report:%'))
   AND (@fromUtc IS NULL OR e.eventUtc >= @fromUtc)
   AND (@toUtc IS NULL OR e.eventUtc < @toUtc)
 ORDER BY e.auditEventId DESC
OFFSET (@pageNumber - 1) * @pageSize ROWS
 FETCH NEXT @pageSize ROWS ONLY
OPTION (RECOMPILE);
""",
    },
    {
        "schema": "logs", "name": "uspListAuditUsers", "kind": "read", "grants": WEB, "ticket": "T051",
        "permission": "audit.view",
        "desc": "Lists the users who appear in the caller's root tenant's audit ledger, by name, for the audit log's user filter.",
        "body": """
SELECT u.appUserId, u.displayName
  FROM auth.appUser AS u
 WHERE EXISTS (SELECT 1
                 FROM logs.auditEvent AS e
                WHERE e.appUserId = u.appUserId
                  AND e.rootTenantId = auth.udfSessionRootTenantId ())
 ORDER BY u.displayName, u.appUserId;
""",
    },
]
