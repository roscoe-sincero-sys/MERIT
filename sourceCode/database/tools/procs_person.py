"""Person registry procedures: search, person page sections, and saves of the person and its child records."""
from procs_common import NEWID, WEB, BOTH, CONCURRENCY, not_found

PERSON_VISIBLE = not_found("@personId", "dbo.person", "personId", "The person")

PROCS = [
    {
        "schema": "dbo", "name": "uspSearch", "kind": "write", "grants": WEB, "ticket": "T075",
        "permission": "person.view",
        "desc": "Global search by name (with aliases), date of birth, address, organization, accession number or case "
                "number, within the caller's access scope. Writes the search text to the purgeable logs.searchLog and, "
                "on an exact name and DOB match outside scope, reports only that a record exists elsewhere.",
        "params": """@searchText      NVARCHAR (200)
, @nameKey1        NVARCHAR (60) = NULL
, @nameKey2        NVARCHAR (60) = NULL
, @dob             DATE = NULL
, @addressKey      NVARCHAR (300) = NULL
, @maxRows         INT = 50
, @namePairsJson   NVARCHAR (MAX) = NULL""",
        "key": "CONCAT (N'maxRows=', @maxRows, N', hasDob=', IIF (@dob IS NULL, 0, 1))",
        "notes": "The application normalizes the text into name keys (upper case, letters only) and an address key; the "
                 "raw text is used as-is for accession and case numbers. Search text never reaches @KeyParameters or the "
                 "audit ledger. Result sets: persons, cases, organizations, then one row with existsElsewhere.",
        "body": f"""
DECLARE @found INT = 0, @elsewhere BIT = 0;
DECLARE @term NVARCHAR (200) = LTRIM (RTRIM (@searchText));
DECLARE @persons TABLE (personId UNIQUEIDENTIFIER PRIMARY KEY, matchedOn NVARCHAR (40));
DECLARE @pairs TABLE (key1 NVARCHAR (60), key2 NVARCHAR (60));
INSERT @pairs VALUES (@nameKey1, @nameKey2);
IF ISJSON (@namePairsJson) = 1
    INSERT @pairs SELECT key1, key2 FROM OPENJSON (@namePairsJson) WITH (key1 NVARCHAR (60), key2 NVARCHAR (60));

IF @nameKey1 IS NOT NULL
BEGIN
    -- A person found by name and by an alias is one row; 'name' wins (MAX), since it is the stronger reason.
    INSERT @persons (personId, matchedOn)
    SELECT TOP (@maxRows) x.personId, MAX (x.matchedOn)
      FROM (SELECT p.personId, N'name' AS matchedOn
              FROM dbo.person AS p
             WHERE p.isDeleted = 0 AND p.mergedIntoPersonId IS NULL
               AND EXISTS (SELECT 1 FROM @pairs AS nk
                            WHERE (p.lastNameKey LIKE nk.key1 + N'%' AND (nk.key2 IS NULL OR p.firstNameKey LIKE nk.key2 + N'%'))
                               OR (nk.key2 IS NOT NULL AND p.lastNameKey LIKE nk.key2 + N'%' AND p.firstNameKey LIKE nk.key1 + N'%'))
               AND (@dob IS NULL OR p.dob = @dob)
            UNION
            SELECT a.personId, N'alias'
              FROM dbo.personAlias AS a
              JOIN dbo.person AS p ON p.personId = a.personId AND p.isDeleted = 0 AND p.mergedIntoPersonId IS NULL
             WHERE a.isDeleted = 0
               AND EXISTS (SELECT 1 FROM @pairs AS nk
                            WHERE (a.lastNameKey LIKE nk.key1 + N'%' AND (nk.key2 IS NULL OR a.firstNameKey LIKE nk.key2 + N'%'))
                               OR (nk.key2 IS NOT NULL AND a.lastNameKey LIKE nk.key2 + N'%' AND a.firstNameKey LIKE nk.key1 + N'%'))
               AND (@dob IS NULL OR p.dob = @dob)) AS x
     GROUP BY x.personId;
END
ELSE IF @dob IS NOT NULL
BEGIN
    INSERT @persons (personId, matchedOn)
    SELECT TOP (@maxRows) p.personId, N'dob' FROM dbo.person AS p
     WHERE p.isDeleted = 0 AND p.dob = @dob AND p.mergedIntoPersonId IS NULL;
END;

INSERT @persons (personId, matchedOn)
SELECT DISTINCT r.personId, N'accession'
  FROM dbo.labResult AS r
 WHERE r.isDeleted = 0 AND r.personId IS NOT NULL AND r.accessionNumber = @term
   AND NOT EXISTS (SELECT 1 FROM @persons AS x WHERE x.personId = r.personId);

IF @addressKey IS NOT NULL AND LEN (@addressKey) >= 4
BEGIN
    INSERT @persons (personId, matchedOn)
    SELECT DISTINCT TOP (@maxRows) pl.personId, N'address'
      FROM dbo.location AS l
      JOIN dbo.personLocation AS pl ON pl.locationId = l.locationId AND pl.isDeleted = 0
     WHERE l.isDeleted = 0 AND l.addressKey LIKE @addressKey + N'%'
       AND NOT EXISTS (SELECT 1 FROM @persons AS x WHERE x.personId = pl.personId);
END;

SELECT TOP (@maxRows) p.personId, p.lastName, p.firstName, p.middleName, p.dob, p.sex, x.matchedOn,
       cur.countyName AS currentCounty,
       (SELECT c.exposureCaseId, c.caseNumber, a.code AS analyteCode, a.displayName AS analyteName, c.classification,
               c.caseStatus, c.population
          FROM dbo.exposureCase AS c JOIN config.analyte AS a ON a.analyteId = c.analyteId
         WHERE c.personId = p.personId AND c.isDeleted = 0 AND c.caseStatus <> 'closed'
         ORDER BY c.openedDate
           FOR JSON PATH) AS openCasesJson
  FROM @persons AS x
  JOIN dbo.person AS p ON p.personId = x.personId
  OUTER APPLY (SELECT TOP (1) sj.subJurisdictionName AS countyName
                 FROM dbo.personLocation AS pl
                 JOIN dbo.location AS l ON l.locationId = pl.locationId
                 LEFT JOIN config.subJurisdiction AS sj ON sj.countyFips = l.countyFips AND sj.rootTenantId = p.rootTenantId
                WHERE pl.personId = p.personId AND pl.isDeleted = 0 AND pl.role = 'residence' AND pl.isPrimary = 1
                  AND pl.startDate <= auth.udfSessionToday ()
                  AND (pl.endDate IS NULL OR pl.endDate >= auth.udfSessionToday ())) AS cur
 ORDER BY p.lastName, p.firstName, p.dob;

SET @found = @@ROWCOUNT;

SELECT c.exposureCaseId, c.caseNumber, c.personId, p.lastName, p.firstName, a.code AS analyteCode, c.classification,
       c.caseStatus, c.openedDate
  FROM dbo.exposureCase AS c
  JOIN dbo.person AS p ON p.personId = c.personId
  JOIN config.analyte AS a ON a.analyteId = c.analyteId
 WHERE c.isDeleted = 0 AND c.caseNumber = @term;

SET @found = @found + @@ROWCOUNT;

SELECT TOP (@maxRows) o.organizationId, o.organizationName, o.naicsCode, o.cliaNumber,
       (SELECT STRING_AGG (ot.organizationTypeCode, ', ') FROM dbo.organizationType AS ot
         WHERE ot.organizationId = o.organizationId AND ot.isDeleted = 0) AS organizationTypes
  FROM dbo.organization AS o
 WHERE o.isDeleted = 0 AND @nameKey1 IS NOT NULL
   AND o.organizationNameKey LIKE @nameKey1 + N'%' + COALESCE (@nameKey2 + N'%', N'')
 ORDER BY o.organizationName;

SET @found = @found + @@ROWCOUNT;

-- Outside scope: only on an exact name and DOB match, through the match-key table, and every hit is audited.
IF NOT EXISTS (SELECT 1 FROM OPENJSON (COALESCE (@namePairsJson, N'[]'))) AND @found = 0 AND @nameKey1 IS NOT NULL AND @nameKey2 IS NOT NULL AND @dob IS NOT NULL
   AND EXISTS (SELECT 1 FROM dbo.personMatchKey AS mk
                WHERE mk.rootTenantId = auth.udfSessionRootTenantId () AND mk.isDeleted = 0 AND mk.dob = @dob
                  AND ((mk.lastNameKey = @nameKey1 AND mk.firstNameKey = @nameKey2)
                    OR (mk.lastNameKey = @nameKey2 AND mk.firstNameKey = @nameKey1)))
BEGIN
    SET @elsewhere = 1;
    INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId, recordCount)
    VALUES (auth.udfSessionUserId (), auth.udfSessionRootTenantId (), 'search', 'personOutsideScope', NULL, 1);
END;

SELECT @elsewhere AS existsElsewhere;

INSERT logs.searchLog (searchLogId, appUserId, rootTenantId, searchText, resultCount, searchedUtc)
VALUES ({NEWID}, COALESCE (auth.udfSessionUserId (), '00000000-0000-0000-0000-000000000000'),
        COALESCE (auth.udfSessionRootTenantId (), '00000000-0000-0000-0000-000000000000'), LEFT (@searchText, 400),
        @found, SYSUTCDATETIME ());

SET @Comments = CONCAT (@found, N' match(es).');
""",
    },
    {
        "schema": "dbo", "name": "uspGetPerson", "kind": "read", "grants": BOTH, "ticket": "T076",
        "permission": "person.view",
        "desc": "The person page header: identity, current primary residence county, open case chips per analyte, aliases, "
                "race values, contact methods and, with person.view_identifiers, external identifiers.",
        "params": "@personId UNIQUEIDENTIFIER",
        "key": "CONCAT (N'personId=', @personId)",
        "notes": "Six result sets. Throws 51404 when the person does not exist or is outside the caller's scope -- the two "
                 "are indistinguishable on purpose.",
        "validate": PERSON_VISIBLE,
        "body": """
DECLARE @today DATE = auth.udfSessionToday ();

SELECT p.personId, p.rootTenantId, p.lastName, p.firstName, p.middleName, p.dob, p.dobVerified, p.sex, p.ethnicity,
       p.preferredLanguage, p.deceasedDate, p.mergedIntoPersonId, p.isPlaceholderName, p.rowVersion,
       cur.countyFips AS currentCountyFips, cur.countyName AS currentCountyName, cur.locationId AS currentPrimaryLocationId,
       CAST (CASE WHEN EXISTS (SELECT 1 FROM dbo.labResult AS r WHERE r.personId = p.personId AND r.isDeleted = 0)
                  THEN 0 ELSE 1 END AS BIT) AS isContactOnly,
       CAST (CASE WHEN EXISTS (SELECT 1 FROM dbo.pregnancyEpisode AS e WHERE e.personId = p.personId AND e.isDeleted = 0)
                  THEN 1 ELSE 0 END AS BIT) AS hasPregnancyEpisodes
  FROM dbo.person AS p
  OUTER APPLY (SELECT TOP (1) l.locationId, l.countyFips, sj.subJurisdictionName AS countyName
                 FROM dbo.personLocation AS pl
                 JOIN dbo.location AS l ON l.locationId = pl.locationId
                 LEFT JOIN config.subJurisdiction AS sj ON sj.countyFips = l.countyFips AND sj.rootTenantId = p.rootTenantId
                WHERE pl.personId = p.personId AND pl.isDeleted = 0 AND pl.role = 'residence' AND pl.isPrimary = 1
                  AND pl.startDate <= @today AND (pl.endDate IS NULL OR pl.endDate >= @today)
                ORDER BY pl.startDate DESC) AS cur
 WHERE p.personId = @personId;

SELECT personAliasId, lastName, firstName, middleName, aliasType, source, firstSeen, lastSeen
  FROM dbo.personAlias WHERE personId = @personId AND isDeleted = 0 ORDER BY lastSeen DESC;

SELECT r.raceCode, lv.displayName
  FROM dbo.personRace AS r
  LEFT JOIN config.lookupValue AS lv ON lv.lookupType = 'race' AND lv.code = r.raceCode
 WHERE r.personId = @personId AND r.isDeleted = 0;

SELECT personContactMethodId, contactType, contactValue, isPreferred
  FROM dbo.personContactMethod WHERE personId = @personId AND isDeleted = 0 ORDER BY contactType, isPreferred DESC;

SELECT personIdentifierId, identifierType, identifierValue, assigningFacility, source, reportedDate
  FROM dbo.personIdentifier
 WHERE personId = @personId AND isDeleted = 0 AND auth.udfHasPermission (N'person.view_identifiers') = 1;

SELECT c.exposureCaseId, c.caseNumber, a.code AS analyteCode, a.displayName AS analyteName, c.classification,
       c.caseStatus, c.population
  FROM dbo.exposureCase AS c
  JOIN config.analyte AS a ON a.analyteId = c.analyteId
 WHERE c.personId = @personId AND c.isDeleted = 0 AND c.caseStatus <> 'closed'
 ORDER BY a.sortOrder;
""",
    },
    {
        "schema": "dbo", "name": "uspGetPersonRelationships", "kind": "read", "grants": BOTH, "ticket": "T076",
        "permission": "person.view",
        "desc": "Relationships section: links in both directions. A row where the person is the related person shows the "
                "role's inverse label, so a parent's page reads 'child of'.",
        "params": "@personId UNIQUEIDENTIFIER",
        "key": "CONCAT (N'personId=', @personId)",
        "body": """
SELECT r.personRelationshipId, CAST (0 AS BIT) AS isInverse, r.relatedPersonId AS otherPersonId,
       o.lastName AS otherLastName, o.firstName AS otherFirstName, o.dob AS otherDob,
       r.role, COALESCE (lv.displayName, r.role) AS roleLabel, r.isPrimaryContact, r.hasLegalCustody,
       r.startDate, r.endDate, r.source, r.verifiedDate, r.rowVersion,
       CAST (CASE WHEN EXISTS (SELECT 1 FROM dbo.labResult AS x WHERE x.personId = o.personId AND x.isDeleted = 0)
                  THEN 0 ELSE 1 END AS BIT) AS otherIsContactOnly
  FROM dbo.personRelationship AS r
  JOIN dbo.person AS o ON o.personId = r.relatedPersonId
  LEFT JOIN config.lookupValue AS lv ON lv.lookupType = 'relationshipRole' AND lv.code = r.role
 WHERE r.personId = @personId AND r.isDeleted = 0
UNION ALL
SELECT r.personRelationshipId, CAST (1 AS BIT), r.personId, o.lastName, o.firstName, o.dob,
       r.role, COALESCE (lv.inverseLabel, r.role), CAST (0 AS BIT), CAST (0 AS BIT),
       r.startDate, r.endDate, r.source, r.verifiedDate, r.rowVersion,
       CAST (CASE WHEN EXISTS (SELECT 1 FROM dbo.labResult AS x WHERE x.personId = o.personId AND x.isDeleted = 0)
                  THEN 0 ELSE 1 END AS BIT)
  FROM dbo.personRelationship AS r
  JOIN dbo.person AS o ON o.personId = r.personId
  LEFT JOIN config.lookupValue AS lv ON lv.lookupType = 'relationshipRole' AND lv.code = r.role
 WHERE r.relatedPersonId = @personId AND r.isDeleted = 0
 ORDER BY isInverse, endDate, roleLabel;
""",
    },
    {
        "schema": "dbo", "name": "uspGetPersonPlaces", "kind": "read", "grants": BOTH, "ticket": "T076",
        "permission": "person.view",
        "desc": "Places section: every person-location row with its address, county, role, primary flag, schedule and "
                "dates. Also used by the primary residence rules in the application to plan a row split.",
        "params": "@personId UNIQUEIDENTIFIER",
        "key": "CONCAT (N'personId=', @personId)",
        "body": """
SELECT pl.personLocationId, pl.locationId, pl.organizationSiteId, os.siteName, org.organizationName,
       pl.role, pl.isPrimary, pl.primaryReason, pl.scheduleType, pl.hoursPerWeek, pl.scheduleNote,
       pl.startDate, pl.endDate, pl.source, pl.verifiedDate, pl.rowVersion,
       l.addressLine1, l.unit, l.city, l.state, l.zip5, l.country, l.countyFips, sj.subJurisdictionName AS countyName,
       l.yearBuilt, l.isCityOnly
  FROM dbo.personLocation AS pl
  JOIN dbo.location AS l ON l.locationId = pl.locationId
  LEFT JOIN config.subJurisdiction AS sj ON sj.countyFips = l.countyFips AND sj.rootTenantId = l.rootTenantId
  LEFT JOIN dbo.organizationSite AS os ON os.organizationSiteId = pl.organizationSiteId
  LEFT JOIN dbo.organization AS org ON org.organizationId = os.organizationId
 WHERE pl.personId = @personId AND pl.isDeleted = 0
 ORDER BY CASE WHEN pl.endDate IS NULL THEN 0 ELSE 1 END, pl.isPrimary DESC, pl.role, pl.startDate DESC;
""",
    },
    {
        "schema": "dbo", "name": "uspGetPersonEmployment", "kind": "read", "grants": BOTH, "ticket": "T076",
        "permission": "person.view",
        "desc": "Employment section: current and past jobs with employer, worksite, codes, metal tasks and analytes.",
        "params": "@personId UNIQUEIDENTIFIER",
        "key": "CONCAT (N'personId=', @personId)",
        "body": """
SELECT e.employmentId, e.organizationId, o.organizationName, o.naicsCode AS organizationNaics, e.worksiteLocationId,
       l.addressLine1 AS worksiteAddress, l.city AS worksiteCity, l.state AS worksiteState,
       e.jobTitle, e.industryText, e.occupationCode, e.occupationCodeVersion, e.naicsOverride, e.metalTasks, e.ppeNotes, e.hoursPerWeek,
       e.startDate, e.endDate, e.source, e.verifiedDate, e.rowVersion,
       (SELECT STRING_AGG (a.code, ', ') FROM dbo.employmentAnalyte AS ea JOIN config.analyte AS a ON a.analyteId = ea.analyteId
         WHERE ea.employmentId = e.employmentId AND ea.isDeleted = 0) AS analytes,
       (SELECT ea.analyteId FROM dbo.employmentAnalyte AS ea WHERE ea.employmentId = e.employmentId AND ea.isDeleted = 0 FOR JSON PATH) AS analyteIdsJson
  FROM dbo.employment AS e
  JOIN dbo.organization AS o ON o.organizationId = e.organizationId
  LEFT JOIN dbo.location AS l ON l.locationId = e.worksiteLocationId
 WHERE e.personId = @personId AND e.isDeleted = 0
 ORDER BY CASE WHEN e.endDate IS NULL THEN 0 ELSE 1 END, e.startDate DESC;
""",
    },
    {
        "schema": "dbo", "name": "uspGetPersonExposureSources", "kind": "read", "grants": BOTH, "ticket": "T076",
        "permission": "person.view",
        "desc": "Exposure sources section: non-occupational sources with product and place.",
        "params": "@personId UNIQUEIDENTIFIER",
        "key": "CONCAT (N'personId=', @personId)",
        "body": """
SELECT s.exposureSourceId, s.exposureCaseId, s.sourceType, COALESCE (lv.displayName, s.sourceType) AS sourceTypeLabel,
       s.sourceDescription, s.productId, pr.productName, pr.brand, s.locationId, l.addressLine1, l.city,
       s.startDate, s.endDate, s.isConfirmed, s.modifiedDateUtc, pr.countryOfOrigin AS productCountry
  FROM dbo.exposureSource AS s
  LEFT JOIN config.lookupValue AS lv ON lv.lookupType = 'exposureSourceType' AND lv.code = s.sourceType
  LEFT JOIN dbo.product AS pr ON pr.productId = s.productId
  LEFT JOIN dbo.location AS l ON l.locationId = s.locationId
 WHERE s.personId = @personId AND s.isDeleted = 0
 ORDER BY s.startDate DESC;
""",
    },
    {
        "schema": "dbo", "name": "uspGetPersonPregnancies", "kind": "read", "grants": BOTH, "ticket": "T076",
        "permission": "person.view",
        "desc": "Pregnancy section, and the episodes population derivation needs: dates, outcome and linked newborn.",
        "params": "@personId UNIQUEIDENTIFIER",
        "key": "CONCAT (N'personId=', @personId)",
        "body": """
SELECT e.pregnancyEpisodeId, e.startDate, e.expectedEndDate, e.actualEndDate, e.outcome, e.source, e.newbornPersonId,
       n.lastName AS newbornLastName, n.firstName AS newbornFirstName, n.dob AS newbornDob, e.rowVersion
  FROM dbo.pregnancyEpisode AS e
  LEFT JOIN dbo.person AS n ON n.personId = e.newbornPersonId
 WHERE e.personId = @personId AND e.isDeleted = 0
 ORDER BY e.startDate DESC;
""",
    },
    {
        "schema": "dbo", "name": "uspGetPersonCases", "kind": "read", "grants": BOTH, "ticket": "T076",
        "permission": "person.view",
        "desc": "Cases and investigations section: one row per case with its workflow, assignment and counts of open "
                "tasks and linked investigations.",
        "params": "@personId UNIQUEIDENTIFIER",
        "key": "CONCAT (N'personId=', @personId)",
        "body": """
SELECT c.exposureCaseId, c.caseNumber, a.code AS analyteCode, a.displayName AS analyteName, c.population,
       c.classification, c.computedClassification, c.caseStatus, c.exposureCategory, c.workflowTemplate,
       c.openedDate, c.closedDate, c.closureReason, u.displayName AS assignedToName, t.tenantName AS owningTenantName,
       hr.valueCanonical AS highestValue, hr.unitCanonical AS highestUnit,
       (SELECT COUNT (*) FROM dbo.workTask AS w WHERE w.exposureCaseId = c.exposureCaseId AND w.isDeleted = 0
           AND w.taskStatus = 'open') AS openTasks,
       (SELECT COUNT (*) FROM dbo.investigationCase AS ic WHERE ic.exposureCaseId = c.exposureCaseId
           AND ic.isDeleted = 0) AS investigations
  FROM dbo.exposureCase AS c
  JOIN config.analyte AS a ON a.analyteId = c.analyteId
  LEFT JOIN auth.appUser AS u ON u.appUserId = c.assignedToUserId
  LEFT JOIN auth.tenant AS t ON t.tenantId = c.owningTenantId
  LEFT JOIN dbo.labResult AS hr ON hr.labResultId = c.highestLabResultId
 WHERE c.personId = @personId AND c.isDeleted = 0
 ORDER BY CASE c.caseStatus WHEN 'closed' THEN 1 ELSE 0 END, c.openedDate DESC;
""",
    },
    {
        "schema": "dbo", "name": "uspGetPersonResults", "kind": "read", "grants": BOTH, "ticket": "T076",
        "permission": "person.view",
        "desc": "Results section and trend chart: every result for the person, optionally for one analyte, with canonical "
                "values, sample type, method and lab.",
        "params": """@personId  UNIQUEIDENTIFIER
, @analyteId UNIQUEIDENTIFIER = NULL""",
        "key": "CONCAT (N'personId=', @personId, N', analyteId=', @analyteId)",
        "body": """
SELECT r.labResultId, r.analyteSpecimenId, a.analyteId, a.code AS analyteCode, a.displayName AS analyteName, s.specimen,
       r.collectedDate, r.reportedDate, r.receivedUtc, r.resultStatus, r.operator, r.valueReported, r.valueText,
       r.unitReported, r.valueCanonical, r.unitCanonical, r.sampleType, r.testMethod, r.speciation, r.population,
       r.isIndeterminate, r.resultRowStatus, r.accessionNumber, lab.organizationName AS labName,
       CONCAT (pv.firstName, N' ', pv.lastName) AS orderingProviderName
  FROM dbo.labResult AS r
  JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId = r.analyteSpecimenId
  JOIN config.analyte AS a ON a.analyteId = s.analyteId
  LEFT JOIN dbo.organization AS lab ON lab.organizationId = r.labOrganizationId
  LEFT JOIN dbo.provider AS pv ON pv.providerId = r.orderingProviderId
 WHERE r.personId = @personId AND r.isDeleted = 0
   AND (@analyteId IS NULL OR a.analyteId = @analyteId)
 ORDER BY r.collectedDate DESC, r.receivedUtc DESC;
""",
    },
    {
        "schema": "dbo", "name": "uspSavePerson", "kind": "write", "grants": BOTH, "ticket": "T041",
        "permission": "person.edit",
        "desc": "Creates or updates a person's identity. A new person is owned by the root tenant; when the caller acts for "
                "a local tenant, that tenant is given access so the creator can see the record. Keeps the match-key row "
                "in step for outside-scope checks.",
        "params": """@personId          UNIQUEIDENTIFIER
, @lastName          NVARCHAR (60)
, @firstName         NVARCHAR (60) = NULL
, @middleName        NVARCHAR (60) = NULL
, @lastNameKey       NVARCHAR (60)
, @firstNameKey      NVARCHAR (60) = NULL
, @isPlaceholderName BIT = 0
, @dob               DATE = NULL
, @dobVerified       BIT = 0
, @sex               VARCHAR (10) = 'unknown'
, @ethnicity         VARCHAR (20) = 'unknown'
, @preferredLanguage VARCHAR (20) = NULL
, @deceasedDate      DATE = NULL
, @rowVersion        BINARY (8) = NULL""",
        "key": "CONCAT (N'personId=', @personId)",
        "notes": "Returns one row: personId, rowVersion, isNew, dobChanged, deceasedChanged. The application reruns "
                 "classification when the DOB changed and creates the deceased review task when a death date was added.",
        "body": f"""
DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();
DECLARE @acting UNIQUEIDENTIFIER = auth.udfSessionActingTenantId ();
DECLARE @isNew BIT = 0, @dobChanged BIT = 0, @deceasedChanged BIT = 0;

IF @dob IS NOT NULL AND @dob > auth.udfSessionToday ()
BEGIN
    ;THROW 51422, N'Date of birth cannot be in the future.', 1;
END;

IF @personId IS NOT NULL AND EXISTS (SELECT 1 FROM dbo.person WHERE personId = @personId)
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.person WHERE personId = @personId AND mergedIntoPersonId IS NOT NULL)
    BEGIN
        ;THROW 51422, N'This record was merged into another and is read-only.', 1;
    END;

    SELECT @dobChanged      = IIF (dob IS DISTINCT FROM @dob, 1, 0),
           @deceasedChanged = IIF (deceasedDate IS NULL AND @deceasedDate IS NOT NULL, 1, 0)
      FROM dbo.person WHERE personId = @personId;

    UPDATE dbo.person
       SET lastName = @lastName, firstName = @firstName, middleName = @middleName, lastNameKey = @lastNameKey,
           firstNameKey = @firstNameKey, isPlaceholderName = @isPlaceholderName, dob = @dob, dobVerified = @dobVerified,
           sex = @sex, ethnicity = @ethnicity, preferredLanguage = @preferredLanguage, deceasedDate = @deceasedDate
     WHERE personId = @personId AND isDeleted = 0 AND rowVersion = @rowVersion;
    {CONCURRENCY.replace(chr(10), chr(10) + '    ')}
END
ELSE
BEGIN
    SET @isNew = 1;
    SET @deceasedChanged = IIF (@deceasedDate IS NULL, 0, 1);

    INSERT dbo.person (personId, rootTenantId, owningTenantId, lastName, firstName, middleName, lastNameKey, firstNameKey,
                       isPlaceholderName, dob, dobVerified, sex, ethnicity, preferredLanguage, deceasedDate)
    VALUES (@personId, @root, @root, @lastName, @firstName, @middleName, @lastNameKey, @firstNameKey,
            @isPlaceholderName, @dob, @dobVerified, @sex, @ethnicity, @preferredLanguage, @deceasedDate);

    IF @acting IS NOT NULL AND @acting <> @root
    BEGIN
        INSERT dbo.personAccess (personAccessId, rootTenantId, personId, tenantId, accessReason)
        VALUES ({NEWID}, @root, @personId, @acting, 'share');
    END;
END;

MERGE dbo.personMatchKey AS t
USING (SELECT @personId AS personId) AS s ON t.personId = s.personId AND t.isDeleted = 0
WHEN MATCHED AND (t.lastNameKey <> @lastNameKey OR t.firstNameKey IS DISTINCT FROM @firstNameKey
                  OR t.dob IS DISTINCT FROM @dob) THEN
    UPDATE SET lastNameKey = @lastNameKey, firstNameKey = @firstNameKey, dob = @dob
WHEN NOT MATCHED THEN
    INSERT (personMatchKeyId, rootTenantId, personId, lastNameKey, firstNameKey, dob)
    VALUES ({NEWID}, @root, @personId, @lastNameKey, @firstNameKey, @dob);

SELECT personId, rowVersion, @isNew AS isNew, @dobChanged AS dobChanged, @deceasedChanged AS deceasedChanged
  FROM dbo.person WHERE personId = @personId;

SET @Comments = IIF (@isNew = 1, N'Person created.', N'Person updated.');
""",
    },
    {
        "schema": "dbo", "name": "uspSavePersonRaces", "kind": "write", "grants": BOTH, "ticket": "T041",
        "permission": "person.edit",
        "desc": "Replaces a person's race values with the set given, as a JSON array of race codes.",
        "params": """@personId      UNIQUEIDENTIFIER
, @raceCodesJson NVARCHAR (MAX)""",
        "key": "CONCAT (N'personId=', @personId)",
        "validate": PERSON_VISIBLE + """

IF ISJSON (@raceCodesJson) = 0
BEGIN
    ;THROW 50000, N'@raceCodesJson must be a JSON array.', 1;
END;""",
        "body": f"""
DECLARE @codes TABLE (raceCode VARCHAR (40) PRIMARY KEY);
INSERT @codes SELECT DISTINCT CAST ([value] AS VARCHAR (40)) FROM OPENJSON (@raceCodesJson);

UPDATE r SET isDeleted = 1
  FROM dbo.personRace AS r
 WHERE r.personId = @personId AND r.isDeleted = 0
   AND NOT EXISTS (SELECT 1 FROM @codes AS c WHERE c.raceCode = r.raceCode);

INSERT dbo.personRace (personRaceId, rootTenantId, owningTenantId, personId, raceCode)
SELECT {NEWID}, p.rootTenantId, p.owningTenantId, @personId, c.raceCode
  FROM @codes AS c
  JOIN dbo.person AS p ON p.personId = @personId
 WHERE NOT EXISTS (SELECT 1 FROM dbo.personRace AS r WHERE r.personId = @personId AND r.raceCode = c.raceCode
                                                       AND r.isDeleted = 0);

SET @Comments = N'Race values saved.';
""",
    },
    {
        "schema": "dbo", "name": "uspSavePersonContactMethod", "kind": "write", "grants": BOTH, "ticket": "T041",
        "permission": "person.edit",
        "desc": "Adds, changes or removes one phone number or email address. Marking one preferred clears the flag on the "
                "others of the same type.",
        "params": """@personContactMethodId UNIQUEIDENTIFIER
, @personId              UNIQUEIDENTIFIER
, @contactType           VARCHAR (10)
, @contactValue          NVARCHAR (200)
, @isPreferred           BIT = 0
, @isDeleted             BIT = 0""",
        "key": "CONCAT (N'personContactMethodId=', @personContactMethodId, N', personId=', @personId)",
        "validate": PERSON_VISIBLE,
        "body": """
IF EXISTS (SELECT 1 FROM dbo.personContactMethod WHERE personContactMethodId = @personContactMethodId)
BEGIN
    UPDATE dbo.personContactMethod
       SET contactType = @contactType, contactValue = @contactValue, isPreferred = @isPreferred, isDeleted = @isDeleted
     WHERE personContactMethodId = @personContactMethodId AND personId = @personId AND isDeleted = 0;
END
ELSE IF @isDeleted = 0
BEGIN
    INSERT dbo.personContactMethod (personContactMethodId, rootTenantId, owningTenantId, personId, contactType,
                                    contactValue, isPreferred)
    SELECT @personContactMethodId, rootTenantId, owningTenantId, @personId, @contactType, @contactValue, @isPreferred
      FROM dbo.person WHERE personId = @personId;
END;

IF @isPreferred = 1 AND @isDeleted = 0
BEGIN
    UPDATE dbo.personContactMethod SET isPreferred = 0
     WHERE personId = @personId AND contactType = @contactType AND personContactMethodId <> @personContactMethodId
       AND isPreferred = 1 AND isDeleted = 0;
END;

SET @Comments = N'Contact method saved.';
""",
    },
    {
        "schema": "dbo", "name": "uspSavePersonIdentifier", "kind": "write", "grants": BOTH, "ticket": "T041",
        "permission": "person.view_identifiers",
        "desc": "Adds or removes an external identifier (medical record number with its facility, Medicaid ID).",
        "params": """@personIdentifierId UNIQUEIDENTIFIER
, @personId            UNIQUEIDENTIFIER
, @identifierType      VARCHAR (20)
, @identifierValue     NVARCHAR (60)
, @assigningFacility   NVARCHAR (120) = NULL
, @source              VARCHAR (20)
, @reportedDate        DATE
, @isDeleted           BIT = 0""",
        "key": "CONCAT (N'personIdentifierId=', @personIdentifierId, N', personId=', @personId)",
        "validate": PERSON_VISIBLE,
        "body": """
IF EXISTS (SELECT 1 FROM dbo.personIdentifier WHERE personIdentifierId = @personIdentifierId)
BEGIN
    UPDATE dbo.personIdentifier SET isDeleted = @isDeleted
     WHERE personIdentifierId = @personIdentifierId AND personId = @personId AND isDeleted = 0;
END
ELSE IF @isDeleted = 0
   AND NOT EXISTS (SELECT 1 FROM dbo.personIdentifier WHERE personId = @personId AND identifierType = @identifierType
                      AND identifierValue = @identifierValue AND assigningFacility IS NOT DISTINCT FROM @assigningFacility
                      AND isDeleted = 0)
BEGIN
    INSERT dbo.personIdentifier (personIdentifierId, rootTenantId, owningTenantId, personId, identifierType,
                                 identifierValue, assigningFacility, source, reportedDate)
    SELECT @personIdentifierId, rootTenantId, owningTenantId, @personId, @identifierType, @identifierValue,
           @assigningFacility, @source, @reportedDate
      FROM dbo.person WHERE personId = @personId;
END;

SET @Comments = N'Identifier saved.';
""",
    },
    {
        "schema": "dbo", "name": "uspSavePersonAlias", "kind": "write", "grants": BOTH, "ticket": "T041",
        "permission": "person.edit",
        "desc": "Records a name as it arrived on a report. The same name arriving again moves lastSeen forward instead of "
                "adding a row.",
        "params": """@personId          UNIQUEIDENTIFIER
, @lastName          NVARCHAR (60)
, @firstName         NVARCHAR (60) = NULL
, @middleName        NVARCHAR (60) = NULL
, @lastNameKey       NVARCHAR (60)
, @firstNameKey      NVARCHAR (60) = NULL
, @aliasType         VARCHAR (20)
, @source            VARCHAR (20)
, @sourceLabResultId UNIQUEIDENTIFIER = NULL
, @seenDate          DATE""",
        "key": "CONCAT (N'personId=', @personId, N', sourceLabResultId=', @sourceLabResultId)",
        "validate": PERSON_VISIBLE,
        "body": f"""
UPDATE dbo.personAlias
   SET lastSeen = IIF (@seenDate > lastSeen, @seenDate, lastSeen)
     , firstSeen = IIF (@seenDate < firstSeen, @seenDate, firstSeen)
 WHERE personId = @personId AND lastNameKey = @lastNameKey AND firstNameKey IS NOT DISTINCT FROM @firstNameKey
   AND isDeleted = 0;

IF @@ROWCOUNT = 0
BEGIN
    INSERT dbo.personAlias (personAliasId, rootTenantId, owningTenantId, personId, lastName, firstName, middleName,
                            lastNameKey, firstNameKey, aliasType, source, sourceLabResultId, firstSeen, lastSeen)
    SELECT {NEWID}, rootTenantId, owningTenantId, @personId, @lastName, @firstName, @middleName, @lastNameKey,
           @firstNameKey, @aliasType, @source, @sourceLabResultId, @seenDate, @seenDate
      FROM dbo.person WHERE personId = @personId;
END;

SET @Comments = N'Alias recorded.';
""",
    },
    {
        "schema": "dbo", "name": "uspSavePregnancyEpisode", "kind": "write", "grants": BOTH, "ticket": "T041",
        "permission": "person.edit",
        "desc": "Adds, changes or removes a pregnancy episode. The application reruns classification for results "
                "collected in the affected dates and warns, rather than blocks, when the person is recorded as male.",
        "params": """@pregnancyEpisodeId UNIQUEIDENTIFIER
, @personId           UNIQUEIDENTIFIER
, @startDate          DATE
, @expectedEndDate    DATE = NULL
, @actualEndDate      DATE = NULL
, @outcome            VARCHAR (20) = NULL
, @source             VARCHAR (20)
, @newbornPersonId    UNIQUEIDENTIFIER = NULL
, @isDeleted          BIT = 0
, @rowVersion         BINARY (8) = NULL""",
        "key": "CONCAT (N'pregnancyEpisodeId=', @pregnancyEpisodeId, N', personId=', @personId)",
        "validate": PERSON_VISIBLE,
        "body": f"""
IF EXISTS (SELECT 1 FROM dbo.pregnancyEpisode WHERE pregnancyEpisodeId = @pregnancyEpisodeId)
BEGIN
    UPDATE dbo.pregnancyEpisode
       SET startDate = @startDate, expectedEndDate = @expectedEndDate, actualEndDate = @actualEndDate,
           outcome = @outcome, source = @source, newbornPersonId = @newbornPersonId, isDeleted = @isDeleted
     WHERE pregnancyEpisodeId = @pregnancyEpisodeId AND personId = @personId AND isDeleted = 0
       AND rowVersion = @rowVersion;
    {CONCURRENCY.replace(chr(10), chr(10) + '    ')}
END
ELSE
BEGIN
    INSERT dbo.pregnancyEpisode (pregnancyEpisodeId, rootTenantId, owningTenantId, personId, startDate, expectedEndDate,
                                 actualEndDate, outcome, source, newbornPersonId)
    SELECT @pregnancyEpisodeId, rootTenantId, owningTenantId, @personId, @startDate, @expectedEndDate, @actualEndDate,
           @outcome, @source, @newbornPersonId
      FROM dbo.person WHERE personId = @personId;
END;

SET @Comments = N'Pregnancy episode saved.';
""",
    },
    {
        "schema": "dbo", "name": "uspSavePersonRelationship", "kind": "write", "grants": BOTH, "ticket": "T041",
        "permission": "person.edit",
        "desc": "Adds, changes or ends a relationship between two persons. At most one current primary contact per person: "
                "marking one clears the flag on the others.",
        "params": """@personRelationshipId UNIQUEIDENTIFIER
, @personId             UNIQUEIDENTIFIER
, @relatedPersonId      UNIQUEIDENTIFIER
, @role                 VARCHAR (30)
, @isPrimaryContact     BIT = 0
, @hasLegalCustody      BIT = 0
, @startDate            DATE = NULL
, @endDate              DATE = NULL
, @source               VARCHAR (20)
, @verifiedDate         DATE = NULL
, @isDeleted            BIT = 0
, @rowVersion           BINARY (8) = NULL""",
        "key": "CONCAT (N'personRelationshipId=', @personRelationshipId, N', personId=', @personId)",
        "validate": PERSON_VISIBLE,
        "body": f"""
IF @relatedPersonId = @personId
BEGIN
    ;THROW 51422, N'A person cannot be related to themselves.', 1;
END;

IF EXISTS (SELECT 1 FROM dbo.personRelationship WHERE personRelationshipId = @personRelationshipId)
BEGIN
    UPDATE dbo.personRelationship
       SET role = @role, isPrimaryContact = @isPrimaryContact, hasLegalCustody = @hasLegalCustody,
           startDate = @startDate, endDate = @endDate, source = @source, verifiedDate = @verifiedDate,
           isDeleted = @isDeleted
     WHERE personRelationshipId = @personRelationshipId AND isDeleted = 0 AND rowVersion = @rowVersion;
    {CONCURRENCY.replace(chr(10), chr(10) + '    ')}
END
ELSE
BEGIN
    INSERT dbo.personRelationship (personRelationshipId, rootTenantId, owningTenantId, personId, relatedPersonId, role,
                                   isPrimaryContact, hasLegalCustody, startDate, endDate, source, verifiedDate)
    SELECT @personRelationshipId, rootTenantId, owningTenantId, @personId, @relatedPersonId, @role, @isPrimaryContact,
           @hasLegalCustody, @startDate, @endDate, @source, @verifiedDate
      FROM dbo.person WHERE personId = @personId;
END;

IF @isPrimaryContact = 1 AND @isDeleted = 0
BEGIN
    UPDATE dbo.personRelationship SET isPrimaryContact = 0
     WHERE personId = @personId AND personRelationshipId <> @personRelationshipId AND isPrimaryContact = 1
       AND isDeleted = 0 AND (endDate IS NULL OR endDate >= auth.udfSessionToday ());
END;

SET @Comments = N'Relationship saved.';
""",
    },
    {
        "schema": "dbo", "name": "uspRefreshPersonAccess", "kind": "write", "grants": BOTH, "ticket": "T042",
        "desc": "Brings dbo.personAccess in step for one person: the tenants serving the county of the current primary "
                "residence get residence access, and tenants owning an open case get case access. Called by every "
                "procedure that changes a primary residence or a case's ownership.",
        "params": "@personId UNIQUEIDENTIFIER",
        "key": "CONCAT (N'personId=', @personId)",
        "notes": "Converges: rows that should exist are added, rows that no longer apply are soft-deleted, rows that are "
                 "right are left alone (so their audit columns do not move).",
        "body": f"""
DECLARE @today DATE = auth.udfSessionToday ();
DECLARE @root UNIQUEIDENTIFIER = (SELECT rootTenantId FROM dbo.person WHERE personId = @personId);
DECLARE @want TABLE (tenantId UNIQUEIDENTIFIER, accessReason VARCHAR (20), PRIMARY KEY (tenantId, accessReason));

INSERT @want (tenantId, accessReason)
SELECT DISTINCT tj.tenantId, 'residence'
  FROM dbo.personLocation AS pl
  JOIN dbo.location AS l ON l.locationId = pl.locationId
  JOIN config.subJurisdiction AS sj ON sj.rootTenantId = l.rootTenantId AND sj.countyFips = l.countyFips AND sj.isDeleted = 0
  JOIN auth.tenantJurisdiction AS tj ON tj.subJurisdictionId = sj.subJurisdictionId AND tj.isDeleted = 0
 WHERE pl.personId = @personId AND pl.isDeleted = 0 AND pl.role = 'residence' AND pl.isPrimary = 1
   AND pl.startDate <= @today AND (pl.endDate IS NULL OR pl.endDate >= @today);

INSERT @want (tenantId, accessReason)
SELECT DISTINCT c.owningTenantId, 'case'
  FROM dbo.exposureCase AS c
 WHERE c.personId = @personId AND c.isDeleted = 0 AND c.caseStatus <> 'closed' AND c.owningTenantId <> @root;

UPDATE pa SET isDeleted = 1
  FROM dbo.personAccess AS pa
 WHERE pa.personId = @personId AND pa.isDeleted = 0 AND pa.accessReason IN ('residence', 'case')
   AND NOT EXISTS (SELECT 1 FROM @want AS w WHERE w.tenantId = pa.tenantId AND w.accessReason = pa.accessReason);

INSERT dbo.personAccess (personAccessId, rootTenantId, personId, tenantId, accessReason)
SELECT {NEWID}, @root, @personId, w.tenantId, w.accessReason
  FROM @want AS w
 WHERE NOT EXISTS (SELECT 1 FROM dbo.personAccess AS pa WHERE pa.personId = @personId AND pa.tenantId = w.tenantId
                                                          AND pa.accessReason = w.accessReason AND pa.isDeleted = 0);

SET @Comments = N'Person access refreshed.';
""",
    },
]
