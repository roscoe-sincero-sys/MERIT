"""Sign-in event log (#191): recording, a user's sign-in history, attack-pattern queries, unknown user names, and purge.

Events go to logs.signInEvent, kept apart from the tamper-evident ledger: IP addresses and user agents are personal
data, and ledger rows can never be changed, while these rows lose their personal data when they age out. Tenant administrators see events for the accounts whose home
tenant they administer. Attempts at names that match no account belong to no jurisdiction, so only holders of
security.investigate see them: hashed, and decrypted only through the web application after a step-up, with each
reveal audited in the ledger.
"""
from procs_common import NEWID, WEB, WORKER

# Accounts the caller may see events for: home tenant administered, or everything with security.investigate.
SCOPE = """DECLARE @all BIT = auth.udfHasPermission (N'security.investigate');
DECLARE @admin TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @admin SELECT tenantId FROM auth.tvfAdminTenants ();
DECLARE @accounts TABLE (appUserId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @accounts SELECT u.appUserId FROM auth.appUser AS u JOIN @admin AS a ON a.tenantId = u.homeTenantId WHERE u.isDeleted = 0;"""

FAILURES = "'passwordFailed', 'unknownUser', 'mfaFailed', 'attemptWhileLocked', 'accountInactive'"

PROCS = [
    {
        "schema": "auth", "name": "uspGetSessionScopeState", "kind": "read", "grants": WEB, "ticket": "T213",
        "desc": "Why a session check failed, for the sign-in event log: the scope's account, expiry, revocation and the "
                "account's current security stamp. Returns nothing for an unknown scope.",
        "params": "@sessionScopeId UNIQUEIDENTIFIER",
        "key": "N'session state'",
        "notes": "The scope key is not written to @KeyParameters.",
        "body": """
SELECT s.appUserId, s.expiresUtc, s.revokedUtc, u.securityStamp
  FROM auth.sessionScope AS s
  JOIN auth.appUser      AS u ON u.appUserId = s.appUserId
 WHERE s.sessionScopeId = @sessionScopeId;
""",
    },
    {
        "schema": "logs", "name": "uspRecordSignInEvent", "kind": "write", "grants": WEB, "ticket": "T213",
        "desc": "Writes one sign-in or account-security event. The session is passed by its scope key and stored by its "
                "non-secret reference. For a known account the failed-attempt count, lockout end and data root are "
                "read from the account itself, so the row shows the state the event left behind.",
        "params": """@eventType              VARCHAR (40)
, @step                   VARCHAR (20) = NULL
, @method                 VARCHAR (20) = NULL
, @reason                 VARCHAR (60) = NULL
, @appUserId              UNIQUEIDENTIFIER = NULL
, @actorUserId            UNIQUEIDENTIFIER = NULL
, @attemptedNameHash      VARBINARY (32) = NULL
, @attemptedNameProtected NVARCHAR (1000) = NULL
, @clientAddress          VARCHAR (45) = NULL
, @forwardedFor           VARCHAR (500) = NULL
, @userAgent              NVARCHAR (400) = NULL
, @sessionScopeId         UNIQUEIDENTIFIER = NULL
, @attemptId              UNIQUEIDENTIFIER = NULL
, @traceId                VARCHAR (64) = NULL""",
        "key": "CONCAT (N'eventType=', @eventType, N', appUserId=', @appUserId)",
        "notes": "Neither the attempted name, the address nor the user agent goes to @KeyParameters.",
        "body": f"""
DECLARE @root UNIQUEIDENTIFIER, @failed INT, @lockout DATETIME2 (3), @reference UNIQUEIDENTIFIER;

IF @appUserId IS NOT NULL
BEGIN
    SELECT @root = rootTenantId, @failed = failedSignInCount
         , @lockout = CASE WHEN lockoutEndUtc > SYSUTCDATETIME () THEN lockoutEndUtc END
      FROM auth.appUser
     WHERE appUserId = @appUserId;
END;

IF @sessionScopeId IS NOT NULL
BEGIN
    SELECT @reference = sessionReference FROM auth.sessionScope WHERE sessionScopeId = @sessionScopeId;
END;

INSERT logs.signInEvent (signInEventId, eventType, step, method, reason, appUserId, actorUserId, rootTenantId,
                         attemptedNameHash, attemptedNameProtected, clientAddress, forwardedFor, userAgent,
                         sessionReference, attemptId, traceId, failedSignInCount, lockoutEndUtc)
VALUES ({NEWID}, @eventType, @step, @method, @reason, @appUserId, @actorUserId, @root,
        @attemptedNameHash, @attemptedNameProtected, @clientAddress, @forwardedFor, LEFT (@userAgent, 400),
        @reference, @attemptId, @traceId, @failed, @lockout);

SET @Comments = CONCAT (N'Sign-in event ', @eventType, N'.');
""",
    },
    {
        "schema": "logs", "name": "uspListSignInEvents", "kind": "read", "grants": WEB, "ticket": "T213",
        "permission": "tenant.administer",
        "desc": "One account's sign-in history, newest first, for an administrator of its home tenant (or a holder of "
                "security.investigate). Never returns attempted names.",
        "params": """@appUserId UNIQUEIDENTIFIER
, @top       INT = 100""",
        "key": "CONCAT (N'appUserId=', @appUserId, N', top=', @top)",
        "body": f"""
{SCOPE}

SELECT TOP (CASE WHEN @top BETWEEN 1 AND 1000 THEN @top ELSE 100 END)
       e.eventUtc, e.eventType, e.step, e.method, e.reason, e.clientAddress, e.forwardedFor, e.userAgent,
       e.sessionReference, e.attemptId, e.traceId, e.failedSignInCount, e.lockoutEndUtc,
       e.actorUserId, a.displayName AS actorDisplayName
  FROM logs.signInEvent AS e
  LEFT JOIN auth.appUser AS a ON a.appUserId = e.actorUserId
 WHERE e.appUserId = @appUserId AND e.isDeleted = 0
   AND (@all = 1 OR EXISTS (SELECT 1 FROM @accounts AS x WHERE x.appUserId = @appUserId))
 ORDER BY e.eventUtc DESC;
""",
    },
    {
        "schema": "logs", "name": "uspGetSignInActivity", "kind": "read", "grants": WEB, "ticket": "T213",
        "permission": "tenant.administer",
        "desc": "Suspicious sign-in patterns over the last @hours: failures by address and by account, one address "
                "trying many accounts (spraying), many addresses trying one account (stuffing), a full sign-in after "
                "repeated failures, and lockouts and rate-limit refusals. Tenant administrators see their accounts; "
                "holders of security.investigate also see unknown names (hashed) and rate-limit refusals.",
        "params": """@hours              INT = 24
, @sprayThreshold     INT = 5
, @stuffingThreshold  INT = 3
, @failuresBefore     INT = 3""",
        "key": "CONCAT (N'hours=', @hours)",
        "body": f"""
{SCOPE}
DECLARE @since DATETIME2 (3) = DATEADD (HOUR, -CASE WHEN @hours BETWEEN 1 AND 2160 THEN @hours ELSE 24 END, SYSUTCDATETIME ());

DECLARE @e TABLE (eventUtc DATETIME2 (3), eventType VARCHAR (40), reason VARCHAR (60), appUserId UNIQUEIDENTIFIER,
                  attemptedNameHash VARBINARY (32), clientAddress VARCHAR (45));
INSERT @e
SELECT e.eventUtc, e.eventType, e.reason, e.appUserId, e.attemptedNameHash, e.clientAddress
  FROM logs.signInEvent AS e
 WHERE e.eventUtc >= @since AND e.isDeleted = 0
   AND (@all = 1 OR e.appUserId IN (SELECT appUserId FROM @accounts));

-- 1. Failures by address.
SELECT TOP (50) clientAddress
     , SUM (CASE WHEN eventType IN ({FAILURES}) THEN 1 ELSE 0 END)            AS failures
     , COUNT (DISTINCT CASE WHEN eventType IN ({FAILURES}) THEN appUserId END) AS distinctAccounts
     , COUNT (DISTINCT CASE WHEN eventType = 'unknownUser' THEN attemptedNameHash END) AS distinctUnknownNames
     , SUM (CASE WHEN eventType = 'accountLocked' THEN 1 ELSE 0 END)            AS lockouts
     , SUM (CASE WHEN eventType = 'rateLimited' THEN 1 ELSE 0 END)              AS rateLimited
     , MIN (eventUtc) AS firstUtc, MAX (eventUtc) AS lastUtc
  FROM @e
 WHERE clientAddress IS NOT NULL
 GROUP BY clientAddress
HAVING SUM (CASE WHEN eventType IN ({FAILURES}, 'accountLocked', 'rateLimited') THEN 1 ELSE 0 END) > 0
 ORDER BY failures DESC, lastUtc DESC;

-- 2. Failures by account.
SELECT TOP (50) e.appUserId, u.userName, u.displayName
     , SUM (CASE WHEN e.eventType IN ({FAILURES}) THEN 1 ELSE 0 END)  AS failures
     , COUNT (DISTINCT CASE WHEN e.eventType IN ({FAILURES}) THEN e.clientAddress END) AS distinctAddresses
     , SUM (CASE WHEN e.eventType = 'accountLocked' THEN 1 ELSE 0 END) AS lockouts
     , MAX (e.eventUtc) AS lastUtc
  FROM @e AS e
  JOIN auth.appUser AS u ON u.appUserId = e.appUserId
 GROUP BY e.appUserId, u.userName, u.displayName
HAVING SUM (CASE WHEN e.eventType IN ({FAILURES}, 'accountLocked') THEN 1 ELSE 0 END) > 0
 ORDER BY failures DESC, lastUtc DESC;

-- 3. Spraying: one address, many different accounts or unknown names.
SELECT clientAddress
     , COUNT (DISTINCT COALESCE (CONVERT (VARCHAR (36), appUserId), CONVERT (VARCHAR (64), attemptedNameHash, 2))) AS targets
     , COUNT (*) AS failures, MIN (eventUtc) AS firstUtc, MAX (eventUtc) AS lastUtc
  FROM @e
 WHERE eventType IN ({FAILURES}) AND clientAddress IS NOT NULL
 GROUP BY clientAddress
HAVING COUNT (DISTINCT COALESCE (CONVERT (VARCHAR (36), appUserId), CONVERT (VARCHAR (64), attemptedNameHash, 2))) >= @sprayThreshold
 ORDER BY targets DESC;

-- 4. Stuffing: one account (or one unknown name) tried from many addresses.
SELECT e.appUserId, u.userName, u.displayName, CONVERT (VARCHAR (64), e.attemptedNameHash, 2) AS attemptedNameHash
     , COUNT (DISTINCT e.clientAddress) AS addresses, COUNT (*) AS failures, MAX (e.eventUtc) AS lastUtc
  FROM @e AS e
  LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
 WHERE e.eventType IN ({FAILURES})
 GROUP BY e.appUserId, u.userName, u.displayName, e.attemptedNameHash
HAVING COUNT (DISTINCT e.clientAddress) >= @stuffingThreshold
 ORDER BY addresses DESC;

-- 5. A full sign-in after repeated failures on the same account within the hour before it.
SELECT s.appUserId, u.userName, u.displayName, s.eventUtc AS successUtc, s.clientAddress AS successAddress
     , f.failures, f.addresses
  FROM @e AS s
  JOIN auth.appUser AS u ON u.appUserId = s.appUserId
 CROSS APPLY (SELECT COUNT (*) AS failures, COUNT (DISTINCT x.clientAddress) AS addresses
                FROM @e AS x
               WHERE x.appUserId = s.appUserId AND x.eventType IN ({FAILURES})
                 AND x.eventUtc < s.eventUtc AND x.eventUtc >= DATEADD (HOUR, -1, s.eventUtc)) AS f
 WHERE s.eventType = 'sessionStarted' AND f.failures >= @failuresBefore
 ORDER BY s.eventUtc DESC;

-- 6. Lockouts and rate-limit refusals.
SELECT TOP (100) e.eventUtc, e.eventType, e.reason, e.appUserId, u.userName, e.clientAddress
  FROM @e AS e
  LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
 WHERE e.eventType IN ('accountLocked', 'rateLimited')
 ORDER BY e.eventUtc DESC;
""",
    },
    {
        "schema": "logs", "name": "uspListUnknownUserNames", "kind": "read", "grants": WEB, "ticket": "T213",
        "permission": "security.investigate",
        "desc": "Attempts at user names that match no account over the last @hours, grouped by the name's keyed hash: "
                "attempts, addresses, first and last time. With @includeProtected = 1 each group also carries one "
                "encrypted copy of the name, which the web application decrypts after a step-up and audits.",
        "params": """@hours            INT = 168
, @includeProtected BIT = 0""",
        "key": "CONCAT (N'hours=', @hours, N', includeProtected=', @includeProtected)",
        "body": """
DECLARE @since DATETIME2 (3) = DATEADD (HOUR, -CASE WHEN @hours BETWEEN 1 AND 8760 THEN @hours ELSE 168 END, SYSUTCDATETIME ());

SELECT TOP (500) CONVERT (VARCHAR (64), e.attemptedNameHash, 2)                  AS attemptedNameHash
     , COUNT (*)                                                                   AS attempts
     , COUNT (DISTINCT e.clientAddress)                                            AS addresses
     , MIN (e.eventUtc)                                                            AS firstUtc
     , MAX (e.eventUtc)                                                            AS lastUtc
     , CASE WHEN @includeProtected = 1 THEN MAX (e.attemptedNameProtected) END     AS attemptedNameProtected
     , (SELECT STRING_AGG (a.clientAddress, ', ')
          FROM (SELECT DISTINCT TOP (5) x.clientAddress
                  FROM logs.signInEvent AS x
                 WHERE x.attemptedNameHash = e.attemptedNameHash AND x.eventUtc >= @since AND x.clientAddress IS NOT NULL
                   AND x.isDeleted = 0) AS a)
                                                                                   AS topAddresses
  FROM logs.signInEvent AS e
 WHERE e.eventType = 'unknownUser' AND e.attemptedNameHash IS NOT NULL AND e.eventUtc >= @since AND e.isDeleted = 0
 GROUP BY e.attemptedNameHash
 ORDER BY attempts DESC, lastUtc DESC;
""",
    },
    {
        "schema": "logs", "name": "uspFindSignInAttemptsByName", "kind": "read", "grants": WEB, "ticket": "T213",
        "permission": "security.investigate",
        "desc": "Every attempt at one user name, found by its keyed hash: the web application hashes the name typed "
                "into the lookup the same way. Nothing is decrypted.",
        "params": """@attemptedNameHash VARBINARY (32)
, @hours             INT = 2160""",
        "key": "CONCAT (N'hours=', @hours)",
        "notes": "The typed name never reaches the database, only its hash.",
        "body": """
DECLARE @since DATETIME2 (3) = DATEADD (HOUR, -CASE WHEN @hours BETWEEN 1 AND 8760 THEN @hours ELSE 2160 END, SYSUTCDATETIME ());

SELECT TOP (500) e.eventUtc, e.eventType, e.clientAddress, e.forwardedFor, e.userAgent, e.attemptId, e.traceId
  FROM logs.signInEvent AS e
 WHERE e.attemptedNameHash = @attemptedNameHash AND e.eventUtc >= @since AND e.isDeleted = 0
 ORDER BY e.eventUtc DESC;
""",
    },
    {
        "schema": "logs", "name": "uspPurgeSignInEvents", "kind": "write", "grants": WORKER, "ticket": "T213",
        "desc": "Purges sign-in events older than @retentionDays (at least 30), in batches: the personal data (address, "
                "forwarded chain, user agent, and both copies of an attempted name) is removed and the row is marked "
                "deleted, leaving only an anonymous count. This database never hard-deletes. The worker runs it daily "
                "(SignInLog:RetentionDays).",
        "params": """@retentionDays INT = 365""",
        "key": "CONCAT (N'retentionDays=', @retentionDays)",
        "validate": """IF @retentionDays < 30
BEGIN
    ;THROW 50000, N'@retentionDays must be at least 30.', 1;
END;""",
        "body": """
DECLARE @cutoff DATETIME2 (3) = DATEADD (DAY, -@retentionDays, SYSUTCDATETIME ());
DECLARE @deleted INT = 0, @batch INT = 1;

WHILE @batch > 0
BEGIN
    UPDATE TOP (5000) logs.signInEvent
       SET clientAddress = NULL, forwardedFor = NULL, userAgent = NULL, attemptedNameHash = NULL,
           attemptedNameProtected = NULL, isDeleted = 1
     WHERE eventUtc < @cutoff AND isDeleted = 0;
    SET @batch = @@ROWCOUNT;
    SET @deleted += @batch;
END;

SELECT @deleted AS purged;

SET @Comments = CONCAT (@deleted, N' sign-in event(s) purged.');
""",
    },
]
