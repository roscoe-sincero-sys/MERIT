"""Person matching review, clusters, the case map and the standard report library."""
from procs_common import NEWID, WEB, BOTH, CONCURRENCY, not_found

ROW_HELD = """IF NOT EXISTS (SELECT 1 FROM dbo.stagingRow WHERE stagingRowId = @stagingRowId AND rowStatus = 'held' AND isDeleted = 0)
BEGIN
    ;THROW 51404, N'The held row was not found, is no longer held, or you do not have access to it.', 1;
END;"""

CLUSTER_VISIBLE = not_found("@clusterId", "dbo.cluster", "clusterId", "The cluster")

PAGING = """IF @pageNumber < 1 OR @pageSize NOT BETWEEN 1 AND 500
BEGIN
    ;THROW 50000, N'@pageNumber must be 1 or more and @pageSize between 1 and 500.', 1;
END;"""

PERIOD = """IF @fromDate IS NULL OR @toDate IS NULL OR @toDate < @fromDate OR DATEDIFF (DAY, @fromDate, @toDate) > 3660
BEGIN
    ;THROW 50000, N'@fromDate and @toDate are required, in order, and at most ten years apart.', 1;
END;"""

# The lowest active case level for a result's analyte specimen and population on its collection date, in canonical
# units. Used by the testing-count report to count elevated results without storing a flag that goes stale when levels
# change.
CASE_LEVEL = """(SELECT MIN (t.lowerValue) FROM config.threshold AS t
          WHERE t.analyteSpecimenId = r.analyteSpecimenId AND t.thresholdType = 'case' AND t.isActive = 1
            AND t.isDeleted = 0 AND t.population IN (r.population, 'all') AND t.effectiveFrom <= r.collectedDate
            AND (t.effectiveTo IS NULL OR t.effectiveTo >= r.collectedDate))"""

PROCS = [
    # -------------------------------------------------------------------------------------------- matching review
    {
        "schema": "dbo", "name": "uspListMatchReviewRows", "kind": "read", "grants": WEB, "ticket": "T108",
        "permission": "result.release_hold",
        "desc": "The person matching review queue: held rows whose identity needs a reviewer (an uncertain match or a "
                "placeholder name), urgent first, then oldest. Rows a reviewer marked Not sure are flagged as escalated "
                "to a supervisor.",
        "params": """@escalatedOnly BIT = 0
, @stagingRowId  UNIQUEIDENTIFIER = NULL
, @pageNumber    INT = 1
, @pageSize      INT = 100""",
        "key": "CONCAT (N'escalatedOnly=', @escalatedOnly, N', pageNumber=', @pageNumber)",
        "validate": PAGING,
        "body": """
SELECT s.stagingRowId, s.intakeBatchId, b.fileName, o.organizationName AS senderName, s.rowNumber, s.parsedJson,
       s.correctedJson, s.isUrgent, s.heldSinceUtc, h.reasonMessage,
       CAST (CASE WHEN w.workTaskId IS NULL THEN 0 ELSE 1 END AS BIT) AS isEscalated, w.detail AS escalationNote,
       COUNT (*) OVER () AS totalCount
  FROM dbo.stagingRow AS s
  JOIN dbo.intakeBatch AS b ON b.intakeBatchId = s.intakeBatchId
  LEFT JOIN dbo.organization AS o ON o.organizationId = b.senderOrganizationId
 CROSS APPLY (SELECT TOP (1) x.reasonMessage FROM dbo.stagingHoldReason AS x
               WHERE x.stagingRowId = s.stagingRowId AND x.reasonCode = 'uncertainMatch' AND x.isResolved = 0
                 AND x.isDeleted = 0
               ORDER BY x.createdDateUtc) AS h
 OUTER APPLY (SELECT TOP (1) t.workTaskId, t.detail FROM dbo.workTask AS t
               WHERE t.stagingRowId = s.stagingRowId AND t.queueCode = 'matchReview' AND t.taskStatus = 'open'
                 AND t.isDeleted = 0) AS w
 WHERE s.rowStatus = 'held' AND s.isDeleted = 0
   AND (@escalatedOnly = 0 OR w.workTaskId IS NOT NULL)
   AND (@stagingRowId IS NULL OR s.stagingRowId = @stagingRowId)
 ORDER BY s.isUrgent DESC, s.heldSinceUtc, s.stagingRowId
OFFSET (@pageNumber - 1) * @pageSize ROWS
 FETCH NEXT @pageSize ROWS ONLY;
""",
    },
    {
        "schema": "dbo", "name": "uspGetMatchCandidateDetails", "kind": "read", "grants": WEB, "ticket": "T108",
        "permission": "person.view",
        "desc": "What the review screen shows for each candidate person: identity and aliases, place history and recent "
                "results, so the reviewer can compare field by field and open the history in one click.",
        "params": "@personIdsJson NVARCHAR (MAX)",
        "key": "N'candidates'",
        "validate": """IF ISJSON (@personIdsJson) = 0
BEGIN
    ;THROW 50000, N'@personIdsJson must be a JSON array of ids.', 1;
END;""",
        "body": """
DECLARE @ids TABLE (personId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @ids SELECT DISTINCT TRY_CAST ([value] AS UNIQUEIDENTIFIER) FROM OPENJSON (@personIdsJson)
             WHERE TRY_CAST ([value] AS UNIQUEIDENTIFIER) IS NOT NULL;

SELECT p.personId, p.lastName, p.firstName, p.middleName, p.dob, p.sex, p.isPlaceholderName,
       (SELECT STRING_AGG (CONCAT (a.lastName, N', ', a.firstName), N'; ')
          FROM dbo.personAlias AS a WHERE a.personId = p.personId AND a.isDeleted = 0
           AND (a.lastName <> p.lastName OR COALESCE (a.firstName, N'') <> COALESCE (p.firstName, N''))) AS aliases,
       (SELECT COUNT (*) FROM dbo.labResult AS r WHERE r.personId = p.personId AND r.isDeleted = 0
           AND r.resultRowStatus = 'valid') AS resultCount,
       (SELECT MAX (r.collectedDate) FROM dbo.labResult AS r WHERE r.personId = p.personId AND r.isDeleted = 0) AS lastCollectedDate,
       (SELECT STRING_AGG (c.caseNumber, ', ') FROM dbo.exposureCase AS c
         WHERE c.personId = p.personId AND c.caseStatus <> 'closed' AND c.isDeleted = 0) AS openCaseNumbers
  FROM dbo.person AS p
  JOIN @ids AS i ON i.personId = p.personId
 WHERE p.isDeleted = 0;

SELECT pl.personId, pl.role, pl.isPrimary, pl.startDate, pl.endDate, l.addressLine1, l.unit, l.city, l.state, l.zip5,
       l.addressKey
  FROM dbo.personLocation AS pl
  JOIN @ids AS i ON i.personId = pl.personId
  JOIN dbo.location AS l ON l.locationId = pl.locationId
 WHERE pl.isDeleted = 0
 ORDER BY pl.personId, CASE WHEN pl.endDate IS NULL THEN 0 ELSE 1 END, pl.startDate DESC;

SELECT x.personId, x.collectedDate, x.analyteCode, x.operator, x.valueCanonical, x.unitCanonical, x.sampleType, x.labName
  FROM (SELECT r.personId, r.collectedDate, a.code AS analyteCode, r.operator, r.valueCanonical, r.unitCanonical,
               r.sampleType, lab.organizationName AS labName,
               ROW_NUMBER () OVER (PARTITION BY r.personId ORDER BY r.collectedDate DESC) AS rowNo
          FROM dbo.labResult AS r
          JOIN @ids AS i ON i.personId = r.personId
          JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId = r.analyteSpecimenId
          JOIN config.analyte AS a ON a.analyteId = s.analyteId
          LEFT JOIN dbo.organization AS lab ON lab.organizationId = r.labOrganizationId
         WHERE r.isDeleted = 0 AND r.resultRowStatus = 'valid') AS x
 WHERE x.rowNo <= 10
 ORDER BY x.personId, x.collectedDate DESC;
""",
    },
    {
        "schema": "dbo", "name": "uspRecordMatchDecision", "kind": "write", "grants": WEB, "ticket": "T108",
        "permission": "result.release_hold",
        "desc": "Records a reviewer's decision on a held row. same: the row links to the chosen person when it is "
                "released. different: the pair is remembered and never suggested again. newPerson: the row creates a new "
                "person when released. notSure: the row goes to a supervisor through the matchReview queue. The parsed "
                "values never change; the decision is stored beside them with the corrections.",
        "params": """@stagingRowId UNIQUEIDENTIFIER
, @decision     VARCHAR (12)
, @personId     UNIQUEIDENTIFIER = NULL
, @incomingKey  NVARCHAR (200) = NULL
, @note         NVARCHAR (500) = NULL""",
        "key": "CONCAT (N'stagingRowId=', @stagingRowId, N', decision=', @decision, N', personId=', @personId)",
        "validate": """IF @decision IS NULL OR @decision NOT IN ('same', 'different', 'newPerson', 'notSure')
BEGIN
    ;THROW 50000, N'@decision must be same, different, newPerson or notSure.', 1;
END;
IF @decision IN ('same', 'different') AND @personId IS NULL
BEGIN
    ;THROW 51422, N'Choose the candidate person for a Same person or Different person decision.', 1;
END;
IF @decision = 'different' AND (@incomingKey IS NULL OR LEN (@incomingKey) = 0)
BEGIN
    ;THROW 51422, N'A Different person decision needs the incoming record''s name and date of birth.', 1;
END;
""" + ROW_HELD + """
IF @personId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.person WHERE personId = @personId AND isDeleted = 0
                                                                     AND mergedIntoPersonId IS NULL)
BEGIN
    ;THROW 51404, N'The candidate person was not found, or you do not have access to them.', 1;
END;""",
        "body": f"""
DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();
DECLARE @me UNIQUEIDENTIFIER = auth.udfSessionUserId ();
DECLARE @owner UNIQUEIDENTIFIER = (SELECT owningTenantId FROM dbo.stagingRow WHERE stagingRowId = @stagingRowId);
DECLARE @corrected NVARCHAR (MAX) = (SELECT COALESCE (correctedJson, N'{{}}') FROM dbo.stagingRow
                                      WHERE stagingRowId = @stagingRowId);

IF @decision = 'same'
BEGIN
    SET @corrected = JSON_MODIFY (JSON_MODIFY (@corrected, '$.matchNewPerson', NULL), '$.matchPersonId',
                                  CONVERT (NVARCHAR (36), @personId));
    UPDATE dbo.stagingRow SET correctedJson = @corrected
     WHERE stagingRowId = @stagingRowId AND rowStatus = 'held';
END;

IF @decision = 'newPerson'
BEGIN
    SET @corrected = JSON_MODIFY (JSON_MODIFY (@corrected, '$.matchPersonId', NULL), '$.matchNewPerson', N'true');
    UPDATE dbo.stagingRow SET correctedJson = @corrected
     WHERE stagingRowId = @stagingRowId AND rowStatus = 'held';
END;

IF @decision = 'different'
   AND NOT EXISTS (SELECT 1 FROM dbo.matchExclusion WHERE incomingKey = @incomingKey AND personId = @personId
                                                      AND isDeleted = 0)
BEGIN
    INSERT dbo.matchExclusion (matchExclusionId, rootTenantId, owningTenantId, personId, incomingKey, stagingRowId,
                               decisionNote)
    VALUES ({NEWID}, @root, COALESCE (auth.udfSessionActingTenantId (), @owner), @personId, @incomingKey,
            @stagingRowId, @note);
END;

IF @decision = 'notSure'
   AND NOT EXISTS (SELECT 1 FROM dbo.workTask WHERE stagingRowId = @stagingRowId AND queueCode = 'matchReview'
                                                AND taskStatus = 'open' AND isDeleted = 0)
BEGIN
    INSERT dbo.workTask (workTaskId, rootTenantId, owningTenantId, queueCode, title, detail, stagingRowId, isUrgent,
                         dueDate)
    SELECT {NEWID}, @root, s.owningTenantId, 'matchReview', N'Person match: reviewer not sure',
           COALESCE (@note, N'A reviewer could not decide whether this record is an existing person.'), s.stagingRowId,
           s.isUrgent, DATEADD (DAY, CASE WHEN s.isUrgent = 1 THEN 1 ELSE 5 END, CAST (SYSUTCDATETIME () AS DATE))
      FROM dbo.stagingRow AS s WHERE s.stagingRowId = @stagingRowId;
END;

IF @decision IN ('same', 'newPerson')
BEGIN
    UPDATE dbo.workTask
       SET taskStatus = 'done', completedUtc = SYSUTCDATETIME (), completedByUserId = @me,
           resolutionNote = CASE @decision WHEN 'same' THEN N'Linked to an existing person.' ELSE N'Created as a new person.' END
     WHERE stagingRowId = @stagingRowId AND queueCode = 'matchReview' AND taskStatus = 'open' AND isDeleted = 0;
END;

SET @Comments = CONCAT (N'Decision ', @decision, N' recorded.');
""",
    },
    # -------------------------------------------------------------------------------------------- clusters
    {
        "schema": "dbo", "name": "uspListClusters", "kind": "read", "grants": WEB, "ticket": "T109",
        "permission": "case.view",
        "desc": "Clusters the caller can see, newest first, with the number of cases still in each.",
        "params": """@clusterStatus VARCHAR (20) = NULL
, @pageNumber    INT = 1
, @pageSize      INT = 50""",
        "key": "CONCAT (N'clusterStatus=', @clusterStatus, N', pageNumber=', @pageNumber)",
        "validate": PAGING,
        "body": """
SELECT cl.clusterId, cl.clusterType, cl.title, cl.clusterStatus, cl.windowStart, cl.windowEnd, cl.notes,
       cl.createdDateUtc, t.tenantName AS owningTenantName,
       (SELECT COUNT (*) FROM dbo.caseCluster AS cc WHERE cc.clusterId = cl.clusterId AND cc.leftDate IS NULL
           AND cc.isDeleted = 0) AS memberCount,
       COUNT (*) OVER () AS totalCount
  FROM dbo.cluster AS cl
  LEFT JOIN auth.tenant AS t ON t.tenantId = cl.owningTenantId
 WHERE cl.isDeleted = 0 AND (@clusterStatus IS NULL OR cl.clusterStatus = @clusterStatus)
 ORDER BY CASE cl.clusterStatus WHEN 'open' THEN 0 WHEN 'underReview' THEN 1 ELSE 2 END, cl.createdDateUtc DESC,
          cl.clusterId
OFFSET (@pageNumber - 1) * @pageSize ROWS
 FETCH NEXT @pageSize ROWS ONLY;
""",
    },
    {
        "schema": "dbo", "name": "uspGetCluster", "kind": "read", "grants": WEB, "ticket": "T109",
        "permission": "case.view",
        "desc": "One cluster: its header, every case that joined (with the primary residence on the case's open date "
                "and its coordinates, for the map) and its open tasks.",
        "params": "@clusterId UNIQUEIDENTIFIER",
        "key": "CONCAT (N'clusterId=', @clusterId)",
        "validate": CLUSTER_VISIBLE,
        "body": """
SELECT cl.clusterId, cl.clusterType, cl.sharedKey, cl.title, cl.clusterStatus, cl.windowStart, cl.windowEnd, cl.notes,
       cl.createdDateUtc, t.tenantName AS owningTenantName, cl.rowVersion
  FROM dbo.cluster AS cl
  LEFT JOIN auth.tenant AS t ON t.tenantId = cl.owningTenantId
 WHERE cl.clusterId = @clusterId;

SELECT cc.caseClusterId, cc.exposureCaseId, c.caseNumber, c.personId, p.lastName, p.firstName, a.code AS analyteCode,
       c.population, c.classification, c.caseStatus, c.openedDate, h.valueCanonical AS highestValue,
       h.unitCanonical AS highestUnit, cc.joinedDate, cc.leftDate, l.locationId, l.addressLine1, l.unit, l.city,
       l.latitude, l.longitude
  FROM dbo.caseCluster AS cc
  JOIN dbo.exposureCase AS c ON c.exposureCaseId = cc.exposureCaseId
  JOIN dbo.person AS p ON p.personId = c.personId
  JOIN config.analyte AS a ON a.analyteId = c.analyteId
  LEFT JOIN dbo.labResult AS h ON h.labResultId = c.highestLabResultId
 OUTER APPLY (SELECT TOP (1) x.locationId, x.addressLine1, x.unit, x.city, x.latitude, x.longitude
                FROM dbo.personLocation AS pl
                JOIN dbo.location AS x ON x.locationId = pl.locationId
               WHERE pl.personId = c.personId AND pl.role = 'residence' AND pl.isPrimary = 1 AND pl.isDeleted = 0
                 AND pl.startDate <= c.openedDate
               ORDER BY CASE WHEN pl.endDate IS NULL OR pl.endDate >= c.openedDate THEN 0 ELSE 1 END,
                        pl.startDate DESC) AS l
 WHERE cc.clusterId = @clusterId AND cc.isDeleted = 0
 ORDER BY CASE WHEN cc.leftDate IS NULL THEN 0 ELSE 1 END, c.openedDate;

SELECT w.workTaskId, w.title, w.detail, w.dueDate, w.isUrgent, u.displayName AS assignedToName
  FROM dbo.workTask AS w
  LEFT JOIN auth.appUser AS u ON u.appUserId = w.assignedToUserId
 WHERE w.clusterId = @clusterId AND w.taskStatus = 'open' AND w.isDeleted = 0;
""",
    },
    {
        "schema": "dbo", "name": "uspUpdateCluster", "kind": "write", "grants": WEB, "ticket": "T109",
        "permission": "case.edit",
        "desc": "Changes a cluster's status and notes. Closing a cluster completes its open alert tasks. Cases are "
                "never merged or changed.",
        "params": """@clusterId     UNIQUEIDENTIFIER
, @clusterStatus VARCHAR (20)
, @notes         NVARCHAR (2000) = NULL
, @rowVersion    BINARY (8)""",
        "key": "CONCAT (N'clusterId=', @clusterId, N', clusterStatus=', @clusterStatus)",
        "validate": """IF @clusterStatus NOT IN ('open', 'underReview', 'closed')
BEGIN
    ;THROW 51422, N'The status must be open, under review or closed.', 1;
END;
""" + CLUSTER_VISIBLE,
        "body": f"""
UPDATE dbo.cluster SET clusterStatus = @clusterStatus, notes = @notes
 WHERE clusterId = @clusterId AND isDeleted = 0 AND rowVersion = @rowVersion;

{CONCURRENCY}

IF @clusterStatus = 'closed'
BEGIN
    UPDATE dbo.workTask
       SET taskStatus = 'done', completedUtc = SYSUTCDATETIME (), completedByUserId = auth.udfSessionUserId (),
           resolutionNote = N'Cluster closed.'
     WHERE clusterId = @clusterId AND taskStatus = 'open' AND isDeleted = 0;
END;

SET @Comments = N'Cluster updated.';
""",
    },
    {
        "schema": "dbo", "name": "uspSetCaseClusterMember", "kind": "write", "grants": WEB, "ticket": "T109",
        "permission": "case.edit",
        "desc": "A case joins a cluster (by case number) or leaves it. Leaving keeps the row with its left date, so the "
                "cluster's history shows who was in it and when.",
        "params": """@clusterId  UNIQUEIDENTIFIER
, @caseNumber VARCHAR (20)
, @isMember   BIT""",
        "key": "CONCAT (N'clusterId=', @clusterId, N', caseNumber=', @caseNumber, N', isMember=', @isMember)",
        "validate": CLUSTER_VISIBLE + """
IF NOT EXISTS (SELECT 1 FROM dbo.exposureCase WHERE caseNumber = @caseNumber AND isDeleted = 0)
BEGIN
    ;THROW 51404, N'No case with that number was found, or you do not have access to it.', 1;
END;
IF EXISTS (SELECT 1 FROM dbo.cluster WHERE clusterId = @clusterId AND clusterStatus = 'closed')
BEGIN
    ;THROW 51409, N'The cluster is closed. Reopen it before changing its cases.', 1;
END;""",
        "body": f"""
DECLARE @caseId UNIQUEIDENTIFIER = (SELECT exposureCaseId FROM dbo.exposureCase WHERE caseNumber = @caseNumber
                                                                                  AND isDeleted = 0);
DECLARE @today DATE = CAST (SYSUTCDATETIME () AS DATE);

IF @isMember = 1
BEGIN
    UPDATE dbo.caseCluster SET leftDate = NULL
     WHERE clusterId = @clusterId AND exposureCaseId = @caseId AND isDeleted = 0 AND leftDate IS NOT NULL;

    INSERT dbo.caseCluster (caseClusterId, rootTenantId, owningTenantId, clusterId, exposureCaseId, joinedDate)
    SELECT {NEWID}, cl.rootTenantId, cl.owningTenantId, cl.clusterId, @caseId, @today
      FROM dbo.cluster AS cl
     WHERE cl.clusterId = @clusterId
       AND NOT EXISTS (SELECT 1 FROM dbo.caseCluster AS cc WHERE cc.clusterId = @clusterId
                                                             AND cc.exposureCaseId = @caseId AND cc.isDeleted = 0);
END
ELSE
BEGIN
    UPDATE dbo.caseCluster SET leftDate = @today
     WHERE clusterId = @clusterId AND exposureCaseId = @caseId AND isDeleted = 0 AND leftDate IS NULL;
END;

SET @Comments = CONCAT (N'Case ', @caseNumber, CASE @isMember WHEN 1 THEN N' joined.' ELSE N' left.' END);
""",
    },
    # -------------------------------------------------------------------------------------------- map
    {
        "schema": "dbo", "name": "uspGetCaseMap", "kind": "read", "grants": WEB, "ticket": "T109",
        "permission": "case.view",
        "desc": "Cases for the map: each case with the primary residence on its open date and that location's "
                "coordinates. Cases whose residence has no coordinates are returned too, so the screen can list them.",
        "params": """@analyteId  UNIQUEIDENTIFIER = NULL
, @openedFrom DATE = NULL
, @openedTo   DATE = NULL
, @openOnly   BIT = 1""",
        "key": "CONCAT (N'analyteId=', @analyteId, N', openOnly=', @openOnly)",
        "body": """
SELECT TOP (5000) c.exposureCaseId, c.caseNumber, c.personId, p.lastName, p.firstName, a.code AS analyteCode,
       c.population, c.classification, c.caseStatus, c.openedDate, h.valueCanonical AS highestValue,
       h.unitCanonical AS highestUnit, l.locationId, l.addressLine1, l.city, l.countyFips, l.latitude, l.longitude,
       l.geocodeQuality,
       (SELECT COUNT (*) FROM dbo.caseCluster AS cc
          JOIN dbo.cluster AS cl ON cl.clusterId = cc.clusterId AND cl.clusterStatus <> 'closed' AND cl.isDeleted = 0
         WHERE cc.exposureCaseId = c.exposureCaseId AND cc.leftDate IS NULL AND cc.isDeleted = 0) AS openClusterCount
  FROM dbo.exposureCase AS c
  JOIN dbo.person AS p ON p.personId = c.personId
  JOIN config.analyte AS a ON a.analyteId = c.analyteId
  LEFT JOIN dbo.labResult AS h ON h.labResultId = c.highestLabResultId
 OUTER APPLY (SELECT TOP (1) x.locationId, x.addressLine1, x.city, x.countyFips, x.latitude, x.longitude,
                     x.geocodeQuality
                FROM dbo.personLocation AS pl
                JOIN dbo.location AS x ON x.locationId = pl.locationId
               WHERE pl.personId = c.personId AND pl.role = 'residence' AND pl.isPrimary = 1 AND pl.isDeleted = 0
                 AND pl.startDate <= c.openedDate
               ORDER BY CASE WHEN pl.endDate IS NULL OR pl.endDate >= c.openedDate THEN 0 ELSE 1 END,
                        pl.startDate DESC) AS l
 WHERE c.isDeleted = 0
   AND (@openOnly = 0 OR c.caseStatus <> 'closed')
   AND (@analyteId IS NULL OR c.analyteId = @analyteId)
   AND (@openedFrom IS NULL OR c.openedDate >= @openedFrom)
   AND (@openedTo IS NULL OR c.openedDate <= @openedTo)
 ORDER BY c.openedDate DESC;
""",
    },
    {
        "schema": "dbo", "name": "uspSetLocationCoordinates", "kind": "write", "grants": BOTH, "ticket": "T109",
        "permission": "person.edit",
        "desc": "Sets a location's coordinates from a geocoder or a pin placed by staff, with the geocode quality.",
        "params": """@locationId     UNIQUEIDENTIFIER
, @latitude       DECIMAL (9, 6)
, @longitude      DECIMAL (9, 6)
, @geocodeQuality VARCHAR (20)""",
        "key": "CONCAT (N'locationId=', @locationId, N', geocodeQuality=', @geocodeQuality)",
        "validate": """IF @latitude NOT BETWEEN -90 AND 90 OR @longitude NOT BETWEEN -180 AND 180
BEGIN
    ;THROW 51422, N'Latitude must be between -90 and 90 and longitude between -180 and 180.', 1;
END;
IF @geocodeQuality NOT IN ('rooftop', 'parcel', 'street', 'zipCentroid', 'manualPin')
BEGIN
    ;THROW 51422, N'The geocode quality must be rooftop, parcel, street, zipCentroid or manualPin.', 1;
END;
""" + not_found("@locationId", "dbo.location", "locationId", "The location"),
        "body": """
UPDATE dbo.location SET latitude = @latitude, longitude = @longitude, geocodeQuality = @geocodeQuality
 WHERE locationId = @locationId AND isDeleted = 0;

SET @Comments = N'Coordinates set.';
""",
    },
    # -------------------------------------------------------------------------------------------- reports
    {
        "schema": "dbo", "name": "uspReportTestingCounts", "kind": "read", "grants": WEB, "ticket": "T110",
        "permission": "result.view",
        "desc": "Testing counts and rates by collection month and population: valid final results, people tested, and "
                "results at or above the case level in force on the collection date.",
        "params": """@fromDate  DATE
, @toDate    DATE
, @analyteId UNIQUEIDENTIFIER = NULL""",
        "key": "CONCAT (N'fromDate=', @fromDate, N', toDate=', @toDate, N', analyteId=', @analyteId)",
        "validate": PERIOD,
        "body": f"""
SELECT CONVERT (CHAR (7), x.monthStart, 126) AS periodMonth, x.population, COUNT (*) AS testCount,
       COUNT (DISTINCT x.personId) AS personCount,
       SUM (CASE WHEN x.caseLevel IS NOT NULL AND x.operator <> '<' AND x.valueCanonical >= x.caseLevel THEN 1 ELSE 0 END)
           AS elevatedCount
  FROM (SELECT DATEFROMPARTS (YEAR (r.collectedDate), MONTH (r.collectedDate), 1) AS monthStart, r.population,
               r.personId, r.operator, r.valueCanonical,
               {CASE_LEVEL} AS caseLevel
          FROM dbo.labResult AS r
          JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId = r.analyteSpecimenId
         WHERE r.isDeleted = 0 AND r.resultRowStatus = 'valid' AND r.resultStatus IN ('final', 'corrected')
           AND r.collectedDate BETWEEN @fromDate AND @toDate
           AND (@analyteId IS NULL OR s.analyteId = @analyteId)) AS x
 GROUP BY x.monthStart, x.population
 ORDER BY x.monthStart, x.population;
""",
    },
    {
        "schema": "dbo", "name": "uspReportElevatedCases", "kind": "read", "grants": WEB, "ticket": "T110",
        "permission": "case.view",
        "desc": "Cases opened in the period by analyte, population and classification, with how many are still open.",
        "params": """@fromDate  DATE
, @toDate    DATE
, @analyteId UNIQUEIDENTIFIER = NULL""",
        "key": "CONCAT (N'fromDate=', @fromDate, N', toDate=', @toDate, N', analyteId=', @analyteId)",
        "validate": PERIOD,
        "body": """
SELECT a.code AS analyteCode, c.population, c.classification, COUNT (*) AS caseCount,
       SUM (CASE WHEN c.caseStatus <> 'closed' THEN 1 ELSE 0 END) AS openCount
  FROM dbo.exposureCase AS c
  JOIN config.analyte AS a ON a.analyteId = c.analyteId
 WHERE c.isDeleted = 0 AND c.openedDate BETWEEN @fromDate AND @toDate
   AND (@analyteId IS NULL OR c.analyteId = @analyteId)
 GROUP BY a.code, c.population, c.classification
 ORDER BY a.code, c.population, c.classification;
""",
    },
    {
        "schema": "dbo", "name": "uspReportCaseTimeliness", "kind": "read", "grants": WEB, "ticket": "T110",
        "permission": "case.view",
        "desc": "Case timeliness by month opened: median days from collection to receipt of the qualifying result, from "
                "receipt to the case opening, and from opening to the first follow-up activity, with the number of "
                "cases followed up within seven days and the number not followed up yet.",
        "notes": "Follow-up means work with the family or provider: a contact attempt, letter, education, referral or "
                 "medical management. Status changes (every case gets one on its open date) and notes are not "
                 "follow-up; counting them made the median 0 every month (issue #13). A case with no follow-up yet "
                 "is counted on its own, not as 0 days, and is left out of the median.",
        "params": """@fromDate  DATE
, @toDate    DATE
, @analyteId UNIQUEIDENTIFIER = NULL""",
        "key": "CONCAT (N'fromDate=', @fromDate, N', toDate=', @toDate, N', analyteId=', @analyteId)",
        "validate": PERIOD,
        "body": """
WITH perCase AS (
    SELECT DATEFROMPARTS (YEAR (c.openedDate), MONTH (c.openedDate), 1) AS monthStart,
           DATEDIFF (DAY, q.collectedDate, CAST (q.receivedUtc AS DATE)) AS daysToReceipt,
           DATEDIFF (DAY, CAST (q.receivedUtc AS DATE), c.openedDate) AS daysToOpen,
           DATEDIFF (DAY, c.openedDate, (SELECT MIN (ca.activityDate) FROM dbo.caseActivity AS ca
                                          WHERE ca.exposureCaseId = c.exposureCaseId AND ca.isDeleted = 0
                                            AND ca.activityType IN ('contactAttempt', 'letter', 'education', 'referral',
                                                                    'medicalManagement'))) AS daysToFollowUp
      FROM dbo.exposureCase AS c
      JOIN dbo.labResult AS q ON q.labResultId = c.qualifyingLabResultId
     WHERE c.isDeleted = 0 AND c.openedDate BETWEEN @fromDate AND @toDate
       AND (@analyteId IS NULL OR c.analyteId = @analyteId))
SELECT DISTINCT CONVERT (CHAR (7), monthStart, 126) AS periodMonth,
       COUNT (*) OVER (PARTITION BY monthStart) AS caseCount,
       CAST (PERCENTILE_CONT (0.5) WITHIN GROUP (ORDER BY daysToReceipt) OVER (PARTITION BY monthStart) AS DECIMAL (9, 1))
           AS medianDaysToReceipt,
       CAST (PERCENTILE_CONT (0.5) WITHIN GROUP (ORDER BY daysToOpen) OVER (PARTITION BY monthStart) AS DECIMAL (9, 1))
           AS medianDaysToOpen,
       CAST (PERCENTILE_CONT (0.5) WITHIN GROUP (ORDER BY daysToFollowUp) OVER (PARTITION BY monthStart) AS DECIMAL (9, 1))
           AS medianDaysToFollowUp,
       SUM (CASE WHEN daysToFollowUp <= 7 THEN 1 ELSE 0 END) OVER (PARTITION BY monthStart) AS followedUpWithin7,
       SUM (CASE WHEN daysToFollowUp IS NULL THEN 1 ELSE 0 END) OVER (PARTITION BY monthStart) AS notFollowedUpYet
  FROM perCase
 ORDER BY periodMonth;
""",
    },
    {
        "schema": "dbo", "name": "uspReportInvestigationOutcomes", "kind": "read", "grants": WEB, "ticket": "T110",
        "permission": "case.view",
        "desc": "Investigations requested in the period by type and status, with the regulatory actions taken and the "
                "average days from request to completion.",
        "params": """@fromDate DATE
, @toDate   DATE""",
        "key": "CONCAT (N'fromDate=', @fromDate, N', toDate=', @toDate)",
        "validate": PERIOD,
        "body": """
SELECT i.investigationType, i.investigationStatus, COUNT (*) AS investigationCount,
       SUM (CASE WHEN i.regulatoryAction IS NOT NULL AND i.regulatoryAction <> 'none' THEN 1 ELSE 0 END) AS actionCount,
       CAST (AVG (CAST (DATEDIFF (DAY, i.requestedDate, i.completedDate) AS DECIMAL (9, 1))) AS DECIMAL (9, 1))
           AS averageDaysToComplete
  FROM dbo.investigation AS i
 WHERE i.isDeleted = 0 AND i.requestedDate BETWEEN @fromDate AND @toDate
 GROUP BY i.investigationType, i.investigationStatus
 ORDER BY i.investigationType, i.investigationStatus;
""",
    },
    {
        "schema": "dbo", "name": "uspReportReporterTimeliness", "kind": "read", "grants": WEB, "ticket": "T110",
        "permission": "result.view",
        "desc": "Reporter timeliness and completeness by performing laboratory: results received, median days from "
                "collection to receipt, the share received within five days, and how often key fields were missing "
                "(blood sample type, test method, ordering provider, patient address). Held rows count what could not "
                "load.",
        "params": """@fromDate  DATE
, @toDate    DATE
, @analyteId UNIQUEIDENTIFIER = NULL""",
        "key": "CONCAT (N'fromDate=', @fromDate, N', toDate=', @toDate, N', analyteId=', @analyteId)",
        "validate": PERIOD,
        "body": """
WITH r AS (
    SELECT r.labOrganizationId, DATEDIFF (DAY, r.collectedDate, CAST (r.receivedUtc AS DATE)) AS daysToReceipt,
           CASE WHEN s.usesSampleType = 1 AND COALESCE (r.sampleType, 'unknown') = 'unknown' THEN 1 ELSE 0 END AS noSampleType,
           CASE WHEN r.testMethod IS NULL OR r.testMethod = 'unknown' THEN 1 ELSE 0 END AS noMethod,
           CASE WHEN r.orderingProviderId IS NULL THEN 1 ELSE 0 END AS noProvider,
           CASE WHEN r.reportedLocationId IS NULL THEN 1 ELSE 0 END AS noAddress
      FROM dbo.labResult AS r
      JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId = r.analyteSpecimenId
     WHERE r.isDeleted = 0 AND r.resultRowStatus IN ('valid', 'duplicate')
       AND CAST (r.receivedUtc AS DATE) BETWEEN @fromDate AND @toDate
       AND (@analyteId IS NULL OR s.analyteId = @analyteId)),
agg AS (
    SELECT DISTINCT labOrganizationId,
           COUNT (*) OVER (PARTITION BY labOrganizationId) AS resultCount,
           CAST (PERCENTILE_CONT (0.5) WITHIN GROUP (ORDER BY daysToReceipt) OVER (PARTITION BY labOrganizationId)
                 AS DECIMAL (9, 1)) AS medianDaysToReceipt,
           SUM (CASE WHEN daysToReceipt <= 5 THEN 1 ELSE 0 END) OVER (PARTITION BY labOrganizationId) AS within5Days,
           SUM (noSampleType) OVER (PARTITION BY labOrganizationId) AS missingSampleType,
           SUM (noMethod) OVER (PARTITION BY labOrganizationId) AS missingMethod,
           SUM (noProvider) OVER (PARTITION BY labOrganizationId) AS missingProvider,
           SUM (noAddress) OVER (PARTITION BY labOrganizationId) AS missingAddress
      FROM r)
SELECT o.organizationName AS labName, a.resultCount, a.medianDaysToReceipt,
       CAST (100.0 * a.within5Days / a.resultCount AS DECIMAL (5, 1)) AS pctWithin5Days,
       CAST (100.0 * a.missingSampleType / a.resultCount AS DECIMAL (5, 1)) AS pctMissingSampleType,
       CAST (100.0 * a.missingMethod / a.resultCount AS DECIMAL (5, 1)) AS pctMissingMethod,
       CAST (100.0 * a.missingProvider / a.resultCount AS DECIMAL (5, 1)) AS pctMissingProvider,
       CAST (100.0 * a.missingAddress / a.resultCount AS DECIMAL (5, 1)) AS pctMissingAddress,
       (SELECT COUNT (*) FROM dbo.stagingRow AS sr
          JOIN dbo.intakeBatch AS b ON b.intakeBatchId = sr.intakeBatchId
         WHERE b.senderOrganizationId = a.labOrganizationId AND sr.rowStatus IN ('held', 'rejected') AND sr.isDeleted = 0
           AND CAST (b.receivedUtc AS DATE) BETWEEN @fromDate AND @toDate) AS heldOrRejectedRows
  FROM agg AS a
  JOIN dbo.organization AS o ON o.organizationId = a.labOrganizationId
 ORDER BY o.organizationName;
""",
    },
]
