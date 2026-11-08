SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO
INSERT auth.rolePermission(rolePermissionId,roleId,permissionId)
SELECT NEWID(),r.roleId,p.permissionId FROM auth.role AS r CROSS JOIN auth.permission AS p
WHERE r.roleCode='supervisor' AND r.isDeleted=0 AND p.permissionCode='workflow.waive' AND p.isDeleted=0
AND NOT EXISTS(SELECT 1 FROM auth.rolePermission WHERE roleId=r.roleId AND permissionId=p.permissionId AND isDeleted=0);
GO
-- Unstructured originals cannot be de-identified. Clinical roles get explicit attachment access; epidemiology does not.
INSERT auth.rolePermission (rolePermissionId, roleId, permissionId)
SELECT NEWID (), r.roleId, p.permissionId FROM auth.role AS r CROSS JOIN auth.permission AS p
 WHERE r.isDeleted = 0 AND r.roleCode IN ('intakeClerk','caseManager','investigator','occupationalInvestigator','supervisor')
   AND p.isDeleted = 0 AND p.permissionCode IN ('document.view','document.edit')
   AND NOT EXISTS (SELECT 1 FROM auth.rolePermission AS rp WHERE rp.roleId = r.roleId AND rp.permissionId = p.permissionId AND rp.isDeleted = 0);
GO
