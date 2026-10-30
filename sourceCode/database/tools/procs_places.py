"""Places, organizations, employment, exposure sources, products and providers."""
from procs_common import NEWID, WEB, BOTH, CONCURRENCY, not_found

PERSON_VISIBLE = not_found("@personId", "dbo.person", "personId", "The person")

PROCS = [
    {
        "schema": "dbo", "name": "uspGetLookupSelection", "kind": "read", "grants": WEB, "ticket": "#66",
        "permission": "person.view",
        "desc": "Rehydrates one selected lookup from its authorized identifier; RLS applies to every source.",
        "params": "@kind VARCHAR (20)\n, @id UNIQUEIDENTIFIER",
        "key": "CONCAT (N'kind=', @kind, N', id=', @id)",
        "body": """
SELECT CONVERT (NVARCHAR (80), p.personId) AS value, CONCAT (p.lastName, N', ', p.firstName) AS label
  FROM dbo.person AS p WHERE @kind = 'person' AND p.personId = @id AND p.isDeleted = 0
UNION ALL
SELECT CONVERT (NVARCHAR (80), o.organizationId), o.organizationName
  FROM dbo.organization AS o WHERE @kind = 'organization' AND o.organizationId = @id AND o.isDeleted = 0
UNION ALL
SELECT CONVERT (NVARCHAR (80), p.providerId), RTRIM (CONCAT (p.lastName, N', ', p.firstName, N' ', p.credential))
  FROM dbo.provider AS p WHERE @kind = 'provider' AND p.providerId = @id AND p.isDeleted = 0
UNION ALL
SELECT CONVERT (NVARCHAR (80), l.locationId), CONCAT (l.addressLine1, CASE WHEN l.unit IS NULL THEN N'' ELSE N' ' + l.unit END)
  FROM dbo.location AS l WHERE @kind = 'location' AND l.locationId = @id AND l.isDeleted = 0
UNION ALL
SELECT CONCAT (s.organizationSiteId, N':', s.locationId), CONCAT (o.organizationName, N' - ', l.addressLine1, N', ', l.city)
  FROM dbo.organizationSite AS s
  JOIN dbo.organization AS o ON o.organizationId = s.organizationId AND o.isDeleted = 0
  JOIN dbo.location AS l ON l.locationId = s.locationId AND l.isDeleted = 0
 WHERE @kind = 'site' AND s.organizationSiteId = @id AND s.isDeleted = 0;
""",
    },
    {
        "schema": "dbo", "name": "uspFindLocations", "kind": "read", "grants": BOTH, "ticket": "T042",
        "permission": "person.view",
        "desc": "Existing locations whose normalized address starts with the text typed, so address entry offers a "
                "matching location before creating a new one.",
        "params": """@addressKeyPrefix NVARCHAR (300)
, @zip5             CHAR (5) = NULL
, @maxRows          INT = 10""",
        "key": "CONCAT (N'zip5=', @zip5, N', maxRows=', @maxRows)",
        "body": """
SELECT TOP (@maxRows) l.locationId, l.addressLine1, l.unit, l.city, l.state, l.zip5, l.country, l.countyFips,
       sj.subJurisdictionName AS countyName, l.isCityOnly, l.yearBuilt
  FROM dbo.location AS l
  LEFT JOIN config.subJurisdiction AS sj ON sj.rootTenantId = l.rootTenantId AND sj.countyFips = l.countyFips
 WHERE l.isDeleted = 0
   AND l.addressKey LIKE @addressKeyPrefix + N'%'
   AND (@zip5 IS NULL OR l.zip5 = @zip5)
 ORDER BY l.addressKey;
""",
    },
    {
        "schema": "dbo", "name": "uspSaveLocation", "kind": "write", "grants": BOTH, "ticket": "T042",
        "permission": "person.edit",
        "desc": "Finds or creates a location. An existing location with the same normalized address and unit is reused, "
                "so everyone at one address shares one record; a unit's building record (same address, no unit) is found "
                "or created and linked.",
        "params": """@locationId         UNIQUEIDENTIFIER
, @country            CHAR (2) = 'US'
, @addressLine1       NVARCHAR (120) = NULL
, @unit               NVARCHAR (30) = NULL
, @city               NVARCHAR (60)
, @state              CHAR (2) = NULL
, @zip5               CHAR (5) = NULL
, @region             NVARCHAR (60) = NULL
, @postalCode         NVARCHAR (20) = NULL
, @countyFips         CHAR (3) = NULL
, @isCityOnly         BIT = 0
, @addressKey         NVARCHAR (300)
, @buildingAddressKey NVARCHAR (300) = NULL
, @yearBuilt          INT = NULL
, @housingType        VARCHAR (20) = NULL
, @tenure             VARCHAR (20) = NULL""",
        "key": "CONCAT (N'locationId=', @locationId)",
        "notes": "Returns one row: locationId (the existing one when reused) and wasExisting.",
        "body": f"""
DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();
DECLARE @existing UNIQUEIDENTIFIER, @building UNIQUEIDENTIFIER;

IF @country = 'US' AND @isCityOnly = 0 AND (@addressLine1 IS NULL OR @state IS NULL OR @zip5 IS NULL)
BEGIN
    ;THROW 51422, N'A US address needs a street address, state and ZIP code, unless it is a city-only location.', 1;
END;

SELECT TOP (1) @existing = locationId
  FROM dbo.location WITH (UPDLOCK, HOLDLOCK)
 WHERE rootTenantId = @root AND addressKey = @addressKey AND isDeleted = 0;

IF @existing IS NOT NULL
BEGIN
    -- Fill gaps on the shared record without overwriting what someone else recorded.
    UPDATE dbo.location
       SET countyFips  = COALESCE (countyFips, @countyFips)
         , yearBuilt   = COALESCE (yearBuilt, @yearBuilt)
         , housingType = COALESCE (housingType, @housingType)
         , tenure      = COALESCE (tenure, @tenure)
     WHERE locationId = @existing
       AND ((countyFips IS NULL AND @countyFips IS NOT NULL) OR (yearBuilt IS NULL AND @yearBuilt IS NOT NULL)
         OR (housingType IS NULL AND @housingType IS NOT NULL) OR (tenure IS NULL AND @tenure IS NOT NULL));

    SELECT @existing AS locationId, CAST (1 AS BIT) AS wasExisting;
END
ELSE
BEGIN
    IF @unit IS NOT NULL AND @buildingAddressKey IS NOT NULL
    BEGIN
        SELECT TOP (1) @building = locationId
          FROM dbo.location WITH (UPDLOCK, HOLDLOCK)
         WHERE rootTenantId = @root AND addressKey = @buildingAddressKey AND isDeleted = 0;

        IF @building IS NULL
        BEGIN
            SET @building = {NEWID};
            INSERT dbo.location (locationId, rootTenantId, owningTenantId, country, addressLine1, city, state, zip5,
                                 region, postalCode, countyFips, addressKey, yearBuilt, housingType)
            VALUES (@building, @root, @root, @country, @addressLine1, @city, @state, @zip5, @region, @postalCode,
                    @countyFips, @buildingAddressKey, @yearBuilt, @housingType);
        END;
    END;

    INSERT dbo.location (locationId, rootTenantId, owningTenantId, country, addressLine1, unit, city, state, zip5, region,
                         postalCode, countyFips, isCityOnly, addressKey, buildingLocationId, yearBuilt, housingType, tenure)
    VALUES (@locationId, @root, @root, @country, @addressLine1, @unit, @city, @state, @zip5, @region, @postalCode,
            @countyFips, @isCityOnly, @addressKey, @building, @yearBuilt, @housingType, @tenure);

    SELECT @locationId AS locationId, CAST (0 AS BIT) AS wasExisting;
END;

SET @Comments = IIF (@existing IS NULL, N'Location created.', N'Existing location reused.');
""",
    },
    {
        "schema": "dbo", "name": "uspSavePersonLocations", "kind": "write", "grants": BOTH, "ticket": "T042",
        "permission": "person.edit",
        "desc": "Applies a set of person-location changes for one person in one transaction and enforces the address "
                "rules in the database: no two primary residences overlap, overlapping residences carry a primary reason, "
                "and every date covered by a residence is covered by a primary. Used for adding a place and for the "
                "row split when the primary residence changes.",
        "params": """@personId    UNIQUEIDENTIFIER
, @changesJson NVARCHAR (MAX)""",
        "key": "CONCAT (N'personId=', @personId)",
        "notes": "The row split itself is planned by Merit.Domain (PrimaryResidencePlanner), so the rule is written once and "
                 "the preview the user sees is the change applied. This procedure is the wall behind it: it takes an "
                 "update lock on the person's location rows so two sessions cannot both save a primary, applies the "
                 "changes, then rejects the whole set with 51422 if any rule is broken.\n\n"
                 "@changesJson is an array of objects with personLocationId, locationId, organizationSiteId, role, "
                 "isPrimary, primaryReason, scheduleType, hoursPerWeek, scheduleNote, startDate, endDate, source, "
                 "verifiedDate, isDeleted and rowVersion (hex, 0x...; omitted for new rows).",
        "validate": PERSON_VISIBLE + """

IF ISJSON (@changesJson) = 0
BEGIN
    ;THROW 50000, N'@changesJson must be a JSON array.', 1;
END;""",
        "body": """
DECLARE @changes TABLE
(
    personLocationId   UNIQUEIDENTIFIER PRIMARY KEY,
    locationId         UNIQUEIDENTIFIER,
    organizationSiteId UNIQUEIDENTIFIER,
    role               VARCHAR (20),
    isPrimary          BIT,
    primaryReason      VARCHAR (30),
    scheduleType       VARCHAR (20),
    hoursPerWeek       DECIMAL (5, 2),
    scheduleNote       NVARCHAR (500),
    startDate          DATE,
    endDate            DATE,
    source             VARCHAR (20),
    verifiedDate       DATE,
    isDeleted          BIT,
    rowVersion         BINARY (8)
);

INSERT @changes
SELECT personLocationId, locationId, organizationSiteId, role, COALESCE (isPrimary, 0), primaryReason, scheduleType,
       hoursPerWeek, scheduleNote, startDate, endDate, source, verifiedDate, COALESCE (isDeleted, 0),
       CONVERT (BINARY (8), rowVersionHex, 1)
  FROM OPENJSON (@changesJson)
  WITH (personLocationId UNIQUEIDENTIFIER, locationId UNIQUEIDENTIFIER, organizationSiteId UNIQUEIDENTIFIER,
        role VARCHAR (20), isPrimary BIT, primaryReason VARCHAR (30), scheduleType VARCHAR (20),
        hoursPerWeek DECIMAL (5, 2), scheduleNote NVARCHAR (500), startDate DATE, endDate DATE, source VARCHAR (20),
        verifiedDate DATE, isDeleted BIT, rowVersionHex VARCHAR (20) '$.rowVersion');

-- Serialize every writer of this person's places (address rule 1: enforced under row locks, not only in the UI).
DECLARE @lock INT = (SELECT COUNT (*) FROM dbo.personLocation WITH (UPDLOCK, HOLDLOCK) WHERE personId = @personId);

DECLARE @expected INT = (SELECT COUNT (*) FROM @changes AS c
                          WHERE EXISTS (SELECT 1 FROM dbo.personLocation AS pl WHERE pl.personLocationId = c.personLocationId));

UPDATE pl
   SET locationId = c.locationId, organizationSiteId = c.organizationSiteId, role = c.role, isPrimary = c.isPrimary,
       primaryReason = c.primaryReason, scheduleType = c.scheduleType, hoursPerWeek = c.hoursPerWeek,
       scheduleNote = c.scheduleNote, startDate = c.startDate, endDate = c.endDate, source = c.source,
       verifiedDate = c.verifiedDate, isDeleted = c.isDeleted
  FROM dbo.personLocation AS pl
  JOIN @changes AS c ON c.personLocationId = pl.personLocationId
 WHERE pl.personId = @personId AND pl.isDeleted = 0 AND (c.rowVersion IS NULL OR pl.rowVersion = c.rowVersion);

IF @@ROWCOUNT <> @expected
BEGIN
    ;THROW 51409, N'These places were changed by someone else. Reload the person and try again.', 1;
END;

INSERT dbo.personLocation (personLocationId, rootTenantId, owningTenantId, personId, locationId, organizationSiteId, role,
                           isPrimary, primaryReason, scheduleType, hoursPerWeek, scheduleNote, startDate, endDate, source,
                           verifiedDate)
SELECT c.personLocationId, p.rootTenantId, p.owningTenantId, @personId, c.locationId, c.organizationSiteId, c.role,
       c.isPrimary, c.primaryReason, c.scheduleType, c.hoursPerWeek, c.scheduleNote, c.startDate, c.endDate, c.source,
       c.verifiedDate
  FROM @changes AS c
  JOIN dbo.person AS p ON p.personId = @personId
 WHERE c.isDeleted = 0
   AND NOT EXISTS (SELECT 1 FROM dbo.personLocation AS pl WHERE pl.personLocationId = c.personLocationId);

-- ---------------------------------------------------------------------------------------- the rules
DECLARE @msg NVARCHAR (400);

-- Rule 1: no two primary residences overlap.
SELECT TOP (1) @msg = CONCAT (N'Two primary residences overlap from ', CONVERT (NVARCHAR (10), IIF (a.startDate > b.startDate, a.startDate, b.startDate), 23),
                              N'. A person has one primary residence on any date.')
  FROM dbo.personLocation AS a
  JOIN dbo.personLocation AS b ON b.personId = a.personId AND b.personLocationId > a.personLocationId
 WHERE a.personId = @personId AND a.isDeleted = 0 AND b.isDeleted = 0
   AND a.role = 'residence' AND b.role = 'residence' AND a.isPrimary = 1 AND b.isPrimary = 1
   AND a.startDate <= COALESCE (b.endDate, '9999-12-31') AND b.startDate <= COALESCE (a.endDate, '9999-12-31');

IF @msg IS NOT NULL THROW 51422, @msg, 1;

-- Rule 3: where residences overlap, the primary one carries a reason.
SELECT TOP (1) @msg = N'Two residences overlap, so the primary residence needs a reason (custody order, most overnights, ...).'
  FROM dbo.personLocation AS a
  JOIN dbo.personLocation AS b ON b.personId = a.personId AND b.personLocationId <> a.personLocationId
 WHERE a.personId = @personId AND a.isDeleted = 0 AND b.isDeleted = 0
   AND a.role = 'residence' AND b.role = 'residence' AND a.isPrimary = 1 AND a.primaryReason IS NULL
   AND a.startDate <= COALESCE (b.endDate, '9999-12-31') AND b.startDate <= COALESCE (a.endDate, '9999-12-31');

IF @msg IS NOT NULL THROW 51422, @msg, 1;

-- Rule 2: every date covered by a residence is covered by a primary. A gap can only begin at a residence's start
-- or on the day after a primary ends, so those are the only dates that need testing.
;WITH residence AS
(
    SELECT startDate, COALESCE (endDate, '9999-12-31') AS endDate, isPrimary
      FROM dbo.personLocation
     WHERE personId = @personId AND isDeleted = 0 AND role = 'residence'
),
candidate AS
(
    SELECT r.startDate AS d FROM residence AS r
    UNION
    SELECT DATEADD (DAY, 1, p.endDate) FROM residence AS p WHERE p.isPrimary = 1 AND p.endDate < '9999-12-31'
)
SELECT TOP (1) @msg = CONCAT (N'On ', CONVERT (NVARCHAR (10), c.d, 23),
                              N' the person has a residence but no primary residence. Pick which residence is primary.')
  FROM candidate AS c
 WHERE EXISTS (SELECT 1 FROM residence AS r WHERE c.d BETWEEN r.startDate AND r.endDate)
   AND NOT EXISTS (SELECT 1 FROM residence AS p WHERE p.isPrimary = 1 AND c.d BETWEEN p.startDate AND p.endDate)
 ORDER BY c.d;

IF @msg IS NOT NULL THROW 51422, @msg, 1;

EXEC dbo.uspRefreshPersonAccess @personId = @personId;

SET @Comments = CONCAT ((SELECT COUNT (*) FROM @changes), N' place change(s) applied.');
""",
    },
    {
        "schema": "dbo", "name": "uspSearchOrganizations", "kind": "read", "grants": BOTH, "ticket": "T085",
        "permission": "person.view",
        "desc": "Server-side type-ahead for organizations by normalized name, optionally of one type, with their first "
                "site address so the user can tell branches apart.",
        "params": """@nameKey              NVARCHAR (120)
, @organizationTypeCode VARCHAR (30) = NULL
, @maxRows              INT = 10""",
        "key": "CONCAT (N'organizationTypeCode=', @organizationTypeCode, N', maxRows=', @maxRows)",
        "body": """
SELECT TOP (@maxRows) o.organizationId, o.organizationName, o.naicsCode, o.cliaNumber, o.npi,
       (SELECT STRING_AGG (ot.organizationTypeCode, ', ') FROM dbo.organizationType AS ot
         WHERE ot.organizationId = o.organizationId AND ot.isDeleted = 0) AS organizationTypes,
       site.organizationSiteId, site.locationId AS siteLocationId, site.siteName,
       site.addressLine1 AS siteAddress, site.city AS siteCity
  FROM dbo.organization AS o
  OUTER APPLY (SELECT TOP (1) os.organizationSiteId, os.locationId, os.siteName, l.addressLine1, l.city
                 FROM dbo.organizationSite AS os JOIN dbo.location AS l ON l.locationId = os.locationId
                WHERE os.organizationId = o.organizationId AND os.isDeleted = 0 AND os.endDate IS NULL
                ORDER BY os.startDate) AS site
 WHERE o.isDeleted = 0 AND o.isActive = 1
   AND o.organizationNameKey LIKE N'%' + @nameKey + N'%'
   AND (@organizationTypeCode IS NULL OR EXISTS (SELECT 1 FROM dbo.organizationType AS ot
                                                  WHERE ot.organizationId = o.organizationId AND ot.isDeleted = 0
                                                    AND ot.organizationTypeCode = @organizationTypeCode))
 ORDER BY CASE WHEN o.organizationNameKey LIKE @nameKey + N'%' THEN 0 ELSE 1 END, o.organizationName;
""",
    },
    {
        "schema": "dbo", "name": "uspGetOrganization", "kind": "read", "grants": BOTH, "ticket": "T043",
        "permission": "person.view",
        "desc": "One organization with its types and sites.",
        "params": "@organizationId UNIQUEIDENTIFIER",
        "key": "CONCAT (N'organizationId=', @organizationId)",
        "validate": not_found("@organizationId", "dbo.organization", "organizationId", "The organization"),
        "body": """
SELECT o.organizationId, o.organizationName, o.naicsCode, o.naicsVersion, o.npi, o.cliaNumber, o.parentOrganizationId,
       po.organizationName AS parentOrganizationName, o.contactName, o.phone, o.email, o.isActive, o.rowVersion
  FROM dbo.organization AS o
  LEFT JOIN dbo.organization AS po ON po.organizationId = o.parentOrganizationId
 WHERE o.organizationId = @organizationId;

SELECT organizationTypeCode FROM dbo.organizationType WHERE organizationId = @organizationId AND isDeleted = 0;

SELECT os.organizationSiteId, os.locationId, os.siteName, os.startDate, os.endDate, l.addressLine1, l.unit, l.city,
       l.state, l.zip5, l.countyFips
  FROM dbo.organizationSite AS os
  JOIN dbo.location AS l ON l.locationId = os.locationId
 WHERE os.organizationId = @organizationId AND os.isDeleted = 0
 ORDER BY os.endDate, os.siteName;
""",
    },
    {
        "schema": "dbo", "name": "uspSaveOrganization", "kind": "write", "grants": BOTH, "ticket": "T043",
        "permission": "person.edit",
        "desc": "Creates or updates an organization and replaces its set of types (JSON array of type codes).",
        "params": """@organizationId       UNIQUEIDENTIFIER
, @organizationName     NVARCHAR (120)
, @organizationNameKey  NVARCHAR (120)
, @naicsCode            VARCHAR (6) = NULL
, @naicsVersion         VARCHAR (10) = NULL
, @npi                  VARCHAR (10) = NULL
, @cliaNumber           VARCHAR (10) = NULL
, @parentOrganizationId UNIQUEIDENTIFIER = NULL
, @contactName          NVARCHAR (120) = NULL
, @phone                NVARCHAR (30) = NULL
, @email                NVARCHAR (200) = NULL
, @isActive             BIT = 1
, @typesJson            NVARCHAR (MAX) = N'[]'
, @rowVersion           BINARY (8) = NULL""",
        "key": "CONCAT (N'organizationId=', @organizationId)",
        "body": f"""
DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();

-- One organization per CLIA number and per NPI (issue #11); the unique indexes enforce it, this says which.
IF @cliaNumber IS NOT NULL AND EXISTS (SELECT 1 FROM dbo.organization
                                        WHERE cliaNumber = @cliaNumber AND organizationId <> @organizationId AND isDeleted = 0)
BEGIN
    ;THROW 51422, N'An organization with that CLIA number already exists.', 1;
END;

IF @npi IS NOT NULL AND EXISTS (SELECT 1 FROM dbo.organization
                                 WHERE npi = @npi AND organizationId <> @organizationId AND isDeleted = 0)
BEGIN
    ;THROW 51422, N'An organization with that NPI already exists.', 1;
END;

IF EXISTS (SELECT 1 FROM dbo.organization WHERE organizationId = @organizationId)
BEGIN
    UPDATE dbo.organization
       SET organizationName = @organizationName, organizationNameKey = @organizationNameKey, naicsCode = @naicsCode,
           naicsVersion = @naicsVersion, npi = @npi, cliaNumber = @cliaNumber,
           parentOrganizationId = @parentOrganizationId, contactName = @contactName, phone = @phone, email = @email,
           isActive = @isActive
     WHERE organizationId = @organizationId AND isDeleted = 0 AND (@rowVersion IS NULL OR rowVersion = @rowVersion);
    {CONCURRENCY.replace(chr(10), chr(10) + '    ')}
END
ELSE
BEGIN
    INSERT dbo.organization (organizationId, rootTenantId, owningTenantId, organizationName, organizationNameKey,
                             naicsCode, naicsVersion, npi, cliaNumber, parentOrganizationId, contactName, phone, email,
                             isActive)
    VALUES (@organizationId, @root, @root, @organizationName, @organizationNameKey, @naicsCode, @naicsVersion, @npi,
            @cliaNumber, @parentOrganizationId, @contactName, @phone, @email, @isActive);
END;

DECLARE @types TABLE (code VARCHAR (30) PRIMARY KEY);
INSERT @types SELECT DISTINCT CAST ([value] AS VARCHAR (30)) FROM OPENJSON (COALESCE (@typesJson, N'[]'));

UPDATE t SET isDeleted = 1
  FROM dbo.organizationType AS t
 WHERE t.organizationId = @organizationId AND t.isDeleted = 0
   AND NOT EXISTS (SELECT 1 FROM @types AS x WHERE x.code = t.organizationTypeCode);

INSERT dbo.organizationType (organizationTypeId, rootTenantId, owningTenantId, organizationId, organizationTypeCode)
SELECT {NEWID}, @root, @root, @organizationId, x.code
  FROM @types AS x
 WHERE NOT EXISTS (SELECT 1 FROM dbo.organizationType AS t WHERE t.organizationId = @organizationId
                                                            AND t.organizationTypeCode = x.code AND t.isDeleted = 0);

SET @Comments = N'Organization saved.';
""",
    },
    {
        "schema": "dbo", "name": "uspSaveOrganizationSite", "kind": "write", "grants": BOTH, "ticket": "T043",
        "permission": "person.edit",
        "desc": "Adds, changes or ends a site of an organization.",
        "params": """@organizationSiteId UNIQUEIDENTIFIER
, @organizationId     UNIQUEIDENTIFIER
, @locationId         UNIQUEIDENTIFIER
, @siteName           NVARCHAR (120) = NULL
, @startDate          DATE = NULL
, @endDate            DATE = NULL
, @isDeleted          BIT = 0""",
        "key": "CONCAT (N'organizationSiteId=', @organizationSiteId, N', organizationId=', @organizationId)",
        "body": """
DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();

IF EXISTS (SELECT 1 FROM dbo.organizationSite WHERE organizationSiteId = @organizationSiteId)
BEGIN
    UPDATE dbo.organizationSite
       SET locationId = @locationId, siteName = @siteName, startDate = @startDate, endDate = @endDate,
           isDeleted = @isDeleted
     WHERE organizationSiteId = @organizationSiteId AND isDeleted = 0;
END
ELSE IF @isDeleted = 0
BEGIN
    INSERT dbo.organizationSite (organizationSiteId, rootTenantId, owningTenantId, organizationId, locationId, siteName,
                                 startDate, endDate)
    VALUES (@organizationSiteId, @root, @root, @organizationId, @locationId, @siteName, @startDate, @endDate);
END;

SET @Comments = N'Site saved.';
""",
    },
    {
        "schema": "dbo", "name": "uspSaveEmployment", "kind": "write", "grants": BOTH, "ticket": "T043",
        "permission": "person.edit",
        "desc": "Adds, changes or ends a job, and replaces the analytes the worker is exposed to (JSON array of analyte "
                "ids).",
        "params": """@employmentId          UNIQUEIDENTIFIER
, @personId              UNIQUEIDENTIFIER
, @organizationId        UNIQUEIDENTIFIER
, @worksiteLocationId    UNIQUEIDENTIFIER = NULL
, @jobTitle              NVARCHAR (120) = NULL
, @industryText          NVARCHAR (200) = NULL
, @occupationCode        VARCHAR (4) = NULL
, @occupationCodeVersion VARCHAR (10) = NULL
, @naicsOverride         VARCHAR (6) = NULL
, @metalTasks            NVARCHAR (1000) = NULL
, @ppeNotes              NVARCHAR (1000) = NULL
, @hoursPerWeek          DECIMAL (5, 2) = NULL
, @startDate             DATE = NULL
, @endDate               DATE = NULL
, @source                VARCHAR (20)
, @verifiedDate          DATE = NULL
, @analyteIdsJson        NVARCHAR (MAX) = N'[]'
, @isDeleted             BIT = 0
, @rowVersion            BINARY (8) = NULL""",
        "key": "CONCAT (N'employmentId=', @employmentId, N', personId=', @personId)",
        "validate": PERSON_VISIBLE,
        "body": f"""
IF EXISTS (SELECT 1 FROM dbo.employment WHERE employmentId = @employmentId)
BEGIN
    UPDATE dbo.employment
       SET organizationId = @organizationId, worksiteLocationId = @worksiteLocationId, jobTitle = @jobTitle,
           industryText = @industryText, occupationCode = @occupationCode, occupationCodeVersion = @occupationCodeVersion,
           naicsOverride = @naicsOverride, metalTasks = @metalTasks, ppeNotes = @ppeNotes, hoursPerWeek = @hoursPerWeek,
           startDate = @startDate, endDate = @endDate, source = @source, verifiedDate = @verifiedDate,
           isDeleted = @isDeleted
     WHERE employmentId = @employmentId AND personId = @personId AND isDeleted = 0
       AND (@rowVersion IS NULL OR rowVersion = @rowVersion);
    {CONCURRENCY.replace(chr(10), chr(10) + '    ')}
END
ELSE
BEGIN
    INSERT dbo.employment (employmentId, rootTenantId, owningTenantId, personId, organizationId, worksiteLocationId,
                           jobTitle, industryText, occupationCode, occupationCodeVersion, naicsOverride, metalTasks,
                           ppeNotes, hoursPerWeek, startDate, endDate, source, verifiedDate)
    SELECT @employmentId, rootTenantId, owningTenantId, @personId, @organizationId, @worksiteLocationId, @jobTitle,
           @industryText, @occupationCode, @occupationCodeVersion, @naicsOverride, @metalTasks, @ppeNotes,
           @hoursPerWeek, @startDate, @endDate, @source, @verifiedDate
      FROM dbo.person WHERE personId = @personId;
END;

DECLARE @analytes TABLE (analyteId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @analytes SELECT DISTINCT TRY_CAST ([value] AS UNIQUEIDENTIFIER) FROM OPENJSON (COALESCE (@analyteIdsJson, N'[]'))
                  WHERE TRY_CAST ([value] AS UNIQUEIDENTIFIER) IS NOT NULL;

UPDATE ea SET isDeleted = 1
  FROM dbo.employmentAnalyte AS ea
 WHERE ea.employmentId = @employmentId AND ea.isDeleted = 0
   AND (@isDeleted = 1 OR NOT EXISTS (SELECT 1 FROM @analytes AS a WHERE a.analyteId = ea.analyteId));

INSERT dbo.employmentAnalyte (employmentAnalyteId, rootTenantId, owningTenantId, personId, employmentId, analyteId)
SELECT {NEWID}, p.rootTenantId, p.owningTenantId, @personId, @employmentId, a.analyteId
  FROM @analytes AS a
  JOIN dbo.person AS p ON p.personId = @personId
 WHERE @isDeleted = 0
   AND NOT EXISTS (SELECT 1 FROM dbo.employmentAnalyte AS ea WHERE ea.employmentId = @employmentId
                                                             AND ea.analyteId = a.analyteId AND ea.isDeleted = 0);

SET @Comments = N'Employment saved.';
""",
    },
    {
        "schema": "dbo", "name": "uspSaveExposureSource", "kind": "write", "grants": BOTH, "ticket": "T043",
        "permission": "person.edit",
        "desc": "Adds, changes or removes a non-occupational exposure source.",
        "params": """@exposureSourceId  UNIQUEIDENTIFIER
, @personId          UNIQUEIDENTIFIER
, @exposureCaseId    UNIQUEIDENTIFIER = NULL
, @sourceType        VARCHAR (30)
, @sourceDescription NVARCHAR (1000) = NULL
, @productId         UNIQUEIDENTIFIER = NULL
, @locationId        UNIQUEIDENTIFIER = NULL
, @startDate         DATE = NULL
, @endDate           DATE = NULL
, @isConfirmed       BIT = 0
, @isDeleted         BIT = 0""",
        "key": "CONCAT (N'exposureSourceId=', @exposureSourceId, N', personId=', @personId)",
        "validate": PERSON_VISIBLE,
        "body": """
IF EXISTS (SELECT 1 FROM dbo.exposureSource WHERE exposureSourceId = @exposureSourceId)
BEGIN
    UPDATE dbo.exposureSource
       SET exposureCaseId = @exposureCaseId, sourceType = @sourceType, sourceDescription = @sourceDescription,
           productId = @productId, locationId = @locationId, startDate = @startDate, endDate = @endDate,
           isConfirmed = @isConfirmed, isDeleted = @isDeleted
     WHERE exposureSourceId = @exposureSourceId AND personId = @personId AND isDeleted = 0;
END
ELSE
BEGIN
    INSERT dbo.exposureSource (exposureSourceId, rootTenantId, owningTenantId, personId, exposureCaseId, sourceType,
                               sourceDescription, productId, locationId, startDate, endDate, isConfirmed)
    SELECT @exposureSourceId, rootTenantId, owningTenantId, @personId, @exposureCaseId, @sourceType, @sourceDescription,
           @productId, @locationId, @startDate, @endDate, @isConfirmed
      FROM dbo.person WHERE personId = @personId;
END;

SET @Comments = N'Exposure source saved.';
""",
    },
    {
        "schema": "dbo", "name": "uspSearchProducts", "kind": "read", "grants": BOTH, "ticket": "T043",
        "permission": "person.view",
        "desc": "Product registry type-ahead by brand or product name.",
        "params": """@text    NVARCHAR (120)
, @maxRows INT = 10""",
        "key": "CONCAT (N'maxRows=', @maxRows)",
        "body": """
SELECT TOP (@maxRows) productId, brand, productName, countryOfOrigin, productType
  FROM dbo.product
 WHERE isDeleted = 0 AND (productName LIKE N'%' + @text + N'%' OR brand LIKE N'%' + @text + N'%')
 ORDER BY productName;
""",
    },
    {
        "schema": "dbo", "name": "uspSaveProduct", "kind": "write", "grants": BOTH, "ticket": "T043",
        "permission": "person.edit",
        "desc": "Adds or changes a consumer product in the product registry.",
        "params": """@productId       UNIQUEIDENTIFIER
, @brand           NVARCHAR (120) = NULL
, @productName     NVARCHAR (200)
, @countryOfOrigin CHAR (2) = NULL
, @productType     VARCHAR (30) = NULL
, @notes           NVARCHAR (1000) = NULL""",
        "key": "CONCAT (N'productId=', @productId)",
        "body": """
DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();

IF EXISTS (SELECT 1 FROM dbo.product WHERE productId = @productId)
BEGIN
    UPDATE dbo.product
       SET brand = @brand, productName = @productName, countryOfOrigin = @countryOfOrigin, productType = @productType,
           notes = @notes
     WHERE productId = @productId AND isDeleted = 0;
END
ELSE
BEGIN
    INSERT dbo.product (productId, rootTenantId, owningTenantId, brand, productName, countryOfOrigin, productType, notes)
    VALUES (@productId, @root, @root, @brand, @productName, @countryOfOrigin, @productType, @notes);
END;

SET @Comments = N'Product saved.';
""",
    },
    {
        "schema": "dbo", "name": "uspSearchProviders", "kind": "read", "grants": BOTH, "ticket": "T085",
        "permission": "person.view",
        "desc": "Provider type-ahead by NPI or by last name.",
        "params": """@text    NVARCHAR (60)
, @maxRows INT = 10""",
        "key": "CONCAT (N'maxRows=', @maxRows)",
        "body": """
SELECT TOP (@maxRows) pv.providerId, pv.npi, pv.lastName, pv.firstName, pv.credential, pv.registryStatus,
       l.city AS practiceCity, l.state AS practiceState
  FROM dbo.provider AS pv
  LEFT JOIN dbo.location AS l ON l.locationId = pv.practiceLocationId
 WHERE pv.isDeleted = 0 AND (pv.npi = @text OR pv.lastName LIKE @text + N'%')
 ORDER BY pv.lastName, pv.firstName;
""",
    },
    {
        "schema": "dbo", "name": "uspGetProviderByNpi", "kind": "read", "grants": BOTH, "ticket": "T171",
        "permission": "person.view",
        "desc": "The provider with exactly this NPI, for linking the ordering provider on an incoming result. A seek on "
                "IX_dbo_provider_npi_rootTenantId; uspSearchProviders is the type-ahead.",
        "params": "@npi VARCHAR (10)",
        "key": "CONCAT (N'npi=', @npi)",
        "body": """
SELECT pv.providerId, pv.npi, pv.lastName, pv.firstName, pv.credential, pv.registryStatus,
       l.city AS practiceCity, l.state AS practiceState
  FROM dbo.provider AS pv
  LEFT JOIN dbo.location AS l ON l.locationId = pv.practiceLocationId
 WHERE pv.isDeleted = 0 AND pv.npi IS NOT NULL AND pv.npi = @npi;
""",
    },
    {
        "schema": "dbo", "name": "uspSaveProvider", "kind": "write", "grants": BOTH, "ticket": "T043",
        "permission": "person.edit",
        "desc": "Adds or changes an individual practitioner. An NPI, when given, is unique within the root tenant and has "
                "already passed the Luhn check in the application.",
        "params": """@providerId         UNIQUEIDENTIFIER
, @npi                VARCHAR (10) = NULL
, @lastName           NVARCHAR (60)
, @firstName          NVARCHAR (60) = NULL
, @credential         NVARCHAR (20) = NULL
, @practiceLocationId UNIQUEIDENTIFIER = NULL""",
        "key": "CONCAT (N'providerId=', @providerId)",
        "body": """
DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();

IF @npi IS NOT NULL AND EXISTS (SELECT 1 FROM dbo.provider WHERE npi = @npi AND providerId <> @providerId AND isDeleted = 0)
BEGIN
    ;THROW 51422, N'A provider with that NPI already exists.', 1;
END;

IF EXISTS (SELECT 1 FROM dbo.provider WHERE providerId = @providerId)
BEGIN
    UPDATE dbo.provider
       SET npi = @npi, lastName = @lastName, firstName = @firstName, credential = @credential,
           practiceLocationId = @practiceLocationId
     WHERE providerId = @providerId AND isDeleted = 0;
END
ELSE
BEGIN
    INSERT dbo.provider (providerId, rootTenantId, owningTenantId, npi, lastName, firstName, credential, practiceLocationId)
    VALUES (@providerId, @root, @root, @npi, @lastName, @firstName, @credential, @practiceLocationId);
END;

SET @Comments = N'Provider saved.';
""",
    },
]


PROCS += [
    {
        "schema": "dbo", "name": "uspListReporters", "kind": "read", "grants": WEB, "ticket": "T163",
        "permission": "person.view",
        "desc": "The reporter registry: laboratories and facilities (organizations with a CLIA number or NPI) and "
                "individual providers, filtered by name or identifier and by kind, for the Reporters screen.",
        "params": """@text    NVARCHAR (120) = NULL
, @kind    VARCHAR (20) = NULL
, @maxRows INT = 200""",
        "key": "CONCAT (N'kind=', @kind, N', maxRows=', @maxRows)",
        "body": """
DECLARE @t NVARCHAR (120) = NULLIF (LTRIM (RTRIM (@text)), N'');

SELECT TOP (@maxRows) x.reporterKind, x.reporterId, x.reporterName, x.cliaNumber, x.npi, x.credential, x.phone,
       x.registryStatus, x.modifiedDateUtc
  FROM (SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.organizationType AS ot WHERE ot.organizationId = o.organizationId
                                                                         AND ot.organizationTypeCode = 'laboratory'
                                                                         AND ot.isDeleted = 0)
                    THEN 'laboratory' ELSE 'facility' END AS reporterKind,
               o.organizationId AS reporterId, o.organizationName AS reporterName, o.cliaNumber, o.npi,
               CAST (NULL AS NVARCHAR (20)) AS credential, o.phone, CAST (NULL AS VARCHAR (20)) AS registryStatus,
               o.modifiedDateUtc
          FROM dbo.organization AS o
         WHERE o.isDeleted = 0 AND o.isActive = 1 AND (o.cliaNumber IS NOT NULL OR o.npi IS NOT NULL)
           AND (@t IS NULL OR o.organizationName LIKE N'%' + @t + N'%' OR o.cliaNumber = @t OR o.npi = @t)
        UNION ALL
        SELECT 'provider', pv.providerId, CONCAT (pv.lastName, CASE WHEN pv.firstName IS NULL THEN N'' ELSE N', ' + pv.firstName END),
               NULL, pv.npi, pv.credential, NULL, pv.registryStatus, pv.modifiedDateUtc
          FROM dbo.provider AS pv
         WHERE pv.isDeleted = 0
           AND (@t IS NULL OR pv.lastName LIKE @t + N'%' OR pv.firstName LIKE @t + N'%' OR pv.npi = @t)) AS x
 WHERE @kind IS NULL OR x.reporterKind = @kind
 ORDER BY x.reporterKind, x.reporterName;
""",
    },
    {
        "schema": "dbo", "name": "uspImportReporters", "kind": "write", "grants": WEB, "ticket": "T163",
        "permission": "registry.edit",
        "desc": "Adds or updates registry entries from checked rows (JSON): laboratories and facilities are matched on "
                "CLIA number, then NPI; providers on NPI. Blank fields never erase what is stored. Returns how many "
                "were added and updated.",
        "params": "@rowsJson NVARCHAR (MAX)",
        "key": "CONCAT (N'rows=', (SELECT COUNT (*) FROM OPENJSON (@rowsJson)))",
        "validate": """IF ISJSON (@rowsJson) = 0
BEGIN
    ;THROW 50000, N'@rowsJson must be a JSON array of registry rows.', 1;
END;""",
        "notes": "The application has already checked identifier formats (NPI Luhn check digit, CLIA pattern) and "
                 "removed duplicate rows; this procedure trusts the shape and decides only add versus update. "
                 "Laboratories and facilities are both organizations, so unmatched rows that share a CLIA number or "
                 "an NPI, directly or through another row, become one new organization with each row's type "
                 "(issue #11).",
        "body": f"""
DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();
DECLARE @rows TABLE (rowNumber INT PRIMARY KEY, reporterKind VARCHAR (20), reporterName NVARCHAR (120),
                     nameKey NVARCHAR (120), lastName NVARCHAR (60), firstName NVARCHAR (60), credential NVARCHAR (20),
                     npi VARCHAR (10), cliaNumber VARCHAR (10), phone NVARCHAR (30), existingId UNIQUEIDENTIFIER,
                     newId UNIQUEIDENTIFIER, groupRow INT);

INSERT @rows (rowNumber, reporterKind, reporterName, nameKey, lastName, firstName, credential, npi, cliaNumber, phone)
SELECT j.rowNumber, j.reporterKind, j.reporterName, j.nameKey, j.lastName, NULLIF (j.firstName, N''),
       NULLIF (j.credential, N''), NULLIF (j.npi, ''), NULLIF (j.cliaNumber, ''), NULLIF (j.phone, N'')
  FROM OPENJSON (@rowsJson)
  WITH (rowNumber INT '$.rowNumber', reporterKind VARCHAR (20) '$.kind', reporterName NVARCHAR (120) '$.name',
        nameKey NVARCHAR (120) '$.nameKey', lastName NVARCHAR (60) '$.lastName', firstName NVARCHAR (60) '$.firstName',
        credential NVARCHAR (20) '$.credential', npi VARCHAR (10) '$.npi', cliaNumber VARCHAR (10) '$.clia',
        phone NVARCHAR (30) '$.phone') AS j
 WHERE j.reporterKind IN ('laboratory', 'facility', 'provider');

-- ------------------------------------------------------------------------------------------ organizations
-- CLIA first, then NPI for the rows still unmatched: each is a seek on its own index, where one ORed condition
-- scanned the organizations for every row.
UPDATE r SET existingId = m.organizationId
  FROM @rows AS r
 CROSS APPLY (SELECT TOP (1) o.organizationId FROM dbo.organization AS o
               WHERE o.isDeleted = 0 AND o.cliaNumber IS NOT NULL AND o.cliaNumber = r.cliaNumber
               ORDER BY o.createdDateUtc, o.organizationId) AS m
 WHERE r.reporterKind IN ('laboratory', 'facility') AND r.cliaNumber IS NOT NULL;

UPDATE r SET existingId = m.organizationId
  FROM @rows AS r
 CROSS APPLY (SELECT TOP (1) o.organizationId FROM dbo.organization AS o
               WHERE o.isDeleted = 0 AND o.npi IS NOT NULL AND o.npi = r.npi
               ORDER BY o.createdDateUtc, o.organizationId) AS m
 WHERE r.reporterKind IN ('laboratory', 'facility') AND r.existingId IS NULL AND r.npi IS NOT NULL;

UPDATE o
   SET organizationName = r.reporterName, organizationNameKey = r.nameKey, npi = COALESCE (r.npi, o.npi),
       cliaNumber = COALESCE (r.cliaNumber, o.cliaNumber), phone = COALESCE (r.phone, o.phone), isActive = 1
  FROM dbo.organization AS o
  JOIN @rows AS r ON r.existingId = o.organizationId;

UPDATE @rows SET newId = {NEWID}, groupRow = rowNumber WHERE existingId IS NULL;

-- Unmatched organization rows that share a CLIA number or an NPI, directly or through a chain of rows, form one group
-- led by its first row: one new organization per identifier, never two (issue #11).
DECLARE @moved INT = 1;
WHILE @moved > 0
BEGIN
    UPDATE r SET groupRow = m.groupRow
      FROM @rows AS r
     CROSS APPLY (SELECT MIN (o.groupRow) AS groupRow FROM @rows AS o
                   WHERE o.existingId IS NULL AND o.reporterKind IN ('laboratory', 'facility')
                     AND ((o.cliaNumber = r.cliaNumber) OR (o.npi = r.npi))) AS m
     WHERE r.existingId IS NULL AND r.reporterKind IN ('laboratory', 'facility') AND m.groupRow < r.groupRow;
    SET @moved = @@ROWCOUNT;
END;

UPDATE r SET newId = l.newId
  FROM @rows AS r
  JOIN @rows AS l ON l.rowNumber = r.groupRow
 WHERE r.existingId IS NULL AND r.reporterKind IN ('laboratory', 'facility');

-- The group's first row names the organization; the identifiers come from whichever rows carry them.
INSERT dbo.organization (organizationId, rootTenantId, owningTenantId, organizationName, organizationNameKey, npi,
                         cliaNumber, phone, isActive)
SELECT l.newId, @root, @root, l.reporterName, l.nameKey,
       COALESCE (l.npi, (SELECT MIN (g.npi) FROM @rows AS g WHERE g.groupRow = l.rowNumber AND g.reporterKind IN ('laboratory', 'facility'))),
       COALESCE (l.cliaNumber, (SELECT MIN (g.cliaNumber) FROM @rows AS g WHERE g.groupRow = l.rowNumber AND g.reporterKind IN ('laboratory', 'facility'))),
       COALESCE (l.phone, (SELECT MIN (g.phone) FROM @rows AS g WHERE g.groupRow = l.rowNumber AND g.reporterKind IN ('laboratory', 'facility'))), 1
  FROM @rows AS l
 WHERE l.existingId IS NULL AND l.reporterKind IN ('laboratory', 'facility') AND l.groupRow = l.rowNumber;
DECLARE @addedOrganizations INT = @@ROWCOUNT;

INSERT dbo.organizationType (organizationTypeId, rootTenantId, owningTenantId, organizationId, organizationTypeCode)
SELECT {NEWID}, @root, @root, d.organizationId, d.organizationTypeCode
  FROM (SELECT DISTINCT COALESCE (r.existingId, r.newId) AS organizationId,
               CASE r.reporterKind WHEN 'laboratory' THEN 'laboratory' ELSE 'providerPractice' END AS organizationTypeCode
          FROM @rows AS r
         WHERE r.reporterKind IN ('laboratory', 'facility')) AS d
 WHERE NOT EXISTS (SELECT 1 FROM dbo.organizationType AS t
                    WHERE t.organizationId = d.organizationId AND t.isDeleted = 0
                      AND t.organizationTypeCode = d.organizationTypeCode);

-- ------------------------------------------------------------------------------------------ providers
UPDATE r SET existingId = pv.providerId
  FROM @rows AS r
  JOIN dbo.provider AS pv ON pv.npi = r.npi AND pv.isDeleted = 0
 WHERE r.reporterKind = 'provider';

UPDATE pv
   SET lastName = r.lastName, firstName = COALESCE (r.firstName, pv.firstName),
       credential = COALESCE (r.credential, pv.credential)
  FROM dbo.provider AS pv
  JOIN @rows AS r ON r.existingId = pv.providerId AND r.reporterKind = 'provider';

-- One new provider per NPI: the first row wins.
INSERT dbo.provider (providerId, rootTenantId, owningTenantId, npi, lastName, firstName, credential)
SELECT r.newId, @root, @root, r.npi, r.lastName, r.firstName, r.credential
  FROM @rows AS r
 WHERE r.existingId IS NULL AND r.reporterKind = 'provider'
   AND NOT EXISTS (SELECT 1 FROM @rows AS e WHERE e.reporterKind = 'provider' AND e.existingId IS NULL AND e.npi = r.npi
                                              AND e.rowNumber < r.rowNumber);

-- Added counts the new registry entries; every other row updated one, including rows merged into a new organization.
DECLARE @added INT = @addedOrganizations + @@ROWCOUNT;
DECLARE @updated INT = (SELECT COUNT (*) FROM @rows) - @added;
SELECT @added AS addedCount, @updated AS updatedCount;

SET @Comments = CONCAT (N'Registry import: ', @added, N' added, ', @updated, N' updated.');
""",
    },
]
