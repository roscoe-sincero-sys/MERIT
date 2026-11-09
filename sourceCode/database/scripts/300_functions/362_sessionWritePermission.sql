SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO
/* Effective write-capable permission at each tenant, clipped to the actual profile/view-as scope.
   Used by work routing so a clinical role in one county cannot combine with epidemiology in another. */
CREATE OR ALTER FUNCTION auth.tvfSessionWritePermission(@permissionCode VARCHAR(60))
RETURNS TABLE AS RETURN
 SELECT m.tenantId FROM auth.sessionScope ss
 CROSS APPLY auth.tvfUserGrantPermissions(ss.appUserId) m
 JOIN auth.sessionScopeTenant st ON st.sessionScopeId=ss.sessionScopeId AND st.tenantId=m.tenantId AND st.isDeleted=0 AND st.accessLevel IN('write','full')
 WHERE ss.sessionScopeId=TRY_CAST(SESSION_CONTEXT(N'scopeKey') AS UNIQUEIDENTIFIER)
 AND ss.isDeleted=0 AND ss.revokedUtc IS NULL AND ss.expiresUtc>SYSUTCDATETIME()
 AND m.permissionCode=@permissionCode AND m.grantRank>=2
 AND ((ss.viewAsUserId IS NOT NULL AND EXISTS(SELECT 1 FROM auth.tvfUserGrantPermissions(ss.viewAsUserId) v WHERE v.tenantId=m.tenantId AND v.permissionCode=m.permissionCode AND v.grantRank>=2)
 AND EXISTS(SELECT 1 FROM auth.tvfUserGrantPermissions(ss.appUserId) a WHERE a.tenantId=m.tenantId AND a.permissionCode='tenant.administer'))
 OR (ss.viewAsUserId IS NULL AND ss.userProfileId IS NOT NULL AND EXISTS(SELECT 1 FROM auth.tvfProfileGrantPermissions(ss.userProfileId) p WHERE p.tenantId=m.tenantId AND p.permissionCode=m.permissionCode AND p.grantRank>=2))
 OR (ss.viewAsUserId IS NULL AND ss.userProfileId IS NULL));
GO
EXEC util.uspSetObjectDescription @SchemaName=N'auth',@ObjectType=N'FUNCTION',@ObjectName=N'tvfSessionWritePermission',@Description=N'Per-tenant write-capable permission clipped to live master, profile and view-as authority.';
GO
