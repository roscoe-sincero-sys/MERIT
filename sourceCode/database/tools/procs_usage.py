"""Retainable, first-party screen usage (#227). Not a substitute for the append-only data-access audit ledger."""
from procs_common import WEB, WORKER

ACTIVE = """DECLARE @scope UNIQUEIDENTIFIER = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER);
DECLARE @me UNIQUEIDENTIFIER = auth.udfSessionUserId (), @reference UNIQUEIDENTIFIER, @root UNIQUEIDENTIFIER,
        @acting UNIQUEIDENTIFIER, @profile UNIQUEIDENTIFIER, @viewAs UNIQUEIDENTIFIER;
SELECT @reference = s.sessionReference, @root = s.rootTenantId, @acting = s.actingTenantId,
       @profile = s.userProfileId, @viewAs = s.viewAsUserId
  FROM auth.sessionScope AS s JOIN auth.appUser AS u ON u.appUserId = s.appUserId
 WHERE s.sessionScopeId = @scope AND s.appUserId = @me AND s.revokedUtc IS NULL
   AND s.expiresUtc > SYSUTCDATETIME () AND u.isActive = 1 AND u.isDeleted = 0;
IF @reference IS NULL
BEGIN
    ;THROW 51403, N'A current signed-in session is required.', 1;
END;"""

VISIBLE = """DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();
DECLARE @scope UNIQUEIDENTIFIER = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER);
DECLARE @from DATETIME2 (3) = DATEADD (DAY, -@days, SYSUTCDATETIME ());
SELECT v.* INTO #visits
  FROM logs.screenVisit AS v
 WHERE v.isDeleted = 0 AND v.rootTenantId = @root AND v.eventUtc >= @from
   AND (EXISTS (SELECT 1 FROM auth.sessionScopeTenant AS st WHERE st.sessionScopeId = @scope AND st.tenantId = v.actingTenantId)
     OR EXISTS (SELECT 1 FROM auth.sessionScopeAdminTenant AS st WHERE st.sessionScopeId = @scope AND st.tenantId = v.actingTenantId));
ALTER TABLE #visits ADD PRIMARY KEY CLUSTERED (screenVisitId);"""

DAYS = """IF @days NOT BETWEEN 1 AND 90
BEGIN
    ;THROW 51422, N'Choose a period from 1 to 90 days.', 1;
END;"""

# Server reconciliation and multiple blur/unload beacons can touch the same visit concurrently.
# Serialize that visit before any row/index locks: the audit trigger also updates its clustered row.
VISIT_LOCK = """
DECLARE @visitLock INT, @visitResource NVARCHAR (100) = CONCAT (N'MERIT.screenVisit.', @screenVisitId);
EXEC @visitLock = sys.sp_getapplock @Resource = @visitResource, @LockMode = 'Exclusive',
     @LockOwner = 'Transaction', @LockTimeout = 1000;
IF @visitLock < 0
BEGIN
    ;THROW 51409, N'The usage visit is busy; navigation is unaffected.', 1;
END;
"""

PROCS = [
    {
        "schema": "auth", "name": "uspContinueUsageSession", "kind": "write", "grants": WEB, "ticket": "227",
        "desc": "Keeps the non-secret logical sign-in reference across a scope/profile/view change. Both scopes must "
                "belong to the authenticated actor and be current. No scope key is written to instrumentation.",
        "params": "@newSessionScopeId UNIQUEIDENTIFIER", "key": "N'logical usage session continuity'",
        "body": ACTIVE + """
UPDATE auth.sessionScope SET sessionReference = @reference
 WHERE sessionScopeId = @newSessionScopeId AND appUserId = @me AND revokedUtc IS NULL
   AND expiresUtc > SYSUTCDATETIME () AND sessionScopeId <> @scope;
IF @@ROWCOUNT <> 1
BEGIN
    ;THROW 51403, N'The replacement session is not available.', 1;
END;
""",
    },
    {
        "schema": "logs", "name": "uspRecordScreenVisit", "kind": "write", "grants": WEB, "ticket": "227",
        "desc": "Idempotently records only screen metadata under the current session. Context is resolved server-side; "
                "a client cannot supply user, root, profile, role or session reference. Reconciles a server-rendered "
                "visit with its browser tab/predecessor hint. No URLs or scope keys are logged.",
        "params": """@screenVisitId UNIQUEIDENTIFIER
, @screenCode VARCHAR (60)
, @tabId UNIQUEIDENTIFIER = NULL
, @previousVisitId UNIQUEIDENTIFIER = NULL
, @navigationType VARCHAR (12) = 'server'""",
        "key": "N'screen usage metadata'",
        "body": ACTIVE + VISIT_LOCK + """
IF @screenVisitId IS NULL OR NULLIF (@screenCode, '') IS NULL
   OR @screenCode COLLATE Latin1_General_100_BIN2 LIKE '%[^a-zA-Z0-9.-]%'
   OR @navigationType NOT IN ('server', 'direct', 'menu', 'link', 'search', 'back', 'reload', 'redirect', 'router')
BEGIN
    ;THROW 51422, N'Invalid screen metadata.', 1;
END;
IF EXISTS (SELECT 1 FROM auth.sessionScope WHERE sessionScopeId IN (@screenVisitId, @tabId, @previousVisitId))
BEGIN
    ;THROW 51422, N'Usage ids must not be authentication keys.', 1;
END;
-- A predecessor is a validated chain within one logical session and tab, not an untrusted URL.
DECLARE @retryPredecessor BIT = 0;
IF NOT EXISTS (SELECT 1 FROM logs.screenVisit WHERE screenVisitId = @previousVisitId AND appUserId = @me
                AND sessionReference = @reference AND tabId = @tabId AND isDeleted = 0 AND screenVisitId <> @screenVisitId)
BEGIN
    IF @previousVisitId IS NOT NULL AND @previousVisitId <> @screenVisitId SET @retryPredecessor = 1;
    SET @previousVisitId = NULL;
END;

IF EXISTS (SELECT 1 FROM logs.screenVisit WHERE screenVisitId = @screenVisitId)
BEGIN
    IF NOT EXISTS (SELECT 1 FROM logs.screenVisit WHERE screenVisitId = @screenVisitId AND appUserId = @me
                    AND sessionReference = @reference AND screenCode = @screenCode AND isDeleted = 0
                    AND (tabId IS NULL OR @tabId IS NULL OR tabId = @tabId))
    BEGIN
        ;THROW 51403, N'That visit is not available.', 1;
    END;
    UPDATE logs.screenVisit
       SET tabId = COALESCE (tabId, @tabId), previousVisitId = COALESCE (previousVisitId, @previousVisitId),
           navigationType = CASE WHEN @navigationType = 'server' THEN navigationType ELSE @navigationType END
     WHERE screenVisitId = @screenVisitId;
END
ELSE
BEGIN
    DECLARE @roles VARCHAR (MAX);
    SELECT @roles = STRING_AGG (CAST (r.roleCode AS VARCHAR (MAX)), ',') WITHIN GROUP (ORDER BY r.roleCode)
      FROM (SELECT DISTINCT ro.roleCode FROM auth.userAssignment AS a
              JOIN auth.role AS ro ON ro.roleId = a.roleId AND ro.isDeleted = 0
             WHERE a.appUserId = COALESCE (@viewAs, @me) AND a.isDeleted = 0
               AND a.startDate <= auth.udfSessionToday () AND (a.endDate IS NULL OR a.endDate >= auth.udfSessionToday ()) AND a.revokedUtc IS NULL
               AND (a.tenantId = @acting OR (a.includeDescendants = 1 AND EXISTS
                    (SELECT 1 FROM auth.tenantClosure AS tc WHERE tc.ancestorTenantId = a.tenantId AND tc.descendantTenantId = @acting AND tc.isDeleted = 0)))
               AND (@profile IS NULL OR EXISTS (SELECT 1 FROM auth.userProfileGrant AS pg WHERE pg.userProfileId = @profile
                    AND pg.roleId = a.roleId AND pg.isDeleted = 0 AND (pg.tenantId = @acting OR (pg.includeDescendants = 1 AND EXISTS
                    (SELECT 1 FROM auth.tenantClosure AS tc WHERE tc.ancestorTenantId = pg.tenantId AND tc.descendantTenantId = @acting AND tc.isDeleted = 0)))))) AS r;
    INSERT logs.screenVisit (screenVisitId, rootTenantId, actingTenantId, appUserId, sessionReference, profileId,
                            viewAsUserId, roleCodes, screenCode, navigationType, tabId, previousVisitId, createdBy, modifiedBy)
    VALUES (@screenVisitId, @root, @acting, @me, @reference, @profile, @viewAs, @roles, @screenCode,
            @navigationType, @tabId, @previousVisitId, N'MERIT usage', N'MERIT usage');
END;
SELECT @retryPredecessor AS retryPredecessor;
""",
    },
    {
        "schema": "logs", "name": "uspSetScreenDuration", "kind": "write", "grants": WEB, "ticket": "227",
        "desc": "Updates only the actor's own visit in the current logical session. Cumulative visible/focused time "
                "is capped at 30 minutes and monotonic, so retries/checkpoints cannot inflate it by summing.",
        "params": "@screenVisitId UNIQUEIDENTIFIER\n, @durationMs INT", "key": "N'screen duration'",
        "body": ACTIVE + VISIT_LOCK + """
IF @durationMs NOT BETWEEN 0 AND 1800000
BEGIN
    ;THROW 51422, N'Invalid visible duration.', 1;
END;
UPDATE logs.screenVisit SET durationMs = @durationMs
 WHERE screenVisitId = @screenVisitId AND appUserId = @me AND sessionReference = @reference AND isDeleted = 0
   AND (durationMs IS NULL OR durationMs < @durationMs);
""",
    },
    {
        "schema": "logs", "name": "uspGetScreenUsage", "kind": "read", "grants": WEB, "ticket": "227",
        "permission": "usage.view", "params": "@days INT = 30", "key": "CONCAT (N'days=', @days)",
        "desc": "Scoped usage aggregates, five paths into manual result entry, back-and-forth loops, strict observed "
                "search/person/entry/saved elapsed flow and the latest 100 sessions. Counts cover the full period; "
                "only the displayed session list is capped. Predecessors outside the reader's scope are not exposed.",
        "body": DAYS + VISIBLE + """
SELECT v.screenCode, CAST (NULL AS VARCHAR (1000)) AS roleCode, COUNT (*) AS visits,
       COUNT (DISTINCT v.sessionReference) AS sessions, COUNT (v.durationMs) AS timedVisits,
       AVG (CAST (v.durationMs AS FLOAT)) AS averageVisibleMs
  FROM #visits AS v GROUP BY v.screenCode
UNION ALL
SELECT v.screenCode, r.value AS roleCode, COUNT (*), COUNT (DISTINCT v.sessionReference), COUNT (v.durationMs), AVG (CAST (v.durationMs AS FLOAT))
  FROM #visits AS v CROSS APPLY STRING_SPLIT (v.roleCodes, ',') AS r GROUP BY v.screenCode, r.value;

SELECT v.screenVisitId, v.screenCode, v.sessionReference, v.eventUtc,
       p.screenCode AS p1, p.eventUtc AS t1, p2.screenCode AS p2, p2.eventUtc AS t2, p3.screenCode AS p3, p3.eventUtc AS t3
  INTO #paths FROM #visits AS v
  LEFT JOIN #visits AS p ON p.screenVisitId = v.previousVisitId
  LEFT JOIN #visits AS p2 ON p2.screenVisitId = p.previousVisitId
  LEFT JOIN #visits AS p3 ON p3.screenVisitId = p2.previousVisitId;

SELECT * FROM (
    SELECT TOP (5) 'intoResult' AS kind,
           CONCAT_WS (' > ', p3, p2, p1, screenCode) AS pathCodes, COUNT (*) AS occurrences,
           COUNT (DISTINCT sessionReference) AS sessions
      FROM #paths WHERE screenCode = 'result.new'
     GROUP BY CONCAT_WS (' > ', p3, p2, p1, screenCode) ORDER BY COUNT (*) DESC, pathCodes
) AS a
UNION ALL
SELECT * FROM (
    SELECT TOP (10) 'loop' AS kind, CONCAT_WS (' > ', p2, p1, screenCode) AS pathCodes, COUNT (*) AS occurrences,
           COUNT (DISTINCT sessionReference) AS sessions
      FROM #paths WHERE p2 = screenCode AND p1 <> screenCode
     GROUP BY CONCAT_WS (' > ', p2, p1, screenCode) ORDER BY COUNT (*) DESC, pathCodes
) AS b;

SELECT COUNT (*) AS completions, AVG (CAST (DATEDIFF (SECOND, t3, eventUtc) AS FLOAT)) AS averageElapsedSeconds
  FROM #paths WHERE screenCode = 'result.saved' AND p1 = 'result.new' AND p2 = 'person' AND p3 = 'people.search'
   AND DATEDIFF (SECOND, t3, eventUtc) BETWEEN 0 AND 86400;

SELECT TOP (100) v.sessionReference, v.appUserId, MAX (u.displayName) AS displayName,
       MIN (v.eventUtc) AS firstUtc, MAX (v.eventUtc) AS lastUtc, COUNT (*) AS visits, COUNT (*) OVER () AS totalCount
  FROM #visits AS v LEFT JOIN auth.appUser AS u ON u.appUserId = v.appUserId
 GROUP BY v.sessionReference, v.appUserId ORDER BY MAX (v.eventUtc) DESC, v.sessionReference;
""",
    },
    {
        "schema": "logs", "name": "uspGetScreenSession", "kind": "read", "grants": WEB, "ticket": "227",
        "permission": "usage.view", "params": "@sessionReference UNIQUEIDENTIFIER\n, @days INT = 30\n, @pageNumber INT = 1",
        "key": "N'scoped screen sequence'",
        "desc": "One non-secret session reference, ordered and paged by 100. Reads only in-scope visits and lifecycle "
                "events for that same actor/reference. No addresses, agents, scope keys or unscoped visits are returned.",
        "body": DAYS + """
IF @pageNumber NOT BETWEEN 1 AND 100000
BEGIN
    ;THROW 51422, N'Choose a valid page.', 1;
END;
""" + VISIBLE + """
SELECT screenVisitId, eventUtc, screenCode, navigationType, tabId,
       CASE WHEN EXISTS (SELECT 1 FROM #visits AS p WHERE p.screenVisitId = v.previousVisitId) THEN previousVisitId END AS previousVisitId, roleCodes,
       actingTenantId, profileId, viewAsUserId, durationMs, COUNT (*) OVER () AS totalCount
  FROM #visits AS v WHERE sessionReference = @sessionReference
 ORDER BY eventUtc, screenVisitId OFFSET (@pageNumber - 1) * 100 ROWS FETCH NEXT 100 ROWS ONLY;

SELECT e.eventUtc, e.eventType, e.reason FROM logs.signInEvent AS e
 WHERE e.isDeleted = 0 AND e.sessionReference = @sessionReference
   AND e.eventType IN ('sessionStarted', 'signedOut', 'sessionExpired', 'sessionRevoked')
   AND EXISTS (SELECT 1 FROM #visits AS v WHERE v.sessionReference = @sessionReference AND v.appUserId = e.appUserId)
 ORDER BY e.eventUtc, e.signInEventId;
""",
    },
    {
        "schema": "logs", "name": "uspPurgeScreenVisits", "kind": "write", "grants": WORKER, "ticket": "227",
        "params": "@retentionDays INT = 90", "key": "CONCAT (N'retentionDays=', @retentionDays)",
        "desc": "Anonymizes and soft-deletes expired usage in batches, including standard audit metadata. Keeps only "
                "root, screen and date for anonymous totals. Never updates or deletes the immutable audit ledger.",
        "body": """
IF @retentionDays NOT BETWEEN 30 AND 365
BEGIN
    ;THROW 51422, N'Retention must be from 30 to 365 days.', 1;
END;
DECLARE @count INT = 0, @batch INT = 1;
WHILE @batch > 0
BEGIN
    UPDATE TOP (5000) logs.screenVisit
       SET appUserId = NULL, sessionReference = NULL, profileId = NULL, viewAsUserId = NULL,
           actingTenantId = NULL, roleCodes = NULL, tabId = NULL, previousVisitId = NULL, durationMs = NULL,
           eventUtc = CAST (CAST (eventUtc AS DATE) AS DATETIME2 (3)), isDeleted = 1,
           createdDateUtc = CAST (CAST (eventUtc AS DATE) AS DATETIME2 (3)), navigationType = 'server',
           createdBy = N'MERIT usage retention', modifiedBy = N'MERIT usage retention', deletedBy = N'MERIT usage retention'
     WHERE eventUtc < DATEADD (DAY, -@retentionDays, SYSUTCDATETIME ()) AND isDeleted = 0;
    SET @batch = @@ROWCOUNT;
    SET @count += @batch;
END;
SELECT @count AS purged;
""",
    },
]
