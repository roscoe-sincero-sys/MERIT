SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
GO

/*
    Issue #300: Reconcile shipped de-identified epidemiologist roles on upgraded databases.
    Removes any identifiable/task permissions (e.g., person.view, task.view) from existing
    epidemiologist roles and revokes open session scopes for users assigned to those roles
    so narrowed permissions take effect immediately.
*/
DECLARE @revokedRoles TABLE (roleId UNIQUEIDENTIFIER);

UPDATE rp
   SET isDeleted = 1
     , modifiedBy = ORIGINAL_LOGIN ()
     , modifiedDateUtc = SYSUTCDATETIME ()
OUTPUT INSERTED.roleId INTO @revokedRoles (roleId)
  FROM auth.rolePermission AS rp
  JOIN auth.role           AS r  ON r.roleId = rp.roleId AND r.isDeleted = 0
  JOIN auth.permission     AS pm ON pm.permissionId = rp.permissionId
 WHERE rp.isDeleted = 0
   AND r.roleCode = 'epidemiologist'
   AND r.isDeidentified = 1
   AND pm.permissionCode NOT IN ('result.view', 'case.view', 'data.export');

IF EXISTS (SELECT 1 FROM @revokedRoles)
BEGIN
    UPDATE ss
       SET revokedUtc = SYSUTCDATETIME ()
      FROM auth.sessionScope AS ss
     WHERE ss.revokedUtc IS NULL
       AND ss.isDeleted = 0
       AND (EXISTS (SELECT 1 FROM auth.userAssignment AS ua
                     WHERE ua.appUserId = ss.appUserId
                       AND ua.roleId IN (SELECT roleId FROM @revokedRoles)
                       AND ua.isDeleted = 0)
            OR (ss.viewAsUserId IS NOT NULL
                AND EXISTS (SELECT 1 FROM auth.userAssignment AS ua
                             WHERE ua.appUserId = ss.viewAsUserId
                               AND ua.roleId IN (SELECT roleId FROM @revokedRoles)
                               AND ua.isDeleted = 0)));
END;
GO
