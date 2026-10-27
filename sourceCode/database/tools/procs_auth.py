"""Authentication, session scope and user administration procedures (schema auth)."""
from procs_common import NEWID, WEB, BOTH, CONCURRENCY

PROCS = [
    {
        "schema": "auth", "name": "uspGetUserForSignIn", "kind": "read", "grants": BOTH, "ticket": "T040",
        "desc": "Returns the account row the sign-in page needs to verify a password and second factor. Runs before any "
                "session exists, so it reads auth tables only, which carry no row-level security.",
        "params": "@userName NVARCHAR (100)",
        "key": "N'userName supplied'",
        "notes": "The user name is not written to @KeyParameters: a mistyped password pasted into the user name field "
                 "would otherwise land in the log.",
        "example": "exec auth.uspGetUserForSignIn @userName = N'cmanager'",
        "body": """
SELECT u.appUserId
     , u.rootTenantId
     , u.userName
     , u.displayName
     , u.passwordHash
     , u.securityStamp
     , u.mfaSecretProtected
     , u.mfaEnabled
     , u.failedSignInCount
     , u.lockoutEndUtc
     , u.isActive
     , CAST (CASE WHEN EXISTS (SELECT 1
                                 FROM auth.userAssignment AS ua
                                 JOIN auth.profile        AS p ON p.profileId = ua.profileId
                                WHERE ua.appUserId = u.appUserId AND ua.isDeleted = 0 AND p.isAdministrator = 1)
                 THEN 1 ELSE 0 END AS BIT) AS isAdministrator
  FROM auth.appUser AS u
 WHERE u.userName  = @userName
   AND u.isDeleted = 0;
""",
    },
    {
        "schema": "auth", "name": "uspRecordSignInResult", "kind": "write", "grants": WEB, "ticket": "T040",
        "desc": "Records the outcome of a sign-in attempt: resets the failure count on success, counts failures and locks "
                "the account after repeated failures, and writes the sign-in audit event.",
        "params": """@appUserId         UNIQUEIDENTIFIER = NULL
, @succeeded         BIT
, @maxFailedAttempts INT = 5
, @lockoutMinutes    INT = 15
, @clientAddress     VARCHAR (45) = NULL""",
        "key": "CONCAT (N'appUserId=', @appUserId, N', succeeded=', @succeeded)",
        "body": f"""
DECLARE @root UNIQUEIDENTIFIER = (SELECT rootTenantId FROM auth.appUser WHERE appUserId = @appUserId);

IF @appUserId IS NOT NULL AND @succeeded = 1
BEGIN
    UPDATE auth.appUser
       SET failedSignInCount = 0
         , lockoutEndUtc     = NULL
         , lastSignInUtc     = SYSUTCDATETIME ()
     WHERE appUserId = @appUserId;
END
ELSE IF @appUserId IS NOT NULL
BEGIN
    UPDATE auth.appUser
       SET failedSignInCount = failedSignInCount + 1
         , lockoutEndUtc     = CASE WHEN failedSignInCount + 1 >= @maxFailedAttempts
                                    THEN DATEADD (MINUTE, @lockoutMinutes, SYSUTCDATETIME ())
                                    ELSE lockoutEndUtc END
     WHERE appUserId = @appUserId;
END;

INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId, clientAddress)
VALUES (@appUserId, @root, CASE WHEN @succeeded = 1 THEN 'signIn' ELSE 'signInFailed' END, 'appUser', @appUserId,
        @clientAddress);

SET @Comments = CASE WHEN @succeeded = 1 THEN N'Sign-in succeeded.' ELSE N'Sign-in failed.' END;
""",
    },
    {
        "schema": "auth", "name": "uspCreateSessionScope", "kind": "write", "grants": BOTH, "ticket": "T040",
        "desc": "Resolves a user's effective access at sign-in: every tenant in their current assignments (with "
                "descendants where the assignment includes them) and the union of their profiles' permissions. The "
                "returned scope key goes into the session cookie and into SESSION_CONTEXT on every connection.",
        "params": """@sessionScopeId UNIQUEIDENTIFIER
, @appUserId      UNIQUEIDENTIFIER
, @expiresUtc     DATETIME2 (3)""",
        "key": "CONCAT (N'sessionScopeId=', @sessionScopeId, N', appUserId=', @appUserId)",
        "notes": "Returns three result sets: the scope summary (root, acting tenant, display name), the permission codes, "
                 "and the tenants covered. The acting tenant is the user's shallowest current assignment; new rows that "
                 "have no other ownership rule are owned by it. The worker calls this for the user a job records, so "
                 "background work runs under the same security as the request that started it.",
        "body": f"""
DECLARE @today DATE = auth.udfSessionToday ();
DECLARE @root  UNIQUEIDENTIFIER = (SELECT rootTenantId
                                      FROM auth.appUser
                                     WHERE appUserId = @appUserId AND isActive = 1 AND isDeleted = 0);

IF @root IS NULL
BEGIN
    ;THROW 51404, N'The account is not active.', 1;
END;

INSERT auth.sessionScope (sessionScopeId, appUserId, rootTenantId, expiresUtc)
VALUES (@sessionScopeId, @appUserId, @root, @expiresUtc);

INSERT auth.sessionScopeTenant (sessionScopeTenantId, sessionScopeId, tenantId)
SELECT {NEWID}, @sessionScopeId, x.tenantId
  FROM (SELECT DISTINCT tc.descendantTenantId AS tenantId
          FROM auth.userAssignment AS ua
          JOIN auth.tenantClosure  AS tc ON tc.ancestorTenantId = ua.tenantId
                                        AND (ua.includeDescendants = 1 OR tc.depth = 0)
                                        AND tc.isDeleted = 0
          JOIN auth.tenant         AS t  ON t.tenantId = tc.descendantTenantId
                                        AND t.rootTenantId = @root
                                        AND t.isActive = 1
                                        AND t.isDeleted = 0
         WHERE ua.appUserId = @appUserId
           AND ua.isDeleted = 0
           AND ua.startDate <= @today
           AND (ua.endDate IS NULL OR ua.endDate >= @today)) AS x;

INSERT auth.sessionScopePermission (sessionScopePermissionId, sessionScopeId, permissionCode)
SELECT {NEWID}, @sessionScopeId, x.permissionCode
  FROM (SELECT DISTINCT pm.permissionCode
          FROM auth.userAssignment    AS ua
          JOIN auth.profilePermission AS pp ON pp.profileId = ua.profileId AND pp.isDeleted = 0
          JOIN auth.permission        AS pm ON pm.permissionId = pp.permissionId AND pm.isDeleted = 0
         WHERE ua.appUserId = @appUserId
           AND ua.isDeleted = 0
           AND ua.startDate <= @today
           AND (ua.endDate IS NULL OR ua.endDate >= @today)) AS x;

DECLARE @acting UNIQUEIDENTIFIER =
    (SELECT TOP (1) ua.tenantId
       FROM auth.userAssignment AS ua
       JOIN auth.tenantClosure  AS tc ON tc.descendantTenantId = ua.tenantId AND tc.ancestorTenantId = @root
      WHERE ua.appUserId = @appUserId AND ua.isDeleted = 0
        AND ua.startDate <= @today AND (ua.endDate IS NULL OR ua.endDate >= @today)
      ORDER BY tc.depth, ua.startDate);

SELECT @sessionScopeId                AS sessionScopeId
     , u.appUserId
     , u.userName
     , u.displayName
     , u.rootTenantId
     , COALESCE (@acting, @root)      AS actingTenantId
     , t.tenantName                   AS actingTenantName
     , r.statePostalCode
     , u.securityStamp
     , @expiresUtc                    AS expiresUtc
  FROM auth.appUser AS u
  JOIN auth.tenant  AS r ON r.tenantId = u.rootTenantId
  LEFT JOIN auth.tenant AS t ON t.tenantId = COALESCE (@acting, @root)
 WHERE u.appUserId = @appUserId;

SELECT permissionCode FROM auth.sessionScopePermission WHERE sessionScopeId = @sessionScopeId ORDER BY permissionCode;

SELECT t.tenantId, t.tenantName, t.tenantType
  FROM auth.sessionScopeTenant AS st
  JOIN auth.tenant             AS t ON t.tenantId = st.tenantId
 WHERE st.sessionScopeId = @sessionScopeId
 ORDER BY t.tenantName;

SET @Comments = N'Session scope created.';
""",
    },
    {
        "schema": "auth", "name": "uspGetSessionScope", "kind": "read", "grants": BOTH, "ticket": "T040",
        "desc": "Validates a session scope on a cookie refresh: returns it only when it is unexpired, unrevoked and its "
                "user is still active with the same security stamp.",
        "params": "@sessionScopeId UNIQUEIDENTIFIER",
        "key": "CONCAT (N'sessionScopeId=', @sessionScopeId)",
        "body": """
SELECT ss.sessionScopeId
     , ss.appUserId
     , ss.rootTenantId
     , ss.expiresUtc
     , u.securityStamp
  FROM auth.sessionScope AS ss
  JOIN auth.appUser      AS u ON u.appUserId = ss.appUserId
 WHERE ss.sessionScopeId = @sessionScopeId
   AND ss.revokedUtc IS NULL
   AND ss.expiresUtc > SYSUTCDATETIME ()
   AND ss.isDeleted = 0
   AND u.isActive = 1
   AND u.isDeleted = 0;
""",
    },
    {
        "schema": "auth", "name": "uspRevokeSessionScope", "kind": "write", "grants": BOTH, "ticket": "T040",
        "desc": "Revokes a session scope at sign-out, so the row-level security predicates stop honoring it at once.",
        "params": "@sessionScopeId UNIQUEIDENTIFIER",
        "key": "CONCAT (N'sessionScopeId=', @sessionScopeId)",
        "body": """
DECLARE @user UNIQUEIDENTIFIER, @root UNIQUEIDENTIFIER;

UPDATE auth.sessionScope
   SET revokedUtc = SYSUTCDATETIME ()
     , @user      = appUserId
     , @root      = rootTenantId
 WHERE sessionScopeId = @sessionScopeId
   AND revokedUtc IS NULL;

IF @user IS NOT NULL
BEGIN
    INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId)
    VALUES (@user, @root, 'signOut', 'appUser', @user);
END;

SET @Comments = N'Session scope revoked.';
""",
    },
    {
        "schema": "auth", "name": "uspSaveMfaEnrollment", "kind": "write", "grants": WEB, "ticket": "T073",
        "desc": "Completes authenticator-app enrollment: stores the protected TOTP secret, replaces the recovery codes with "
                "a new set and changes the security stamp so other sessions must sign in again.",
        "params": """@appUserId              UNIQUEIDENTIFIER
, @mfaSecretProtected     NVARCHAR (500)
, @recoveryCodeHashesJson NVARCHAR (MAX)""",
        "key": "CONCAT (N'appUserId=', @appUserId)",
        "validate": """IF ISJSON (@recoveryCodeHashesJson) = 0
BEGIN
    ;THROW 50000, N'@recoveryCodeHashesJson must be a JSON array of strings.', 1;
END;""",
        "body": f"""
UPDATE auth.appUser
   SET mfaSecretProtected = @mfaSecretProtected
     , mfaEnabled         = 1
     , securityStamp      = NEWID ()
 WHERE appUserId = @appUserId AND isDeleted = 0;

UPDATE auth.mfaRecoveryCode SET isDeleted = 1 WHERE appUserId = @appUserId AND isDeleted = 0;

INSERT auth.mfaRecoveryCode (mfaRecoveryCodeId, appUserId, codeHash)
SELECT {NEWID}, @appUserId, j.[value]
  FROM OPENJSON (@recoveryCodeHashesJson) AS j;

INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId)
SELECT @appUserId, rootTenantId, 'mfaEnrolled', 'appUser', @appUserId FROM auth.appUser WHERE appUserId = @appUserId;

SET @Comments = N'MFA enrolled.';
""",
    },
    {
        "schema": "auth", "name": "uspGetRecoveryCodes", "kind": "read", "grants": WEB, "ticket": "T073",
        "desc": "Unused recovery code hashes for a user, so the sign-in page can verify a typed recovery code.",
        "params": "@appUserId UNIQUEIDENTIFIER",
        "key": "CONCAT (N'appUserId=', @appUserId)",
        "body": """
SELECT mfaRecoveryCodeId, codeHash
  FROM auth.mfaRecoveryCode
 WHERE appUserId = @appUserId AND usedUtc IS NULL AND isDeleted = 0;
""",
    },
    {
        "schema": "auth", "name": "uspUseRecoveryCode", "kind": "write", "grants": WEB, "ticket": "T073",
        "desc": "Marks a recovery code used so it can never be used again.",
        "params": "@mfaRecoveryCodeId UNIQUEIDENTIFIER",
        "key": "CONCAT (N'mfaRecoveryCodeId=', @mfaRecoveryCodeId)",
        "body": """
UPDATE auth.mfaRecoveryCode
   SET usedUtc = SYSUTCDATETIME ()
 WHERE mfaRecoveryCodeId = @mfaRecoveryCodeId AND usedUtc IS NULL AND isDeleted = 0;

SET @Comments = CONCAT (@@ROWCOUNT, N' code(s) used.');
""",
    },
    {
        "schema": "auth", "name": "uspListUsers", "kind": "read", "grants": WEB, "ticket": "T082",
        "permission": "tenant.administer",
        "desc": "Users of the caller's root tenant with their current assignments, for the user administration screen.",
        "params": "",
        "key": "NULL",
        "body": """
DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();

SELECT u.appUserId, u.userName, u.displayName, u.email, u.isActive, u.mfaEnabled, u.lastSignInUtc, u.lockoutEndUtc
     , STRING_AGG (CONCAT (p.profileName, N' @ ', t.tenantName), N'; ') WITHIN GROUP (ORDER BY p.profileName) AS assignments
  FROM auth.appUser AS u
  LEFT JOIN auth.userAssignment AS ua ON ua.appUserId = u.appUserId AND ua.isDeleted = 0
                                     AND (ua.endDate IS NULL OR ua.endDate >= auth.udfSessionToday ())
  LEFT JOIN auth.profile AS p ON p.profileId = ua.profileId
  LEFT JOIN auth.tenant  AS t ON t.tenantId = ua.tenantId
 WHERE u.rootTenantId = @root AND u.isDeleted = 0
 GROUP BY u.appUserId, u.userName, u.displayName, u.email, u.isActive, u.mfaEnabled, u.lastSignInUtc, u.lockoutEndUtc
 ORDER BY u.displayName;
""",
    },
    {
        "schema": "auth", "name": "uspGetUser", "kind": "read", "grants": WEB, "ticket": "T082",
        "permission": "tenant.administer",
        "desc": "One user of the caller's root tenant and all of their assignments.",
        "params": "@appUserId UNIQUEIDENTIFIER",
        "key": "CONCAT (N'appUserId=', @appUserId)",
        "body": """
DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();

SELECT appUserId, userName, displayName, email, isActive, mfaEnabled, lastSignInUtc, lockoutEndUtc
  FROM auth.appUser
 WHERE appUserId = @appUserId AND rootTenantId = @root AND isDeleted = 0;

SELECT ua.userAssignmentId, ua.profileId, p.profileName, ua.tenantId, t.tenantName, ua.includeDescendants,
       ua.startDate, ua.endDate
  FROM auth.userAssignment AS ua
  JOIN auth.profile AS p ON p.profileId = ua.profileId
  JOIN auth.tenant  AS t ON t.tenantId = ua.tenantId
 WHERE ua.appUserId = @appUserId AND ua.isDeleted = 0 AND t.rootTenantId = @root
 ORDER BY ua.startDate DESC;
""",
    },
    {
        "schema": "auth", "name": "uspSaveUser", "kind": "write", "grants": WEB, "ticket": "T082",
        "permission": "tenant.administer",
        "desc": "Creates or updates a built-in account in the caller's root tenant. A new account must enroll in MFA at "
                "first sign-in.",
        "params": """@appUserId    UNIQUEIDENTIFIER
, @userName     NVARCHAR (100)
, @displayName  NVARCHAR (120)
, @email        NVARCHAR (200) = NULL
, @isActive     BIT = 1
, @passwordHash NVARCHAR (500) = NULL""",
        "key": "CONCAT (N'appUserId=', @appUserId)",
        "body": """
DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();

IF EXISTS (SELECT 1 FROM auth.appUser WHERE appUserId = @appUserId)
BEGIN
    UPDATE auth.appUser
       SET displayName   = @displayName
         , email         = @email
         , isActive      = @isActive
         , passwordHash  = COALESCE (@passwordHash, passwordHash)
         , securityStamp = CASE WHEN @passwordHash IS NOT NULL OR @isActive = 0 THEN NEWID () ELSE securityStamp END
     WHERE appUserId = @appUserId AND rootTenantId = @root AND isDeleted = 0;

    IF @@ROWCOUNT = 0
    BEGIN
        ;THROW 51404, N'User was not found.', 1;
    END;
END
ELSE
BEGIN
    IF @passwordHash IS NULL
    BEGIN
        ;THROW 51422, N'A new account needs an initial password.', 1;
    END;

    IF EXISTS (SELECT 1 FROM auth.appUser WHERE userName = @userName AND isDeleted = 0)
    BEGIN
        ;THROW 51422, N'That user name is already taken.', 1;
    END;

    INSERT auth.appUser (appUserId, rootTenantId, userName, displayName, email, passwordHash, securityStamp, isActive)
    VALUES (@appUserId, @root, @userName, @displayName, @email, @passwordHash, NEWID (), @isActive);
END;

SET @Comments = N'User saved.';
""",
    },
    {
        "schema": "auth", "name": "uspSaveUserAssignment", "kind": "write", "grants": WEB, "ticket": "T082",
        "permission": "tenant.administer",
        "desc": "Adds or ends a profile assignment. A tenant administrator cannot grant a permission they do not hold "
                "themselves, and can assign only within their own tenant set.",
        "params": """@userAssignmentId   UNIQUEIDENTIFIER
, @appUserId          UNIQUEIDENTIFIER
, @profileId          UNIQUEIDENTIFIER
, @tenantId           UNIQUEIDENTIFIER
, @includeDescendants BIT = 1
, @startDate          DATE
, @endDate            DATE = NULL""",
        "key": "CONCAT (N'userAssignmentId=', @userAssignmentId, N', appUserId=', @appUserId)",
        "body": """
DECLARE @scope UNIQUEIDENTIFIER = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER);

IF USER_NAME () <> N'dbo'
   AND NOT EXISTS (SELECT 1 FROM auth.sessionScopeTenant WHERE sessionScopeId = @scope AND tenantId = @tenantId)
BEGIN
    ;THROW 51403, N'You can assign profiles only at tenants you administer.', 1;
END;

IF USER_NAME () <> N'dbo'
   AND EXISTS (SELECT 1
                 FROM auth.profilePermission AS pp
                 JOIN auth.permission        AS pm ON pm.permissionId = pp.permissionId
                WHERE pp.profileId = @profileId AND pp.isDeleted = 0
                  AND NOT EXISTS (SELECT 1 FROM auth.sessionScopePermission AS sp
                                   WHERE sp.sessionScopeId = @scope AND sp.permissionCode = pm.permissionCode))
BEGIN
    ;THROW 51403, N'That profile grants permissions you do not hold, so you cannot assign it.', 1;
END;

IF EXISTS (SELECT 1 FROM auth.userAssignment WHERE userAssignmentId = @userAssignmentId)
BEGIN
    UPDATE auth.userAssignment
       SET endDate = @endDate, includeDescendants = @includeDescendants
     WHERE userAssignmentId = @userAssignmentId AND isDeleted = 0;
END
ELSE
BEGIN
    INSERT auth.userAssignment (userAssignmentId, appUserId, profileId, tenantId, includeDescendants, startDate, endDate)
    VALUES (@userAssignmentId, @appUserId, @profileId, @tenantId, @includeDescendants, @startDate, @endDate);
END;

-- An assignment change invalidates the user's cached permissions: end their open sessions.
UPDATE auth.sessionScope SET revokedUtc = SYSUTCDATETIME () WHERE appUserId = @appUserId AND revokedUtc IS NULL;

SET @Comments = N'Assignment saved; user sessions revoked.';
""",
    },
    {
        "schema": "auth", "name": "uspResetUserMfa", "kind": "write", "grants": WEB, "ticket": "T082",
        "permission": "tenant.administer",
        "desc": "Clears a user's second factor after the administrator has verified the person, so they enroll again at "
                "next sign-in. Revokes their sessions.",
        "params": "@appUserId UNIQUEIDENTIFIER",
        "key": "CONCAT (N'appUserId=', @appUserId)",
        "body": """
DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();

UPDATE auth.appUser
   SET mfaEnabled = 0, mfaSecretProtected = NULL, securityStamp = NEWID (), failedSignInCount = 0, lockoutEndUtc = NULL
 WHERE appUserId = @appUserId AND rootTenantId = @root AND isDeleted = 0;

UPDATE auth.mfaRecoveryCode SET isDeleted = 1 WHERE appUserId = @appUserId AND isDeleted = 0;
UPDATE auth.sessionScope SET revokedUtc = SYSUTCDATETIME () WHERE appUserId = @appUserId AND revokedUtc IS NULL;

SET @Comments = N'MFA reset.';
""",
    },
    {
        "schema": "auth", "name": "uspListProfiles", "kind": "read", "grants": WEB, "ticket": "T082",
        "desc": "Profiles defined at the caller's root tenant, with their permissions.",
        "params": "",
        "key": "NULL",
        "body": """
DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();

SELECT p.profileId, p.profileCode, p.profileName, p.isAdministrator, p.isDeidentified
     , STRING_AGG (pm.permissionCode, ', ') WITHIN GROUP (ORDER BY pm.permissionCode) AS permissions
  FROM auth.profile AS p
  LEFT JOIN auth.profilePermission AS pp ON pp.profileId = p.profileId AND pp.isDeleted = 0
  LEFT JOIN auth.permission        AS pm ON pm.permissionId = pp.permissionId
 WHERE p.rootTenantId = @root AND p.isDeleted = 0
 GROUP BY p.profileId, p.profileCode, p.profileName, p.isAdministrator, p.isDeidentified
 ORDER BY p.profileName;
""",
    },
    {
        "schema": "auth", "name": "uspListTenants", "kind": "read", "grants": BOTH, "ticket": "T020",
        "desc": "Tenants of the caller's root, with their parent and depth, for pickers and the tenant tree.",
        "params": "",
        "key": "NULL",
        "body": """
DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();

SELECT t.tenantId, t.parentTenantId, t.tenantCode, t.tenantName, t.tenantType, tc.depth
  FROM auth.tenant AS t
  JOIN auth.tenantClosure AS tc ON tc.descendantTenantId = t.tenantId AND tc.ancestorTenantId = @root
 WHERE t.rootTenantId = @root AND t.isDeleted = 0
 ORDER BY tc.depth, t.tenantName;
""",
    },
]


# Access model v2 (procs_access.py) replaces the original user administration and session procedures.
from procs_access import PROCS as _ACCESS  # noqa: E402

_REPLACED = {p["name"] for p in _ACCESS} | {"uspListProfiles"}
PROCS = [p for p in PROCS if p["name"] not in _REPLACED] + _ACCESS
