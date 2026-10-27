"""Configuration and reference data procedures (schema config)."""
from procs_common import NEWID, WEB, BOTH, CONCURRENCY

PROCS = [
    {
        "schema": "config", "name": "uspGetReferenceData", "kind": "read", "grants": BOTH, "ticket": "T050",
        "desc": "All shipped reference data in one call, cached by the application: analytes, analyte specimens, accepted "
                "units, LOINC codes, action codes and lookup values. Drives the cascading result form and every "
                "drop-down.",
        "params": "",
        "key": "NULL",
        "notes": "Six result sets, in the order listed. System reference data carries no row-level security.",
        "body": """
SELECT analyteId, code, displayName, isActive, sortOrder, extractTargets
  FROM config.analyte WHERE isDeleted = 0 ORDER BY sortOrder, displayName;

SELECT analyteSpecimenId, analyteId, specimen, canonicalUnit, usesSampleType, requiresCreatinine, speciationOptions, isActive
  FROM config.analyteSpecimen WHERE isDeleted = 0;

SELECT acceptedUnitId, analyteSpecimenId, unitText, multiplierToCanonical, sortOrder
  FROM config.acceptedUnit WHERE isDeleted = 0 ORDER BY analyteSpecimenId, sortOrder, unitText;

SELECT loincCodeId, analyteSpecimenId, loincCode, impliedSampleType, impliedSpeciation, isDiscouraged, loincDescription
  FROM config.loincCode WHERE isDeleted = 0;

SELECT actionCodeId, code, displayName, queueCode FROM config.actionCode WHERE isDeleted = 0;

SELECT lookupValueId, lookupType, code, displayName, inverseLabel, sortOrder, isActive, cblsCode, ablesCode
  FROM config.lookupValue WHERE isDeleted = 0 ORDER BY lookupType, sortOrder, displayName;
""",
    },
    {
        "schema": "config", "name": "uspGetThresholds", "kind": "read", "grants": BOTH, "ticket": "T050",
        "desc": "Threshold rows of the caller's root tenant. With @asOfDate, only active rows in force on that collection "
                "date (what classification compares against); without it, every row for the configuration screen.",
        "params": """@analyteSpecimenId UNIQUEIDENTIFIER = NULL
, @asOfDate          DATE = NULL""",
        "key": "CONCAT (N'analyteSpecimenId=', @analyteSpecimenId, N', asOfDate=', @asOfDate)",
        "body": """
SELECT th.thresholdId, th.analyteSpecimenId, a.code AS analyteCode, a.displayName AS analyteName, s.specimen,
       th.population, th.ageMinMonths, th.ageMaxMonths, th.speciation, th.sampleType, th.thresholdType,
       th.actionCodeId, ac.code AS actionCode, ac.displayName AS actionName, ac.queueCode, th.dueIntervalDays,
       th.lowerValue, th.upperValue, th.unit, th.comparator, th.effectiveFrom, th.effectiveTo, th.authority, th.isActive,
       th.rowVersion
  FROM config.threshold       AS th
  JOIN config.analyteSpecimen AS s  ON s.analyteSpecimenId = th.analyteSpecimenId
  JOIN config.analyte         AS a  ON a.analyteId = s.analyteId
  LEFT JOIN config.actionCode AS ac ON ac.actionCodeId = th.actionCodeId
 WHERE th.isDeleted = 0
   AND (@analyteSpecimenId IS NULL OR th.analyteSpecimenId = @analyteSpecimenId)
   AND (@asOfDate IS NULL OR (th.isActive = 1 AND th.effectiveFrom <= @asOfDate
                              AND (th.effectiveTo IS NULL OR th.effectiveTo >= @asOfDate)))
 ORDER BY a.sortOrder, s.specimen, th.population, th.thresholdType, th.lowerValue;
""",
    },
    {
        "schema": "config", "name": "uspSaveThreshold", "kind": "write", "grants": WEB, "ticket": "T050",
        "permission": "config.edit",
        "desc": "Creates or updates a threshold row. Rows for the same analyte specimen, population, age band, speciation, "
                "sample type, type and action code may not overlap in effective dates, so tiered action levels run side "
                "by side while a changed level is a new effective-dated row.",
        "params": """@thresholdId       UNIQUEIDENTIFIER
, @analyteSpecimenId UNIQUEIDENTIFIER
, @population        VARCHAR (10)
, @ageMinMonths      INT = NULL
, @ageMaxMonths      INT = NULL
, @speciation        VARCHAR (20) = NULL
, @sampleType        VARCHAR (20) = NULL
, @thresholdType     VARCHAR (12)
, @actionCodeId      UNIQUEIDENTIFIER = NULL
, @dueIntervalDays   INT = NULL
, @lowerValue        DECIMAL (18, 6)
, @upperValue        DECIMAL (18, 6) = NULL
, @unit              NVARCHAR (20)
, @comparator        VARCHAR (2) = '>='
, @effectiveFrom     DATE
, @effectiveTo       DATE = NULL
, @authority         NVARCHAR (300) = NULL
, @isActive          BIT = 1
, @rowVersion        BINARY (8) = NULL""",
        "key": "CONCAT (N'thresholdId=', @thresholdId)",
        "body": """
DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();

IF @isActive = 1 AND EXISTS (
    SELECT 1 FROM config.threshold AS t WITH (UPDLOCK, HOLDLOCK)
     WHERE t.isDeleted = 0 AND t.isActive = 1 AND t.thresholdId <> @thresholdId
       AND t.analyteSpecimenId = @analyteSpecimenId AND t.population = @population
       AND t.thresholdType = @thresholdType
       AND t.ageMinMonths IS NOT DISTINCT FROM @ageMinMonths AND t.ageMaxMonths IS NOT DISTINCT FROM @ageMaxMonths
       AND t.speciation IS NOT DISTINCT FROM @speciation AND t.sampleType IS NOT DISTINCT FROM @sampleType
       AND t.actionCodeId IS NOT DISTINCT FROM @actionCodeId
       AND t.lowerValue = @lowerValue
       AND t.effectiveFrom <= COALESCE (@effectiveTo, '9999-12-31')
       AND COALESCE (t.effectiveTo, '9999-12-31') >= @effectiveFrom)
BEGIN
    ;THROW 51422, N'An active row with the same analyte, population, band, type and action already covers part of those dates. End that row first.', 1;
END;

IF EXISTS (SELECT 1 FROM config.threshold WHERE thresholdId = @thresholdId)
BEGIN
    UPDATE config.threshold
       SET population = @population, ageMinMonths = @ageMinMonths, ageMaxMonths = @ageMaxMonths,
           speciation = @speciation, sampleType = @sampleType, thresholdType = @thresholdType,
           actionCodeId = @actionCodeId, dueIntervalDays = @dueIntervalDays, lowerValue = @lowerValue,
           upperValue = @upperValue, unit = @unit, comparator = @comparator, effectiveFrom = @effectiveFrom,
           effectiveTo = @effectiveTo, authority = @authority, isActive = @isActive
     WHERE thresholdId = @thresholdId AND isDeleted = 0 AND rowVersion = @rowVersion;
    {CONCURRENCY}
END
ELSE
BEGIN
    INSERT config.threshold (thresholdId, rootTenantId, analyteSpecimenId, population, ageMinMonths, ageMaxMonths,
                             speciation, sampleType, thresholdType, actionCodeId, dueIntervalDays, lowerValue, upperValue,
                             unit, comparator, effectiveFrom, effectiveTo, authority, isActive)
    VALUES (@thresholdId, @root, @analyteSpecimenId, @population, @ageMinMonths, @ageMaxMonths, @speciation, @sampleType,
            @thresholdType, @actionCodeId, @dueIntervalDays, @lowerValue, @upperValue, @unit, @comparator, @effectiveFrom,
            @effectiveTo, @authority, @isActive);
END;

SET @Comments = N'Threshold saved.';
""".replace("{CONCURRENCY}", CONCURRENCY.replace("\n", "\n    ")),
    },
    {
        "schema": "config", "name": "uspGetSettings", "kind": "read", "grants": BOTH, "ticket": "T050",
        "desc": "Jurisdiction settings of the caller's root tenant in force on @asOfDate (default today); with "
                "@includeHistory, every row so the screen can show what changed and when.",
        "params": """@asOfDate       DATE = NULL
, @includeHistory BIT = 0""",
        "key": "CONCAT (N'asOfDate=', @asOfDate, N', includeHistory=', @includeHistory)",
        "body": """
DECLARE @d DATE = COALESCE (@asOfDate, auth.udfSessionToday ());

SELECT s.jurisdictionSettingId, s.settingKey, s.settingValue, s.effectiveFrom, s.effectiveTo, s.settingDescription,
       s.modifiedBy, s.modifiedDateUtc
  FROM config.jurisdictionSetting AS s
 WHERE s.isDeleted = 0
   AND (@includeHistory = 1 OR (s.effectiveFrom <= @d AND (s.effectiveTo IS NULL OR s.effectiveTo >= @d)))
 ORDER BY s.settingKey, s.effectiveFrom DESC;
""",
    },
    {
        "schema": "config", "name": "uspSaveSetting", "kind": "write", "grants": WEB, "ticket": "T050",
        "permission": "config.edit",
        "desc": "Changes a jurisdiction setting from a date: the row in force is ended the day before and a new row starts, "
                "so every change is effective-dated and kept.",
        "params": """@settingKey    VARCHAR (80)
, @settingValue  NVARCHAR (2000)
, @effectiveFrom DATE""",
        "key": "CONCAT (N'settingKey=', @settingKey, N', effectiveFrom=', @effectiveFrom)",
        "body": f"""
DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();
DECLARE @desc NVARCHAR (500);

SELECT TOP (1) @desc = settingDescription
  FROM config.jurisdictionSetting WITH (UPDLOCK, HOLDLOCK)
 WHERE settingKey = @settingKey AND isDeleted = 0
 ORDER BY effectiveFrom DESC;

-- A same-day change replaces the value; a later change ends the current row.
UPDATE config.jurisdictionSetting
   SET isDeleted = 1
 WHERE settingKey = @settingKey AND isDeleted = 0 AND effectiveFrom >= @effectiveFrom;

UPDATE config.jurisdictionSetting
   SET effectiveTo = DATEADD (DAY, -1, @effectiveFrom)
 WHERE settingKey = @settingKey AND isDeleted = 0 AND effectiveFrom < @effectiveFrom
   AND (effectiveTo IS NULL OR effectiveTo >= @effectiveFrom);

INSERT config.jurisdictionSetting (jurisdictionSettingId, rootTenantId, settingKey, settingValue, effectiveFrom,
                                   settingDescription)
VALUES ({NEWID}, @root, @settingKey, @settingValue, @effectiveFrom, @desc);

SET @Comments = N'Setting changed.';
""",
    },
    {
        "schema": "config", "name": "uspSaveAnalyte", "kind": "write", "grants": WEB, "ticket": "T050",
        "permission": "config.edit",
        "desc": "Turns an analyte on or off and sets its drop-down order. Inactive analytes stay on old records.",
        "params": """@analyteId UNIQUEIDENTIFIER
, @isActive  BIT
, @sortOrder INT""",
        "key": "CONCAT (N'analyteId=', @analyteId)",
        "body": """
UPDATE config.analyte SET isActive = @isActive, sortOrder = @sortOrder WHERE analyteId = @analyteId AND isDeleted = 0;
SET @Comments = CONCAT (@@ROWCOUNT, N' analyte(s) updated.');
""",
    },
    {
        "schema": "config", "name": "uspListSubJurisdictions", "kind": "read", "grants": BOTH, "ticket": "T020",
        "desc": "Counties and other sub-jurisdictions of the caller's root, with the tenants that serve them (names for "
                "display, ids for choosing among a parent's counties).",
        "params": "",
        "key": "NULL",
        "body": """
SELECT sj.subJurisdictionId, sj.countyFips, sj.subJurisdictionName, sj.jurisdictionType,
       STRING_AGG (t.tenantName, N', ') AS servedBy,
       STRING_AGG (CAST (t.tenantId AS NVARCHAR (36)), N',') AS servedByTenantIds
  FROM config.subJurisdiction AS sj
  LEFT JOIN auth.tenantJurisdiction AS tj ON tj.subJurisdictionId = sj.subJurisdictionId AND tj.isDeleted = 0
  LEFT JOIN auth.tenant AS t ON t.tenantId = tj.tenantId
 WHERE sj.isDeleted = 0
 GROUP BY sj.subJurisdictionId, sj.countyFips, sj.subJurisdictionName, sj.jurisdictionType
 ORDER BY sj.subJurisdictionName;
""",
    },
    {
        "schema": "config", "name": "uspGetEnvironmentalStandards", "kind": "read", "grants": BOTH, "ticket": "T046",
        "desc": "Environmental standards of the caller's root tenant, current and past.",
        "params": "",
        "key": "NULL",
        "body": """
SELECT environmentalStandardId, sampleType, surface, standardValue, unit, authority, effectiveFrom, effectiveTo
  FROM config.environmentalStandard
 WHERE isDeleted = 0
 ORDER BY sampleType, surface, effectiveFrom DESC;
""",
    },
    {
        "schema": "config", "name": "uspGetExtractLayout", "kind": "read", "grants": BOTH, "ticket": "T052",
        "desc": "The active layout for a federal extract and its fields in order.",
        "params": "@extractType VARCHAR (10)",
        "key": "CONCAT (N'extractType=', @extractType)",
        "body": """
DECLARE @id UNIQUEIDENTIFIER = (SELECT TOP (1) extractLayoutId FROM config.extractLayout
                                 WHERE extractType = @extractType AND isActive = 1 AND isDeleted = 0
                                 ORDER BY effectiveFrom DESC);

SELECT extractLayoutId, extractType, layoutVersion, fileFormat, delimiter, effectiveFrom
  FROM config.extractLayout WHERE extractLayoutId = @id;

SELECT fieldOrder, fieldName, fieldWidth, sourceKey, padWith, alignRight, isRequired
  FROM config.extractLayoutField
 WHERE extractLayoutId = @id AND isDeleted = 0
 ORDER BY fieldOrder;
""",
    },
]
