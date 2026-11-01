"""Lab result procedures."""
from procs_common import NEWID, WEB, BOTH, CONCURRENCY, not_found

PROCS = [
    {
        "schema": "dbo", "name": "uspGetClassificationContext", "kind": "read", "grants": BOTH, "ticket": "T044",
        "permission": "result.view",
        "desc": "Everything case classification needs about a person for one analyte: DOB and sex, pregnancy episodes, the "
                "open case, and the earlier valid results. Population and classification are computed in Merit.Domain "
                "from these rows.",
        "params": """@personId  UNIQUEIDENTIFIER
, @analyteId UNIQUEIDENTIFIER""",
        "key": "CONCAT (N'personId=', @personId, N', analyteId=', @analyteId)",
        "notes": "Four result sets: person, pregnancy episodes, open case (zero or one row), prior results. Preliminary, "
                 "superseded, duplicate and entered-in-error results are excluded because they never count toward "
                 "classification.",
        "body": """
SELECT personId, dob, sex, deceasedDate, mergedIntoPersonId FROM dbo.person WHERE personId = @personId AND isDeleted = 0;

SELECT pregnancyEpisodeId, startDate, expectedEndDate, actualEndDate
  FROM dbo.pregnancyEpisode WHERE personId = @personId AND isDeleted = 0;

SELECT c.exposureCaseId, c.caseNumber, c.classification, c.computedClassification, c.overrideReason, c.population,
       c.caseStatus, c.highestLabResultId, h.valueCanonical AS highestValue, c.openedDate, c.owningTenantId
  FROM dbo.exposureCase AS c
  LEFT JOIN dbo.labResult AS h ON h.labResultId = c.highestLabResultId
 WHERE c.personId = @personId AND c.analyteId = @analyteId AND c.isDeleted = 0 AND c.caseStatus <> 'closed';

SELECT r.labResultId, r.analyteSpecimenId, s.specimen, r.collectedDate, r.sampleType, r.testMethod, r.speciation,
       r.operator, r.valueCanonical, r.population, r.isIndeterminate
  FROM dbo.labResult AS r
  JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId = r.analyteSpecimenId
 WHERE r.personId = @personId AND s.analyteId = @analyteId AND r.isDeleted = 0
   AND r.resultRowStatus = 'valid' AND r.resultStatus IN ('final', 'corrected')
 ORDER BY r.collectedDate;
""",
    },
    {
        "schema": "dbo", "name": "uspFindDuplicateResult", "kind": "read", "grants": BOTH, "ticket": "T044",
        "permission": "result.view",
        "desc": "Looks for an existing result that a new one duplicates or corrects: same person, analyte specimen, "
                "collected date and lab, matched on accession number, or on value when the accession is missing. Native "
                "HL7 results additionally match the observation identity in the reported snapshot; legacy rows use NULL.",
        "params": """@personId          UNIQUEIDENTIFIER
, @analyteSpecimenId UNIQUEIDENTIFIER
, @collectedDate     DATE
, @labOrganizationId UNIQUEIDENTIFIER
, @accessionNumber   NVARCHAR (60) = NULL
, @valueReported     DECIMAL (18, 6) = NULL
, @sourceObservationKey VARCHAR (64) = NULL""",
        "key": "CONCAT (N'personId=', @personId, N', analyteSpecimenId=', @analyteSpecimenId)",
        "body": """
SELECT TOP (1) r.labResultId, r.resultStatus, r.resultRowStatus, r.valueReported, r.accessionNumber
  FROM dbo.labResult AS r
 WHERE r.personId = @personId AND r.analyteSpecimenId = @analyteSpecimenId AND r.collectedDate = @collectedDate
   AND r.labOrganizationId = @labOrganizationId AND r.isDeleted = 0
   AND r.resultRowStatus IN ('valid', 'superseded')
   -- Native HL7 distinguishes observations under one accession. Legacy delimited/manual rows keep a NULL key.
   AND JSON_VALUE (r.reportedSnapshotJson, '$.hl7ObservationKey') IS NOT DISTINCT FROM @sourceObservationKey
   AND ((@accessionNumber IS NOT NULL AND r.accessionNumber = @accessionNumber)
     OR (@accessionNumber IS NULL AND r.accessionNumber IS NULL AND r.valueReported = @valueReported))
 ORDER BY CASE r.resultRowStatus WHEN 'valid' THEN 0 ELSE 1 END, r.receivedUtc DESC;
""",
    },
    {
        "schema": "dbo", "name": "uspSaveLabResult", "kind": "write", "grants": BOTH, "ticket": "T044",
        "permission": "result.edit",
        "desc": "Inserts a lab result with its normalized values. A corrected or cancelled result names the result it "
                "supersedes, which is marked superseded. Manual entry with no intake batch gets a manual batch of its "
                "own, so every result points to where it came from.",
        "params": """@labResultId          UNIQUEIDENTIFIER
, @personId             UNIQUEIDENTIFIER = NULL
, @analyteSpecimenId    UNIQUEIDENTIFIER
, @collectedDate        DATE
, @analyzedDate         DATE = NULL
, @reportedDate         DATE = NULL
, @receivedUtc          DATETIME2 (3)
, @resultStatus         VARCHAR (12)
, @supersedesLabResultId UNIQUEIDENTIFIER = NULL
, @valueReported        DECIMAL (18, 6) = NULL
, @valueText            NVARCHAR (60) = NULL
, @operator             VARCHAR (1) = '='
, @unitReported         NVARCHAR (20)
, @valueCanonical       DECIMAL (18, 6) = NULL
, @unitCanonical        NVARCHAR (20)
, @sampleType           VARCHAR (20) = NULL
, @testMethod           VARCHAR (30) = NULL
, @deviceModel          NVARCHAR (60) = NULL
, @deviceSerial         NVARCHAR (60) = NULL
, @kitLot               NVARCHAR (60) = NULL
, @speciation           VARCHAR (20) = NULL
, @creatinineValue      DECIMAL (18, 6) = NULL
, @valueCreatinineAdj   DECIMAL (18, 6) = NULL
, @reasonForTest        VARCHAR (30) = NULL
, @payerType            VARCHAR (20) = NULL
, @labOrganizationId    UNIQUEIDENTIFIER
, @orderingProviderId   UNIQUEIDENTIFIER = NULL
, @orderingFacilityId   UNIQUEIDENTIFIER = NULL
, @accessionNumber      NVARCHAR (60) = NULL
, @loincReported        VARCHAR (10) = NULL
, @reportedSnapshotJson NVARCHAR (MAX)
, @reportedLocationId   UNIQUEIDENTIFIER = NULL
, @ageAtCollectionMonths INT = NULL
, @population           VARCHAR (10) = 'unknown'
, @isIndeterminate      BIT = 0
, @resultRowStatus      VARCHAR (20) = 'valid'
, @intakeBatchId        UNIQUEIDENTIFIER = NULL""",
        "key": "CONCAT (N'labResultId=', @labResultId, N', personId=', @personId, N', intakeBatchId=', @intakeBatchId)",
        "notes": "Value, unit and snapshot are payload and are never written to @KeyParameters. Returns labResultId and "
                 "intakeBatchId.",
        "body": f"""
DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();
DECLARE @acting UNIQUEIDENTIFIER = auth.udfSessionActingTenantId ();
DECLARE @dob DATE = (SELECT dob FROM dbo.person WHERE personId = @personId);

IF @dob IS NOT NULL AND @collectedDate < @dob
BEGIN
    ;THROW 51422, N'The collection date is before the date of birth.', 1;
END;

IF @collectedDate > CAST (@receivedUtc AS DATE) OR (@reportedDate IS NOT NULL AND @collectedDate > @reportedDate)
BEGIN
    ;THROW 51422, N'The collection date must be on or before the reported and received dates.', 1;
END;

IF @analyzedDate IS NOT NULL AND (@analyzedDate < @collectedDate OR (@reportedDate IS NOT NULL AND @analyzedDate > @reportedDate))
BEGIN
    ;THROW 51422, N'The analyzed date must fall between the collected and reported dates.', 1;
END;

IF @intakeBatchId IS NULL
BEGIN
    SET @intakeBatchId = {NEWID};
    INSERT dbo.intakeBatch (intakeBatchId, rootTenantId, owningTenantId, senderOrganizationId, sourceChannel, fileName,
                            receivedUtc, batchStatus, receivedCount, loadedCount, submittedByUserId)
    VALUES (@intakeBatchId, @root, @acting, @labOrganizationId, 'manual', N'Manual entry', @receivedUtc, 'loaded', 1, 1,
            auth.udfSessionUserId ());
END;

IF @supersedesLabResultId IS NOT NULL
BEGIN
    UPDATE dbo.labResult SET resultRowStatus = 'superseded'
     WHERE labResultId = @supersedesLabResultId AND resultRowStatus = 'valid' AND isDeleted = 0;
END;

INSERT dbo.labResult (labResultId, rootTenantId, owningTenantId, personId, analyteSpecimenId, collectedDate, analyzedDate,
                      reportedDate, receivedUtc, resultStatus, supersedesLabResultId, valueReported, valueText, operator,
                      unitReported, valueCanonical, unitCanonical, sampleType, testMethod, deviceModel, deviceSerial, kitLot,
                      speciation, creatinineValue, valueCreatinineAdj, reasonForTest, payerType, labOrganizationId,
                      orderingProviderId, orderingFacilityId, accessionNumber, loincReported, reportedSnapshotJson,
                      reportedLocationId, ageAtCollectionMonths, population, isIndeterminate, resultRowStatus,
                      intakeBatchId)
VALUES (@labResultId, @root, @root, @personId, @analyteSpecimenId, @collectedDate, @analyzedDate, @reportedDate,
        @receivedUtc, @resultStatus, @supersedesLabResultId, @valueReported, @valueText, @operator, @unitReported,
        @valueCanonical, @unitCanonical, @sampleType, @testMethod, @deviceModel, @deviceSerial, @kitLot, @speciation,
        @creatinineValue, @valueCreatinineAdj, @reasonForTest, @payerType, @labOrganizationId, @orderingProviderId,
        @orderingFacilityId, @accessionNumber, @loincReported, @reportedSnapshotJson, @reportedLocationId,
        @ageAtCollectionMonths, @population, @isIndeterminate, @resultRowStatus, @intakeBatchId);

SELECT @labResultId AS labResultId, @intakeBatchId AS intakeBatchId;

SET @Comments = N'Lab result saved.';
""",
    },
    {
        "schema": "dbo", "name": "uspUpdateLabResultDerived", "kind": "write", "grants": BOTH, "ticket": "T044",
        "permission": "result.edit",
        "desc": "Stores the values computed for a result after matching or a rerun of classification: the person it "
                "belongs to, age at collection, population and whether it is indeterminate. The as-reported fields and "
                "snapshot are never touched.",
        "params": """@labResultId           UNIQUEIDENTIFIER
, @personId              UNIQUEIDENTIFIER = NULL
, @ageAtCollectionMonths INT = NULL
, @population            VARCHAR (10)
, @isIndeterminate       BIT""",
        "key": "CONCAT (N'labResultId=', @labResultId)",
        "body": """
UPDATE dbo.labResult
   SET personId = COALESCE (@personId, personId), ageAtCollectionMonths = @ageAtCollectionMonths,
       population = @population, isIndeterminate = @isIndeterminate
 WHERE labResultId = @labResultId AND isDeleted = 0
   AND (personId IS DISTINCT FROM COALESCE (@personId, personId)
        OR ageAtCollectionMonths IS DISTINCT FROM @ageAtCollectionMonths
        OR population <> @population OR isIndeterminate <> @isIndeterminate);

SET @Comments = CONCAT (@@ROWCOUNT, N' result(s) updated.');
""",
    },
    {
        "schema": "dbo", "name": "uspSetLabResultRowStatus", "kind": "write", "grants": BOTH, "ticket": "T044",
        "permission": "result.edit",
        "desc": "Marks a result entered in error, duplicate or rejected. The row is kept; classification is rerun by the "
                "application for any case it qualified or led.",
        "params": """@labResultId     UNIQUEIDENTIFIER
, @resultRowStatus VARCHAR (20)
, @rowVersion      BINARY (8)""",
        "key": "CONCAT (N'labResultId=', @labResultId, N', resultRowStatus=', @resultRowStatus)",
        "body": f"""
UPDATE dbo.labResult SET resultRowStatus = @resultRowStatus
 WHERE labResultId = @labResultId AND isDeleted = 0 AND rowVersion = @rowVersion;
{CONCURRENCY}

SET @Comments = N'Result status changed.';
""",
    },
    {
        "schema": "dbo", "name": "uspGetLabResult", "kind": "read", "grants": BOTH, "ticket": "T044",
        "permission": "result.view",
        "desc": "One result with its analyte, lab, provider and the as-reported snapshot.",
        "params": "@labResultId UNIQUEIDENTIFIER",
        "key": "CONCAT (N'labResultId=', @labResultId)",
        "validate": not_found("@labResultId", "dbo.labResult", "labResultId", "The result"),
        "body": """
SELECT r.*, a.analyteId, a.code AS analyteCode, a.displayName AS analyteName, s.specimen,
       lab.organizationName AS labName, fac.organizationName AS orderingFacilityName,
       CONCAT (pv.firstName, N' ', pv.lastName) AS orderingProviderName, ib.sourceChannel, ib.fileName
  FROM dbo.labResult AS r
  JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId = r.analyteSpecimenId
  JOIN config.analyte AS a ON a.analyteId = s.analyteId
  LEFT JOIN dbo.organization AS lab ON lab.organizationId = r.labOrganizationId
  LEFT JOIN dbo.organization AS fac ON fac.organizationId = r.orderingFacilityId
  LEFT JOIN dbo.provider AS pv ON pv.providerId = r.orderingProviderId
  LEFT JOIN dbo.intakeBatch AS ib ON ib.intakeBatchId = r.intakeBatchId
 WHERE r.labResultId = @labResultId;
""",
    },
    {
        "schema": "dbo", "name": "uspListLabResults", "kind": "read", "grants": BOTH, "ticket": "T106",
        "permission": "result.view",
        "desc": "One page of the results line list, filtered and sorted on the server, with the total row count.",
        "params": """@analyteId         UNIQUEIDENTIFIER = NULL
, @collectedFrom     DATE = NULL
, @collectedTo       DATE = NULL
, @population        VARCHAR (10) = NULL
, @minValue          DECIMAL (18, 6) = NULL
, @labOrganizationId UNIQUEIDENTIFIER = NULL
, @unmatchedOnly     BIT = 0
, @sortBy            VARCHAR (30) = 'collectedDate'
, @sortDescending    BIT = 1
, @pageNumber        INT = 1
, @pageSize          INT = 50""",
        "key": "CONCAT (N'analyteId=', @analyteId, N', pageNumber=', @pageNumber, N', pageSize=', @pageSize, N', sortBy=', @sortBy)",
        "validate": """IF @pageNumber < 1 OR @pageSize NOT BETWEEN 1 AND 500
BEGIN
    ;THROW 50000, N'@pageNumber must be 1 or more and @pageSize between 1 and 500.', 1;
END;

IF @sortBy NOT IN ('collectedDate', 'valueCanonical', 'lastName', 'analyteCode', 'receivedUtc')
BEGIN
    ;THROW 50000, N'@sortBy must be one of collectedDate, valueCanonical, lastName, analyteCode, receivedUtc.', 1;
END;""",
        "body": """
SELECT r.labResultId, r.personId, p.lastName, p.firstName, p.dob, a.code AS analyteCode, s.specimen, r.collectedDate,
       r.receivedUtc, r.operator, r.valueCanonical, r.unitCanonical, r.sampleType, r.testMethod, r.population,
       r.resultStatus, r.resultRowStatus, r.isIndeterminate, lab.organizationName AS labName,
       COUNT (*) OVER () AS totalCount
  FROM dbo.labResult AS r
  JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId = r.analyteSpecimenId
  JOIN config.analyte AS a ON a.analyteId = s.analyteId
  LEFT JOIN dbo.person AS p ON p.personId = r.personId
  LEFT JOIN dbo.organization AS lab ON lab.organizationId = r.labOrganizationId
 WHERE r.isDeleted = 0
   AND (@analyteId IS NULL OR a.analyteId = @analyteId)
   AND (@collectedFrom IS NULL OR r.collectedDate >= @collectedFrom)
   AND (@collectedTo IS NULL OR r.collectedDate <= @collectedTo)
   AND (@population IS NULL OR r.population = @population)
   AND (@minValue IS NULL OR r.valueCanonical >= @minValue)
   AND (@labOrganizationId IS NULL OR r.labOrganizationId = @labOrganizationId)
   AND (@unmatchedOnly = 0 OR r.personId IS NULL)
 ORDER BY CASE WHEN @sortBy = 'collectedDate'  AND @sortDescending = 0 THEN r.collectedDate END ASC,
          CASE WHEN @sortBy = 'collectedDate'  AND @sortDescending = 1 THEN r.collectedDate END DESC,
          CASE WHEN @sortBy = 'valueCanonical' AND @sortDescending = 0 THEN r.valueCanonical END ASC,
          CASE WHEN @sortBy = 'valueCanonical' AND @sortDescending = 1 THEN r.valueCanonical END DESC,
          CASE WHEN @sortBy = 'lastName'       AND @sortDescending = 0 THEN p.lastName END ASC,
          CASE WHEN @sortBy = 'lastName'       AND @sortDescending = 1 THEN p.lastName END DESC,
          CASE WHEN @sortBy = 'analyteCode'    AND @sortDescending = 0 THEN a.code END ASC,
          CASE WHEN @sortBy = 'analyteCode'    AND @sortDescending = 1 THEN a.code END DESC,
          CASE WHEN @sortBy = 'receivedUtc'    AND @sortDescending = 0 THEN r.receivedUtc END ASC,
          CASE WHEN @sortBy = 'receivedUtc'    AND @sortDescending = 1 THEN r.receivedUtc END DESC,
          r.labResultId
OFFSET (@pageNumber - 1) * @pageSize ROWS
 FETCH NEXT @pageSize ROWS ONLY;
""",
    },
]
