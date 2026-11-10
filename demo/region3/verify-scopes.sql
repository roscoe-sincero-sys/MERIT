SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
IF DB_NAME() <> 'MERITRegion3Demo' THROW 51000, 'Run only on MERITRegion3Demo.', 1;
DECLARE @scopes TABLE(userName nvarchar(100),scopeId uniqueidentifier,tenantCount int,permissionCount int);
INSERT @scopes
SELECT u.userName,s.sessionScopeId,
 (SELECT COUNT(*) FROM auth.sessionScopeTenant t WHERE t.sessionScopeId=s.sessionScopeId AND t.isDeleted=0),
 (SELECT COUNT(*) FROM auth.sessionScopePermission p WHERE p.sessionScopeId=s.sessionScopeId AND p.isDeleted=0)
FROM auth.appUser u CROSS APPLY
 (SELECT TOP 1 sessionScopeId FROM auth.sessionScope s WHERE s.appUserId=u.appUserId ORDER BY createdDateUtc DESC) s
WHERE u.userName IN ('region3demo','marylanddemo','countydemo','platformdemo');
IF (SELECT COUNT(*) FROM @scopes)<>4 THROW 51000,'Missing demo session scope.',1;
IF EXISTS(SELECT 1 FROM @scopes WHERE userName='region3demo' AND (tenantCount<>16 OR permissionCount<>24)) THROW 51000,'Unexpected Region 3 scope.',1;
IF EXISTS(SELECT 1 FROM @scopes WHERE userName='marylanddemo' AND (tenantCount<>5 OR permissionCount<>24)) THROW 51000,'Unexpected Maryland scope.',1;
IF EXISTS(SELECT 1 FROM @scopes WHERE userName='countydemo' AND (tenantCount<>1 OR permissionCount<>24)) THROW 51000,'Unexpected county scope.',1;
IF EXISTS(SELECT 1 FROM @scopes WHERE userName='platformdemo' AND tenantCount<>0) THROW 51000,'Platform has record scope.',1;
IF EXISTS(SELECT 1 FROM @scopes s JOIN auth.sessionScopeTenant st ON st.sessionScopeId=s.scopeId WHERE st.accessLevel<>'full') THROW 51000,'Demo data scope must have full access.',1;
SELECT userName,tenantCount,permissionCount FROM @scopes ORDER BY userName;
SELECT t.tenantCode,t.tenantName,p.tenantCode AS parentCode FROM auth.tenant t LEFT JOIN auth.tenant p ON p.tenantId=t.parentTenantId ORDER BY t.tenantCode;
