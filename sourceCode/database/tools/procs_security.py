"""Behavioral anomaly detection (#327), structured log export (#328), search log retention & reporting (#329),
unified per-user behavior summary & operational logs (#330), and row-change history access/purge (#332).
"""
from procs_common import NEWID, WEB, WORKER, AUDIT_SESSION_VARS

REVIEW_ACCOUNTS = """DECLARE @all BIT = auth.udfHasPermission (N'security.investigate');
DECLARE @accounts TABLE (appUserId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @accounts SELECT appUserId FROM auth.tvfReviewAccounts ('security.review');"""

# Exact #270 audit-event review scope used by logs.uspSearchAuditEvents (Claude I4, Codex #1).
AUDIT_SCOPE_270 = """DECLARE @auditScope TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @auditScope SELECT tenantId FROM auth.tvfReviewTenants ('audit.view');
DECLARE @wholeRoots TABLE (rootTenantId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @wholeRoots SELECT t.tenantId FROM auth.tenant AS t JOIN @auditScope AS s ON s.tenantId = t.tenantId WHERE t.tenantId = t.rootTenantId;
DECLARE @partRoots TABLE (rootTenantId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @partRoots SELECT DISTINCT t.rootTenantId FROM auth.tenant AS t JOIN @auditScope AS s ON s.tenantId = t.tenantId
 WHERE NOT EXISTS (SELECT 1 FROM @wholeRoots AS w WHERE w.rootTenantId = t.rootTenantId);
DECLARE @auditUsers TABLE (appUserId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @auditUsers SELECT u.appUserId FROM auth.appUser AS u
 WHERE EXISTS (SELECT 1 FROM @partRoots)
   AND (EXISTS (SELECT 1 FROM @auditScope AS s WHERE s.tenantId = u.homeTenantId)
        OR EXISTS (SELECT 1 FROM auth.userAssignment AS ua JOIN @auditScope AS s ON s.tenantId = ua.tenantId WHERE ua.appUserId = u.appUserId AND ua.isDeleted = 0));"""

AUDIT_PREDICATE_270 = """(EXISTS (SELECT 1 FROM @wholeRoots AS w WHERE w.rootTenantId = e.rootTenantId)
    OR (EXISTS (SELECT 1 FROM @partRoots AS p WHERE p.rootTenantId = e.rootTenantId)
        AND ((e.owningTenantId IS NULL AND EXISTS (SELECT 1 FROM @auditUsers AS x WHERE x.appUserId = e.appUserId))
             OR EXISTS (SELECT 1 FROM @auditScope AS s WHERE s.tenantId = e.owningTenantId)
             OR (e.owningTenantId = e.rootTenantId AND e.entityType IN ('jurisdictionSetting', 'threshold', 'notificationRule')))))"""

FAILURES = "'passwordFailed', 'unknownUser', 'mfaFailed', 'attemptWhileLocked', 'accountInactive'"
MFA_ABUSE_EVENTS = "'mfaFailed', 'emailCodeSent', 'emailCodeRefused', 'passwordResetRequested', 'recoveryCodeUsed'"

# Network-prefix expression (/24 for IPv4; first 3 hextets for IPv6 with :: expansion so 2001:db8::1 == 2001:db8:0:5::1, Claude I11).
IP_PREFIX_EXPR = """CASE
    WHEN CHARINDEX ('.', e.clientAddress) > 0
         THEN LEFT (e.clientAddress, LEN (e.clientAddress) - CHARINDEX ('.', REVERSE (e.clientAddress)))
    WHEN CHARINDEX (':', e.clientAddress) > 0
         THEN COALESCE (
              LEFT (REPLACE (CASE WHEN LEFT (e.clientAddress, 2) = '::' THEN '0' + e.clientAddress ELSE e.clientAddress END, '::', ':0:0:0:'),
                    NULLIF (CHARINDEX (':', REPLACE (CASE WHEN LEFT (e.clientAddress, 2) = '::' THEN '0' + e.clientAddress ELSE e.clientAddress END, '::', ':0:0:0:'),
                    NULLIF (CHARINDEX (':', REPLACE (CASE WHEN LEFT (e.clientAddress, 2) = '::' THEN '0' + e.clientAddress ELSE e.clientAddress END, '::', ':0:0:0:'),
                    NULLIF (CHARINDEX (':', REPLACE (CASE WHEN LEFT (e.clientAddress, 2) = '::' THEN '0' + e.clientAddress ELSE e.clientAddress END, '::', ':0:0:0:')), 0) + 1), 0) + 1), 0) - 1),
              e.clientAddress)
    ELSE e.clientAddress
END"""

LOGICAL_EVENT_KEY = "CASE WHEN e.entityType = 'structuredLogExport' AND e.entityId IS NOT NULL THEN CAST (e.entityId AS VARCHAR (36)) ELSE CAST (e.auditEventId AS VARCHAR (20)) END"

SINGLE_OWNER_EXPR = "CASE WHEN SUM (CASE WHEN COALESCE (e.owningTenantId, e.actingTenantId) IS NULL THEN 1 ELSE 0 END) = 0 AND COUNT (DISTINCT COALESCE (e.owningTenantId, e.actingTenantId)) = 1 AND COUNT (DISTINCT e.actingTenantId) <= 1 THEN MAX (COALESCE (e.owningTenantId, e.actingTenantId)) END"

SINGLE_SEARCH_OWNER_EXPR = "CASE WHEN SUM (CASE WHEN s.actingTenantId IS NULL THEN 1 ELSE 0 END) = 0 AND COUNT (DISTINCT s.actingTenantId) = 1 THEN MAX (s.actingTenantId) END"

AUTOMATED_UA_PREDICATE = """(   e.userAgent LIKE N'%curl/%'
     OR e.userAgent LIKE N'%python-requests%'
     OR e.userAgent LIKE N'%HeadlessChrome%'
     OR e.userAgent LIKE N'%Playwright%'
     OR e.userAgent LIKE N'%Selenium%'
     OR e.userAgent LIKE N'%Scrapy%'
     OR e.userAgent LIKE N'%wget%'
     OR e.userAgent LIKE N'%Go-http-client%')"""

PROCS = [
    # --------------------------------------------------------------------------------------------- #329 searchLog
    {
        "schema": "logs", "name": "uspPurgeSearchLog", "kind": "write", "no_tx": True, "grants": WORKER, "ticket": "329",
        "desc": "Scrubs searchText and personal correlation fields and soft-deletes logs.searchLog rows older than "
                "@retentionDays (30 to 3650, default 90) in independently committed 5,000-row batches (#329, #340). "
                "Worker-only; never hard-deletes.",
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
    BEGIN TRANSACTION;
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
    COMMIT TRANSACTION;
    SET @count += @batch;
    IF @batch = 5000
    BEGIN
        WAITFOR DELAY '00:00:00.050';
    END;
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
{AUDIT_SCOPE_270}
DECLARE @secTenants TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @secTenants SELECT tenantId FROM auth.tvfReviewTenants ('security.review');

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
   AND (@all = 1
        OR EXISTS (SELECT 1 FROM @wholeRoots AS w WHERE w.rootTenantId = s.rootTenantId)
        OR s.actingTenantId IN (SELECT tenantId FROM @auditScope)
        OR s.actingTenantId IN (SELECT tenantId FROM @secTenants)
        OR (s.actingTenantId IS NULL AND s.appUserId IN (SELECT appUserId FROM @accounts)))
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
{AUDIT_SCOPE_270}
DECLARE @secTenants TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @secTenants SELECT tenantId FROM auth.tvfReviewTenants ('security.review');

SELECT TOP (@limit)
       s.searchLogId, s.searchedUtc, s.appUserId, u.userName, u.displayName,
       s.rootTenantId, s.actingTenantId, s.sessionReference, s.clientAddress,
       s.resultCount, s.existsElsewhere, s.searchSource,
       CASE WHEN @canSeeText = 1 THEN s.searchText ELSE NULL END AS searchText
  FROM logs.searchLog AS s
  LEFT JOIN auth.appUser AS u ON u.appUserId = s.appUserId
 WHERE s.isDeleted = 0
   AND (@fromUtc IS NULL OR s.searchedUtc >= @fromUtc)
   AND (@toUtc IS NULL OR s.searchedUtc < @toUtc)
   AND (@appUserId IS NULL OR s.appUserId = @appUserId)
   AND (@all = 1
        OR EXISTS (SELECT 1 FROM @wholeRoots AS w WHERE w.rootTenantId = s.rootTenantId)
        OR s.actingTenantId IN (SELECT tenantId FROM @auditScope)
        OR s.actingTenantId IN (SELECT tenantId FROM @secTenants)
        OR (s.actingTenantId IS NULL AND s.appUserId IN (SELECT appUserId FROM @accounts)))
 ORDER BY s.searchedUtc DESC, s.searchLogId DESC;
""",
    },

    # --------------------------------------------------------------------------------------------- #332 row-change history
    {
        "schema": "logs", "name": "uspGetAuditEventChanges", "kind": "read", "grants": WEB, "ticket": "332",
        "permission": ["audit.view", "security.review"],
        "desc": "Returns purgeable before/after change snapshots from logs.auditEventChange for an auditEventId ONLY when "
                "the caller holds both #270 audit review scope AND identified clinical-record read permission on the "
                "underlying patient/result/case record (#332, Codex #1).",
        "params": "@auditEventId BIGINT",
        "key": "CONCAT (N'auditEventId=', @auditEventId)",
        "body": f"""
{AUDIT_SCOPE_270}
DECLARE @canPerson BIT = auth.udfHasPermission (N'person.view');
DECLARE @canResult BIT = auth.udfHasPermission (N'result.view');
DECLARE @canCase   BIT = auth.udfHasPermission (N'case.view');

SELECT c.auditEventChangeId, c.auditEventId, c.rootTenantId, c.owningTenantId,
       c.entityType, c.entityId, c.changedUtc, c.beforeJson, c.afterJson
  FROM logs.auditEventChange AS c
  JOIN logs.auditEvent AS e ON e.auditEventId = c.auditEventId
 WHERE c.auditEventId = @auditEventId AND c.isDeleted = 0
   AND {AUDIT_PREDICATE_270}
   AND (   (c.entityType = 'person' AND @canPerson = 1
            AND EXISTS (SELECT 1 FROM dbo.person AS p WHERE p.personId = c.entityId AND p.isDeleted = 0))
        OR (c.entityType = 'labResult' AND @canResult = 1
            AND EXISTS (SELECT 1 FROM dbo.labResult AS r WHERE r.labResultId = c.entityId AND r.isDeleted = 0
                         AND auth.udfCanReadIdentified (r.owningTenantId, 'result.view', r.personId) = 1))
        OR (c.entityType = 'exposureCase' AND @canCase = 1
            AND EXISTS (SELECT 1 FROM dbo.exposureCase AS ec WHERE ec.exposureCaseId = c.entityId AND ec.isDeleted = 0
                         AND auth.udfCanReadIdentified (ec.owningTenantId, 'case.view', NULL) = 1)))
 ORDER BY c.changedUtc, c.auditEventChangeId;
""",
    },
    {
        "schema": "logs", "name": "uspPurgeAuditEventChanges", "kind": "write", "no_tx": True, "grants": WORKER, "ticket": "332",
        "desc": "Scrubs beforeJson/afterJson and soft-deletes logs.auditEventChange rows older than @retentionDays in "
                "independently committed 5,000-row batches so clinical saves are not blocked (#332, #340). Worker-only.",
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
    BEGIN TRANSACTION;
    UPDATE TOP (5000) logs.auditEventChange
       SET beforeJson = NULL
         , afterJson  = NULL
         , isDeleted  = 1
         , deletedBy  = N'MERIT change history retention'
         , modifiedBy = N'MERIT change history retention'
     WHERE changedUtc < @cutoff AND isDeleted = 0;
    SET @batch = @@ROWCOUNT;
    COMMIT TRANSACTION;
    SET @count += @batch;
    IF @batch = 5000
    BEGIN
        WAITFOR DELAY '00:00:00.050';
    END;
END;
SELECT @count AS purged;
SET @Comments = CONCAT (N'Scrubbed ', @count, N' audit change detail row(s).');
""",
    },

    # --------------------------------------------------------------------------------------------- #327 anomaly detection
    {
        "schema": "logs", "name": "uspGetBehaviorAnomalies", "kind": "read", "grants": WEB, "ticket": "327",
        "permission": ["security.review", "tenant.administer"],
        "desc": "Evaluates all 10 post-authentication and sign-in behavioral anomaly rules over the last @hours within the "
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
DECLARE @secTenants TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @secTenants SELECT tenantId FROM auth.tvfReviewTenants ('security.review');
DECLARE @wholeSecRoots TABLE (rootTenantId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @wholeSecRoots SELECT t.tenantId FROM auth.tenant AS t JOIN @secTenants AS r ON r.tenantId = t.tenantId WHERE t.tenantId = t.rootTenantId;

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

-- Rule 1: Bulk patient/result/case viewing velocity (event-level tenant scope, Codex #2)
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
   AND (@all = 1 OR e.rootTenantId IN (SELECT rootTenantId FROM @wholeSecRoots)
        OR e.owningTenantId IN (SELECT tenantId FROM @secTenants)
        OR (e.owningTenantId IS NULL AND e.actingTenantId IN (SELECT tenantId FROM @secTenants)))
 GROUP BY e.appUserId, u.userName, u.displayName, e.sessionReference
HAVING COUNT (DISTINCT e.entityId) >= @viewVelocityThreshold;

-- Rule 2: Bulk or unusual data exports (structuredLogExport paired by entityId; single-entry downloads by auditEventId, I13 / Codex #3)
INSERT @findings
SELECT 'bulkExportSpike',
       CASE WHEN SUM (COALESCE (e.recordCount, 1)) >= @exportRowsThreshold * 2
                 OR COUNT (DISTINCT {LOGICAL_EVENT_KEY}) >= @exportSpikeThreshold * 2
            THEN 'high' ELSE 'medium' END,
       e.appUserId, u.userName, u.displayName, NULL, NULL,
       COUNT (DISTINCT {LOGICAL_EVENT_KEY}),
       CONCAT (N'Bulk export spike (', COUNT (DISTINCT {LOGICAL_EVENT_KEY}), N' exports, ', SUM (COALESCE (e.recordCount, 0)), N' rows)'),
       (SELECT COUNT (DISTINCT {LOGICAL_EVENT_KEY}) AS exportCount, SUM (COALESCE (e.recordCount, 0)) AS totalRows, @exportSpikeThreshold AS countThreshold, @exportRowsThreshold AS rowsThreshold FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       MIN (e.eventUtc), MAX (e.eventUtc)
  FROM logs.auditEvent AS e
  LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
 WHERE e.eventUtc >= @since AND e.eventAction = 'export'
   AND (@all = 1 OR e.rootTenantId IN (SELECT rootTenantId FROM @wholeSecRoots)
        OR e.owningTenantId IN (SELECT tenantId FROM @secTenants)
        OR (e.owningTenantId IS NULL AND e.actingTenantId IN (SELECT tenantId FROM @secTenants)))
 GROUP BY e.appUserId, u.userName, u.displayName
HAVING COUNT (DISTINCT {LOGICAL_EVENT_KEY}) >= @exportSpikeThreshold
    OR SUM (COALESCE (e.recordCount, 0)) >= @exportRowsThreshold;

-- Rule 3: Repeated permissionDenied, authenticated notFound, or outside-scope probing (#327)
INSERT @findings
SELECT 'probingDeniedOrOutsideScope',
       CASE WHEN COUNT (*) >= @probingThreshold * 2 THEN 'high' ELSE 'medium' END,
       e.appUserId, u.userName, u.displayName, MAX (e.clientAddress), NULL,
       COUNT (*),
       CONCAT (N'Repeated permission-denied, not-found, or outside-scope probing (', COUNT (*), N' events)'),
       (SELECT SUM (CASE WHEN e.eventAction = 'permissionDenied' THEN 1 ELSE 0 END) AS permissionDenied,
               SUM (CASE WHEN e.eventAction = 'notFound' THEN 1 ELSE 0 END) AS notFound,
               SUM (CASE WHEN e.entityType = 'personOutsideScope' THEN 1 ELSE 0 END) AS outsideScope,
               @probingThreshold AS threshold FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       MIN (e.eventUtc), MAX (e.eventUtc)
  FROM logs.auditEvent AS e
  LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
 WHERE e.eventUtc >= @since
   AND (e.eventAction IN ('permissionDenied', 'notFound') OR e.entityType = 'personOutsideScope')
   AND (@all = 1 OR e.rootTenantId IN (SELECT rootTenantId FROM @wholeSecRoots)
        OR e.owningTenantId IN (SELECT tenantId FROM @secTenants)
        OR (e.owningTenantId IS NULL AND e.actingTenantId IN (SELECT tenantId FROM @secTenants)))
 GROUP BY e.appUserId, u.userName, u.displayName
HAVING COUNT (*) >= @probingThreshold;

-- Rule 4: High-velocity searchLog querying (event-level tenant scope, Codex #1)
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
   AND (@all = 1 OR s.rootTenantId IN (SELECT rootTenantId FROM @wholeSecRoots)
        OR s.actingTenantId IN (SELECT tenantId FROM @secTenants))
 GROUP BY s.appUserId, u.userName, u.displayName
HAVING COUNT (*) >= @searchVelocityThreshold;

-- Rule 5: Mid-session network-prefix drift (event-level tenant scope, Codex #1)
INSERT @findings
SELECT 'midSessionIpDrift',
       CASE WHEN COUNT (DISTINCT {IP_PREFIX_EXPR}) >= 3 THEN 'high' ELSE 'medium' END,
       e.appUserId, u.userName, u.displayName, NULL, e.sessionReference,
       COUNT (DISTINCT {IP_PREFIX_EXPR}),
       CONCAT (N'Mid-session network change (', COUNT (DISTINCT {IP_PREFIX_EXPR}), N' distinct network prefixes in one session)'),
       (SELECT COUNT (DISTINCT {IP_PREFIX_EXPR}) AS distinctPrefixes, COUNT (DISTINCT e.clientAddress) AS distinctAddresses, COUNT (*) AS events FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       MIN (e.eventUtc), MAX (e.eventUtc)
  FROM logs.auditEvent AS e
  LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
 WHERE e.eventUtc >= @since AND e.sessionReference IS NOT NULL AND e.clientAddress IS NOT NULL
   AND (@all = 1 OR e.rootTenantId IN (SELECT rootTenantId FROM @wholeSecRoots)
        OR e.owningTenantId IN (SELECT tenantId FROM @secTenants)
        OR (e.owningTenantId IS NULL AND e.actingTenantId IN (SELECT tenantId FROM @secTenants)))
 GROUP BY e.appUserId, u.userName, u.displayName, e.sessionReference
HAVING COUNT (DISTINCT {IP_PREFIX_EXPR}) > 1;

-- Rule 11: Mid-session User-Agent drift (event-level tenant scope, #327)
INSERT @findings
SELECT 'midSessionUserAgentDrift',
       CASE WHEN COUNT (DISTINCT e.userAgentHash) >= 3 THEN 'high' ELSE 'medium' END,
       e.appUserId, u.userName, u.displayName, MAX (e.clientAddress), e.sessionReference,
       COUNT (DISTINCT e.userAgentHash),
       CONCAT (N'Mid-session User-Agent change (', COUNT (DISTINCT e.userAgentHash), N' distinct client agents in one session)'),
       (SELECT COUNT (DISTINCT e.userAgentHash) AS distinctUserAgents, COUNT (*) AS events FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       MIN (e.eventUtc), MAX (e.eventUtc)
  FROM logs.auditEvent AS e
  LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
 WHERE e.eventUtc >= @since AND e.sessionReference IS NOT NULL AND e.userAgentHash IS NOT NULL
   AND (@all = 1 OR e.rootTenantId IN (SELECT rootTenantId FROM @wholeSecRoots)
        OR e.owningTenantId IN (SELECT tenantId FROM @secTenants)
        OR (e.owningTenantId IS NULL AND e.actingTenantId IN (SELECT tenantId FROM @secTenants)))
 GROUP BY e.appUserId, u.userName, u.displayName, e.sessionReference
HAVING COUNT (DISTINCT e.userAgentHash) > 1;

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
   AND {AUTOMATED_UA_PREDICATE}
   AND (@all = 1 OR e.appUserId IN (SELECT appUserId FROM @accounts))
 GROUP BY e.appUserId, u.userName, u.displayName;

-- Rule 7: Privileged / security-admin action bursts (event-level tenant scope, Codex #1)
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
   AND (@all = 1 OR e.rootTenantId IN (SELECT rootTenantId FROM @wholeSecRoots)
        OR e.owningTenantId IN (SELECT tenantId FROM @secTenants)
        OR (e.owningTenantId IS NULL AND e.actingTenantId IN (SELECT tenantId FROM @secTenants)))
 GROUP BY e.appUserId, u.userName, u.displayName
HAVING COUNT (*) >= @privBurstThreshold;

-- Rule 8: MFA fatigue / email-code / password-reset abuse (uses canonical SignInEventTypes, Claude I2)
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
   AND e.eventType IN ({MFA_ABUSE_EVENTS})
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

-- Rule 10: API-only data access with zero screen visits (logical count aligns with bulkExportSpike, Codex #3)
INSERT @findings
SELECT 'apiOnlyNoScreenVisits', 'medium',
       e.appUserId, u.userName, u.displayName, MAX (e.clientAddress), e.sessionReference,
       COUNT (DISTINCT {LOGICAL_EVENT_KEY}),
       CONCAT (N'Data-access session with zero UI screen visits (', COUNT (DISTINCT {LOGICAL_EVENT_KEY}), N' reads)'),
       (SELECT COUNT (DISTINCT {LOGICAL_EVENT_KEY}) AS readEvents, COUNT (DISTINCT e.entityId) AS distinctEntities FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       MIN (e.eventUtc), MAX (e.eventUtc)
  FROM logs.auditEvent AS e
  LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
 WHERE e.eventUtc >= @since AND e.sessionReference IS NOT NULL
   AND e.eventAction IN ('view', 'list', 'export')
   AND (@all = 1 OR e.rootTenantId IN (SELECT rootTenantId FROM @wholeSecRoots)
        OR e.owningTenantId IN (SELECT tenantId FROM @secTenants)
        OR (e.owningTenantId IS NULL AND e.actingTenantId IN (SELECT tenantId FROM @secTenants)))
   AND NOT EXISTS (SELECT 1 FROM logs.screenVisit AS v
                    WHERE v.sessionReference = e.sessionReference AND v.isDeleted = 0)
 GROUP BY e.appUserId, u.userName, u.displayName, e.sessionReference
HAVING COUNT (DISTINCT {LOGICAL_EVENT_KEY}) >= 25;

SELECT ruleCode, severity, appUserId, userName, displayName, clientAddress, sessionReference,
       eventCount, title, detailJson, firstUtc, lastUtc
  FROM @findings
 ORDER BY CASE severity WHEN 'critical' THEN 1 WHEN 'high' THEN 2 WHEN 'medium' THEN 3 ELSE 4 END,
          lastUtc DESC;
""",
    },
    {
        "schema": "logs", "name": "uspRunSecurityFindingScan", "kind": "write", "grants": WORKER, "ticket": "327",
        "desc": "Scheduled worker scan evaluating all 10 behavioral anomaly rules across active tenants (and platform-scoped "
                "unattributed events), serialized via sp_getapplock, deduplicated by unique fingerprint (#327).",
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
DECLARE @scanLock INT;
EXEC @scanLock = sys.sp_getapplock @Resource = N'MERIT.securityFindingScan', @LockMode = 'Exclusive',
     @LockOwner = 'Transaction', @LockTimeout = 5000;
IF @scanLock < 0
BEGIN
    SELECT TOP (0) securityFindingId, rootTenantId, appUserId, ruleCode, severity, title, detailJson, detectedUtc
      FROM logs.securityFinding;
    SET @Comments = N'Another worker instance is running the scan.';
END
ELSE
BEGIN
DECLARE @winHours INT = CASE WHEN @hours BETWEEN 1 AND 168 THEN @hours ELSE 24 END;
DECLARE @now DATETIME2 (3) = SYSUTCDATETIME ();
DECLARE @since DATETIME2 (3) = DATEADD (HOUR, -@winHours, @now);
DECLARE @dayBucket VARCHAR (10) = CONVERT (VARCHAR (10), @now, 23);
DECLARE @platformRoot UNIQUEIDENTIFIER = '00000000-0000-0000-0000-000000000000';

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
    fingerprint      VARCHAR (300) NOT NULL
);

-- Rule 1: bulkViewVelocity (owningTenantId derived from source event ownership + acting tenant, NULL when mixed, Codex #2)
INSERT @candidates
SELECT COALESCE (e.rootTenantId, @platformRoot),
       {SINGLE_OWNER_EXPR},
       e.appUserId, NULL, e.sessionReference,
       'bulkViewVelocity', CASE WHEN COUNT (DISTINCT e.entityId) >= @viewVelocityThreshold * 2 THEN 'high' ELSE 'medium' END,
       CONCAT (N'High distinct record view velocity (', COUNT (DISTINCT e.entityId), N' records in ', @winHours, N'h)'),
       (SELECT COUNT (DISTINCT e.entityId) AS distinctEntities, COUNT (*) AS totalViews FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       CONCAT ('bulkViewVelocity|', CONVERT (VARCHAR (36), COALESCE (e.rootTenantId, @platformRoot)), '|', COALESCE (CONVERT (VARCHAR (36), e.appUserId), 'none'), '|', COALESCE (CONVERT (VARCHAR (36), e.sessionReference), @dayBucket))
  FROM logs.auditEvent AS e
 WHERE e.eventUtc >= @since AND e.eventAction = 'view' AND e.entityId IS NOT NULL
 GROUP BY COALESCE (e.rootTenantId, @platformRoot), e.appUserId, e.sessionReference
HAVING COUNT (DISTINCT e.entityId) >= @viewVelocityThreshold;

-- Rule 2: bulkExportSpike (structuredLogExport paired by entityId; single-entry downloads by auditEventId, I13 / Codex #3)
INSERT @candidates
SELECT COALESCE (e.rootTenantId, @platformRoot),
       {SINGLE_OWNER_EXPR},
       e.appUserId, NULL, NULL,
       'bulkExportSpike',
       CASE WHEN SUM (COALESCE (e.recordCount, 1)) >= @exportRowsThreshold * 2
                 OR COUNT (DISTINCT {LOGICAL_EVENT_KEY}) >= @exportSpikeThreshold * 2
            THEN 'high' ELSE 'medium' END,
       CONCAT (N'Bulk export spike (', COUNT (DISTINCT {LOGICAL_EVENT_KEY}), N' exports, ', SUM (COALESCE (e.recordCount, 0)), N' rows)'),
       (SELECT COUNT (DISTINCT {LOGICAL_EVENT_KEY}) AS exportCount, SUM (COALESCE (e.recordCount, 0)) AS totalRows FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       CONCAT ('bulkExportSpike|', CONVERT (VARCHAR (36), COALESCE (e.rootTenantId, @platformRoot)), '|', COALESCE (CONVERT (VARCHAR (36), e.appUserId), 'none'), '|', @dayBucket)
  FROM logs.auditEvent AS e
 WHERE e.eventUtc >= @since AND e.eventAction = 'export'
 GROUP BY COALESCE (e.rootTenantId, @platformRoot), e.appUserId
HAVING COUNT (DISTINCT {LOGICAL_EVENT_KEY}) >= @exportSpikeThreshold
    OR SUM (COALESCE (e.recordCount, 0)) >= @exportRowsThreshold;

-- Rule 3: probingDeniedOrOutsideScope (includes authenticated 404s, handles NULL rootTenantId safely under platform-only scope, #327)
INSERT @candidates
SELECT COALESCE (e.rootTenantId, @platformRoot),
       {SINGLE_OWNER_EXPR},
       e.appUserId, MAX (e.clientAddress), NULL,
       'probingDeniedOrOutsideScope', CASE WHEN COUNT (*) >= @probingThreshold * 2 THEN 'high' ELSE 'medium' END,
       CONCAT (N'Repeated permission-denied, not-found, or outside-scope probing (', COUNT (*), N' events)'),
       (SELECT SUM (CASE WHEN e.eventAction = 'permissionDenied' THEN 1 ELSE 0 END) AS permissionDenied,
               SUM (CASE WHEN e.eventAction = 'notFound' THEN 1 ELSE 0 END) AS notFound,
               SUM (CASE WHEN e.entityType = 'personOutsideScope' THEN 1 ELSE 0 END) AS outsideScope,
               COUNT (*) AS events FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       CONCAT ('probingDeniedOrOutsideScope|', CONVERT (VARCHAR (36), COALESCE (e.rootTenantId, @platformRoot)), '|', COALESCE (CONVERT (VARCHAR (36), e.appUserId), 'none'), '|', @dayBucket)
  FROM logs.auditEvent AS e
 WHERE e.eventUtc >= @since AND (e.eventAction IN ('permissionDenied', 'notFound') OR e.entityType = 'personOutsideScope')
 GROUP BY COALESCE (e.rootTenantId, @platformRoot), e.appUserId
HAVING COUNT (*) >= @probingThreshold;

-- Rule 4: searchVelocitySpike (county-owned ONLY when every contributing row has the same non-NULL actingTenantId, Codex #3)
INSERT @candidates
SELECT COALESCE (s.rootTenantId, @platformRoot),
       {SINGLE_SEARCH_OWNER_EXPR},
       s.appUserId, MAX (s.clientAddress), NULL,
       'searchVelocitySpike', CASE WHEN COUNT (*) >= @searchVelocityThreshold * 2 THEN 'high' ELSE 'medium' END,
       CONCAT (N'High-velocity searching (', COUNT (*), N' queries)'),
       (SELECT COUNT (*) AS searches, SUM (CASE WHEN s.resultCount = 0 THEN 1 ELSE 0 END) AS zeroResults FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       CONCAT ('searchVelocitySpike|', CONVERT (VARCHAR (36), COALESCE (s.rootTenantId, @platformRoot)), '|', CONVERT (VARCHAR (36), s.appUserId), '|', @dayBucket)
  FROM logs.searchLog AS s
 WHERE s.isDeleted = 0 AND s.searchedUtc >= @since
 GROUP BY COALESCE (s.rootTenantId, @platformRoot), s.appUserId
HAVING COUNT (*) >= @searchVelocityThreshold;

-- Rule 5: midSessionIpDrift (source-event ownership, Codex #1/#3; network prefix comparison, Claude I5 / I11)
INSERT @candidates
SELECT COALESCE (e.rootTenantId, @platformRoot),
       {SINGLE_OWNER_EXPR},
       e.appUserId, NULL, e.sessionReference,
       'midSessionIpDrift', CASE WHEN COUNT (DISTINCT {IP_PREFIX_EXPR}) >= 3 THEN 'high' ELSE 'medium' END,
       CONCAT (N'Mid-session network change (', COUNT (DISTINCT {IP_PREFIX_EXPR}), N' distinct network prefixes)'),
       (SELECT COUNT (DISTINCT {IP_PREFIX_EXPR}) AS distinctPrefixes, COUNT (DISTINCT e.clientAddress) AS distinctAddresses, COUNT (*) AS events FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       CONCAT ('midSessionIpDrift|', CONVERT (VARCHAR (36), COALESCE (e.rootTenantId, @platformRoot)), '|', COALESCE (CONVERT (VARCHAR (36), e.appUserId), 'none'), '|', CONVERT (VARCHAR (36), e.sessionReference))
  FROM logs.auditEvent AS e
 WHERE e.eventUtc >= @since AND e.sessionReference IS NOT NULL AND e.clientAddress IS NOT NULL
 GROUP BY COALESCE (e.rootTenantId, @platformRoot), e.appUserId, e.sessionReference
HAVING COUNT (DISTINCT {IP_PREFIX_EXPR}) > 1;

-- Rule 11: midSessionUserAgentDrift (source-event ownership, #327)
INSERT @candidates
SELECT COALESCE (e.rootTenantId, @platformRoot),
       {SINGLE_OWNER_EXPR},
       e.appUserId, MAX (e.clientAddress), e.sessionReference,
       'midSessionUserAgentDrift', CASE WHEN COUNT (DISTINCT e.userAgentHash) >= 3 THEN 'high' ELSE 'medium' END,
       CONCAT (N'Mid-session User-Agent change (', COUNT (DISTINCT e.userAgentHash), N' distinct client agents)'),
       (SELECT COUNT (DISTINCT e.userAgentHash) AS distinctUserAgents, COUNT (*) AS events FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       CONCAT ('midSessionUserAgentDrift|', CONVERT (VARCHAR (36), COALESCE (e.rootTenantId, @platformRoot)), '|', COALESCE (CONVERT (VARCHAR (36), e.appUserId), 'none'), '|', CONVERT (VARCHAR (36), e.sessionReference))
  FROM logs.auditEvent AS e
 WHERE e.eventUtc >= @since AND e.sessionReference IS NOT NULL AND e.userAgentHash IS NOT NULL
 GROUP BY COALESCE (e.rootTenantId, @platformRoot), e.appUserId, e.sessionReference
HAVING COUNT (DISTINCT e.userAgentHash) > 1;

-- Rule 6: automatedUserAgent (Codex #6 parity)
INSERT @candidates
SELECT COALESCE (e.rootTenantId, @platformRoot), NULL, e.appUserId, MAX (e.clientAddress), MAX (e.sessionReference),
       'automatedUserAgent', 'medium',
       CONCAT (N'Automated or headless client user-agent detected (', COUNT (*), N' sign-in events)'),
       (SELECT COUNT (*) AS events, LEFT (MAX (e.userAgent), 120) AS sampleUserAgent FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       CONCAT ('automatedUserAgent|', CONVERT (VARCHAR (36), COALESCE (e.rootTenantId, @platformRoot)), '|', COALESCE (CONVERT (VARCHAR (36), e.appUserId), 'none'), '|', @dayBucket)
  FROM logs.signInEvent AS e
 WHERE e.isDeleted = 0 AND e.eventUtc >= @since AND e.userAgent IS NOT NULL
   AND {AUTOMATED_UA_PREDICATE}
 GROUP BY COALESCE (e.rootTenantId, @platformRoot), e.appUserId;

-- Rule 7: privilegedActionBurst (source-event ownership, Codex #1/#3)
INSERT @candidates
SELECT COALESCE (e.rootTenantId, @platformRoot),
       {SINGLE_OWNER_EXPR},
       e.appUserId, MAX (e.clientAddress), NULL,
       'privilegedActionBurst', 'high',
       CONCAT (N'Privileged security/configuration burst (', COUNT (*), N' actions)'),
       (SELECT COUNT (*) AS actions, @privBurstThreshold AS threshold FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       CONCAT ('privilegedActionBurst|', CONVERT (VARCHAR (36), COALESCE (e.rootTenantId, @platformRoot)), '|', COALESCE (CONVERT (VARCHAR (36), e.appUserId), 'none'), '|', @dayBucket)
  FROM logs.auditEvent AS e
 WHERE e.eventUtc >= @since
   AND (   e.eventAction IN ('grant', 'revoke', 'deactivate', 'mfaResetByAdmin', 'passwordSetByAdmin')
        OR (e.entityType IN ('threshold', 'jurisdictionSetting', 'userAssignment', 'role') AND e.eventAction IN ('create', 'update', 'delete')))
 GROUP BY COALESCE (e.rootTenantId, @platformRoot), e.appUserId
HAVING COUNT (*) >= @privBurstThreshold;

-- Rule 8: mfaOrResetAbuse (Claude I2 canonical event types)
INSERT @candidates
SELECT COALESCE (e.rootTenantId, @platformRoot), NULL, e.appUserId, MAX (e.clientAddress), NULL,
       'mfaOrResetAbuse', 'high',
       CONCAT (N'Repeated MFA or password-reset attempts (', COUNT (*), N' events)'),
       (SELECT COUNT (*) AS events, COUNT (DISTINCT e.clientAddress) AS distinctAddresses FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       CONCAT ('mfaOrResetAbuse|', CONVERT (VARCHAR (36), COALESCE (e.rootTenantId, @platformRoot)), '|', COALESCE (CONVERT (VARCHAR (36), e.appUserId), 'none'), '|', @dayBucket)
  FROM logs.signInEvent AS e
 WHERE e.isDeleted = 0 AND e.eventUtc >= @since AND e.eventType IN ({MFA_ABUSE_EVENTS})
 GROUP BY COALESCE (e.rootTenantId, @platformRoot), e.appUserId
HAVING COUNT (*) >= @mfaAbuseThreshold;

-- Rule 9: distributedLowAndSlowFailures (Codex #6 parity)
INSERT @candidates
SELECT COALESCE (e.rootTenantId, @platformRoot), NULL, e.appUserId, NULL, NULL,
       'distributedLowAndSlowFailures', 'medium',
       CONCAT (N'Distributed low-and-slow sign-in failures (', COUNT (*), N' failures across ', COUNT (DISTINCT e.clientAddress), N' addresses)'),
       (SELECT COUNT (*) AS failures, COUNT (DISTINCT e.clientAddress) AS distinctAddresses, @distributedFailThreshold AS threshold FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       CONCAT ('distributedLowAndSlowFailures|', CONVERT (VARCHAR (36), COALESCE (e.rootTenantId, @platformRoot)), '|', COALESCE (CONVERT (VARCHAR (36), e.appUserId), 'none'), '|', @dayBucket)
  FROM logs.signInEvent AS e
 WHERE e.isDeleted = 0 AND e.eventUtc >= @since AND e.eventType IN ({FAILURES})
 GROUP BY COALESCE (e.rootTenantId, @platformRoot), e.appUserId
HAVING COUNT (*) >= @distributedFailThreshold AND COUNT (DISTINCT e.clientAddress) >= 4;

-- Rule 10: apiOnlyNoScreenVisits (Codex #3 logical event count + #2 source ownership)
INSERT @candidates
SELECT COALESCE (e.rootTenantId, @platformRoot),
       {SINGLE_OWNER_EXPR},
       e.appUserId, MAX (e.clientAddress), e.sessionReference,
       'apiOnlyNoScreenVisits', 'medium',
       CONCAT (N'Data-access session with zero UI screen visits (', COUNT (DISTINCT {LOGICAL_EVENT_KEY}), N' reads)'),
       (SELECT COUNT (DISTINCT {LOGICAL_EVENT_KEY}) AS readEvents, COUNT (DISTINCT e.entityId) AS distinctEntities FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       CONCAT ('apiOnlyNoScreenVisits|', CONVERT (VARCHAR (36), COALESCE (e.rootTenantId, @platformRoot)), '|', COALESCE (CONVERT (VARCHAR (36), e.appUserId), 'none'), '|', CONVERT (VARCHAR (36), e.sessionReference))
  FROM logs.auditEvent AS e
 WHERE e.eventUtc >= @since AND e.sessionReference IS NOT NULL
   AND e.eventAction IN ('view', 'list', 'export')
   AND NOT EXISTS (SELECT 1 FROM logs.screenVisit AS v
                    WHERE v.sessionReference = e.sessionReference AND v.isDeleted = 0)
 GROUP BY COALESCE (e.rootTenantId, @platformRoot), e.appUserId, e.sessionReference
HAVING COUNT (DISTINCT {LOGICAL_EVENT_KEY}) >= 25;

-- Deduplicate candidates by fingerprint within this scan batch before inserting against the unique index.
DECLARE @deduped TABLE (
    rootTenantId     UNIQUEIDENTIFIER NOT NULL,
    owningTenantId   UNIQUEIDENTIFIER NULL,
    appUserId        UNIQUEIDENTIFIER NULL,
    clientAddress    VARCHAR (45) NULL,
    sessionReference UNIQUEIDENTIFIER NULL,
    ruleCode         VARCHAR (50) NOT NULL,
    severity         VARCHAR (15) NOT NULL,
    title            NVARCHAR (200) NOT NULL,
    detailJson       NVARCHAR (2000) NULL,
    fingerprint      VARCHAR (128) NOT NULL PRIMARY KEY
);
-- Codex #1/#2: Hash the complete canonical key (ruleCode|rootTenantId|groupingIdentity|effectiveOwner) with SHA-256
-- so neither cross-root sessions nor long county-scoped keys can collide or truncate, and a prior county-level
-- disposition (including falsePositive) can never suppress a newly root-scoped finding.
INSERT @deduped
SELECT rootTenantId, owningTenantId, appUserId, clientAddress, sessionReference, ruleCode, severity, title, detailJson, scopedFingerprint
  FROM (SELECT *,
               CONVERT (VARCHAR (64), HASHBYTES ('SHA2_256', CAST (CONCAT (fingerprint, '|', COALESCE (CONVERT (VARCHAR (36), owningTenantId), 'root')) AS VARCHAR (400))), 2) AS scopedFingerprint,
               ROW_NUMBER () OVER (PARTITION BY CONVERT (VARCHAR (64), HASHBYTES ('SHA2_256', CAST (CONCAT (fingerprint, '|', COALESCE (CONVERT (VARCHAR (36), owningTenantId), 'root')) AS VARCHAR (400))), 2)
                                       ORDER BY CASE severity WHEN 'critical' THEN 1 WHEN 'high' THEN 2 WHEN 'medium' THEN 3 ELSE 4 END) AS rn
          FROM @candidates) AS x
 WHERE rn = 1;

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

-- Codex #6 / Claude S8: Persist upward severity escalations on active non-falsePositive findings, reopening resolved ones.
UPDATE f
   SET f.severity     = c.severity
     , f.title        = c.title
     , f.detailJson   = c.detailJson
     , f.windowEndUtc = @now
     , f.status       = CASE WHEN f.status = 'resolved' THEN 'open' ELSE f.status END
     , f.acknowledgedByUserId = CASE WHEN f.status = 'resolved' THEN NULL ELSE f.acknowledgedByUserId END
     , f.acknowledgedUtc      = CASE WHEN f.status = 'resolved' THEN NULL ELSE f.acknowledgedUtc END
     , f.owningTenantId = CASE WHEN f.owningTenantId = c.owningTenantId THEN f.owningTenantId ELSE NULL END
OUTPUT INSERTED.securityFindingId, INSERTED.rootTenantId, INSERTED.appUserId, INSERTED.ruleCode,
       INSERTED.severity, INSERTED.title, INSERTED.detailJson, INSERTED.detectedUtc
  INTO @inserted
  FROM logs.securityFinding AS f
  JOIN @deduped AS c ON c.fingerprint = f.fingerprint
 WHERE f.isDeleted = 0 AND f.status <> 'falsePositive'
   AND (CASE c.severity WHEN 'critical' THEN 4 WHEN 'high' THEN 3 WHEN 'medium' THEN 2 ELSE 1 END)
       > (CASE f.severity WHEN 'critical' THEN 4 WHEN 'high' THEN 3 WHEN 'medium' THEN 2 ELSE 1 END);

INSERT logs.securityFinding (securityFindingId, rootTenantId, owningTenantId, appUserId, clientAddress, sessionReference,
                             ruleCode, severity, title, detailJson, windowStartUtc, windowEndUtc, detectedUtc, status, fingerprint)
OUTPUT INSERTED.securityFindingId, INSERTED.rootTenantId, INSERTED.appUserId, INSERTED.ruleCode,
       INSERTED.severity, INSERTED.title, INSERTED.detailJson, INSERTED.detectedUtc
  INTO @inserted
SELECT {NEWID}, c.rootTenantId, c.owningTenantId, c.appUserId, c.clientAddress, c.sessionReference,
       c.ruleCode, c.severity, c.title, c.detailJson, @since, @now, @now, 'open', c.fingerprint
  FROM @deduped AS c
 WHERE NOT EXISTS (SELECT 1 FROM logs.securityFinding AS existing
                    WHERE existing.fingerprint = c.fingerprint AND existing.isDeleted = 0);

SELECT securityFindingId, rootTenantId, appUserId, ruleCode, severity, title, detailJson, detectedUtc
  FROM @inserted
 ORDER BY detectedUtc DESC;

SET @Comments = CONCAT (N'Persisted/escalated ', (SELECT COUNT (*) FROM @inserted), N' security finding(s).');
END;
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
        "key": "CONCAT (N'days=', @days, N', top=', @top)",
        "body": f"""
DECLARE @since DATETIME2 (3) = DATEADD (DAY, -CASE WHEN @days BETWEEN 1 AND 365 THEN @days ELSE 30 END, SYSUTCDATETIME ());
DECLARE @limit INT = CASE WHEN @top BETWEEN 1 AND 1000 THEN @top ELSE 200 END;
{REVIEW_ACCOUNTS}
DECLARE @reviewTenants TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @reviewTenants SELECT tenantId FROM auth.tvfReviewTenants ('security.review');
DECLARE @wholeSecRoots TABLE (rootTenantId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @wholeSecRoots SELECT t.tenantId FROM auth.tenant AS t JOIN @reviewTenants AS r ON r.tenantId = t.tenantId WHERE t.tenantId = t.rootTenantId;

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
        OR f.owningTenantId IN (SELECT tenantId FROM @reviewTenants)
        OR (f.owningTenantId IS NULL AND f.rootTenantId IN (SELECT rootTenantId FROM @wholeSecRoots)))
 ORDER BY CASE f.severity WHEN 'critical' THEN 1 WHEN 'high' THEN 2 WHEN 'medium' THEN 3 ELSE 4 END,
          f.detectedUtc DESC;
""",
    },
    {
        "schema": "logs", "name": "uspUpdateSecurityFindingStatus", "kind": "write", "grants": WEB, "ticket": "327",
        "permission": ["tenant.administer", "security.investigate"],
        "desc": "Updates the review status of a persisted security finding (requires tenant.administer or security.investigate, "
                "not read-only security.review) and audits the transition in the same transaction (#327, Codex #4).",
        "params": """@securityFindingId UNIQUEIDENTIFIER
, @status            VARCHAR (20)""",
        "key": "CONCAT (N'securityFindingId=', @securityFindingId)",
        "validate": """IF @status NOT IN ('open', 'acknowledged', 'resolved', 'falsePositive')
BEGIN
    ;THROW 51422, N'Choose a valid finding status (open, acknowledged, resolved, falsePositive).', 1;
END;""",
        "body": f"""
DECLARE @all BIT = auth.udfHasPermission (N'security.investigate');
DECLARE @adminTenants TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @adminTenants SELECT tenantId FROM auth.tvfSessionWritePermission ('tenant.administer');
DECLARE @wholeAdminRoots TABLE (rootTenantId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @wholeAdminRoots SELECT t.tenantId FROM auth.tenant AS t JOIN @adminTenants AS a ON a.tenantId = t.tenantId WHERE t.tenantId = t.rootTenantId;

-- Codex #1: Enforce owner/root scope atomically in the UPDATE statement itself and capture post-update root/owner via OUTPUT.
DECLARE @updated TABLE (rootTenantId UNIQUEIDENTIFIER NOT NULL, owningTenantId UNIQUEIDENTIFIER NULL);

UPDATE logs.securityFinding
   SET status = @status,
       acknowledgedByUserId = auth.udfSessionUserId (),
       acknowledgedUtc = SYSUTCDATETIME ()
OUTPUT INSERTED.rootTenantId, INSERTED.owningTenantId INTO @updated
 WHERE securityFindingId = @securityFindingId AND isDeleted = 0
   AND (@all = 1
        OR owningTenantId IN (SELECT tenantId FROM @adminTenants)
        OR (owningTenantId IS NULL AND rootTenantId IN (SELECT rootTenantId FROM @wholeAdminRoots)));

IF NOT EXISTS (SELECT 1 FROM @updated)
BEGIN
    ;THROW 51404, N'The security finding was not found, or you do not have permission to update it.', 1;
END;

DECLARE @root UNIQUEIDENTIFIER, @owner UNIQUEIDENTIFIER;
SELECT TOP (1) @root = rootTenantId, @owner = owningTenantId FROM @updated;

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
        "desc": "Cursor-paginated canonical export of logs.auditEvent rows over the half-open UTC range [@fromUtc, @toUtc) "
                "using the exact #270 audit review scope (#328, Claude I4, Codex #2).",
        "params": """@fromUtc           DATETIME2 (3)
, @toUtc             DATETIME2 (3)
, @afterEventUtc     DATETIME2 (3) = NULL
, @afterAuditEventId BIGINT = NULL
, @batchSize         INT = 2000""",
        "key": "CONCAT (N'batchSize=', @batchSize, N', afterAuditEventId=', @afterAuditEventId)",
        "body": f"""
DECLARE @limit INT = CASE WHEN @batchSize BETWEEN 1 AND 5000 THEN @batchSize ELSE 2000 END;
{AUDIT_SCOPE_270}

SELECT TOP (@limit)
       e.auditEventId, e.eventUtc, e.eventAction, e.entityType, e.entityId, e.recordCount, e.viewKind,
       e.appUserId, u.userName, u.displayName,
       e.rootTenantId, e.owningTenantId, e.actingTenantId, e.viewAsUserId,
       e.sessionReference, e.traceId, e.clientAddress, e.filterJson
  FROM logs.auditEvent AS e
  LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
 WHERE e.eventUtc >= @fromUtc AND e.eventUtc < @toUtc
   AND (@afterAuditEventId IS NULL
        OR e.eventUtc > @afterEventUtc
        OR (e.eventUtc = @afterEventUtc AND e.auditEventId > @afterAuditEventId))
   AND {AUDIT_PREDICATE_270}
 ORDER BY e.eventUtc ASC, e.auditEventId ASC;
""",
    },
    {
        "schema": "logs", "name": "uspExportSignInEvents", "kind": "read", "grants": WEB, "ticket": "328",
        "permission": ["security.review", "tenant.administer"],
        "desc": "Cursor-paginated canonical export of logs.signInEvent rows over [@fromUtc, @toUtc) within the caller's security review scope. Never returns attempted names (#328).",
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
 WHERE e.isDeleted = 0 AND e.eventUtc >= @fromUtc AND e.eventUtc < @toUtc
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
        "desc": "Cursor-paginated canonical export of logs.screenVisit rows over [@fromUtc, @toUtc) within the caller's usage.view scope (#328).",
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
 WHERE v.isDeleted = 0 AND v.eventUtc >= @fromUtc AND v.eventUtc < @toUtc
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
        "desc": "Cursor-paginated canonical export of logs.searchLog metadata (never raw searchText) over [@fromUtc, @toUtc) (#328/#329).",
        "params": """@fromUtc          DATETIME2 (3)
, @toUtc            DATETIME2 (3)
, @afterSearchedUtc DATETIME2 (3) = NULL
, @afterSearchLogId UNIQUEIDENTIFIER = NULL
, @batchSize        INT = 2000""",
        "key": "CONCAT (N'batchSize=', @batchSize)",
        "body": f"""
DECLARE @limit INT = CASE WHEN @batchSize BETWEEN 1 AND 5000 THEN @batchSize ELSE 2000 END;
{REVIEW_ACCOUNTS}
{AUDIT_SCOPE_270}
DECLARE @secTenants TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @secTenants SELECT tenantId FROM auth.tvfReviewTenants ('security.review');

SELECT TOP (@limit)
       s.searchLogId, s.searchedUtc, s.appUserId, u.userName, u.displayName,
       s.rootTenantId, s.actingTenantId, s.sessionReference, s.clientAddress,
       s.resultCount, s.existsElsewhere, s.searchSource
  FROM logs.searchLog AS s
  LEFT JOIN auth.appUser AS u ON u.appUserId = s.appUserId
 WHERE s.isDeleted = 0 AND s.searchedUtc >= @fromUtc AND s.searchedUtc < @toUtc
   AND (@afterSearchLogId IS NULL
        OR s.searchedUtc > @afterSearchedUtc
        OR (s.searchedUtc = @afterSearchedUtc AND s.searchLogId > @afterSearchLogId))
   AND (@all = 1
        OR EXISTS (SELECT 1 FROM @wholeRoots AS w WHERE w.rootTenantId = s.rootTenantId)
        OR s.actingTenantId IN (SELECT tenantId FROM @auditScope)
        OR s.actingTenantId IN (SELECT tenantId FROM @secTenants)
        OR (s.actingTenantId IS NULL AND s.appUserId IN (SELECT appUserId FROM @accounts)))
 ORDER BY s.searchedUtc ASC, s.searchLogId ASC;
""",
    },

    # --------------------------------------------------------------------------------------------- #330 user behavior & ops logs
    {
        "schema": "logs", "name": "uspGetUserBehaviorSummary", "kind": "read", "grants": WEB, "ticket": "330",
        "permission": ["security.review", "audit.view", "tenant.administer"],
        "desc": "Unified per-user behavioral summary applying strict event-level tenant scope to audit, search, usage, "
                "and security-finding aggregates (#330, Codex #1).",
        "params": """@appUserId UNIQUEIDENTIFIER
, @days      INT = 30""",
        "key": "CONCAT (N'appUserId=', @appUserId, N', days=', @days)",
        "body": f"""
DECLARE @winDays INT = CASE WHEN @days BETWEEN 1 AND 365 THEN @days ELSE 30 END;
DECLARE @since DATETIME2 (3) = DATEADD (DAY, -@winDays, SYSUTCDATETIME ());
DECLARE @canAudit BIT = auth.udfHasPermission (N'audit.view');
DECLARE @canUsage BIT = auth.udfHasPermission (N'usage.view');
{REVIEW_ACCOUNTS}
{AUDIT_SCOPE_270}
DECLARE @secTenants TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @secTenants SELECT tenantId FROM auth.tvfReviewTenants ('security.review');
DECLARE @wholeSecRoots TABLE (rootTenantId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @wholeSecRoots SELECT t.tenantId FROM auth.tenant AS t JOIN @secTenants AS r ON r.tenantId = t.tenantId WHERE t.tenantId = t.rootTenantId;
DECLARE @usageTenants TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
IF @canUsage = 1 INSERT @usageTenants SELECT tenantId FROM auth.tvfReviewTenants ('usage.view');

IF @all = 0
   AND NOT EXISTS (SELECT 1 FROM @accounts WHERE appUserId = @appUserId)
   AND NOT EXISTS (SELECT 1 FROM @auditUsers WHERE appUserId = @appUserId)
   AND NOT EXISTS (SELECT 1 FROM auth.appUser AS u WHERE u.appUserId = @appUserId AND u.rootTenantId IN (SELECT rootTenantId FROM @wholeRoots))
BEGIN
    ;THROW 51404, N'The user was not found, or you do not have access to review this account.', 1;
END;

-- 1. Account overview & event-scoped posture
SELECT u.appUserId, u.userName, u.displayName, u.isActive, u.mfaEnabled, u.lastSignInUtc,
       (SELECT COUNT (*) FROM auth.sessionScope AS ss
         WHERE ss.appUserId = u.appUserId AND ss.revokedUtc IS NULL AND ss.expiresUtc > SYSUTCDATETIME ()) AS activeSessions,
       (SELECT COUNT (*) FROM logs.signInEvent AS se WHERE se.appUserId = u.appUserId AND se.isDeleted = 0 AND se.eventUtc >= @since AND se.eventType = 'sessionStarted') AS signInSuccesses,
       (SELECT COUNT (*) FROM logs.signInEvent AS se WHERE se.appUserId = u.appUserId AND se.isDeleted = 0 AND se.eventUtc >= @since AND se.eventType IN ({FAILURES})) AS signInFailures,
       (SELECT COUNT (*) FROM logs.signInEvent AS se WHERE se.appUserId = u.appUserId AND se.isDeleted = 0 AND se.eventUtc >= @since AND se.eventType = 'accountLocked') AS lockouts,
       (SELECT COUNT (DISTINCT se.clientAddress) FROM logs.signInEvent AS se WHERE se.appUserId = u.appUserId AND se.isDeleted = 0 AND se.eventUtc >= @since AND se.clientAddress IS NOT NULL) AS distinctAddresses,
       (SELECT COUNT (*) FROM logs.searchLog AS sl
         WHERE sl.appUserId = u.appUserId AND sl.isDeleted = 0 AND sl.searchedUtc >= @since
           AND (@all = 1 OR EXISTS (SELECT 1 FROM @wholeRoots AS w WHERE w.rootTenantId = sl.rootTenantId)
                OR sl.actingTenantId IN (SELECT tenantId FROM @auditScope)
                OR sl.actingTenantId IN (SELECT tenantId FROM @secTenants))) AS totalSearches,
       (SELECT COUNT (*) FROM logs.searchLog AS sl
         WHERE sl.appUserId = u.appUserId AND sl.isDeleted = 0 AND sl.searchedUtc >= @since AND sl.resultCount = 0
           AND (@all = 1 OR EXISTS (SELECT 1 FROM @wholeRoots AS w WHERE w.rootTenantId = sl.rootTenantId)
                OR sl.actingTenantId IN (SELECT tenantId FROM @auditScope)
                OR sl.actingTenantId IN (SELECT tenantId FROM @secTenants))) AS zeroResultSearches,
       (SELECT COUNT (*) FROM logs.searchLog AS sl
         WHERE sl.appUserId = u.appUserId AND sl.isDeleted = 0 AND sl.searchedUtc >= @since AND sl.existsElsewhere = 1
           AND (@all = 1 OR EXISTS (SELECT 1 FROM @wholeRoots AS w WHERE w.rootTenantId = sl.rootTenantId)
                OR sl.actingTenantId IN (SELECT tenantId FROM @auditScope)
                OR sl.actingTenantId IN (SELECT tenantId FROM @secTenants))) AS outsideScopeSearches,
       (SELECT COUNT (DISTINCT sv.sessionReference) FROM logs.screenVisit AS sv
         WHERE @canUsage = 1 AND sv.appUserId = u.appUserId AND sv.isDeleted = 0 AND sv.eventUtc >= @since
           AND sv.actingTenantId IN (SELECT tenantId FROM @usageTenants)) AS usageSessions,
       (SELECT COUNT (*) FROM logs.screenVisit AS sv
         WHERE @canUsage = 1 AND sv.appUserId = u.appUserId AND sv.isDeleted = 0 AND sv.eventUtc >= @since
           AND sv.actingTenantId IN (SELECT tenantId FROM @usageTenants)) AS screenVisits
  FROM auth.appUser AS u
 WHERE u.appUserId = @appUserId AND u.isDeleted = 0;

-- 2. Event-scoped audit ledger action breakdown (requires audit.view + #270 event ownership scope)
SELECT e.eventAction, e.entityType, COUNT (*) AS eventCount,
       SUM (COALESCE (e.recordCount, 1)) AS totalRecords,
       MAX (e.eventUtc) AS lastUtc
  FROM logs.auditEvent AS e
 WHERE @canAudit = 1 AND e.appUserId = @appUserId AND e.eventUtc >= @since
   AND {AUDIT_PREDICATE_270}
 GROUP BY e.eventAction, e.entityType
 ORDER BY eventCount DESC, lastUtc DESC;

-- 3. Event-scoped recent security findings for this user
SELECT TOP (25) f.securityFindingId, f.ruleCode, f.severity, f.title, f.status, f.detectedUtc
  FROM logs.securityFinding AS f
 WHERE f.appUserId = @appUserId AND f.isDeleted = 0 AND f.detectedUtc >= @since
   AND (@all = 1
        OR f.owningTenantId IN (SELECT tenantId FROM @secTenants)
        OR (f.owningTenantId IS NULL AND f.rootTenantId IN (SELECT rootTenantId FROM @wholeSecRoots)))
 ORDER BY f.detectedUtc DESC;

-- 4. Event-scoped recent screen-usage sessions (requires usage.view + actingTenantId scope)
SELECT TOP (25) v.sessionReference, MIN (v.eventUtc) AS firstUtc, MAX (v.eventUtc) AS lastUtc, COUNT (*) AS visits
  FROM logs.screenVisit AS v
 WHERE @canUsage = 1 AND v.appUserId = @appUserId AND v.isDeleted = 0 AND v.eventUtc >= @since
   AND v.actingTenantId IN (SELECT tenantId FROM @usageTenants)
 GROUP BY v.sessionReference
 ORDER BY MAX (v.eventUtc) DESC;
""",
    },
    {
        "schema": "logs", "name": "uspDdlAuditVerifyInternal", "kind": "read", "grants": [], "ticket": "330",
        "execute_as_owner": True,
        "desc": "Ungranted internal helper running logs.uspDdlAuditVerify under owner catalog visibility, reached ONLY "
                "through ownership chaining from authorized wrapper procedures (Claude B3, Codex #2/#5).",
        "params": "@ProblemsOnly BIT = 0",
        "key": "CONCAT (N'problemsOnly=', @ProblemsOnly)",
        "body": """
EXEC logs.uspDdlAuditVerify @ProblemsOnly = @ProblemsOnly;
""",
    },
    {
        "schema": "logs", "name": "uspGetOperationalLogs", "kind": "read", "grants": WEB, "ticket": "330",
        "permission": "security.investigate",
        "desc": "Platform-admin operational log summary: per-token allowlisted logs.ExecutionLog failures (#338), recent "
                "logs.DdlChange events, and logs.uspDdlAuditVerify integrity posture via ungranted owner helper (#330, B3).",
        "params": """@hours INT = 24
, @top   INT = 100""",
        "key": "CONCAT (N'hours=', @hours, N', top=', @top)",
        "body": """
DECLARE @since DATETIME2 (3) = DATEADD (HOUR, -CASE WHEN @hours BETWEEN 1 AND 720 THEN @hours ELSE 24 END, SYSUTCDATETIME ());
DECLARE @limit INT = CASE WHEN @top BETWEEN 1 AND 500 THEN @top ELSE 100 END;

-- 1. Allowlisted ExecutionLog failure diagnostics (#338, Claude I10 / S9): deny-first per-token filtering with ordinal order.
SELECT TOP (@limit)
       e.ExecutionLogId, e.ProcedureName, e.StartDateUtc, e.EndDateUtc, e.ElapsedMilliseconds,
       e.ReCreatedAfterRollback, e.ErrorNumber, e.ErrorLine, e.ErrorProcedure,
       CASE
           WHEN e.KeyParameters IS NULL OR CHARINDEX (N'=', e.KeyParameters) = 0 THEN LEFT (e.KeyParameters, 200)
           ELSE (SELECT LEFT (STRING_AGG (
                    CASE
                        WHEN kv.k IS NOT NULL
                             AND kv.k NOT IN (N'token', N'secret', N'ssn', N'firstName', N'lastName', N'middleName', N'searchText', N'attemptedName')
                             AND kv.k NOT LIKE N'%password%' AND kv.k NOT LIKE N'%secret%' AND kv.k NOT LIKE N'%token%'
                             AND kv.k NOT LIKE N'%birth%' AND kv.k NOT LIKE N'%phone%' AND kv.k NOT LIKE N'%email%'
                             AND kv.k NOT LIKE N'%street%' AND kv.k NOT LIKE N'%zip%' AND kv.k NOT LIKE N'%ssn%'
                             AND kv.k NOT LIKE N'%postal%' AND kv.k NOT LIKE N'%address%' AND kv.k NOT LIKE N'%accession%' AND kv.k NOT LIKE N'%medicalRecord%'
                             AND (   kv.k LIKE N'%Id' OR kv.k LIKE N'%Count' OR kv.k LIKE N'%Number'
                                  OR kv.k LIKE N'%Days' OR kv.k LIKE N'%Hours' OR kv.k LIKE N'%Minutes'
                                  OR kv.k LIKE N'%Size' OR kv.k LIKE N'%Rows' OR kv.k LIKE N'%Code'
                                  OR kv.k LIKE N'%Type' OR kv.k LIKE N'%Action' OR kv.k LIKE N'%Kind'
                                  OR kv.k LIKE N'%Source' OR kv.k LIKE N'%Role' OR kv.k LIKE N'%By'
                                  OR kv.k LIKE N'%Only' OR kv.k LIKE N'%Fips' OR kv.k LIKE N'%Status'
                                  OR kv.k IN (N'fromDate', N'toDate', N'fromUtc', N'toUtc', N'asOfDate', N'effectiveFrom', N'effectiveTo',
                                              N'periodStart', N'periodEnd', N'isActive', N'isMember', N'isDeleted', N'includeHistory',
                                              N'includeProtected', N'includeSearchText', N'hasDob', N'top', N'page', N'assignedToMe',
                                              N'classification', N'closureReason', N'viewKind', N'workerName', N'settingKey',
                                              N'decision', N'population', N'sort', N'status', N'step', N'method'))
                        THEN LTRIM (RTRIM (tok.value))
                        ELSE N'[redacted]'
                    END, N', ') WITHIN GROUP (ORDER BY tok.ordinal), 500)
                   FROM STRING_SPLIT (LEFT (e.KeyParameters, 500), N',', 1) AS tok
                  CROSS APPLY (SELECT LTRIM (RTRIM (LEFT (tok.value, NULLIF (CHARINDEX (N'=', tok.value), 0) - 1))) AS k) AS kv)
       END AS KeyParameters,
       e.createdBy
  FROM logs.ExecutionLog AS e
 WHERE e.isDeleted = 0 AND e.Successful = 0 AND e.StartDateUtc >= @since
 ORDER BY e.ExecutionLogId DESC;

-- 2. Recent DDL changes
SELECT TOP (@limit)
       ChangeId, RecordedUtc AS EventTimeUtc, EventType, SchemaName AS ObjectSchema, ObjectName, ObjectType,
       EventLoginName AS LoginName, createdBy AS AppUser
  FROM logs.DdlChange
 WHERE RecordedUtc >= @since
 ORDER BY ChangeId DESC;

-- 3. DDL audit integrity posture (called in owner context ONLY after caller-context security.investigate check above)
EXEC logs.uspDdlAuditVerifyInternal @ProblemsOnly = 0;
""",
    },
    {
        "schema": "logs", "name": "uspCheckOperationalIntegrity", "kind": "read", "grants": WORKER, "ticket": "330",
        "desc": "Worker-only operational integrity check: counts recent logs.ExecutionLog failures and runs "
                "logs.uspDdlAuditVerify @ProblemsOnly = 1 via ungranted owner helper (#330, Codex #5).",
        "params": "@minutes INT = 15",
        "key": "CONCAT (N'minutes=', @minutes)",
        "body": """
DECLARE @since DATETIME2 (3) = DATEADD (MINUTE, -CASE WHEN @minutes BETWEEN 1 AND 1440 THEN @minutes ELSE 15 END, SYSUTCDATETIME ());

SELECT COUNT (*) AS failureCount,
       COUNT (DISTINCT ProcedureName) AS affectedProcedures,
       MAX (ExecutionLogId) AS latestExecutionLogId
  FROM logs.ExecutionLog
 WHERE isDeleted = 0 AND Successful = 0 AND StartDateUtc >= @since;

EXEC logs.uspDdlAuditVerifyInternal @ProblemsOnly = 1;
""",
    },
]
