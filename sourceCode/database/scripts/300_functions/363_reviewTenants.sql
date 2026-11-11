SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO
/*
ObjectName: auth.tvfReviewTenants
Author: MERIT build
CreateDate: 2026-11-11
Description: The tenants whose security reports the caller's session may review: every tenant it administers, plus
             every tenant where the session holds @permissionCode (for example audit.view or security.review).
Requirements: auth.tvfAdminTenants, auth.tvfUserGrantPermissions, auth.tvfProfileGrantPermissions.
Notes: Issues 270 and 271. Admin-level permissions (audit.view, security.review) do not add record tenants to a session,
       so this expands the grants directly, clipped exactly as auth.tvfSessionWritePermission clips them: a custom
       profile keeps only its own grants, and view-as keeps only what both people hold where the viewer administers.
       A platform administrator administers every tenant. Grants no access by itself.
Example Usage: SELECT tenantId FROM auth.tvfReviewTenants ('audit.view');
Modification History: 2026-11-11, issues 270/271, initial version.
*/
CREATE OR ALTER FUNCTION auth.tvfReviewTenants (@permissionCode VARCHAR (60))
RETURNS TABLE
AS
RETURN
    SELECT a.tenantId FROM auth.tvfAdminTenants () AS a
    UNION
    SELECT m.tenantId
      FROM auth.sessionScope AS ss
     CROSS APPLY auth.tvfUserGrantPermissions (ss.appUserId) AS m
     WHERE ss.sessionScopeId = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER)
       AND ss.isDeleted = 0 AND ss.revokedUtc IS NULL AND ss.expiresUtc > SYSUTCDATETIME ()
       AND m.permissionCode = @permissionCode
       AND ((ss.viewAsUserId IS NOT NULL
             AND EXISTS (SELECT 1 FROM auth.tvfUserGrantPermissions (ss.viewAsUserId) AS v WHERE v.tenantId = m.tenantId AND v.permissionCode = m.permissionCode)
             AND EXISTS (SELECT 1 FROM auth.tvfUserGrantPermissions (ss.appUserId) AS ad WHERE ad.tenantId = m.tenantId AND ad.permissionCode = 'tenant.administer'))
         OR (ss.viewAsUserId IS NULL AND ss.userProfileId IS NOT NULL
             AND EXISTS (SELECT 1 FROM auth.tvfProfileGrantPermissions (ss.userProfileId) AS p WHERE p.tenantId = m.tenantId AND p.permissionCode = m.permissionCode))
         OR (ss.viewAsUserId IS NULL AND ss.userProfileId IS NULL));
GO
EXEC util.uspSetObjectDescription @SchemaName=N'auth',@ObjectType=N'FUNCTION',@ObjectName=N'tvfReviewTenants',
 @Description=N'Tenants whose security reports the session may review: administered tenants plus tenants where the session holds the given permission, clipped by profile and view-as (issues 270/271).';
GO
