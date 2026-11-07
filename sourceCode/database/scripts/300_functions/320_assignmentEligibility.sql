SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO
/***********************************************************************************************************************
ObjectName: auth.tvfAssignableUserTenants
Author: MERIT build
CreateDate: 2026-11-07
========================================================================================================================
Description:
Active workers whose master grants permit reading and editing the requested work at a tenant. A temporary reduced
profile does not remove eligibility: the worker can switch to their master profile to perform the work.
========================================================================================================================
Requirements and Key Dependencies:
auth.appUser, userAssignment, role, rolePermission, permission, tenantClosure and tenant. Dated grants use jurisdiction time.
========================================================================================================================
Notes:
Read-only, expired, future, deleted and inactive grants/users/tenants are excluded. Case/investigation workers must
also view people; task writes separately check access to each actual linked patient and case.
========================================================================================================================
Example Usage and Performance:
SELECT * FROM auth.tvfAssignableUserTenants ('case.edit');
Inline grouped grant expansion, reused by pickers and write validation.
========================================================================================================================
Modification History:
Date: 2026-11-07
Author: MERIT build
Ticket: 211
Description: Validate responsibility against current capability and tenant scope.
***********************************************************************************************************************/
CREATE OR ALTER FUNCTION auth.tvfAssignableUserTenants (@requiredPermission VARCHAR (60))
RETURNS TABLE
AS
RETURN
    SELECT u.appUserId, tc.descendantTenantId AS tenantId
      FROM auth.appUser AS u
      JOIN auth.userAssignment AS ua ON ua.appUserId = u.appUserId AND ua.isDeleted = 0
      JOIN auth.role AS r ON r.roleId = ua.roleId AND r.isDeleted = 0
      JOIN auth.tenantClosure AS tc ON tc.ancestorTenantId = ua.tenantId AND tc.isDeleted = 0
                                  AND (ua.includeDescendants = 1 OR tc.depth = 0)
      JOIN auth.tenant AS t ON t.tenantId = tc.descendantTenantId AND t.isActive = 1 AND t.isDeleted = 0
      JOIN auth.rolePermission AS rp ON rp.roleId = r.roleId AND rp.isDeleted = 0
      JOIN auth.permission AS pm ON pm.permissionId = rp.permissionId AND pm.isDeleted = 0
     WHERE u.isActive = 1 AND u.isDeleted = 0
       AND ua.startDate <= auth.udfSessionToday () AND (ua.endDate IS NULL OR ua.endDate >= auth.udfSessionToday ())
       AND (ua.accessLevel <> 'read' OR pm.accessLevel = 'read')
       AND @requiredPermission IN ('case.edit', 'task.edit', 'investigation.edit')
     GROUP BY u.appUserId, tc.descendantTenantId
    HAVING MAX (CASE WHEN pm.permissionCode = @requiredPermission AND ua.accessLevel IN ('write', 'full') THEN 1 ELSE 0 END) = 1
       AND MAX (CASE WHEN pm.permissionCode = CASE @requiredPermission WHEN 'task.edit' THEN 'task.view' ELSE 'case.view' END THEN 1 ELSE 0 END) = 1
       AND (@requiredPermission = 'task.edit' OR MAX (CASE WHEN pm.permissionCode = 'person.view' THEN 1 ELSE 0 END) = 1);
GO
EXEC util.uspSetObjectDescription @SchemaName = N'auth', @ObjectType = N'FUNCTION', @ObjectName = N'tvfAssignableUserTenants',
     @Description = N'Active master-grant workers eligible for a work capability at each tenant. Used by assignment pickers and writes; selected reduced profiles do not remove master eligibility. Task writes separately verify linked records.';
GO
