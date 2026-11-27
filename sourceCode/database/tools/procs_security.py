"""Behavioral anomaly detection (#327), structured log export (#328), search log retention & reporting (#329),
unified per-user behavior summary & operational logs (#330), and row-change history access/purge (#332).
"""
from procs_common import NEWID, WEB, WORKER, AUDIT_SESSION_VARS

REVIEW_ACCOUNTS = """DECLARE @all BIT = auth.udfHasPermission (N'security.investigate');
DECLARE @accounts TABLE (appUserId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @accounts SELECT appUserId FROM auth.tvfReviewAccounts ('security.review');"""

AUDIT_TENANTS = """DECLARE @allAudit BIT = auth.udfHasPermission (N'security.investigate');
DECLARE @auditTenants TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @auditTenants SELECT tenantId FROM auth.tvfReviewTenants ('audit.view');"""

FAILURES = "'passwordFailed', 'unknownUser', 'mfaFailed', 'attemptWhileLocked', 'accountInactive'"

PROCS = [
    # --------------------------------------------------------------------------------------------- #329 searchLog
    {
        "schema": "logs", "name": "uspPurgeSearchLog", "kind": "write", "grants": WORKER, "ticket": "329",
        "desc": "Scrubs searchText and personal correlation fields and soft-deletes logs.searchLog rows older than "
                "@retentionDays (30 to 3650, default 90) in batches. Worker-only; never hard-deletes (#329).",
        "params": "@retentionDays INT = 90",
        "key": "CONCAT (N'retentionDays=', @retentionDays)",
        "validate": """IF @retentionDays NOT BETWEEN 30 AND 3650
BEGIN
    ;THROW 51422, N'Retention must be from 30 to 3650 days.', 1;
END;""",
        "body": """
DECLARE @cutoff DATETIME2 (3) = DATEADD (DAY, -@retentionDays, SYSUTCDATETIME ());
DECLARE @count INT = 0, @batch INT = 1;
WHILE @batch > 0
BEGIN
    UPDATE TOP (5000) logs.searchLog
       SET searchText       = N''
         , clientAddress    = NULL
         , sessionReference = NULL
         , actingTenantId   = NULL
         , isDeleted        = 1
         , deletedBy        = N'MERIT search log retention'
         , modifiedBy       = N'MERIT search log retention'
     WHERE searchedUtc < @cutoff AND isDeleted = 0;
    SET @batch = @@ROWCOUNT;
    SET @count += @batch;
END;
SELECT @count AS purged;
SET @Comments = CONCAT (N'Scrubbed ', @count, N' search log row(s).');
""",
    },
    {
        "schema": "logs", "name": "uspGetSearchLogSummary", "kind": "read", "grants": WEB, "ticket": "329",
        "permission": ["security.review", "audit.view"],
        "desc": "Scoped per-user search volume, zero-result counts, and outside-scope hit counts from logs.searchLog "
                "over the last @days. Never returns raw searchText (#329).",
        "params": """@days      INT = 30
, @appUserId UNIQUEIDENTIFIER = NULL""",
        "key": "CONCAT (N'days=', @days, N', appUserId=', @appUserId)",
        "body": f"""
DECLARE @since DATETIME2 (3) = DATEADD (DAY, -CASE WHEN @days BETWEEN 1 AND 365 THEN @days ELSE 30 END, SYSUTCDATETIME ());
{REVIEW_ACCOUNTS}
{AUDIT_TENANTS}

SELECT s.appUserId, u.userName, u.displayName,
       COUNT (*) AS totalSearches,
       SUM (CASE WHEN s.resultCount = 0 THEN 1 ELSE 0 END) AS zeroResultSearches,
       SUM (CASE WHEN s.existsElsewhere = 1 THEN 1 ELSE 0 END) AS outsideScopeHits,
       COUNT (DISTINCT s.sessionReference) AS distinctSessions,
       MIN (s.searchedUtc) AS firstUtc,
       MAX (s.searchedUtc) AS lastUtc
  FROM logs.searchLog AS s
  LEFT JOIN auth.appUser AS u ON u.appUserId = s.appUserId
 WHERE s.isDeleted = 0 AND s.searchedUtc >= @since
   AND (@appUserId IS NULL OR s.appUserId = @appUserId)
   AND (@all = 1 OR @allAudit = 1
        OR s.appUserId IN (SELECT appUserId FROM @accounts)
        OR s.actingTenantId IN (SELECT tenantId FROM @auditTenants)
        OR s.rootTenantId IN (SELECT tenantId FROM @auditTenants))
 GROUP BY s.appUserId, u.userName, u.displayName
 ORDER BY totalSearches DESC, lastUtc DESC;
""",
    },
    {
        "schema": "logs", "name": "uspListSearchLogEvents", "kind": "read", "grants": WEB, "ticket": "329",
        "permission": ["security.review", "audit.view"],
        "desc": "Scoped logs.searchLog rows for investigation. Raw searchText is returned ONLY when @includeSearchText = 1 "
                "and the caller holds security.investigate (#329).",
        "params": """@fromUtc           DATETIME2 (3) = NULL
, @toUtc             DATETIME2 (3) = NULL
, @appUserId         UNIQUEIDENTIFIER = NULL
, @includeSearchText BIT = 0
, @top               INT = 200""",
        "key": "CONCAT (N'appUserId=', @appUserId, N', includeSearchText=', @includeSearchText, N', top=', @top)",
        "body": f"""
DECLARE @canSeeText BIT = CASE WHEN @includeSearchText = 1 AND auth.udfHasPermission (N'security.investigate') = 1 THEN 1 ELSE 0 END;
DECLARE @limit INT = CASE WHEN @top BETWEEN 1 AND 1000 THEN @top ELSE 200 END;
{REVIEW_ACCOUNTS}
{AUDIT_TENANTS}

SELECT TOP (@limit)
       s.searchLogId, s.searchedUtc, s.appUserId, u.userName, u.displayName,
       s.rootTenantId, s.actingTenantId, s.sessionReference, s.clientAddress,
       s.resultCount, s.existsElsewhere, s.searchSource,
       CASE WHEN @canSeeText = 1 THEN s.searchText ELSE NULL END AS searchText
  FROM logs.searchLog AS s
  LEFT JOIN auth.appUser AS u ON u.appUserId = s.appUserId
 WHERE s.isDeleted = 0
   AND (@fromUtc IS NULL OR s.searchedUtc >= @fromUtc)
   AND (@toUtc IS NULL OR s.searchedUtc <= @toUtc)
   AND (@appUserId IS NULL OR s.appUserId = @appUserId)
   AND (@all = 1 OR @allAudit = 1
        OR s.appUserId IN (SELECT appUserId FROM @accounts)
        OR s.actingTenantId IN (SELECT tenantId FROM @auditTenants)
        OR s.rootTenantId IN (SELECT tenantId FROM @auditTenants))
 ORDER BY s.searchedUtc DESC, s.searchLogId DESC;
""",
    },

    # --------------------------------------------------------------------------------------------- #332 row-change history
    {
        "schema": "logs", "name": "uspGetAuditEventChanges", "kind": "read", "grants": WEB, "ticket": "332",
        "permission": ["audit.view", "security.review"],
        "desc": "Returns purgeable before/after change snapshots from logs.auditEventChange for an auditEventId within the caller's review scope (#332).",
        "params": "@auditEventId BIGINT",
        "key": "CONCAT (N'auditEventId=', @auditEventId)",
        "body": f"""
{AUDIT_TENANTS}
SELECT c.auditEventChangeId, c.auditEventId, c.rootTenantId, c.owningTenantId,
       c.entityType, c.entityId, c.changedUtc, c.beforeJson, c.afterJson
  FROM logs.auditEventChange AS c
  JOIN logs.auditEvent AS e ON e.auditEventId = c.auditEventId
 WHERE c.auditEventId = @auditEventId AND c.isDeleted = 0
   AND (@allAudit = 1
        OR e.owningTenantId IN (SELECT tenantId FROM @auditTenants)
        OR (e.owningTenantId IS NULL AND e.rootTenantId IN (SELECT tenantId FROM @auditTenants)))
 ORDER BY c.changedUtc, c.auditEventChangeId;
""",
    },
    {
        "schema": "logs", "name": "uspPurgeAuditEventChanges", "kind": "write", "grants": WORKER, "ticket": "332",
        "desc": "Scrubs beforeJson/afterJson and soft-deletes logs.auditEventChange rows older than @retentionDays in batches. Worker-only (#332).",
        "params": "@retentionDays INT = 365",
        "key": "CONCAT (N'retentionDays=', @retentionDays)",
        "validate": """IF @retentionDays NOT BETWEEN 30 AND 3650
BEGIN
    ;THROW 51422, N'Retention must be from 30 to 3650 days.', 1;
END;""",
        "body": """
DECLARE @cutoff DATETIME2 (3) = DATEADD (DAY, -@retentionDays, SYSUTCDATETIME ());
DECLARE @count INT = 0, @batch INT = 1;
WHILE @batch > 0
BEGIN
    UPDATE TOP (5000) logs.auditEventChange
       SET beforeJson = NULL
         , afterJson  = NULL
         , isDeleted  = 1
         , deletedBy  = N'MERIT change history retention'
         , modifiedBy = N'MERIT change history retention'
     WHERE changedUtc < @cutoff AND isDeleted = 0;
    SET @batch = @@ROWCOUNT;
    SET @count += @batch;
END;
SELECT @count AS purged;
SET @Comments = CONCAT (N'Scrubbed ', @count, N' audit change detail row(s).');
""",
    },

    # --------------------------------------------------------------------------------------------- #327 anomaly detection
    {
        "schema": "logs", "name": "uspGetBehaviorAnomalies", "kind": "read", "grants": WEB, "ticket": "327",
        "permission": ["security.review", "tenant.administer"],
        "desc": "Evaluates post-authentication and sign-in behavioral anomaly rules over the last @hours within the "
                "caller's review scope, with configurable thresholds (#327).",
        "params": """@hours                    INT = 24
, @viewVelocityThreshold    INT = 50
, @exportSpikeThreshold     INT = 5
, @exportRowsThreshold      INT = 500
, @probingThreshold         INT = 10
, @searchVelocityThreshold  INT = 40
, @privBurstThreshold       INT = 5
, @mfaAbuseThreshold        INT = 5
, @distributedFailThreshold INT = 10""",
        "key": "CONCAT (N'hours=', @hours)",
        "body": f"""
DECLARE @winHours INT = CASE WHEN @hours BETWEEN 1 AND 2160 THEN @hours ELSE 24 END;
DECLARE @windowEnd DATETIME2 (3) = SYSUTCDATETIME ();
DECLARE @since DATETIME2 (3) = DATEADD (HOUR, -@winHours, @windowEnd);
{REVIEW_ACCOUNTS}

DECLARE @findings TABLE (
    ruleCode         VARCHAR (50),
    severity         VARCHAR (15),
    appUserId        UNIQUEIDENTIFIER NULL,
    userName         NVARCHAR (100) NULL,
    displayName      NVARCHAR (150) NULL,
    clientAddress    VARCHAR (45) NULL,
    sessionReference UNIQUEIDENTIFIER NULL,
    eventCount       INT,
    title            NVARCHAR (200),
    detailJson       NVARCHAR (2000),
    firstUtc         DATETIME2 (3),
    lastUtc          DATETIME2 (3)
);

-- Rule 1: Bulk patient/result/case viewing velocity
INSERT @findings
SELECT 'bulkViewVelocity',
       CASE WHEN COUNT (DISTINCT e.entityId) >= @viewVelocityThreshold * 2 THEN 'high' ELSE 'medium' END,
       e.appUserId, u.userName, u.displayName, NULL, e.sessionReference,
       COUNT (DISTINCT e.entityId),
       CONCAT (N'High distinct record view velocity (', COUNT (DISTINCT e.entityId), N' records in ', @winHours, N'h)'),
       (SELECT COUNT (DISTINCT e.entityId) AS distinctEntities, COUNT (*) AS totalViews, @viewVelocityThreshold AS threshold FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       MIN (e.eventUtc), MAX (e.eventUtc)
  FROM logs.auditEvent AS e
  LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
 WHERE e.eventUtc >= @since AND e.eventAction = 'view' AND e.entityId IS NOT NULL
   AND (@all = 1 OR e.appUserId IN (SELECT appUserId FROM @accounts))
 GROUP BY e.appUserId, u.userName, u.displayName, e.sessionReference
HAVING COUNT (DISTINCT e.entityId) >= @viewVelocityThreshold;

-- Rule 2: Bulk or unusual data exports
INSERT @findings
SELECT 'bulkExportSpike',
       CASE WHEN SUM (COALESCE (e.recordCount, 1)) >= @exportRowsThreshold * 2 OR COUNT (*) >= @exportSpikeThreshold * 2 THEN 'high' ELSE 'medium' END,
       e.appUserId, u.userName, u.displayName, NULL, NULL,
       COUNT (*),
       CONCAT (N'Bulk export spike (', COUNT (*), N' exports, ', SUM (COALESCE (e.recordCount, 0)), N' rows)'),
       (SELECT COUNT (*) AS exportCount, SUM (COALESCE (e.recordCount, 0)) AS totalRows, @exportSpikeThreshold AS countThreshold, @exportRowsThreshold AS rowsThreshold FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       MIN (e.eventUtc), MAX (e.eventUtc)
  FROM logs.auditEvent AS e
  LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
 WHERE e.eventUtc >= @since AND e.eventAction = 'export'
   AND (@all = 1 OR e.appUserId IN (SELECT appUserId FROM @accounts))
 GROUP BY e.appUserId, u.userName, u.displayName
HAVING COUNT (*) >= @exportSpikeThreshold OR SUM (COALESCE (e.recordCount, 0)) >= @exportRowsThreshold;

-- Rule 3: Repeated permissionDenied or outside-scope probing
INSERT @findings
SELECT 'probingDeniedOrOutsideScope',
       CASE WHEN COUNT (*) >= @probingThreshold * 2 THEN 'high' ELSE 'medium' END,
       e.appUserId, u.userName, u.displayName, MAX (e.clientAddress), NULL,
       COUNT (*),
       CONCAT (N'Repeated permission-denied or outside-scope probing (', COUNT (*), N' events)'),
       (SELECT SUM (CASE WHEN e.eventAction = 'permissionDenied' THEN 1 ELSE 0 END) AS permissionDenied,
               SUM (CASE WHEN e.entityType = 'personOutsideScope' THEN 1 ELSE 0 END) AS outsideScope,
               @probingThreshold AS threshold FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       MIN (e.eventUtc), MAX (e.eventUtc)
  FROM logs.auditEvent AS e
  LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
 WHERE e.eventUtc >= @since
   AND (e.eventAction = 'permissionDenied' OR e.entityType = 'personOutsideScope')
   AND (@all = 1 OR e.appUserId IN (SELECT appUserId FROM @accounts))
 GROUP BY e.appUserId, u.userName, u.displayName
HAVING COUNT (*) >= @probingThreshold;

-- Rule 4: High-velocity searchLog querying
INSERT @findings
SELECT 'searchVelocitySpike',
       CASE WHEN COUNT (*) >= @searchVelocityThreshold * 2 THEN 'high' ELSE 'medium' END,
       s.appUserId, u.userName, u.displayName, MAX (s.clientAddress), NULL,
       COUNT (*),
       CONCAT (N'High-velocity searching (', COUNT (*), N' queries, ', SUM (CASE WHEN s.resultCount = 0 THEN 1 ELSE 0 END), N' zero-result)'),
       (SELECT COUNT (*) AS searches, SUM (CASE WHEN s.resultCount = 0 THEN 1 ELSE 0 END) AS zeroResults,
               SUM (CASE WHEN s.existsElsewhere = 1 THEN 1 ELSE 0 END) AS outsideScope, @searchVelocityThreshold AS threshold FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       MIN (s.searchedUtc), MAX (s.searchedUtc)
  FROM logs.searchLog AS s
  LEFT JOIN auth.appUser AS u ON u.appUserId = s.appUserId
 WHERE s.isDeleted = 0 AND s.searchedUtc >= @since
   AND (@all = 1 OR s.appUserId IN (SELECT appUserId FROM @accounts))
 GROUP BY s.appUserId, u.userName, u.displayName
HAVING COUNT (*) >= @searchVelocityThreshold;

-- Rule 5: Mid-session IP drift (possible session hijacking)
INSERT @findings
SELECT 'midSessionIpDrift', 'high',
       e.appUserId, u.userName, u.displayName, NULL, e.sessionReference,
       COUNT (DISTINCT e.clientAddress),
       CONCAT (N'Mid-session IP address change (', COUNT (DISTINCT e.clientAddress), N' distinct addresses in one session)'),
       (SELECT COUNT (DISTINCT e.clientAddress) AS distinctAddresses, COUNT (*) AS events FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       MIN (e.eventUtc), MAX (e.eventUtc)
  FROM logs.auditEvent AS e
  LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
 WHERE e.eventUtc >= @since AND e.sessionReference IS NOT NULL AND e.clientAddress IS NOT NULL
   AND (@all = 1 OR e.appUserId IN (SELECT appUserId FROM @accounts))
 GROUP BY e.appUserId, u.userName, u.displayName, e.sessionReference
HAVING COUNT (DISTINCT e.clientAddress) > 1;

-- Rule 6: Automated / headless User-Agent strings
INSERT @findings
SELECT 'automatedUserAgent', 'medium',
       e.appUserId, u.userName, u.displayName, MAX (e.clientAddress), MAX (e.sessionReference),
       COUNT (*),
       CONCAT (N'Automated or headless client user-agent detected (', COUNT (*), N' sign-in events)'),
       (SELECT COUNT (*) AS events, LEFT (MAX (e.userAgent), 120) AS sampleUserAgent FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       MIN (e.eventUtc), MAX (e.eventUtc)
  FROM logs.signInEvent AS e
  LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
 WHERE e.isDeleted = 0 AND e.eventUtc >= @since AND e.userAgent IS NOT NULL
   AND (   e.userAgent LIKE N'%curl/%'
        OR e.userAgent LIKE N'%python-requests%'
        OR e.userAgent LIKE N'%HeadlessChrome%'
        OR e.userAgent LIKE N'%Playwright%'
        OR e.userAgent LIKE N'%Selenium%'
        OR e.userAgent LIKE N'%Scrapy%'
        OR e.userAgent LIKE N'%wget%'
        OR e.userAgent LIKE N'%Go-http-client%')
   AND (@all = 1 OR e.appUserId IN (SELECT appUserId FROM @accounts))
 GROUP BY e.appUserId, u.userName, u.displayName;

-- Rule 7: Privileged / security-admin action bursts
INSERT @findings
SELECT 'privilegedActionBurst', 'high',
       e.appUserId, u.userName, u.displayName, MAX (e.clientAddress), NULL,
       COUNT (*),
       CONCAT (N'Privileged security/configuration burst (', COUNT (*), N' actions)'),
       (SELECT COUNT (*) AS actions, @privBurstThreshold AS threshold FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       MIN (e.eventUtc), MAX (e.eventUtc)
  FROM logs.auditEvent AS e
  LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
 WHERE e.eventUtc >= @since
   AND (   e.eventAction IN ('grant', 'revoke', 'deactivate', 'mfaResetByAdmin', 'passwordSetByAdmin')
        OR (e.entityType IN ('threshold', 'jurisdictionSetting', 'userAssignment', 'role') AND e.eventAction IN ('create', 'update', 'delete')))
   AND (@all = 1 OR e.appUserId IN (SELECT appUserId FROM @accounts))
 GROUP BY e.appUserId, u.userName, u.displayName
HAVING COUNT (*) >= @privBurstThreshold;

-- Rule 8: MFA fatigue / password-reset abuse
INSERT @findings
SELECT 'mfaOrResetAbuse', 'high',
       e.appUserId, u.userName, u.displayName, MAX (e.clientAddress), NULL,
       COUNT (*),
       CONCAT (N'Repeated MFA or password-reset attempts (', COUNT (*), N' events)'),
       (SELECT COUNT (*) AS events, COUNT (DISTINCT e.clientAddress) AS distinctAddresses, @mfaAbuseThreshold AS threshold FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       MIN (e.eventUtc), MAX (e.eventUtc)
  FROM logs.signInEvent AS e
  LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
 WHERE e.isDeleted = 0 AND e.eventUtc >= @since
   AND e.eventType IN ('mfaFailed', 'mfaEmailCodeSent', 'passwordResetRequested')
   AND (@all = 1 OR e.appUserId IN (SELECT appUserId FROM @accounts))
 GROUP BY e.appUserId, u.userName, u.displayName
HAVING COUNT (*) >= @mfaAbuseThreshold;

-- Rule 9: Distributed low-and-slow failures across IP addresses
INSERT @findings
SELECT 'distributedLowAndSlowFailures', 'medium',
       e.appUserId, u.userName, u.displayName, NULL, NULL,
       COUNT (*),
       CONCAT (N'Distributed low-and-slow sign-in failures (', COUNT (*), N' failures across ', COUNT (DISTINCT e.clientAddress), N' addresses)'),
       (SELECT COUNT (*) AS failures, COUNT (DISTINCT e.clientAddress) AS distinctAddresses, @distributedFailThreshold AS threshold FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       MIN (e.eventUtc), MAX (e.eventUtc)
  FROM logs.signInEvent AS e
  LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
 WHERE e.isDeleted = 0 AND e.eventUtc >= @since
   AND e.eventType IN ({FAILURES})
   AND (@all = 1 OR e.appUserId IN (SELECT appUserId FROM @accounts))
 GROUP BY e.appUserId, u.userName, u.displayName
HAVING COUNT (*) >= @distributedFailThreshold AND COUNT (DISTINCT e.clientAddress) >= 4;

-- Rule 10: API-only data access with zero screen visits (machine-speed / automated scraping)
INSERT @findings
SELECT 'apiOnlyNoScreenVisits', 'medium',
       e.appUserId, u.userName, u.displayName, MAX (e.clientAddress), e.sessionReference,
       COUNT (*),
       CONCAT (N'Data-access session with zero UI screen visits (', COUNT (*), N' reads)'),
       (SELECT COUNT (*) AS readEvents, COUNT (DISTINCT e.entityId) AS distinctEntities FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       MIN (e.eventUtc), MAX (e.eventUtc)
  FROM logs.auditEvent AS e
  LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
 WHERE e.eventUtc >= @since AND e.sessionReference IS NOT NULL
   AND e.eventAction IN ('view', 'list', 'export')
   AND (@all = 1 OR e.appUserId IN (SELECT appUserId FROM @accounts))
   AND NOT EXISTS (SELECT 1 FROM logs.screenVisit AS v
                    WHERE v.sessionReference = e.sessionReference AND v.isDeleted = 0)
 GROUP BY e.appUserId, u.userName, u.displayName, e.sessionReference
HAVING COUNT (*) >= 25;

SELECT ruleCode, severity, appUserId, userName, displayName, clientAddress, sessionReference,
       eventCount, title, detailJson, firstUtc, lastUtc
  FROM @findings
 ORDER BY CASE severity WHEN 'critical' THEN 1 WHEN 'high' THEN 2 WHEN 'medium' THEN 3 ELSE 4 END,
          lastUtc DESC;
""",
    },
    {
        "schema": "logs", "name": "uspRunSecurityFindingScan", "kind": "write", "grants": WORKER, "ticket": "327",
        "desc": "Scheduled worker scan that evaluates behavioral anomaly rules across all active tenants, deduplicates "
                "by fingerprint within the window, persists new findings into logs.securityFinding, and returns newly inserted rows (#327).",
        "params": """@hours                    INT = 24
, @viewVelocityThreshold    INT = 50
, @exportSpikeThreshold     INT = 5
, @exportRowsThreshold      INT = 500
, @probingThreshold         INT = 10
, @searchVelocityThreshold  INT = 40
, @privBurstThreshold       INT = 5
, @mfaAbuseThreshold        INT = 5""",
        "key": "CONCAT (N'hours=', @hours)",
        "body": f"""
DECLARE @winHours INT = CASE WHEN @hours BETWEEN 1 AND 168 THEN @hours ELSE 24 END;
DECLARE @now DATETIME2 (3) = SYSUTCDATETIME ();
DECLARE @since DATETIME2 (3) = DATEADD (HOUR, -@winHours, @now);
DECLARE @dayBucket VARCHAR (10) = CONVERT (VARCHAR (10), @now, 23);

DECLARE @candidates TABLE (
    rootTenantId     UNIQUEIDENTIFIER NOT NULL,
    owningTenantId   UNIQUEIDENTIFIER NULL,
    appUserId        UNIQUEIDENTIFIER NULL,
    clientAddress    VARCHAR (45) NULL,
    sessionReference UNIQUEIDENTIFIER NULL,
    ruleCode         VARCHAR (50) NOT NULL,
    severity         VARCHAR (15) NOT NULL,
    title            NVARCHAR (200) NOT NULL,
    detailJson       NVARCHAR (2000) NULL,
    fingerprint      VARCHAR (128) NOT NULL
);

INSERT @candidates
SELECT e.rootTenantId, MAX (e.actingTenantId), e.appUserId, NULL, e.sessionReference,
       'bulkViewVelocity', CASE WHEN COUNT (DISTINCT e.entityId) >= @viewVelocityThreshold * 2 THEN 'high' ELSE 'medium' END,
       CONCAT (N'High distinct record view velocity (', COUNT (DISTINCT e.entityId), N' records in ', @winHours, N'h)'),
       (SELECT COUNT (DISTINCT e.entityId) AS distinctEntities, COUNT (*) AS totalViews FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       CONCAT ('bulkViewVelocity|', COALESCE (CONVERT (VARCHAR (36), e.appUserId), 'none'), '|', COALESCE (CONVERT (VARCHAR (36), e.sessionReference), @dayBucket))
  FROM logs.auditEvent AS e
 WHERE e.eventUtc >= @since AND e.eventAction = 'view' AND e.entityId IS NOT NULL
 GROUP BY e.rootTenantId, e.appUserId, e.sessionReference
HAVING COUNT (DISTINCT e.entityId) >= @viewVelocityThreshold;

INSERT @candidates
SELECT e.rootTenantId, MAX (e.actingTenantId), e.appUserId, NULL, NULL,
       'bulkExportSpike', CASE WHEN SUM (COALESCE (e.recordCount, 1)) >= @exportRowsThreshold * 2 THEN 'high' ELSE 'medium' END,
       CONCAT (N'Bulk export spike (', COUNT (*), N' exports, ', SUM (COALESCE (e.recordCount, 0)), N' rows)'),
       (SELECT COUNT (*) AS exportCount, SUM (COALESCE (e.recordCount, 0)) AS totalRows FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       CONCAT ('bulkExportSpike|', COALESCE (CONVERT (VARCHAR (36), e.appUserId), 'none'), '|', @dayBucket)
  FROM logs.auditEvent AS e
 WHERE e.eventUtc >= @since AND e.eventAction = 'export'
 GROUP BY e.rootTenantId, e.appUserId
HAVING COUNT (*) >= @exportSpikeThreshold OR SUM (COALESCE (e.recordCount, 0)) >= @exportRowsThreshold;

INSERT @candidates
SELECT e.rootTenantId, MAX (e.actingTenantId), e.appUserId, MAX (e.clientAddress), NULL,
       'probingDeniedOrOutsideScope', CASE WHEN COUNT (*) >= @probingThreshold * 2 THEN 'high' ELSE 'medium' END,
       CONCAT (N'Repeated permission-denied or outside-scope probing (', COUNT (*), N' events)'),
       (SELECT COUNT (*) AS events FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       CONCAT ('probingDeniedOrOutsideScope|', COALESCE (CONVERT (VARCHAR (36), e.appUserId), 'none'), '|', @dayBucket)
  FROM logs.auditEvent AS e
 WHERE e.eventUtc >= @since AND (e.eventAction = 'permissionDenied' OR e.entityType = 'personOutsideScope')
 GROUP BY e.rootTenantId, e.appUserId
HAVING COUNT (*) >= @probingThreshold;

INSERT @candidates
SELECT e.rootTenantId, MAX (e.actingTenantId), e.appUserId, NULL, e.sessionReference,
       'midSessionIpDrift', 'high',
       CONCAT (N'Mid-session IP address change (', COUNT (DISTINCT e.clientAddress), N' addresses)'),
       (SELECT COUNT (DISTINCT e.clientAddress) AS distinctAddresses, COUNT (*) AS events FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       CONCAT ('midSessionIpDrift|', CONVERT (VARCHAR (36), e.sessionReference))
  FROM logs.auditEvent AS e
 WHERE e.eventUtc >= @since AND e.sessionReference IS NOT NULL AND e.clientAddress IS NOT NULL
 GROUP BY e.rootTenantId, e.appUserId, e.sessionReference
HAVING COUNT (DISTINCT e.clientAddress) > 1;

INSERT @candidates
SELECT s.rootTenantId, MAX (s.actingTenantId), s.appUserId, MAX (s.clientAddress), NULL,
       'searchVelocitySpike', CASE WHEN COUNT (*) >= @searchVelocityThreshold * 2 THEN 'high' ELSE 'medium' END,
       CONCAT (N'High-velocity searching (', COUNT (*), N' queries)'),
       (SELECT COUNT (*) AS searches, SUM (CASE WHEN s.resultCount = 0 THEN 1 ELSE 0 END) AS zeroResults FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       CONCAT ('searchVelocitySpike|', CONVERT (VARCHAR (36), s.appUserId), '|', @dayBucket)
  FROM logs.searchLog AS s
 WHERE s.isDeleted = 0 AND s.searchedUtc >= @since
 GROUP BY s.rootTenantId, s.appUserId
HAVING COUNT (*) >= @searchVelocityThreshold;

INSERT @candidates
SELECT COALESCE (e.rootTenantId, '00000000-0000-0000-0000-000000000000'), NULL, e.appUserId, MAX (e.clientAddress), NULL,
       'mfaOrResetAbuse', 'high',
       CONCAT (N'Repeated MFA or password-reset attempts (', COUNT (*), N' events)'),
       (SELECT COUNT (*) AS events, COUNT (DISTINCT e.clientAddress) AS distinctAddresses FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       CONCAT ('mfaOrResetAbuse|', COALESCE (CONVERT (VARCHAR (36), e.appUserId), 'none'), '|', @dayBucket)
  FROM logs.signInEvent AS e
 WHERE e.isDeleted = 0 AND e.eventUtc >= @since AND e.eventType IN ('mfaFailed', 'mfaEmailCodeSent', 'passwordResetRequested')
 GROUP BY e.rootTenantId, e.appUserId
HAVING COUNT (*) >= @mfaAbuseThreshold;

DECLARE @inserted TABLE (
    securityFindingId UNIQUEIDENTIFIER,
    rootTenantId      UNIQUEIDENTIFIER,
    appUserId         UNIQUEIDENTIFIER NULL,
    ruleCode          VARCHAR (50),
    severity          VARCHAR (15),
    title             NVARCHAR (200),
    detailJson        NVARCHAR (2000),
    detectedUtc       DATETIME2 (3)
);

INSERT logs.securityFinding (securityFindingId, rootTenantId, owningTenantId, appUserId, clientAddress, sessionReference,
                             ruleCode, severity, title, detailJson, windowStartUtc, windowEndUtc, detectedUtc, status, fingerprint)
OUTPUT INSERTED.securityFindingId, INSERTED.rootTenantId, INSERTED.appUserId, INSERTED.ruleCode,
       INSERTED.severity, INSERTED.title, INSERTED.detailJson, INSERTED.detectedUtc
  INTO @inserted
SELECT {NEWID}, c.rootTenantId, c.owningTenantId, c.appUserId, c.clientAddress, c.sessionReference,
       c.ruleCode, c.severity, c.title, c.detailJson, @since, @now, @now, 'open', c.fingerprint
  FROM @candidates AS c
 WHERE NOT EXISTS (SELECT 1 FROM logs.securityFinding AS existing
                    WHERE existing.fingerprint = c.fingerprint AND existing.isDeleted = 0);

SELECT securityFindingId, rootTenantId, appUserId, ruleCode, severity, title, detailJson, detectedUtc
  FROM @inserted
 ORDER BY detectedUtc DESC;

SET @Comments = CONCAT (N'Inserted ', (SELECT COUNT (*) FROM @inserted), N' new security finding(s).');
""",
    },
    {
        "schema": "logs", "name": "uspListSecurityFindings", "kind": "read", "grants": WEB, "ticket": "327",
        "permission": ["security.review", "tenant.administer"],
        "desc": "Lists persisted behavioral anomaly findings from logs.securityFinding within the caller's review scope (#327).",
        "params": """@days      INT = 30
, @status    VARCHAR (20) = NULL
, @severity  VARCHAR (15) = NULL
, @appUserId UNIQUEIDENTIFIER = NULL
, @top       INT = 200""",
        "key": "CONCAT (N'days=', @days, N', status=', @status, N', severity=', @severity)",
        "body": f"""
DECLARE @since DATETIME2 (3) = DATEADD (DAY, -CASE WHEN @days BETWEEN 1 AND 365 THEN @days ELSE 30 END, SYSUTCDATETIME ());
DECLARE @limit INT = CASE WHEN @top BETWEEN 1 AND 1000 THEN @top ELSE 200 END;
{REVIEW_ACCOUNTS}
DECLARE @reviewTenants TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @reviewTenants SELECT tenantId FROM auth.tvfReviewTenants ('security.review');

SELECT TOP (@limit)
       f.securityFindingId, f.rootTenantId, f.owningTenantId, f.appUserId,
       u.userName, u.displayName, f.clientAddress, f.sessionReference,
       f.ruleCode, f.severity, f.title, f.detailJson,
       f.windowStartUtc, f.windowEndUtc, f.detectedUtc, f.status,
       f.acknowledgedByUserId, ack.displayName AS acknowledgedByName, f.acknowledgedUtc
  FROM logs.securityFinding AS f
  LEFT JOIN auth.appUser AS u ON u.appUserId = f.appUserId
  LEFT JOIN auth.appUser AS ack ON ack.appUserId = f.acknowledgedByUserId
 WHERE f.isDeleted = 0 AND f.detectedUtc >= @since
   AND (@status IS NULL OR f.status = @status)
   AND (@severity IS NULL OR f.severity = @severity)
   AND (@appUserId IS NULL OR f.appUserId = @appUserId)
   AND (@all = 1
        OR f.appUserId IN (SELECT appUserId FROM @accounts)
        OR f.owningTenantId IN (SELECT tenantId FROM @reviewTenants)
        OR f.rootTenantId IN (SELECT tenantId FROM @reviewTenants))
 ORDER BY CASE f.severity WHEN 'critical' THEN 1 WHEN 'high' THEN 2 WHEN 'medium' THEN 3 ELSE 4 END,
          f.detectedUtc DESC;
""",
    },
    {
        "schema": "logs", "name": "uspUpdateSecurityFindingStatus", "kind": "write", "grants": WEB, "ticket": "327",
        "permission": ["security.review", "tenant.administer"],
        "desc": "Updates the review status of a persisted security finding and audits the transition in the same transaction (#327).",
        "params": """@securityFindingId UNIQUEIDENTIFIER
, @status            VARCHAR (20)""",
        "key": "CONCAT (N'securityFindingId=', @securityFindingId, N', status=', @status)",
        "validate": """IF @status NOT IN ('open', 'acknowledged', 'resolved', 'falsePositive')
BEGIN
    ;THROW 51422, N'Choose a valid finding status (open, acknowledged, resolved, falsePositive).', 1;
END;""",
        "body": f"""
{REVIEW_ACCOUNTS}
DECLARE @reviewTenants TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @reviewTenants SELECT tenantId FROM auth.tvfReviewTenants ('security.review');

DECLARE @root UNIQUEIDENTIFIER, @owner UNIQUEIDENTIFIER;
SELECT @root = f.rootTenantId, @owner = f.owningTenantId
  FROM logs.securityFinding AS f
 WHERE f.securityFindingId = @securityFindingId AND f.isDeleted = 0
   AND (@all = 1
        OR f.appUserId IN (SELECT appUserId FROM @accounts)
        OR f.owningTenantId IN (SELECT tenantId FROM @reviewTenants)
        OR f.rootTenantId IN (SELECT tenantId FROM @reviewTenants));

IF @root IS NULL
BEGIN
    ;THROW 51404, N'The security finding was not found, or you do not have access to it.', 1;
END;

UPDATE logs.securityFinding
   SET status = @status,
       acknowledgedByUserId = auth.udfSessionUserId (),
       acknowledgedUtc = SYSUTCDATETIME ()
 WHERE securityFindingId = @securityFindingId AND isDeleted = 0;

{AUDIT_SESSION_VARS}
INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId, clientAddress, owningTenantId,
                        sessionReference, traceId, actingTenantId, viewAsUserId)
VALUES (auth.udfSessionUserId (), @root, 'update', 'securityFinding', @securityFindingId, @auditClientAddress,
        @owner, @auditSessionRef, @auditTraceId, @auditActingTenant, @auditViewAs);

SET @Comments = CONCAT (N'Finding status updated to ', @status, N'.');
""",
    },

    # --------------------------------------------------------------------------------------------- #328 structured export
    {
        "schema": "logs", "name": "uspExportAuditEvents", "kind": "read", "grants": WEB, "ticket": "328",
        "permission": "audit.view",
        "desc": "Cursor-paginated canonical export of logs.auditEvent rows for a UTC range within the caller's audit.view scope (#328).",
        "params": """@fromUtc           DATETIME2 (3)
, @toUtc             DATETIME2 (3)
, @afterEventUtc     DATETIME2 (3) = NULL
, @afterAuditEventId BIGINT = NULL
, @batchSize         INT = 2000""",
        "key": "CONCAT (N'batchSize=', @batchSize, N', afterAuditEventId=', @afterAuditEventId)",
        "body": f"""
DECLARE @limit INT = CASE WHEN @batchSize BETWEEN 1 AND 5000 THEN @batchSize ELSE 2000 END;
{AUDIT_TENANTS}

SELECT TOP (@limit)
       e.auditEventId, e.eventUtc, e.eventAction, e.entityType, e.entityId, e.recordCount, e.viewKind,
       e.appUserId, u.userName, u.displayName,
       e.rootTenantId, e.owningTenantId, e.actingTenantId, e.viewAsUserId,
       e.sessionReference, e.traceId, e.clientAddress, e.filterJson
  FROM logs.auditEvent AS e
  LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
 WHERE e.eventUtc >= @fromUtc AND e.eventUtc <= @toUtc
   AND (@afterAuditEventId IS NULL
        OR e.eventUtc > @afterEventUtc
        OR (e.eventUtc = @afterEventUtc AND e.auditEventId > @afterAuditEventId))
   AND (@allAudit = 1
        OR e.owningTenantId IN (SELECT tenantId FROM @auditTenants)
        OR (e.owningTenantId IS NULL AND e.rootTenantId IN (SELECT tenantId FROM @auditTenants)))
 ORDER BY e.eventUtc ASC, e.auditEventId ASC;
""",
    },
    {
        "schema": "logs", "name": "uspExportSignInEvents", "kind": "read", "grants": WEB, "ticket": "328",
        "permission": ["security.review", "tenant.administer"],
        "desc": "Cursor-paginated canonical export of logs.signInEvent rows for a UTC range within the caller's security review scope. Never returns attempted names (#328).",
        "params": """@fromUtc            DATETIME2 (3)
, @toUtc              DATETIME2 (3)
, @afterEventUtc      DATETIME2 (3) = NULL
, @afterSignInEventId UNIQUEIDENTIFIER = NULL
, @batchSize          INT = 2000""",
        "key": "CONCAT (N'batchSize=', @batchSize)",
        "body": f"""
DECLARE @limit INT = CASE WHEN @batchSize BETWEEN 1 AND 5000 THEN @batchSize ELSE 2000 END;
{REVIEW_ACCOUNTS}

SELECT TOP (@limit)
       e.signInEventId, e.eventUtc, e.eventType, e.step, e.method, e.reason,
       e.appUserId, u.userName, u.displayName, e.actorUserId, e.rootTenantId,
       e.clientAddress, e.userAgent, e.sessionReference, e.attemptId, e.traceId,
       e.failedSignInCount, e.lockoutEndUtc
  FROM logs.signInEvent AS e
  LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
 WHERE e.isDeleted = 0 AND e.eventUtc >= @fromUtc AND e.eventUtc <= @toUtc
   AND (@afterSignInEventId IS NULL
        OR e.eventUtc > @afterEventUtc
        OR (e.eventUtc = @afterEventUtc AND e.signInEventId > @afterSignInEventId))
   AND (@all = 1 OR e.appUserId IN (SELECT appUserId FROM @accounts))
 ORDER BY e.eventUtc ASC, e.signInEventId ASC;
""",
    },
    {
        "schema": "logs", "name": "uspExportScreenVisits", "kind": "read", "grants": WEB, "ticket": "328",
        "permission": "usage.view",
        "desc": "Cursor-paginated canonical export of logs.screenVisit rows for a UTC range within the caller's usage.view scope (#328).",
        "params": """@fromUtc            DATETIME2 (3)
, @toUtc              DATETIME2 (3)
, @afterEventUtc      DATETIME2 (3) = NULL
, @afterScreenVisitId UNIQUEIDENTIFIER = NULL
, @batchSize          INT = 2000""",
        "key": "CONCAT (N'batchSize=', @batchSize)",
        "body": """
DECLARE @limit INT = CASE WHEN @batchSize BETWEEN 1 AND 5000 THEN @batchSize ELSE 2000 END;
DECLARE @review TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @review SELECT tenantId FROM auth.tvfReviewTenants ('usage.view');

SELECT TOP (@limit)
       v.screenVisitId, v.eventUtc, v.rootTenantId, v.actingTenantId,
       v.appUserId, u.userName, u.displayName,
       v.sessionReference, v.profileId, v.viewAsUserId,
       v.roleCodes, v.screenCode, v.navigationType, v.tabId, v.previousVisitId, v.durationMs
  FROM logs.screenVisit AS v
  LEFT JOIN auth.appUser AS u ON u.appUserId = v.appUserId
 WHERE v.isDeleted = 0 AND v.eventUtc >= @fromUtc AND v.eventUtc <= @toUtc
   AND (@afterScreenVisitId IS NULL
        OR v.eventUtc > @afterEventUtc
        OR (v.eventUtc = @afterEventUtc AND v.screenVisitId > @afterScreenVisitId))
   AND EXISTS (SELECT 1 FROM @review AS r WHERE r.tenantId = v.actingTenantId)
 ORDER BY v.eventUtc ASC, v.screenVisitId ASC;
""",
    },
    {
        "schema": "logs", "name": "uspExportSearchLogs", "kind": "read", "grants": WEB, "ticket": "328",
        "permission": ["security.review", "audit.view"],
        "desc": "Cursor-paginated canonical export of logs.searchLog metadata (never raw searchText) for a UTC range (#328/#329).",
        "params": """@fromUtc          DATETIME2 (3)
, @toUtc            DATETIME2 (3)
, @afterSearchedUtc DATETIME2 (3) = NULL
, @afterSearchLogId UNIQUEIDENTIFIER = NULL
, @batchSize        INT = 2000""",
        "key": "CONCAT (N'batchSize=', @batchSize)",
        "body": f"""
DECLARE @limit INT = CASE WHEN @batchSize BETWEEN 1 AND 5000 THEN @batchSize ELSE 2000 END;
{REVIEW_ACCOUNTS}
{AUDIT_TENANTS}

SELECT TOP (@limit)
       s.searchLogId, s.searchedUtc, s.appUserId, u.userName, u.displayName,
       s.rootTenantId, s.actingTenantId, s.sessionReference, s.clientAddress,
       s.resultCount, s.existsElsewhere, s.searchSource
  FROM logs.searchLog AS s
  LEFT JOIN auth.appUser AS u ON u.appUserId = s.appUserId
 WHERE s.isDeleted = 0 AND s.searchedUtc >= @fromUtc AND s.searchedUtc <= @toUtc
   AND (@afterSearchLogId IS NULL
        OR s.searchedUtc > @afterSearchedUtc
        OR (s.searchedUtc = @afterSearchedUtc AND s.searchLogId > @afterSearchLogId))
   AND (@all = 1 OR @allAudit = 1
        OR s.appUserId IN (SELECT appUserId FROM @accounts)
        OR s.actingTenantId IN (SELECT tenantId FROM @auditTenants)
        OR s.rootTenantId IN (SELECT tenantId FROM @auditTenants))
 ORDER BY s.searchedUtc ASC, s.searchLogId ASC;
""",
    },

    # --------------------------------------------------------------------------------------------- #330 user behavior & ops logs
    {
        "schema": "logs", "name": "uspGetUserBehaviorSummary", "kind": "read", "grants": WEB, "ticket": "330",
        "permission": ["security.review", "audit.view", "tenant.administer"],
        "desc": "Unified per-user behavioral summary across sign-ins, active sessions, audit ledger actions, search volume, "
                "screen usage, and open security findings within the caller's review scope (#330).",
        "params": """@appUserId UNIQUEIDENTIFIER
, @days      INT = 30""",
        "key": "CONCAT (N'appUserId=', @appUserId, N', days=', @days)",
        "body": f"""
DECLARE @winDays INT = CASE WHEN @days BETWEEN 1 AND 365 THEN @days ELSE 30 END;
DECLARE @since DATETIME2 (3) = DATEADD (DAY, -@winDays, SYSUTCDATETIME ());
{REVIEW_ACCOUNTS}
{AUDIT_TENANTS}

IF @all = 0 AND @allAudit = 0
   AND NOT EXISTS (SELECT 1 FROM @accounts WHERE appUserId = @appUserId)
   AND NOT EXISTS (SELECT 1 FROM auth.appUser AS u WHERE u.appUserId = @appUserId AND u.homeTenantId IN (SELECT tenantId FROM @auditTenants))
BEGIN
    ;THROW 51404, N'The user was not found, or you do not have access to review this account.', 1;
END;

-- 1. Account overview & sign-in posture
SELECT u.appUserId, u.userName, u.displayName, u.isActive, u.mfaEnabled, u.lastSignInUtc,
       (SELECT COUNT (*) FROM auth.sessionScope AS ss
         WHERE ss.appUserId = u.appUserId AND ss.revokedUtc IS NULL AND ss.expiresUtc > SYSUTCDATETIME ()) AS activeSessions,
       (SELECT COUNT (*) FROM logs.signInEvent AS se WHERE se.appUserId = u.appUserId AND se.isDeleted = 0 AND se.eventUtc >= @since AND se.eventType = 'sessionStarted') AS signInSuccesses,
       (SELECT COUNT (*) FROM logs.signInEvent AS se WHERE se.appUserId = u.appUserId AND se.isDeleted = 0 AND se.eventUtc >= @since AND se.eventType IN ({FAILURES})) AS signInFailures,
       (SELECT COUNT (*) FROM logs.signInEvent AS se WHERE se.appUserId = u.appUserId AND se.isDeleted = 0 AND se.eventUtc >= @since AND se.eventType = 'accountLocked') AS lockouts,
       (SELECT COUNT (DISTINCT se.clientAddress) FROM logs.signInEvent AS se WHERE se.appUserId = u.appUserId AND se.isDeleted = 0 AND se.eventUtc >= @since AND se.clientAddress IS NOT NULL) AS distinctAddresses,
       (SELECT COUNT (*) FROM logs.searchLog AS sl WHERE sl.appUserId = u.appUserId AND sl.isDeleted = 0 AND sl.searchedUtc >= @since) AS totalSearches,
       (SELECT COUNT (*) FROM logs.searchLog AS sl WHERE sl.appUserId = u.appUserId AND sl.isDeleted = 0 AND sl.searchedUtc >= @since AND sl.resultCount = 0) AS zeroResultSearches,
       (SELECT COUNT (*) FROM logs.searchLog AS sl WHERE sl.appUserId = u.appUserId AND sl.isDeleted = 0 AND sl.searchedUtc >= @since AND sl.existsElsewhere = 1) AS outsideScopeSearches,
       (SELECT COUNT (DISTINCT sv.sessionReference) FROM logs.screenVisit AS sv WHERE sv.appUserId = u.appUserId AND sv.isDeleted = 0 AND sv.eventUtc >= @since) AS usageSessions,
       (SELECT COUNT (*) FROM logs.screenVisit AS sv WHERE sv.appUserId = u.appUserId AND sv.isDeleted = 0 AND sv.eventUtc >= @since) AS screenVisits
  FROM auth.appUser AS u
 WHERE u.appUserId = @appUserId AND u.isDeleted = 0;

-- 2. Audit ledger action breakdown
SELECT e.eventAction, e.entityType, COUNT (*) AS eventCount,
       SUM (COALESCE (e.recordCount, 1)) AS totalRecords,
       MAX (e.eventUtc) AS lastUtc
  FROM logs.auditEvent AS e
 WHERE e.appUserId = @appUserId AND e.eventUtc >= @since
 GROUP BY e.eventAction, e.entityType
 ORDER BY eventCount DESC, lastUtc DESC;

-- 3. Recent security findings for this user
SELECT TOP (25) f.securityFindingId, f.ruleCode, f.severity, f.title, f.status, f.detectedUtc
  FROM logs.securityFinding AS f
 WHERE f.appUserId = @appUserId AND f.isDeleted = 0 AND f.detectedUtc >= @since
 ORDER BY f.detectedUtc DESC;

-- 4. Recent screen-usage sessions for this user
SELECT TOP (25) v.sessionReference, MIN (v.eventUtc) AS firstUtc, MAX (v.eventUtc) AS lastUtc, COUNT (*) AS visits
  FROM logs.screenVisit AS v
 WHERE v.appUserId = @appUserId AND v.isDeleted = 0 AND v.eventUtc >= @since
 GROUP BY v.sessionReference
 ORDER BY MAX (v.eventUtc) DESC;
""",
    },
    {
        "schema": "logs", "name": "uspGetOperationalLogs", "kind": "read", "grants": WEB, "ticket": "330",
        "permission": "security.investigate",
        "desc": "Platform-admin operational log summary: allowlisted logs.ExecutionLog failures (#338), recent logs.DdlChange "
                "events, and logs.uspDdlAuditVerify integrity posture (#330).",
        "params": """@hours INT = 24
, @top   INT = 100""",
        "key": "CONCAT (N'hours=', @hours, N', top=', @top)",
        "body": """
DECLARE @since DATETIME2 (3) = DATEADD (HOUR, -CASE WHEN @hours BETWEEN 1 AND 720 THEN @hours ELSE 24 END, SYSUTCDATETIME ());
DECLARE @limit INT = CASE WHEN @top BETWEEN 1 AND 500 THEN @top ELSE 100 END;

-- 1. Allowlisted ExecutionLog failure diagnostics (#338): procedure identity, timing, error code/line/procedure, allowlisted KeyParameters, actor.
--    Raw ErrorMessage, DynamicSql, and ContextMessage are excluded from operational reports/exports.
SELECT TOP (@limit)
       ExecutionLogId, ProcedureName, StartDateUtc, EndDateUtc, ElapsedMilliseconds,
       ReCreatedAfterRollback, ErrorNumber, ErrorLine, ErrorProcedure, KeyParameters, createdBy
  FROM logs.ExecutionLog
 WHERE isDeleted = 0 AND Successful = 0 AND StartDateUtc >= @since
 ORDER BY ExecutionLogId DESC;

-- 2. Recent DDL changes
SELECT TOP (@limit)
       ChangeId, RecordedUtc AS EventTimeUtc, EventType, SchemaName AS ObjectSchema, ObjectName, ObjectType,
       EventLoginName AS LoginName, createdBy AS AppUser
  FROM logs.DdlChange
 WHERE RecordedUtc >= @since
 ORDER BY ChangeId DESC;

-- 3. DDL audit integrity posture
EXEC logs.uspDdlAuditVerify;
""",
    },
    {
        "schema": "logs", "name": "uspCheckOperationalIntegrity", "kind": "read", "grants": WORKER, "ticket": "330",
        "desc": "Worker-only operational integrity check: counts recent logs.ExecutionLog failures and runs logs.uspDdlAuditVerify @ProblemsOnly = 1 (#330).",
        "params": "@minutes INT = 15",
        "key": "CONCAT (N'minutes=', @minutes)",
        "body": """
DECLARE @since DATETIME2 (3) = DATEADD (MINUTE, -CASE WHEN @minutes BETWEEN 1 AND 1440 THEN @minutes ELSE 15 END, SYSUTCDATETIME ());

SELECT COUNT (*) AS failureCount,
       COUNT (DISTINCT ProcedureName) AS affectedProcedures,
       MAX (ExecutionLogId) AS latestExecutionLogId
  FROM logs.ExecutionLog
 WHERE isDeleted = 0 AND Successful = 0 AND StartDateUtc >= @since;

EXEC logs.uspDdlAuditVerify @ProblemsOnly = 1;
""",
    },
]
