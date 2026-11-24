SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
GO

/*
    Issue #300: Atomically reconcile shipped de-identified epidemiologist roles and backfill
    per-(tenant, permission) identified-read flags on existing open session scopes during upgrade.
    1. Removes identifiable/task/extract permissions from existing epidemiologist roles.
    2. Revokes open session scopes for users/view-as assigned to reconciled roles or still caching
       stale permissions not backed by active grants (idempotent even after an interrupted run).
    3. Backfills canReadIdentifiedResults and canReadIdentifiedCases on remaining open active
       clinician session scopes so already-signed-in clinical users keep chart access across deploy.
*/
BEGIN TRANSACTION;

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

-- Revoke open scopes affected by the role change, OR any open scope that still caches a permission
-- no longer granted to its effective user/profile (makes reruns after interruption safe and idempotent).
UPDATE ss
   SET revokedUtc = SYSUTCDATETIME ()
  FROM auth.sessionScope AS ss
 WHERE ss.revokedUtc IS NULL
   AND ss.isDeleted = 0
   AND (
        (EXISTS (SELECT 1 FROM @revokedRoles)
         AND (EXISTS (SELECT 1 FROM auth.userAssignment AS ua
                       WHERE ua.appUserId = ss.appUserId
                         AND ua.roleId IN (SELECT roleId FROM @revokedRoles)
                         AND ua.isDeleted = 0)
              OR (ss.viewAsUserId IS NOT NULL
                  AND EXISTS (SELECT 1 FROM auth.userAssignment AS ua
                               WHERE ua.appUserId = ss.viewAsUserId
                                 AND ua.roleId IN (SELECT roleId FROM @revokedRoles)
                                 AND ua.isDeleted = 0))))
        OR EXISTS (
            SELECT 1
              FROM auth.sessionScopePermission AS sp
             WHERE sp.sessionScopeId = ss.sessionScopeId
               AND sp.isDeleted = 0
               AND sp.permissionCode IN ('person.view', 'task.view', 'extract.run')
               AND NOT EXISTS (
                   SELECT 1
                     FROM auth.tvfUserGrantPermissions (COALESCE (ss.viewAsUserId, ss.appUserId)) AS g
                    WHERE g.permissionCode = sp.permissionCode))
   );

-- Backfill per-tenant identified clinical flags for pre-upgrade open active scopes whose new columns
-- defaulted to 0 so signed-in clinicians do not lose chart access on in-place upgrade.
UPDATE st
   SET canReadIdentifiedResults = CASE WHEN EXISTS (
           SELECT 1
             FROM auth.tvfUserGrantPermissions (ss.appUserId) AS m
            WHERE m.tenantId = st.tenantId
              AND m.permissionCode = 'result.view'
              AND m.hasIdentified = 1
              AND (
                   (ss.viewAsUserId IS NOT NULL
                    AND EXISTS (SELECT 1 FROM auth.tvfUserGrantPermissions (ss.viewAsUserId) AS v
                                 WHERE v.tenantId = st.tenantId AND v.permissionCode = 'result.view' AND v.hasIdentified = 1)
                    AND EXISTS (SELECT 1 FROM auth.tvfUserGrantPermissions (ss.appUserId) AS a
                                 WHERE a.tenantId = st.tenantId AND a.permissionCode = 'tenant.administer'))
                   OR (ss.viewAsUserId IS NULL AND ss.userProfileId IS NOT NULL
                       AND EXISTS (SELECT 1 FROM auth.tvfProfileGrantPermissions (ss.userProfileId) AS p
                                    WHERE p.tenantId = st.tenantId AND p.permissionCode = 'result.view' AND p.hasIdentified = 1))
                   OR (ss.viewAsUserId IS NULL AND ss.userProfileId IS NULL)
              )) THEN 1 ELSE 0 END
     , canReadIdentifiedCases = CASE WHEN EXISTS (
           SELECT 1
             FROM auth.tvfUserGrantPermissions (ss.appUserId) AS m
            WHERE m.tenantId = st.tenantId
              AND m.permissionCode = 'case.view'
              AND m.hasIdentified = 1
              AND (
                   (ss.viewAsUserId IS NOT NULL
                    AND EXISTS (SELECT 1 FROM auth.tvfUserGrantPermissions (ss.viewAsUserId) AS v
                                 WHERE v.tenantId = st.tenantId AND v.permissionCode = 'case.view' AND v.hasIdentified = 1)
                    AND EXISTS (SELECT 1 FROM auth.tvfUserGrantPermissions (ss.appUserId) AS a
                                 WHERE a.tenantId = st.tenantId AND a.permissionCode = 'tenant.administer'))
                   OR (ss.viewAsUserId IS NULL AND ss.userProfileId IS NOT NULL
                       AND EXISTS (SELECT 1 FROM auth.tvfProfileGrantPermissions (ss.userProfileId) AS p
                                    WHERE p.tenantId = st.tenantId AND p.permissionCode = 'case.view' AND p.hasIdentified = 1))
                   OR (ss.viewAsUserId IS NULL AND ss.userProfileId IS NULL)
              )) THEN 1 ELSE 0 END
  FROM auth.sessionScopeTenant AS st
  JOIN auth.sessionScope       AS ss ON ss.sessionScopeId = st.sessionScopeId
 WHERE st.isDeleted = 0
   AND st.canReadIdentifiedResults = 0
   AND st.canReadIdentifiedCases = 0
   AND ss.isDeleted = 0
   AND ss.revokedUtc IS NULL
   AND ss.expiresUtc > SYSUTCDATETIME ()
   AND ss.isPlatformAdmin = 0;

-- Keep sessionScope.isDeidentified aligned for open active scopes after backfill.
UPDATE ss
   SET isDeidentified = CASE WHEN EXISTS (SELECT 1 FROM auth.sessionScopeTenant AS st
                                           WHERE st.sessionScopeId = ss.sessionScopeId AND st.isDeleted = 0)
                              AND NOT EXISTS (SELECT 1 FROM auth.sessionScopeTenant AS st
                                               WHERE st.sessionScopeId = ss.sessionScopeId
                                                 AND st.isDeleted = 0
                                                 AND (st.canReadIdentifiedResults = 1 OR st.canReadIdentifiedCases = 1))
                             THEN 1 ELSE 0 END
  FROM auth.sessionScope AS ss
 WHERE ss.isDeleted = 0
   AND ss.revokedUtc IS NULL
   AND ss.expiresUtc > SYSUTCDATETIME ()
   AND ss.isPlatformAdmin = 0;

COMMIT TRANSACTION;
GO
