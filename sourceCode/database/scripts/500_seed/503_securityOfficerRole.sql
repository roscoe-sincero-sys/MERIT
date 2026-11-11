SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO
-- Issue 271: converge data roots installed before the Security officer role existed. New roots get the same role and
-- permissions from config.uspInstallRootTenant. Re-runnable: inserts only what is missing and changes nothing else.
-- New sessions resolve the permissions; existing sessions are not expanded in place.

-- 1. The Security officer role at every data root (not the platform), with the id uspInstallRootTenant would give it.
INSERT auth.role (roleId, rootTenantId, roleCode, roleName, isAdministrator, isDeidentified)
SELECT CAST (HASHBYTES ('MD5', CONCAT (N'profile:', t.tenantCode, N':securityOfficer')) AS UNIQUEIDENTIFIER), t.tenantId,
       'securityOfficer', N'Security officer (security reports, read only)', 1, 0
  FROM auth.tenant AS t
 WHERE t.tenantId = t.rootTenantId AND t.tenantType <> 'platform' AND t.isDeleted = 0
   AND EXISTS (SELECT 1 FROM auth.role AS x WHERE x.rootTenantId = t.tenantId AND x.roleCode = 'administrator' AND x.isDeleted = 0)
   AND NOT EXISTS (SELECT 1 FROM auth.role AS x WHERE x.rootTenantId = t.tenantId AND x.roleCode = 'securityOfficer' AND x.isDeleted = 0);
GO

-- 2. Its permissions, and security.review for administrators (who already see these reports) so a state or county
--    administrator can delegate the Security officer role under the "cannot grant what you do not hold" rule.
INSERT auth.rolePermission (rolePermissionId, roleId, permissionId)
SELECT CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER),
       r.roleId, p.permissionId
  FROM auth.role AS r
  JOIN (VALUES ('securityOfficer', 'audit.view'), ('securityOfficer', 'usage.view'), ('securityOfficer', 'security.review'),
               ('administrator', 'security.review')) AS g (roleCode, permissionCode) ON g.roleCode = r.roleCode
  JOIN auth.permission AS p ON p.permissionCode = g.permissionCode AND p.isDeleted = 0
 WHERE r.isDeleted = 0
   AND NOT EXISTS (SELECT 1 FROM auth.rolePermission AS rp WHERE rp.roleId = r.roleId AND rp.permissionId = p.permissionId AND rp.isDeleted = 0);
GO
