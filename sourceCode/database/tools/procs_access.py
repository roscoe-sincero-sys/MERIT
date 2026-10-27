"""Access model v2: sessions from profiles, view-as, administration scoped to the tenant tree, self-service profiles.

procs_auth.py includes these definitions and drops its older versions of the same procedures.

Vocabulary (also the user documentation's): a role is a named bundle of permissions; a grant gives a role at a tenant,
for that tenant only or with everything below it, at an access level (read, write, full = write plus soft delete); a
user's master profile is their assignments, which only administrators change; other profiles are subsets the user makes
for daily work. An administrator administers the tenants where they hold tenant.administer, and below them when the
grant includes descendants; they manage accounts whose home tenant is there and grant roles there, never more than they
hold there themselves. A platform administrator manages every account and role and sees no records.
"""
from procs_common import NEWID, WEB, BOTH

PLATFORM = "CAST (HASHBYTES ('MD5', N'tenant:PLATFORM') AS UNIQUEIDENTIFIER)"
RANK = "CASE {0} WHEN 'full' THEN 3 WHEN 'write' THEN 2 ELSE 1 END"
LEVEL = "CASE {0} WHEN 3 THEN 'full' WHEN 2 THEN 'write' ELSE 'read' END"

ADMIN_TENANTS = """DECLARE @admin TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @admin SELECT tenantId FROM auth.tvfAdminTenants ();"""

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
                                 JOIN auth.role           AS r ON r.roleId = ua.roleId
                                WHERE ua.appUserId = u.appUserId AND ua.isDeleted = 0 AND r.isAdministrator = 1)
                 THEN 1 ELSE 0 END AS BIT) AS isAdministrator
  FROM auth.appUser AS u
 WHERE u.userName  = @userName
   AND u.isDeleted = 0;
""",
    },
    {
        "schema": "auth", "name": "uspCreateSessionScope", "kind": "write", "grants": BOTH, "ticket": "T160",
        "desc": "Resolves what a session may see and do: from the master profile, from one of the user's own profiles "
                "clipped to the master, or (view as) from another user's master profile clipped to the tenants the "
                "caller administers and to the caller's own rights. Stores the tenants with an access level each, the "
                "data roots, the administration scope and the permissions; the key goes into the session cookie and "
                "into SESSION_CONTEXT on every connection.",
        "params": """@sessionScopeId UNIQUEIDENTIFIER
, @appUserId      UNIQUEIDENTIFIER
, @expiresUtc     DATETIME2 (3)
, @userProfileId  UNIQUEIDENTIFIER = NULL
, @viewAsUserId   UNIQUEIDENTIFIER = NULL
, @actingTenantId UNIQUEIDENTIFIER = NULL""",
        "key": "CONCAT (N'sessionScopeId=', @sessionScopeId, N', appUserId=', @appUserId, N', userProfileId=', @userProfileId, N', viewAsUserId=', @viewAsUserId)",
        "notes": "Returns three result sets: the scope summary, the permission codes, and the tenants covered with their "
                 "access level. A platform administrator has one profile: switching and viewing as are refused. The "
                 "worker calls this for the user a job records, so background work runs under the same security as "
                 "the request that started it.",
        "body": f"""
DECLARE @platform UNIQUEIDENTIFIER = {PLATFORM};
DECLARE @isPlatformUser BIT = (SELECT CASE WHEN h.tenantType = 'platform' THEN 1 ELSE 0 END
                                  FROM auth.appUser AS u
                                  LEFT JOIN auth.tenant AS h ON h.tenantId = u.homeTenantId
                                 WHERE u.appUserId = @appUserId AND u.isActive = 1 AND u.isDeleted = 0);

IF @isPlatformUser IS NULL
BEGIN
    ;THROW 51404, N'The account is not active.', 1;
END;

-- Grants are dated today in the user's jurisdiction (issue #36). A signed-in caller's connection already carries the
-- zone; a sign-in does not yet, so it is set here from the user's data root.
IF SESSION_CONTEXT (N'timeZone') IS NULL
BEGIN
    DECLARE @zone VARCHAR (64) = (SELECT r.timeZone FROM auth.appUser AS u JOIN auth.tenant AS r ON r.tenantId = u.rootTenantId
                                   WHERE u.appUserId = @appUserId);
    EXEC sys.sp_set_session_context @key = N'timeZone', @value = @zone;
END;

IF @userProfileId IS NOT NULL
   AND EXISTS (SELECT 1 FROM auth.userProfile WHERE userProfileId = @userProfileId AND isMaster = 1)
BEGIN
    SET @userProfileId = NULL;
END;

IF @isPlatformUser = 1 AND (@userProfileId IS NOT NULL OR @viewAsUserId IS NOT NULL)
BEGIN
    ;THROW 51403, N'A platform administrator has one profile and cannot switch profiles or view as another user.', 1;
END;

DECLARE @master TABLE (tenantId UNIQUEIDENTIFIER, permissionCode VARCHAR (60), permissionLevel VARCHAR (10), grantRank INT,
                       PRIMARY KEY (tenantId, permissionCode));
INSERT @master SELECT tenantId, permissionCode, permissionLevel, grantRank FROM auth.tvfUserGrantPermissions (@appUserId);

DECLARE @x TABLE (tenantId UNIQUEIDENTIFIER, permissionCode VARCHAR (60), permissionLevel VARCHAR (10), grantRank INT,
                  PRIMARY KEY (tenantId, permissionCode));
DECLARE @profileName NVARCHAR (120) = N'Master profile';

IF @viewAsUserId IS NOT NULL
BEGIN
    IF @viewAsUserId = @appUserId
    BEGIN
        ;THROW 51422, N'You are already viewing MERIT as yourself.', 1;
    END;

    IF NOT EXISTS (SELECT 1 FROM auth.appUser AS u JOIN auth.tenant AS h ON h.tenantId = u.homeTenantId
                    WHERE u.appUserId = @viewAsUserId AND u.isActive = 1 AND u.isDeleted = 0 AND h.tenantType <> 'platform')
    BEGIN
        ;THROW 51404, N'That user was not found or is not active.', 1;
    END;

    -- What they can see and do, only where the caller administers, and never more than the caller may do there.
    INSERT @x
    SELECT v.tenantId, v.permissionCode, v.permissionLevel, IIF (v.grantRank < m.grantRank, v.grantRank, m.grantRank)
      FROM auth.tvfUserGrantPermissions (@viewAsUserId) AS v
      JOIN @master AS m ON m.tenantId = v.tenantId AND m.permissionCode = v.permissionCode
     WHERE EXISTS (SELECT 1 FROM @master AS a WHERE a.tenantId = v.tenantId AND a.permissionCode = 'tenant.administer');

    IF NOT EXISTS (SELECT 1 FROM @x)
    BEGIN
        ;THROW 51403, N'You can view MERIT as a user only where you administer their access.', 1;
    END;

    SET @profileName = (SELECT CONCAT (N'Viewing as ', displayName) FROM auth.appUser WHERE appUserId = @viewAsUserId);
END
ELSE IF @userProfileId IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM auth.userProfile WHERE userProfileId = @userProfileId AND appUserId = @appUserId
                                                    AND isActive = 1 AND isDeleted = 0)
    BEGIN
        ;THROW 51404, N'That profile was not found or is not active.', 1;
    END;

    -- A profile is clipped to the master as it is today, so it never outlives a narrowed master.
    INSERT @x
    SELECT p.tenantId, p.permissionCode, p.permissionLevel, IIF (p.grantRank < m.grantRank, p.grantRank, m.grantRank)
      FROM auth.tvfProfileGrantPermissions (@userProfileId) AS p
      JOIN @master AS m ON m.tenantId = p.tenantId AND m.permissionCode = p.permissionCode;

    SET @profileName = (SELECT profileName FROM auth.userProfile WHERE userProfileId = @userProfileId);
END
ELSE
BEGIN
    INSERT @x SELECT tenantId, permissionCode, permissionLevel, grantRank FROM @master;
END;

-- Records: tenants where the scope holds a read or write permission, at the highest level held there.
DECLARE @tenants TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY, accessRank INT);
INSERT @tenants SELECT tenantId, MAX (grantRank) FROM @x WHERE permissionLevel IN ('read', 'write') GROUP BY tenantId;

DECLARE @adminScope TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @adminScope SELECT DISTINCT tenantId FROM @x WHERE permissionCode = 'tenant.administer';

DECLARE @isPlatformAdmin BIT = CASE WHEN @isPlatformUser = 1
                                     AND EXISTS (SELECT 1 FROM @x WHERE permissionCode = 'platform.administer')
                                    THEN 1 ELSE 0 END;

-- Acting tenant: the one asked for when the scope covers it, else the highest tenant in the scope.
DECLARE @acting UNIQUEIDENTIFIER =
    CASE WHEN EXISTS (SELECT 1 FROM @tenants WHERE tenantId = @actingTenantId)
           OR EXISTS (SELECT 1 FROM @adminScope WHERE tenantId = @actingTenantId) THEN @actingTenantId END;
IF @acting IS NULL
BEGIN
    SET @acting = (SELECT TOP (1) s.tenantId
                     FROM (SELECT tenantId FROM @tenants UNION SELECT tenantId FROM @adminScope) AS s
                     JOIN auth.tenantClosure AS c ON c.descendantTenantId = s.tenantId AND c.ancestorTenantId = @platform
                                                 AND c.isDeleted = 0
                    ORDER BY c.depth, s.tenantId);
END;
IF @acting IS NULL
BEGIN
    SET @acting = (SELECT homeTenantId FROM auth.appUser WHERE appUserId = COALESCE (@viewAsUserId, @appUserId));
END;

DECLARE @root UNIQUEIDENTIFIER = COALESCE ((SELECT rootTenantId FROM auth.tenant WHERE tenantId = @acting), @platform);

INSERT auth.sessionScope (sessionScopeId, appUserId, rootTenantId, actingTenantId, userProfileId, viewAsUserId,
                          isPlatformAdmin, expiresUtc)
VALUES (@sessionScopeId, @appUserId, @root, @acting, @userProfileId, @viewAsUserId, @isPlatformAdmin, @expiresUtc);

INSERT auth.sessionScopeTenant (sessionScopeTenantId, sessionScopeId, tenantId, accessLevel)
SELECT {NEWID}, @sessionScopeId, tenantId, {LEVEL.format('accessRank')} FROM @tenants;

INSERT auth.sessionScopeRoot (sessionScopeRootId, sessionScopeId, rootTenantId, accessLevel)
SELECT {NEWID}, @sessionScopeId, r.rootTenantId, {LEVEL.format('r.accessRank')}
  FROM (SELECT t.rootTenantId, MAX (x.accessRank) AS accessRank
          FROM @tenants AS x JOIN auth.tenant AS t ON t.tenantId = x.tenantId
         GROUP BY t.rootTenantId) AS r;

INSERT auth.sessionScopeAdminTenant (sessionScopeAdminTenantId, sessionScopeId, tenantId)
SELECT {NEWID}, @sessionScopeId, tenantId FROM @adminScope;

INSERT auth.sessionScopePermission (sessionScopePermissionId, sessionScopeId, permissionCode)
SELECT {NEWID}, @sessionScopeId, p.permissionCode FROM (SELECT DISTINCT permissionCode FROM @x) AS p;

IF @viewAsUserId IS NOT NULL
BEGIN
    INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId)
    VALUES (@appUserId, @root, 'view', 'viewAsUser', @viewAsUserId);
END;

SELECT @sessionScopeId                AS sessionScopeId
     , u.appUserId
     , u.userName
     , u.displayName
     , @root                          AS rootTenantId
     , @acting                        AS actingTenantId
     , t.tenantName                   AS actingTenantName
     , r.statePostalCode
     , COALESCE (r.timeZone, 'Eastern Standard Time') AS timeZone
     , u.securityStamp
     , @expiresUtc                    AS expiresUtc
     , @userProfileId                 AS userProfileId
     , @profileName                   AS profileName
     , @viewAsUserId                  AS viewAsUserId
     , v.displayName                  AS viewAsDisplayName
     , @isPlatformAdmin               AS isPlatformAdmin
  FROM auth.appUser AS u
  LEFT JOIN auth.tenant  AS r ON r.tenantId = @root
  LEFT JOIN auth.tenant  AS t ON t.tenantId = @acting
  LEFT JOIN auth.appUser AS v ON v.appUserId = @viewAsUserId
 WHERE u.appUserId = @appUserId;

SELECT permissionCode FROM auth.sessionScopePermission WHERE sessionScopeId = @sessionScopeId ORDER BY permissionCode;

SELECT t.tenantId, t.tenantName, t.tenantType, st.accessLevel
  FROM auth.sessionScopeTenant AS st
  JOIN auth.tenant             AS t ON t.tenantId = st.tenantId
 WHERE st.sessionScopeId = @sessionScopeId
 ORDER BY t.tenantName;

SET @Comments = CONCAT (N'Session scope created: ', @profileName, N'.');
""",
    },
    {
        "schema": "auth", "name": "uspListUsers", "kind": "read", "grants": WEB, "ticket": "T160",
        "permission": "tenant.administer",
        "desc": "The accounts an administrator can see: people whose home tenant they administer, and people holding a "
                "grant at a tenant they administer (whose accounts they cannot change). Every account for a platform "
                "administrator.",
        "params": "",
        "key": "NULL",
        "body": f"""
{ADMIN_TENANTS}

SELECT u.appUserId, u.userName, u.displayName, u.email, u.isActive, u.mfaEnabled, u.lastSignInUtc, u.lockoutEndUtc
     , h.tenantName AS homeTenantName, h.tenantType AS homeTenantType
     , CAST (CASE WHEN EXISTS (SELECT 1 FROM @admin AS a WHERE a.tenantId = u.homeTenantId) THEN 1 ELSE 0 END AS BIT)
           AS canManageAccount
     , (SELECT STRING_AGG (CONCAT (r.roleName, N' @ ', t.tenantName,
                                   CASE ua.accessLevel WHEN 'full' THEN N'' ELSE CONCAT (N' (', ua.accessLevel, N' only)') END),
                           N'; ') WITHIN GROUP (ORDER BY r.roleName)
          FROM auth.userAssignment AS ua
          JOIN auth.role   AS r ON r.roleId = ua.roleId
          JOIN auth.tenant AS t ON t.tenantId = ua.tenantId
         WHERE ua.appUserId = u.appUserId AND ua.isDeleted = 0
           AND (ua.endDate IS NULL OR ua.endDate >= auth.udfSessionToday ())) AS assignments
  FROM auth.appUser AS u
  LEFT JOIN auth.tenant AS h ON h.tenantId = u.homeTenantId
 WHERE u.isDeleted = 0
   AND (EXISTS (SELECT 1 FROM @admin AS a WHERE a.tenantId = u.homeTenantId)
        OR EXISTS (SELECT 1 FROM auth.userAssignment AS ua JOIN @admin AS a ON a.tenantId = ua.tenantId
                    WHERE ua.appUserId = u.appUserId AND ua.isDeleted = 0
                      AND (ua.endDate IS NULL OR ua.endDate >= auth.udfSessionToday ())))
 ORDER BY u.displayName;
""",
    },
    {
        "schema": "auth", "name": "uspGetUser", "kind": "read", "grants": WEB, "ticket": "T160",
        "permission": "tenant.administer",
        "desc": "One account the administrator can see, with every grant in its master profile and whether the "
                "administrator may change each (only grants at tenants they administer).",
        "params": "@appUserId UNIQUEIDENTIFIER",
        "key": "CONCAT (N'appUserId=', @appUserId)",
        "body": f"""
{ADMIN_TENANTS}

SELECT u.appUserId, u.userName, u.displayName, u.email, u.isActive, u.mfaEnabled, u.lastSignInUtc, u.lockoutEndUtc,
       u.homeTenantId, h.tenantName AS homeTenantName, h.tenantType AS homeTenantType,
       CAST (CASE WHEN EXISTS (SELECT 1 FROM @admin AS a WHERE a.tenantId = u.homeTenantId) THEN 1 ELSE 0 END AS BIT)
           AS canManageAccount
  FROM auth.appUser AS u
  LEFT JOIN auth.tenant AS h ON h.tenantId = u.homeTenantId
 WHERE u.appUserId = @appUserId AND u.isDeleted = 0
   AND (EXISTS (SELECT 1 FROM @admin AS a WHERE a.tenantId = u.homeTenantId)
        OR EXISTS (SELECT 1 FROM auth.userAssignment AS ua JOIN @admin AS a ON a.tenantId = ua.tenantId
                    WHERE ua.appUserId = u.appUserId AND ua.isDeleted = 0));

SELECT ua.userAssignmentId, ua.roleId, r.roleName, ua.tenantId, t.tenantName, ua.includeDescendants, ua.accessLevel,
       ua.startDate, ua.endDate,
       CAST (CASE WHEN EXISTS (SELECT 1 FROM @admin AS a WHERE a.tenantId = ua.tenantId) THEN 1 ELSE 0 END AS BIT) AS canManage
  FROM auth.userAssignment AS ua
  JOIN auth.role   AS r ON r.roleId = ua.roleId
  JOIN auth.tenant AS t ON t.tenantId = ua.tenantId
 WHERE ua.appUserId = @appUserId AND ua.isDeleted = 0
 ORDER BY ua.startDate DESC;
""",
    },
    {
        "schema": "auth", "name": "uspFindUserForGrant", "kind": "read", "grants": WEB, "ticket": "T160",
        "permission": "tenant.administer",
        "desc": "Finds an account by its exact user name, so an administrator can give a person who works elsewhere "
                "(for example in two counties) access at a tenant they administer.",
        "params": "@userName NVARCHAR (100)",
        "key": "N'userName supplied'",
        "body": """
SELECT u.appUserId, u.userName, u.displayName, h.tenantName AS homeTenantName
  FROM auth.appUser AS u
  JOIN auth.tenant  AS h ON h.tenantId = u.homeTenantId
 WHERE u.userName = @userName AND u.isDeleted = 0 AND u.isActive = 1
   AND (h.tenantType <> 'platform' OR auth.udfIsPlatformAdmin () = 1);
""",
    },
    {
        "schema": "auth", "name": "uspSaveUser", "kind": "write", "grants": WEB, "ticket": "T160",
        "permission": "tenant.administer",
        "desc": "Creates or updates an account. New accounts work for a tenant the administrator administers and get an "
                "empty master profile; an existing account can be changed only by an administrator of its home tenant. "
                "Nobody can deactivate their own account. A new account must enroll in MFA at first sign-in.",
        "params": """@appUserId    UNIQUEIDENTIFIER
, @userName     NVARCHAR (100)
, @displayName  NVARCHAR (120)
, @email        NVARCHAR (200) = NULL
, @isActive     BIT = 1
, @passwordHash NVARCHAR (500) = NULL
, @homeTenantId UNIQUEIDENTIFIER = NULL""",
        "key": "CONCAT (N'appUserId=', @appUserId, N', homeTenantId=', @homeTenantId)",
        "body": f"""
{ADMIN_TENANTS}
DECLARE @me UNIQUEIDENTIFIER = auth.udfSessionUserId ();
DECLARE @root UNIQUEIDENTIFIER = (SELECT rootTenantId FROM auth.tenant WHERE tenantId = @homeTenantId AND isDeleted = 0);

IF @homeTenantId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM @admin WHERE tenantId = @homeTenantId)
BEGIN
    ;THROW 51403, N'You can place people only in tenants you administer.', 1;
END;

IF EXISTS (SELECT 1 FROM auth.appUser WHERE appUserId = @appUserId)
BEGIN
    IF NOT EXISTS (SELECT 1 FROM auth.appUser AS u JOIN @admin AS a ON a.tenantId = u.homeTenantId
                    WHERE u.appUserId = @appUserId AND u.isDeleted = 0)
    BEGIN
        ;THROW 51403, N'Only an administrator of the tenant this person works for can change their account.', 1;
    END;

    IF @appUserId = @me AND @isActive = 0
    BEGIN
        ;THROW 51422, N'You cannot deactivate your own account.', 1;
    END;

    UPDATE auth.appUser
       SET displayName   = @displayName
         , email         = @email
         , isActive      = @isActive
         , homeTenantId  = COALESCE (@homeTenantId, homeTenantId)
         , rootTenantId  = COALESCE (@root, rootTenantId)
         , passwordHash  = COALESCE (@passwordHash, passwordHash)
         , securityStamp = CASE WHEN @passwordHash IS NOT NULL OR @isActive = 0 THEN NEWID () ELSE securityStamp END
     WHERE appUserId = @appUserId AND isDeleted = 0;

    IF @isActive = 0
    BEGIN
        UPDATE auth.sessionScope SET revokedUtc = SYSUTCDATETIME () WHERE appUserId = @appUserId AND revokedUtc IS NULL;
    END;
END
ELSE
BEGIN
    IF @homeTenantId IS NULL OR @root IS NULL
    BEGIN
        ;THROW 51422, N'Choose the tenant this person works for.', 1;
    END;

    IF @passwordHash IS NULL
    BEGIN
        ;THROW 51422, N'A new account needs an initial password.', 1;
    END;

    IF EXISTS (SELECT 1 FROM auth.appUser WHERE userName = @userName AND isDeleted = 0)
    BEGIN
        ;THROW 51422, N'That user name is already taken.', 1;
    END;

    INSERT auth.appUser (appUserId, rootTenantId, homeTenantId, userName, displayName, email, passwordHash, securityStamp,
                         isActive)
    VALUES (@appUserId, @root, @homeTenantId, @userName, @displayName, @email, @passwordHash, NEWID (), @isActive);

    INSERT auth.userProfile (userProfileId, appUserId, profileName, profileDescription, isMaster, isActive)
    VALUES (CAST (HASHBYTES ('MD5', CONCAT (N'masterProfile:', @appUserId)) AS UNIQUEIDENTIFIER), @appUserId,
            N'Master profile', N'Everything you have been given. Managed by your administrator.', 1, 1);
END;

SET @Comments = N'User saved.';
""",
    },
    {
        "schema": "auth", "name": "uspSaveUserAssignment", "kind": "write", "grants": WEB, "ticket": "T160",
        "permission": "tenant.administer",
        "desc": "Adds, changes or ends a grant in a user's master profile. Only at tenants the administrator "
                "administers; never in their own master profile; a role from the tenant's part of the tree (platform "
                "roles only at the platform, for platform accounts); and, except for a platform administrator, never a "
                "permission or access level the administrator does not hold at that tenant. The user's sessions end.",
        "params": """@userAssignmentId   UNIQUEIDENTIFIER
, @appUserId          UNIQUEIDENTIFIER
, @roleId             UNIQUEIDENTIFIER
, @tenantId           UNIQUEIDENTIFIER
, @includeDescendants BIT = 1
, @accessLevel        VARCHAR (10) = 'full'
, @startDate          DATE
, @endDate            DATE = NULL""",
        "key": "CONCAT (N'userAssignmentId=', @userAssignmentId, N', appUserId=', @appUserId, N', tenantId=', @tenantId)",
        "validate": """IF @accessLevel IS NULL OR @accessLevel NOT IN ('read', 'write', 'full')
BEGIN
    ;THROW 50000, N'@accessLevel must be read, write or full.', 1;
END;""",
        "body": f"""
{ADMIN_TENANTS}
DECLARE @platform UNIQUEIDENTIFIER = {PLATFORM};
DECLARE @me UNIQUEIDENTIFIER = auth.udfSessionUserId ();
DECLARE @rank INT = {RANK.format('@accessLevel')};

IF USER_NAME () <> N'dbo' AND @appUserId = @me
BEGIN
    ;THROW 51403, N'Nobody can change their own master profile. Ask another administrator.', 1;
END;

IF EXISTS (SELECT 1 FROM auth.userAssignment WHERE userAssignmentId = @userAssignmentId)
BEGIN
    IF NOT EXISTS (SELECT 1 FROM auth.userAssignment AS ua JOIN @admin AS a ON a.tenantId = ua.tenantId
                    WHERE ua.userAssignmentId = @userAssignmentId AND ua.appUserId = @appUserId AND ua.isDeleted = 0)
    BEGIN
        ;THROW 51403, N'That grant is at a tenant you do not administer.', 1;
    END;

    UPDATE auth.userAssignment
       SET endDate = @endDate
     WHERE userAssignmentId = @userAssignmentId AND isDeleted = 0;
END
ELSE
BEGIN
    IF NOT EXISTS (SELECT 1 FROM @admin WHERE tenantId = @tenantId)
    BEGIN
        ;THROW 51403, N'You can grant roles only at tenants you administer.', 1;
    END;

    DECLARE @roleRoot UNIQUEIDENTIFIER = (SELECT rootTenantId FROM auth.role WHERE roleId = @roleId AND isDeleted = 0);
    DECLARE @targetIsPlatform BIT = (SELECT CASE WHEN h.tenantType = 'platform' THEN 1 ELSE 0 END
                                       FROM auth.appUser AS u JOIN auth.tenant AS h ON h.tenantId = u.homeTenantId
                                      WHERE u.appUserId = @appUserId AND u.isDeleted = 0);

    IF @roleRoot IS NULL OR @targetIsPlatform IS NULL
    BEGIN
        ;THROW 51404, N'The user or the role was not found.', 1;
    END;

    DECLARE @roleIsPlatform BIT = IIF (@roleRoot = @platform, 1, 0);
    DECLARE @tenantIsPlatform BIT = IIF (@tenantId = @platform, 1, 0);
    IF @roleIsPlatform <> @tenantIsPlatform OR @roleIsPlatform <> @targetIsPlatform
    BEGIN
        ;THROW 51422, N'Platform accounts hold only platform roles, granted at the platform; everyone else holds record roles at a region, state, tribe or jurisdiction.', 1;
    END;

    IF @roleRoot <> @platform
       AND NOT EXISTS (SELECT 1 FROM auth.tenantClosure WHERE ancestorTenantId = @roleRoot AND descendantTenantId = @tenantId
                                                          AND isDeleted = 0)
    BEGIN
        ;THROW 51422, N'That role belongs to another part of the tenant tree.', 1;
    END;

    -- No one grants what they do not hold, at that tenant and that access level; the platform administrator excepted.
    IF auth.udfIsPlatformAdmin () = 0
       AND EXISTS (SELECT 1
                     FROM auth.rolePermission AS rp
                     JOIN auth.permission     AS pm ON pm.permissionId = rp.permissionId AND pm.isDeleted = 0
                    WHERE rp.roleId = @roleId AND rp.isDeleted = 0
                      AND (@accessLevel <> 'read' OR pm.accessLevel = 'read')
                      AND NOT EXISTS (SELECT 1 FROM auth.tvfUserGrantPermissions (@me) AS m
                                       WHERE m.tenantId = @tenantId AND m.permissionCode = pm.permissionCode
                                         AND m.grantRank >= @rank))
    BEGIN
        ;THROW 51403, N'That role, at that access level, includes rights you do not have at that tenant, so you cannot grant it.', 1;
    END;

    INSERT auth.userAssignment (userAssignmentId, appUserId, roleId, tenantId, includeDescendants, accessLevel, startDate,
                                endDate)
    VALUES (@userAssignmentId, @appUserId, @roleId, @tenantId, @includeDescendants, @accessLevel, @startDate, @endDate);
END;

-- A grant change invalidates the user's resolved access: end their open sessions.
UPDATE auth.sessionScope SET revokedUtc = SYSUTCDATETIME () WHERE appUserId = @appUserId AND revokedUtc IS NULL;

SET @Comments = N'Grant saved; user sessions revoked.';
""",
    },
    {
        "schema": "auth", "name": "uspResetUserMfa", "kind": "write", "grants": WEB, "ticket": "T160",
        "permission": "tenant.administer",
        "desc": "Clears a user's second factor after the administrator has verified the person, so they enroll again at "
                "next sign-in. Only for accounts whose home tenant the administrator administers, and not their own.",
        "params": "@appUserId UNIQUEIDENTIFIER",
        "key": "CONCAT (N'appUserId=', @appUserId)",
        "body": f"""
{ADMIN_TENANTS}

IF USER_NAME () <> N'dbo' AND @appUserId = auth.udfSessionUserId ()
BEGIN
    ;THROW 51403, N'Ask another administrator to reset your own second factor.', 1;
END;

IF NOT EXISTS (SELECT 1 FROM auth.appUser AS u JOIN @admin AS a ON a.tenantId = u.homeTenantId
                WHERE u.appUserId = @appUserId AND u.isDeleted = 0)
BEGIN
    ;THROW 51403, N'Only an administrator of the tenant this person works for can reset their second factor.', 1;
END;

UPDATE auth.appUser
   SET mfaEnabled = 0, mfaSecretProtected = NULL, securityStamp = NEWID (), failedSignInCount = 0, lockoutEndUtc = NULL
 WHERE appUserId = @appUserId AND isDeleted = 0;

UPDATE auth.mfaRecoveryCode SET isDeleted = 1 WHERE appUserId = @appUserId AND isDeleted = 0;
UPDATE auth.sessionScope SET revokedUtc = SYSUTCDATETIME () WHERE appUserId = @appUserId AND revokedUtc IS NULL;

SET @Comments = N'MFA reset.';
""",
    },
    {
        "schema": "auth", "name": "uspListRoles", "kind": "read", "grants": WEB, "ticket": "T160",
        "desc": "Roles the caller can work with: for administrators, roles usable at the tenants they administer; for "
                "anyone, the roles in their own master profile (for building their own profiles). Platform roles only "
                "for a platform administrator.",
        "params": "",
        "key": "NULL",
        "body": f"""
{ADMIN_TENANTS}
DECLARE @platform UNIQUEIDENTIFIER = {PLATFORM};
DECLARE @me UNIQUEIDENTIFIER = auth.udfSessionUserId ();

SELECT r.roleId, r.roleCode, r.roleName, r.isAdministrator, r.isDeidentified, r.rootTenantId, rt.tenantName AS rootTenantName
     , (SELECT STRING_AGG (pm.permissionCode, ', ') WITHIN GROUP (ORDER BY pm.permissionCode)
          FROM auth.rolePermission AS rp JOIN auth.permission AS pm ON pm.permissionId = rp.permissionId
         WHERE rp.roleId = r.roleId AND rp.isDeleted = 0) AS permissions
  FROM auth.role   AS r
  JOIN auth.tenant AS rt ON rt.tenantId = r.rootTenantId
 WHERE r.isDeleted = 0
   AND (r.rootTenantId <> @platform OR auth.udfIsPlatformAdmin () = 1)
   AND (auth.udfIsPlatformAdmin () = 1
        OR EXISTS (SELECT 1 FROM @admin AS a
                     JOIN auth.tenantClosure AS c ON c.descendantTenantId = a.tenantId AND c.ancestorTenantId = r.rootTenantId
                                                 AND c.isDeleted = 0)
        OR EXISTS (SELECT 1 FROM auth.userAssignment AS ua WHERE ua.appUserId = @me AND ua.roleId = r.roleId
                                                             AND ua.isDeleted = 0))
 ORDER BY rt.tenantName, r.roleName;
""",
    },
    {
        "schema": "auth", "name": "uspListTenants", "kind": "read", "grants": BOTH, "ticket": "T160",
        "desc": "Tenants for pickers and the tenant tree: those the caller administers, those whose records the session "
                "covers, and those in the caller's master profile; every tenant for a platform administrator. Depth is "
                "counted from the platform tenant; canAdminister marks the tenants the caller may add tenants under.",
        "params": "",
        "key": "NULL",
        "body": f"""
{ADMIN_TENANTS}
DECLARE @platform UNIQUEIDENTIFIER = {PLATFORM};
DECLARE @me UNIQUEIDENTIFIER = auth.udfSessionUserId ();

DECLARE @ids TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @ids
SELECT tenantId FROM @admin
UNION
SELECT st.tenantId
  FROM auth.sessionScopeTenant AS st
 WHERE st.sessionScopeId = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER) AND st.isDeleted = 0
UNION
SELECT tenantId FROM auth.tvfUserGrantPermissions (@me);

SELECT t.tenantId, t.parentTenantId, t.tenantCode, t.tenantName, t.tenantType, t.rootTenantId, tc.depth,
       CAST (CASE WHEN EXISTS (SELECT 1 FROM @admin AS a WHERE a.tenantId = t.tenantId) THEN 1 ELSE 0 END AS BIT) AS canAdminister
  FROM auth.tenant AS t
  JOIN @ids AS i ON i.tenantId = t.tenantId
  JOIN auth.tenantClosure AS tc ON tc.descendantTenantId = t.tenantId AND tc.ancestorTenantId = @platform AND tc.isDeleted = 0
 WHERE t.isDeleted = 0
 ORDER BY tc.depth, t.tenantName;
""",
    },
    {
        "schema": "auth", "name": "uspListMyProfiles", "kind": "read", "grants": WEB, "ticket": "T160",
        "desc": "The signed-in user's profiles (the master first) and each profile's grants: the master's are the "
                "user's current assignments, the others are the grants the user chose.",
        "params": "",
        "key": "NULL",
        "body": """
DECLARE @me UNIQUEIDENTIFIER = auth.udfSessionUserId ();

SELECT p.userProfileId, p.profileName, p.profileDescription, p.isMaster, p.isActive
  FROM auth.userProfile AS p
 WHERE p.appUserId = @me AND p.isDeleted = 0
 ORDER BY p.isMaster DESC, p.isActive DESC, p.profileName;

SELECT p.userProfileId, ua.roleId, r.roleName, ua.tenantId, t.tenantName, ua.includeDescendants, ua.accessLevel
  FROM auth.userProfile    AS p
  JOIN auth.userAssignment AS ua ON ua.appUserId = p.appUserId AND ua.isDeleted = 0
                                AND ua.startDate <= auth.udfSessionToday ()
                                AND (ua.endDate IS NULL OR ua.endDate >= auth.udfSessionToday ())
  JOIN auth.role   AS r ON r.roleId = ua.roleId
  JOIN auth.tenant AS t ON t.tenantId = ua.tenantId
 WHERE p.appUserId = @me AND p.isMaster = 1 AND p.isDeleted = 0
UNION ALL
SELECT g.userProfileId, g.roleId, r.roleName, g.tenantId, t.tenantName, g.includeDescendants, g.accessLevel
  FROM auth.userProfile      AS p
  JOIN auth.userProfileGrant AS g ON g.userProfileId = p.userProfileId AND g.isDeleted = 0
  JOIN auth.role   AS r ON r.roleId = g.roleId
  JOIN auth.tenant AS t ON t.tenantId = g.tenantId
 WHERE p.appUserId = @me AND p.isMaster = 0 AND p.isDeleted = 0;
""",
    },
    {
        "schema": "auth", "name": "uspSaveMyProfile", "kind": "write", "grants": WEB, "ticket": "T160",
        "desc": "Creates, changes or deactivates one of the signed-in user's own profiles. Each grant must stay inside "
                "the master profile: every permission it gives, at every tenant it reaches, must be in the master there "
                "at the same or a higher access level. The master profile itself cannot be changed or deactivated here, "
                "and a platform administrator has no other profiles.",
        "params": """@userProfileId      UNIQUEIDENTIFIER
, @profileName        NVARCHAR (80)
, @profileDescription NVARCHAR (300) = NULL
, @isActive           BIT = 1
, @grantsJson         NVARCHAR (MAX)""",
        "key": "CONCAT (N'userProfileId=', @userProfileId, N', isActive=', @isActive)",
        "validate": """IF @profileName IS NULL OR LEN (LTRIM (RTRIM (@profileName))) = 0
BEGIN
    ;THROW 51422, N'Give the profile a name.', 1;
END;
IF ISJSON (@grantsJson) = 0
BEGIN
    ;THROW 50000, N'@grantsJson must be a JSON array of grants.', 1;
END;""",
        "body": f"""
DECLARE @me UNIQUEIDENTIFIER = auth.udfSessionUserId ();
DECLARE @msg NVARCHAR (2048);

IF @me IS NULL
   OR EXISTS (SELECT 1 FROM auth.appUser AS u JOIN auth.tenant AS h ON h.tenantId = u.homeTenantId
               WHERE u.appUserId = @me AND h.tenantType = 'platform')
BEGIN
    ;THROW 51403, N'A platform administrator has only the master profile.', 1;
END;

IF EXISTS (SELECT 1 FROM auth.userProfile WHERE userProfileId = @userProfileId)
   AND NOT EXISTS (SELECT 1 FROM auth.userProfile WHERE userProfileId = @userProfileId AND appUserId = @me
                                                    AND isMaster = 0 AND isDeleted = 0)
BEGIN
    ;THROW 51403, N'You can change only your own extra profiles. The master profile is managed by your administrator.', 1;
END;

DECLARE @g TABLE (tenantId UNIQUEIDENTIFIER, roleId UNIQUEIDENTIFIER, includeDescendants BIT, accessLevel VARCHAR (10));
INSERT @g (tenantId, roleId, includeDescendants, accessLevel)
SELECT tenantId, roleId, COALESCE (includeDescendants, 1), COALESCE (accessLevel, 'read')
  FROM OPENJSON (@grantsJson)
  WITH (tenantId UNIQUEIDENTIFIER '$.tenantId', roleId UNIQUEIDENTIFIER '$.roleId', includeDescendants BIT '$.includeDescendants',
        accessLevel VARCHAR (10) '$.accessLevel');

IF @isActive = 1 AND NOT EXISTS (SELECT 1 FROM @g)
BEGIN
    ;THROW 51422, N'Add at least one grant: a role, a tenant and an access level.', 1;
END;

IF EXISTS (SELECT 1 FROM @g WHERE tenantId IS NULL OR roleId IS NULL OR accessLevel NOT IN ('read', 'write', 'full'))
BEGIN
    ;THROW 51422, N'Each grant needs a tenant, a role and an access level of read, write or full.', 1;
END;

-- Never more than the master: every (tenant, permission) the profile would reach must be in the master there.
SET @msg = (SELECT TOP (1) CONCAT (N'This profile would give you more than your master profile: ', pm.permissionName,
                                   N' at ', t.tenantName, N' (', g.accessLevel, N').')
              FROM @g AS g
              JOIN auth.tenantClosure  AS tc ON tc.ancestorTenantId = g.tenantId
                                            AND (g.includeDescendants = 1 OR tc.depth = 0) AND tc.isDeleted = 0
              JOIN auth.tenant         AS t  ON t.tenantId = tc.descendantTenantId AND t.isActive = 1 AND t.isDeleted = 0
              JOIN auth.rolePermission AS rp ON rp.roleId = g.roleId AND rp.isDeleted = 0
              JOIN auth.permission     AS pm ON pm.permissionId = rp.permissionId AND pm.isDeleted = 0
             WHERE (g.accessLevel <> 'read' OR pm.accessLevel = 'read')
               AND NOT EXISTS (SELECT 1 FROM auth.tvfUserGrantPermissions (@me) AS m
                                WHERE m.tenantId = tc.descendantTenantId AND m.permissionCode = pm.permissionCode
                                  AND m.grantRank >= {RANK.format('g.accessLevel')})
             ORDER BY t.tenantName, pm.permissionCode);
IF @msg IS NOT NULL
BEGIN
    ;THROW 51422, @msg, 1;
END;

IF EXISTS (SELECT 1 FROM auth.userProfile WHERE userProfileId = @userProfileId)
BEGIN
    UPDATE auth.userProfile
       SET profileName = @profileName, profileDescription = @profileDescription, isActive = @isActive
     WHERE userProfileId = @userProfileId;
END
ELSE
BEGIN
    INSERT auth.userProfile (userProfileId, appUserId, profileName, profileDescription, isMaster, isActive)
    VALUES (@userProfileId, @me, @profileName, @profileDescription, 0, @isActive);
END;

UPDATE auth.userProfileGrant SET isDeleted = 1 WHERE userProfileId = @userProfileId AND isDeleted = 0;

INSERT auth.userProfileGrant (userProfileGrantId, userProfileId, roleId, tenantId, includeDescendants, accessLevel)
SELECT {NEWID}, @userProfileId, roleId, tenantId, includeDescendants, accessLevel FROM @g;

-- Sessions using the profile end, so a narrowed or deactivated profile takes effect at once.
UPDATE auth.sessionScope SET revokedUtc = SYSUTCDATETIME ()
 WHERE userProfileId = @userProfileId AND revokedUtc IS NULL
   AND sessionScopeId <> COALESCE (TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER), NEWID ());

SET @Comments = N'Profile saved.';
""",
    },
    {
        "schema": "auth", "name": "uspListViewAsUsers", "kind": "read", "grants": WEB, "ticket": "T160",
        "permission": "tenant.administer",
        "desc": "People an administrator can view MERIT as: active, not platform accounts, not themselves, working for "
                "or holding a grant at a tenant the administrator administers. None for a platform administrator.",
        "params": "",
        "key": "NULL",
        "body": f"""
{ADMIN_TENANTS}
DECLARE @me UNIQUEIDENTIFIER = auth.udfSessionUserId ();

SELECT u.appUserId, u.userName, u.displayName, h.tenantName AS homeTenantName
  FROM auth.appUser AS u
  JOIN auth.tenant  AS h ON h.tenantId = u.homeTenantId
 WHERE u.isActive = 1 AND u.isDeleted = 0 AND u.appUserId <> @me AND h.tenantType <> 'platform'
   AND auth.udfIsPlatformAdmin () = 0
   AND (EXISTS (SELECT 1 FROM @admin AS a WHERE a.tenantId = u.homeTenantId)
        OR EXISTS (SELECT 1 FROM auth.userAssignment AS ua JOIN @admin AS a ON a.tenantId = ua.tenantId
                    WHERE ua.appUserId = u.appUserId AND ua.isDeleted = 0))
 ORDER BY u.displayName;
""",
    },
]
