SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
GO

/*
    The platform tenant: the top of every installation's tenant tree. Its members manage accounts only; they see no
    records. Every data root (a region, state, tribe or stand-alone jurisdiction) hangs below it.

    Converges on every run:
      - creates the platform tenant and its closure row;
      - moves any tenant still without a parent under the platform (older installations had state roots at the top);
      - ships the Platform administrator role (tenant.administer, platform.administer, audit.view);
      - fills in each account's home tenant and master profile where missing.

    The first platform administrator account is created by Deploy-MeritDatabase.ps1 -PlatformAdminUserName (or, for
    the demo, by 900_demo).
*/

DECLARE @platform UNIQUEIDENTIFIER = CAST (HASHBYTES ('MD5', N'tenant:PLATFORM') AS UNIQUEIDENTIFIER);

IF NOT EXISTS (SELECT 1 FROM auth.tenant WHERE tenantId = @platform)
BEGIN
    INSERT auth.tenant (tenantId, parentTenantId, rootTenantId, tenantCode, tenantName, tenantType, statePostalCode)
    VALUES (@platform, NULL, @platform, 'PLATFORM', N'MERIT platform', 'platform', NULL);
END;

IF NOT EXISTS (SELECT 1 FROM auth.tenantClosure WHERE ancestorTenantId = @platform AND descendantTenantId = @platform)
BEGIN
    INSERT auth.tenantClosure (tenantClosureId, ancestorTenantId, descendantTenantId, depth)
    VALUES (CAST (HASHBYTES ('MD5', N'closure:PLATFORM:PLATFORM') AS UNIQUEIDENTIFIER), @platform, @platform, 0);
END;

-- Older trees: put each top-level tenant (and everything under it) below the platform.
DECLARE @tops TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @tops SELECT tenantId FROM auth.tenant WHERE parentTenantId IS NULL AND tenantId <> @platform AND isDeleted = 0;

UPDATE t SET parentTenantId = @platform
  FROM auth.tenant AS t JOIN @tops AS x ON x.tenantId = t.tenantId;

INSERT auth.tenantClosure (tenantClosureId, ancestorTenantId, descendantTenantId, depth)
SELECT NEWID (), @platform, c.descendantTenantId, c.depth + 1
  FROM auth.tenantClosure AS c
  JOIN @tops AS x ON x.tenantId = c.ancestorTenantId
 WHERE c.isDeleted = 0
   AND NOT EXISTS (SELECT 1 FROM auth.tenantClosure AS y WHERE y.ancestorTenantId = @platform
                                                         AND y.descendantTenantId = c.descendantTenantId AND y.isDeleted = 0);

-- The platform role. It holds no record permissions, so a platform administrator's sessions see no data at all.
DECLARE @role UNIQUEIDENTIFIER = CAST (HASHBYTES ('MD5', N'role:PLATFORM:platformAdministrator') AS UNIQUEIDENTIFIER);
IF NOT EXISTS (SELECT 1 FROM auth.role WHERE roleId = @role)
BEGIN
    INSERT auth.role (roleId, rootTenantId, roleCode, roleName, isAdministrator, isDeidentified)
    VALUES (@role, @platform, 'platformAdministrator', N'Platform administrator', 1, 0);
END;

INSERT auth.rolePermission (rolePermissionId, roleId, permissionId)
SELECT CAST (HASHBYTES ('MD5', N'rolePermission:PLATFORM:' + p.permissionCode) AS UNIQUEIDENTIFIER), @role, p.permissionId
  FROM auth.permission AS p
 WHERE p.permissionCode IN ('tenant.administer', 'platform.administer', 'audit.view') AND p.isDeleted = 0
   AND NOT EXISTS (SELECT 1 FROM auth.rolePermission AS x WHERE x.roleId = @role AND x.permissionId = p.permissionId
                                                          AND x.isDeleted = 0);

-- Home tenants: the shallowest tenant a user is assigned at, or their data root.
UPDATE u SET homeTenantId = COALESCE (
       (SELECT TOP (1) ua.tenantId
          FROM auth.userAssignment AS ua
          JOIN auth.tenantClosure  AS c ON c.descendantTenantId = ua.tenantId AND c.ancestorTenantId = @platform
         WHERE ua.appUserId = u.appUserId AND ua.isDeleted = 0
         ORDER BY c.depth, ua.startDate), u.rootTenantId)
  FROM auth.appUser AS u
 WHERE u.homeTenantId IS NULL;

-- Every account has exactly one master profile.
INSERT auth.userProfile (userProfileId, appUserId, profileName, profileDescription, isMaster, isActive)
SELECT CAST (HASHBYTES ('MD5', CONCAT (N'masterProfile:', u.appUserId)) AS UNIQUEIDENTIFIER), u.appUserId,
       N'Master profile', N'Everything you have been given. Managed by your administrator.', 1, 1
  FROM auth.appUser AS u
 WHERE NOT EXISTS (SELECT 1 FROM auth.userProfile AS p WHERE p.appUserId = u.appUserId AND p.isMaster = 1
                                                       AND p.isDeleted = 0);
GO

-- ------------------------------------------------------------------------------------------------ first platform user
-- Created only when a user name is passed (Deploy-MeritDatabase.ps1 -PlatformAdminUserName) and the account does not
-- exist yet. The password hash is computed by the deploy script (ASP.NET Core Identity V3); the password never reaches
-- SQL Server. The account must enroll MFA at first sign-in.
DECLARE @userName NVARCHAR (100) = N'$(PlatformAdminUserName)';
DECLARE @hash NVARCHAR (500) = N'$(PlatformAdminPasswordHash)';
DECLARE @platform UNIQUEIDENTIFIER = CAST (HASHBYTES ('MD5', N'tenant:PLATFORM') AS UNIQUEIDENTIFIER);
DECLARE @role UNIQUEIDENTIFIER = CAST (HASHBYTES ('MD5', N'role:PLATFORM:platformAdministrator') AS UNIQUEIDENTIFIER);

IF @userName <> N'none' AND @hash <> N'none' AND NOT EXISTS (SELECT 1 FROM auth.appUser WHERE userName = @userName)
BEGIN
    DECLARE @id UNIQUEIDENTIFIER = NEWID ();
    INSERT auth.appUser (appUserId, rootTenantId, homeTenantId, userName, displayName, passwordHash, securityStamp)
    VALUES (@id, @platform, @platform, @userName, N'Platform administrator', @hash, NEWID ());
    INSERT auth.userAssignment (userAssignmentId, appUserId, roleId, tenantId, includeDescendants, accessLevel, startDate)
    VALUES (NEWID (), @id, @role, @platform, 1, 'full', CAST (SYSUTCDATETIME () AS DATE));
    INSERT auth.userProfile (userProfileId, appUserId, profileName, profileDescription, isMaster, isActive)
    VALUES (CAST (HASHBYTES ('MD5', CONCAT (N'masterProfile:', @id)) AS UNIQUEIDENTIFIER), @id, N'Master profile',
            N'Platform administration: accounts and roles only.', 1, 1);
    PRINT CONCAT (N'Platform administrator ', @userName, N' created.');
END;
GO
