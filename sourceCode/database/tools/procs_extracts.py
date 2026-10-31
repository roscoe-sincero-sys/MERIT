"""Federal extract procedures: CBLS (children, quarterly) and ABLES (adults, annual)."""
from procs_common import NEWID, WEB, WORKER, BOTH, CONCURRENCY, JOB_SCOPE, JOB_SCOPE_COLUMNS, JOB_SCOPE_VALUES

# Reporting county: the county of the primary residence on the collection date; when none is known, the result's
# as-reported address (address rule 6).
REPORTING_COUNTY = """OUTER APPLY (SELECT TOP (1) l.countyFips, l.zip5, l.state, l.censusTract, l.yearBuilt, l.country
                 FROM dbo.personLocation AS pl
                 JOIN dbo.location AS l ON l.locationId = pl.locationId
                WHERE pl.personId = r.personId AND pl.role = 'residence' AND pl.isPrimary = 1 AND pl.isDeleted = 0
                  AND pl.startDate <= r.collectedDate AND (pl.endDate IS NULL OR pl.endDate >= r.collectedDate)
                ORDER BY pl.startDate DESC) AS res
  LEFT JOIN dbo.location AS rep ON rep.locationId = r.reportedLocationId"""

PROCS = [
    {
        "schema": "dbo", "name": "uspResolveExtractFindingPersons", "kind": "read", "grants": WEB, "ticket": "T084",
        "permission": "extract.run", "desc": "Resolves finding surrogate ids to visible persons in the extract root, only with person.view. Ambiguous surrogate ids have no link.",
        "params": "@extractRunId UNIQUEIDENTIFIER, @recordIdsJson NVARCHAR (MAX)",
        "key": "CONCAT (N'extractRunId=', @extractRunId)",
        "body": """
SELECT ids.recordId, TRY_CONVERT (UNIQUEIDENTIFIER, MIN (CONVERT (CHAR (36), p.personId))) AS personId
  FROM OPENJSON (@recordIdsJson) WITH (recordId VARCHAR (15) '$') AS ids
  JOIN dbo.person AS p ON UPPER (LEFT (REPLACE (CONVERT (CHAR (36), p.personId), '-', ''), 15)) = ids.recordId
  JOIN dbo.extractRun AS run ON run.extractRunId = @extractRunId AND run.rootTenantId = p.rootTenantId AND run.isDeleted = 0
 WHERE p.isDeleted = 0 AND auth.udfHasPermission (N'person.view') = 1
 GROUP BY ids.recordId
HAVING COUNT (DISTINCT p.personId) = 1;
""",
    },
    {
        "schema": "dbo", "name": "uspCreateExtractRun", "kind": "write", "grants": WEB, "ticket": "T084",
        "permission": "extract.run",
        "desc": "Requests a federal extract for a period with the active layout, and queues the job that builds and "
                "validates it. The federal extracts screen polls the run.",
        "params": """@extractRunId UNIQUEIDENTIFIER
, @extractType  VARCHAR (10)
, @periodStart  DATE
, @periodEnd    DATE
, @jobId        UNIQUEIDENTIFIER""",
        "key": "CONCAT (N'extractRunId=', @extractRunId, N', extractType=', @extractType, N', jobId=', @jobId)",
        "body": """
DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();
DECLARE @user UNIQUEIDENTIFIER = auth.udfSessionUserId ();
DECLARE @layout UNIQUEIDENTIFIER = (SELECT TOP (1) extractLayoutId FROM config.extractLayout
                                     WHERE extractType = @extractType AND isActive = 1 AND isDeleted = 0
                                     ORDER BY effectiveFrom DESC);

IF @layout IS NULL
BEGIN
    ;THROW 51422, N'No active layout is configured for this extract.', 1;
END;

IF @periodEnd < @periodStart
BEGIN
    ;THROW 51422, N'The period end is before its start.', 1;
END;

""" + JOB_SCOPE + """

INSERT dbo.jobQueue (jobId, rootTenantId, owningTenantId, jobType, payloadJson, payloadVersion, availableUtc,
                     requestedByUserId, """ + JOB_SCOPE_COLUMNS + """)
VALUES (@jobId, @root, @root, 'extractRun', CONCAT (N'{"extractRunId":"', @extractRunId, N'"}'), 1, SYSUTCDATETIME (),
        @user, """ + JOB_SCOPE_VALUES + """);

INSERT dbo.extractRun (extractRunId, rootTenantId, owningTenantId, extractType, periodStart, periodEnd, extractLayoutId,
                       runStatus, jobId, requestedByUserId)
VALUES (@extractRunId, @root, @root, @extractType, @periodStart, @periodEnd, @layout, 'queued', @jobId, @user);

SET @Comments = N'Extract run queued.';
""",
    },
    {
        "schema": "dbo", "name": "uspListExtractRuns", "kind": "read", "grants": WEB, "ticket": "T084",
        "permission": "extract.run",
        "desc": "Recent extract runs with their status, counts and validation summary, for the status cards.",
        "params": "@maxRows INT = 20",
        "key": "CONCAT (N'maxRows=', @maxRows)",
        "body": """
SELECT TOP (@maxRows) x.extractRunId, x.extractType, x.periodStart, x.periodEnd, x.runStatus, x.recordCount, x.errorCount,
       x.fileName, x.createdDateUtc, l.layoutVersion, u.displayName AS requestedBy, j.jobStatus, j.lastError
  FROM dbo.extractRun AS x
  JOIN config.extractLayout AS l ON l.extractLayoutId = x.extractLayoutId
  LEFT JOIN auth.appUser AS u ON u.appUserId = x.requestedByUserId
  LEFT JOIN dbo.jobQueue AS j ON j.jobId = x.jobId
 WHERE x.isDeleted = 0
 ORDER BY x.createdDateUtc DESC;
""",
    },
    {
        "schema": "dbo", "name": "uspGetExtractRun", "kind": "read", "grants": BOTH, "ticket": "T084",
        "permission": "extract.run",
        "desc": "One extract run; with @includeFile, the generated file for download.",
        "params": """@extractRunId UNIQUEIDENTIFIER
, @includeFile  BIT = 0""",
        "key": "CONCAT (N'extractRunId=', @extractRunId, N', includeFile=', @includeFile)",
        "body": """
SELECT x.extractRunId, x.extractType, x.periodStart, x.periodEnd, x.extractLayoutId, x.runStatus, x.recordCount,
       x.errorCount, x.validationReportJson, x.fileName, IIF (@includeFile = 1, x.fileContent, NULL) AS fileContent,
       x.requestedByUserId, x.jobId
  FROM dbo.extractRun AS x
 WHERE x.extractRunId = @extractRunId AND x.isDeleted = 0;
""",
    },
    {
        "schema": "dbo", "name": "uspUpdateExtractRun", "kind": "write", "grants": WORKER, "ticket": "T122",
        "permission": "extract.run",
        "desc": "Records the progress and outcome of an extract run: running, ready (with the file and validation "
                "report) or failed.",
        "params": """@extractRunId         UNIQUEIDENTIFIER
, @runStatus            VARCHAR (20)
, @recordCount          INT = NULL
, @errorCount           INT = NULL
, @validationReportJson NVARCHAR (MAX) = NULL
, @fileName             NVARCHAR (200) = NULL
, @fileContent          NVARCHAR (MAX) = NULL""",
        "key": "CONCAT (N'extractRunId=', @extractRunId, N', runStatus=', @runStatus, N', recordCount=', @recordCount)",
        "body": """
UPDATE dbo.extractRun
   SET runStatus = @runStatus, recordCount = COALESCE (@recordCount, recordCount),
       errorCount = COALESCE (@errorCount, errorCount),
       validationReportJson = COALESCE (@validationReportJson, validationReportJson),
       fileName = COALESCE (@fileName, fileName), fileContent = COALESCE (@fileContent, fileContent)
 WHERE extractRunId = @extractRunId AND isDeleted = 0;

SET @Comments = CONCAT (N'Extract run ', @runStatus, N'.');
""",
    },
    {
        "schema": "dbo", "name": "uspGetCblsExtractRows", "kind": "read", "grants": WORKER, "ticket": "T122",
        "permission": "extract.run",
        "desc": "Source rows for the CDC CBLS quarterly extract: every valid final blood lead result for a child collected "
                "in the period, with the child's surrogate id, demographics and reporting county. No names, street "
                "addresses, phone numbers or record numbers are returned.",
        "params": """@periodStart DATE
, @periodEnd   DATE""",
        "key": "CONCAT (N'periodStart=', @periodStart, N', periodEnd=', @periodEnd)",
        "body": f"""
SELECT r.labResultId, r.personId, p.dob, p.sex, p.ethnicity,
       (SELECT STRING_AGG (COALESCE (lv.cblsCode, pr.raceCode), ',') FROM dbo.personRace AS pr
          LEFT JOIN config.lookupValue AS lv ON lv.lookupType = 'race' AND lv.code = pr.raceCode
         WHERE pr.personId = p.personId AND pr.isDeleted = 0) AS raceCodes,
       r.collectedDate, r.sampleType, r.testMethod, r.operator, r.valueCanonical, r.reasonForTest, r.payerType,
       r.ageAtCollectionMonths, COALESCE (res.countyFips, rep.countyFips) AS countyFips,
       COALESCE (res.zip5, rep.zip5) AS zip5, COALESCE (res.censusTract, rep.censusTract) AS censusTract,
       res.yearBuilt, lab.cliaNumber AS labClia, s.specimen
  FROM dbo.labResult AS r
  JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId = r.analyteSpecimenId AND s.specimen = 'wholeBlood'
  JOIN config.analyte AS a ON a.analyteId = s.analyteId AND a.code = 'PB'
  JOIN dbo.person AS p ON p.personId = r.personId
  LEFT JOIN dbo.organization AS lab ON lab.organizationId = r.labOrganizationId
  {REPORTING_COUNTY}
 WHERE r.isDeleted = 0 AND r.resultRowStatus = 'valid' AND r.resultStatus IN ('final', 'corrected')
   AND r.population IN ('child')
   AND r.collectedDate BETWEEN @periodStart AND @periodEnd
 ORDER BY r.personId, r.collectedDate;
""",
    },
    {
        "schema": "dbo", "name": "uspGetAblesExtractRows", "kind": "read", "grants": WORKER, "ticket": "T122",
        "permission": "extract.run",
        "desc": "Source rows for the NIOSH ABLES annual extract: each adult's highest valid final blood lead result in the "
                "year, with demographics, reporting county, the exposure category and employment of their case, the "
                "exposure source, and whether they had a result of 10 ug/dL or more the year before (registry status).",
        "params": "@reportYear INT",
        "key": "CONCAT (N'reportYear=', @reportYear)",
        "body": f"""
DECLARE @from DATE = DATEFROMPARTS (@reportYear, 1, 1), @to DATE = DATEFROMPARTS (@reportYear, 12, 31);

;WITH ranked AS
(
    SELECT r.*, ROW_NUMBER () OVER (PARTITION BY r.personId ORDER BY r.valueCanonical DESC, r.collectedDate) AS rn
      FROM dbo.labResult AS r
      JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId = r.analyteSpecimenId AND s.specimen = 'wholeBlood'
      JOIN config.analyte AS a ON a.analyteId = s.analyteId AND a.code = 'PB'
     WHERE r.isDeleted = 0 AND r.resultRowStatus = 'valid' AND r.resultStatus IN ('final', 'corrected')
       AND r.personId IS NOT NULL AND r.collectedDate BETWEEN @from AND @to
       AND DATEDIFF (YEAR, (SELECT dob FROM dbo.person WHERE personId = r.personId), r.collectedDate) >= 16
)
SELECT r.labResultId, r.personId, p.dob, p.sex, p.ethnicity,
       (SELECT STRING_AGG (COALESCE (lv.ablesCode, pr.raceCode), ',') FROM dbo.personRace AS pr
          LEFT JOIN config.lookupValue AS lv ON lv.lookupType = 'race' AND lv.code = pr.raceCode
         WHERE pr.personId = p.personId AND pr.isDeleted = 0) AS raceCodes,
       r.collectedDate, r.valueCanonical, r.ageAtCollectionMonths,
       COALESCE (res.state, rep.state) AS stateRes, COALESCE (res.countyFips, rep.countyFips) AS countyRes,
       COALESCE (res.country, rep.country) AS countryRes,
       c.exposureCategory,
       COALESCE (e.naicsOverride, o.naicsCode) AS naics, e.occupationCode,
       wl.state AS stateExp, wl.countyFips AS countyExp,
       src.sourceDescription AS processText, srcType.ablesCode AS processCode,
       CAST (CASE WHEN EXISTS (SELECT 1 FROM dbo.labResult AS pr2
                                 JOIN config.analyteSpecimen AS s2 ON s2.analyteSpecimenId = pr2.analyteSpecimenId
                                 JOIN config.analyte AS a2 ON a2.analyteId = s2.analyteId AND a2.code = 'PB'
                                WHERE pr2.personId = r.personId AND pr2.isDeleted = 0 AND pr2.resultRowStatus = 'valid'
                                  AND YEAR (pr2.collectedDate) = @reportYear - 1 AND pr2.valueCanonical >= 10)
                  THEN 1 ELSE 0 END AS BIT) AS priorYearElevated,
       CAST (CASE WHEN EXISTS (SELECT 1 FROM dbo.labResult AS pr3
                                WHERE pr3.personId = r.personId AND pr3.isDeleted = 0 AND YEAR (pr3.collectedDate) < @reportYear)
                  THEN 1 ELSE 0 END AS BIT) AS inRegistryBefore
  FROM ranked AS r
  JOIN dbo.person AS p ON p.personId = r.personId
  {REPORTING_COUNTY}
  OUTER APPLY (SELECT TOP (1) c.exposureCaseId, c.exposureCategory FROM dbo.exposureCase AS c
                JOIN config.analyte AS a ON a.analyteId = c.analyteId AND a.code = 'PB'
               WHERE c.personId = r.personId AND c.isDeleted = 0 ORDER BY c.openedDate DESC) AS c
  OUTER APPLY (SELECT TOP (1) e.* FROM dbo.employment AS e
                LEFT JOIN dbo.investigationCase AS ic ON ic.employmentId = e.employmentId AND ic.exposureCaseId = c.exposureCaseId
               WHERE e.personId = r.personId AND e.isDeleted = 0
                 AND (e.startDate IS NULL OR e.startDate <= r.collectedDate)
                 AND (e.endDate IS NULL OR e.endDate >= DATEADD (DAY, -90, r.collectedDate))
               ORDER BY CASE WHEN ic.investigationCaseId IS NOT NULL THEN 0 ELSE 1 END,
                        CASE WHEN e.metalTasks IS NOT NULL THEN 0 ELSE 1 END, e.startDate DESC) AS e
  LEFT JOIN dbo.organization AS o ON o.organizationId = e.organizationId
  LEFT JOIN dbo.location AS wl ON wl.locationId = e.worksiteLocationId
  OUTER APPLY (SELECT TOP (1) es.sourceDescription, es.sourceType FROM dbo.exposureSource AS es
                WHERE es.personId = r.personId AND es.isDeleted = 0 ORDER BY es.isConfirmed DESC, es.startDate DESC) AS src
  LEFT JOIN config.lookupValue AS srcType ON srcType.lookupType = 'exposureSourceType' AND srcType.code = src.sourceType
 WHERE r.rn = 1
 ORDER BY r.personId;
""",
    },
]


PROCS += [
    {
        "schema": "dbo", "name": "uspMarkExtractSubmitted", "kind": "write", "grants": WEB, "ticket": "T164",
        "permission": "extract.submit",
        "desc": "Records that a ready extract file was sent to CDC or NIOSH, so the next period's work starts from a "
                "clear list. Only a ready run can be marked; the file itself is unchanged.",
        "params": "@extractRunId UNIQUEIDENTIFIER",
        "key": "CONCAT (N'extractRunId=', @extractRunId)",
        "validate": """IF NOT EXISTS (SELECT 1 FROM dbo.extractRun WHERE extractRunId = @extractRunId AND isDeleted = 0)
BEGIN
    ;THROW 51404, N'The extract run was not found.', 1;
END;
IF NOT EXISTS (SELECT 1 FROM dbo.extractRun WHERE extractRunId = @extractRunId AND isDeleted = 0 AND runStatus = 'ready')
BEGIN
    ;THROW 51422, N'Only a ready extract can be marked submitted.', 1;
END;""",
        "body": """
UPDATE dbo.extractRun SET runStatus = 'submitted' WHERE extractRunId = @extractRunId AND isDeleted = 0;

SET @Comments = N'Extract marked submitted.';
""",
    },
]
