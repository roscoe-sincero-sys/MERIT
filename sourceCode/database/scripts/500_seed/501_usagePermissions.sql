SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO
-- #227: converge installed standard roles as well as roots created after this release.
-- New sessions resolve the permission; existing cookies are not expanded in place.
INSERT auth.rolePermission (rolePermissionId, roleId, permissionId)
SELECT CAST (NEWID () AS UNIQUEIDENTIFIER), r.roleId, p.permissionId
  FROM auth.role AS r CROSS JOIN auth.permission AS p
 WHERE r.roleCode IN ('supervisor', 'administrator') AND r.isDeleted = 0
   AND p.permissionCode = 'usage.view' AND p.isDeleted = 0
   AND NOT EXISTS (SELECT 1 FROM auth.rolePermission AS rp WHERE rp.roleId = r.roleId AND rp.permissionId = p.permissionId AND rp.isDeleted = 0);
GO
