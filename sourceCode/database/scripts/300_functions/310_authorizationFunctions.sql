SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO

/*
    Authorization helpers shared by the session, profile and user administration procedures.

    A grant gives a role at a tenant (and, optionally, everything below it) at an access level: read, write or full.
    "Expanding" a set of grants lists every (tenant, permission) pair it gives, with the highest access level, so two
    sets can be compared pair by pair: a profile may use only pairs its owner's master profile has, at no higher level,
    and an administrator may grant only pairs they hold.

    Under a read grant only read permissions count; write and admin permissions need a write or full grant.
    Access ranks: read = 1, write = 2, full = 3.
*/

/***********************************************************************************************************************
ObjectName:   auth.tvfUserGrantPermissions
Author:       MERIT build
CreateDate:   2026-10-25
========================================================================================================================
Description:

Expands a user's master profile (their current assignments) into one row per tenant and permission, with the
permission's kind (read, write, admin) and the highest access rank any grant gives there.

========================================================================================================================
Requirements and Key Dependencies:

auth.userAssignment, auth.tenantClosure, auth.tenant, auth.rolePermission, auth.permission.

========================================================================================================================
Notes:

Inactive tenants and assignments outside their start and end dates give nothing.

========================================================================================================================
Example Usage and Performance:

SELECT * FROM auth.tvfUserGrantPermissions (@appUserId) WHERE tenantId = @tenantId;

Inline; one pass over the user's assignments and their closure rows.

========================================================================================================================
Modification History:

Date:		2026-10-25
Author:		MERIT build
Ticket:		T160
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER FUNCTION auth.tvfUserGrantPermissionsAsOf (@appUserId UNIQUEIDENTIFIER, @asOfDate DATE)
RETURNS TABLE
AS
RETURN
    SELECT tc.descendantTenantId AS tenantId, pm.permissionCode, pm.accessLevel AS permissionLevel,
           MAX (CASE ua.accessLevel WHEN 'full' THEN 3 WHEN 'write' THEN 2 ELSE 1 END) AS grantRank,
           CAST (MAX (CASE WHEN r.isDeidentified = 0 THEN 1 ELSE 0 END) AS BIT) AS hasIdentified
      FROM auth.userAssignment AS ua
      JOIN auth.role           AS r  ON r.roleId = ua.roleId AND r.isDeleted = 0
      JOIN auth.tenantClosure  AS tc ON tc.ancestorTenantId = ua.tenantId
                                    AND (ua.includeDescendants = 1 OR tc.depth = 0)
                                    AND tc.isDeleted = 0
      JOIN auth.tenant         AS t  ON t.tenantId = tc.descendantTenantId AND t.isActive = 1 AND t.isDeleted = 0
      JOIN auth.rolePermission AS rp ON rp.roleId = ua.roleId AND rp.isDeleted = 0
      JOIN auth.permission     AS pm ON pm.permissionId = rp.permissionId AND pm.isDeleted = 0
     WHERE ua.appUserId = @appUserId
       AND ua.isDeleted = 0
       AND ua.startDate <= @asOfDate
       AND (ua.endDate IS NULL OR ua.endDate >= @asOfDate) AND ua.revokedUtc IS NULL
       AND (ua.accessLevel <> 'read' OR pm.accessLevel = 'read')
     GROUP BY tc.descendantTenantId, pm.permissionCode, pm.accessLevel;
GO

CREATE OR ALTER FUNCTION auth.tvfUserGrantPermissions (@appUserId UNIQUEIDENTIFIER)
RETURNS TABLE
AS
RETURN
    SELECT tenantId, permissionCode, permissionLevel, grantRank, hasIdentified
      FROM auth.tvfUserGrantPermissionsAsOf (@appUserId, auth.udfSessionToday ());
GO

/***********************************************************************************************************************
ObjectName:   auth.tvfProfileGrantPermissions
Author:       MERIT build
CreateDate:   2026-10-25
========================================================================================================================
Description:

Expands the grants of a user-made profile the same way auth.tvfUserGrantPermissions expands a master profile.

========================================================================================================================
Requirements and Key Dependencies:

auth.userProfile, auth.userProfileGrant, auth.tenantClosure, auth.tenant, auth.rolePermission, auth.permission.

========================================================================================================================
Notes:

The result is not yet clipped to the master; auth.uspCreateSessionScope joins it to the master's expansion.

========================================================================================================================
Example Usage and Performance:

SELECT * FROM auth.tvfProfileGrantPermissions (@userProfileId);

Inline.

========================================================================================================================
Modification History:

Date:		2026-10-25
Author:		MERIT build
Ticket:		T160
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER FUNCTION auth.tvfProfileGrantPermissions (@userProfileId UNIQUEIDENTIFIER)
RETURNS TABLE
AS
RETURN
    SELECT tc.descendantTenantId AS tenantId, pm.permissionCode, pm.accessLevel AS permissionLevel,
           MAX (CASE g.accessLevel WHEN 'full' THEN 3 WHEN 'write' THEN 2 ELSE 1 END) AS grantRank,
           CAST (MAX (CASE WHEN r.isDeidentified = 0 THEN 1 ELSE 0 END) AS BIT) AS hasIdentified
      FROM auth.userProfile      AS up
      JOIN auth.userProfileGrant AS g  ON g.userProfileId = up.userProfileId AND g.isDeleted = 0
      JOIN auth.role             AS r  ON r.roleId = g.roleId AND r.isDeleted = 0
      JOIN auth.tenantClosure    AS tc ON tc.ancestorTenantId = g.tenantId
                                      AND (g.includeDescendants = 1 OR tc.depth = 0)
                                      AND tc.isDeleted = 0
      JOIN auth.tenant           AS t  ON t.tenantId = tc.descendantTenantId AND t.isActive = 1 AND t.isDeleted = 0
      JOIN auth.rolePermission   AS rp ON rp.roleId = g.roleId AND rp.isDeleted = 0
      JOIN auth.permission       AS pm ON pm.permissionId = rp.permissionId AND pm.isDeleted = 0
     WHERE up.userProfileId = @userProfileId
       AND up.isDeleted = 0
       AND (g.accessLevel <> 'read' OR pm.accessLevel = 'read')
     GROUP BY tc.descendantTenantId, pm.permissionCode, pm.accessLevel;
GO

/***********************************************************************************************************************
ObjectName:   auth.udfIsPlatformAdmin
Author:       MERIT build
CreateDate:   2026-10-25
========================================================================================================================
Description:

1 when the current session is a platform administrator's (accounts and roles only, no records); dbo counts as one.

========================================================================================================================
Requirements and Key Dependencies:

auth.sessionScope; SESSION_CONTEXT (N'scopeKey').

========================================================================================================================
Notes:

Fails closed: 0 without a valid scope.

========================================================================================================================
Example Usage and Performance:

IF auth.udfIsPlatformAdmin () = 1 ...

One clustered seek.

========================================================================================================================
Modification History:

Date:		2026-10-25
Author:		MERIT build
Ticket:		T160
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER FUNCTION auth.udfIsPlatformAdmin ()
RETURNS BIT
AS
BEGIN
    IF USER_NAME () = N'dbo' RETURN 1;

    RETURN COALESCE ((SELECT ss.isPlatformAdmin
                        FROM auth.sessionScope AS ss
                       WHERE ss.sessionScopeId = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER)
                         AND ss.revokedUtc IS NULL
                         AND ss.expiresUtc > SYSUTCDATETIME ()
                         AND ss.isDeleted = 0), 0);
END;
GO

CREATE OR ALTER FUNCTION auth.udfIsDeidentified ()
RETURNS BIT
AS
BEGIN
    IF USER_NAME () = N'dbo' RETURN 0;

    RETURN COALESCE ((SELECT ss.isDeidentified
                        FROM auth.sessionScope AS ss
                       WHERE ss.sessionScopeId = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER)
                         AND ss.revokedUtc IS NULL
                         AND ss.expiresUtc > SYSUTCDATETIME ()
                         AND ss.isDeleted = 0), 0);
END;
GO

CREATE OR ALTER FUNCTION auth.tvfIdentifiedTenants (@permissionCode VARCHAR (60))
RETURNS TABLE
AS
RETURN
    SELECT st.tenantId
      FROM auth.sessionScope       AS ss
      JOIN auth.sessionScopeTenant AS st ON st.sessionScopeId = ss.sessionScopeId AND st.isDeleted = 0
     WHERE ss.sessionScopeId = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER)
       AND ss.revokedUtc IS NULL
       AND ss.expiresUtc > SYSUTCDATETIME ()
       AND ss.isDeleted = 0
       AND ((@permissionCode = 'result.view' AND st.canReadIdentifiedResults = 1)
            OR (@permissionCode = 'case.view' AND st.canReadIdentifiedCases = 1));
GO

CREATE OR ALTER FUNCTION auth.udfCanReadIdentified (@owningTenantId UNIQUEIDENTIFIER, @permissionCode VARCHAR (60), @personId UNIQUEIDENTIFIER = NULL)
RETURNS BIT
AS
BEGIN
    IF USER_NAME () = N'dbo' RETURN 1;
    IF @owningTenantId IS NULL AND @personId IS NULL RETURN 0;

    IF EXISTS (
        SELECT 1
          FROM auth.sessionScope       AS ss
          JOIN auth.sessionScopeTenant AS st ON st.sessionScopeId = ss.sessionScopeId
                                            AND st.isDeleted = 0
         WHERE ss.sessionScopeId = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER)
           AND ss.revokedUtc IS NULL
           AND ss.expiresUtc > SYSUTCDATETIME ()
           AND ss.isDeleted = 0
           AND ((@permissionCode = 'result.view' AND st.canReadIdentifiedResults = 1)
                OR (@permissionCode = 'case.view' AND st.canReadIdentifiedCases = 1))
           AND (st.tenantId = @owningTenantId
                OR (@permissionCode = 'result.view'
                    AND @personId IS NOT NULL
                    AND EXISTS (SELECT 1 FROM dbo.personAccess AS pa
                                 WHERE pa.personId = @personId
                                   AND pa.tenantId = st.tenantId
                                   AND pa.isDeleted = 0))))
        RETURN 1;

    RETURN 0;
END;
GO

CREATE OR ALTER FUNCTION auth.udfHasRootPermission (@permissionCode VARCHAR (60))
RETURNS BIT
AS
BEGIN
    IF USER_NAME () = N'dbo' RETURN 1;

    DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();
    IF @root IS NULL RETURN 0;

    IF EXISTS (
        SELECT 1
          FROM auth.sessionScope AS ss
         CROSS APPLY auth.tvfUserGrantPermissions (ss.appUserId) AS m
         WHERE ss.sessionScopeId = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER)
           AND ss.isDeleted = 0
           AND ss.revokedUtc IS NULL
           AND ss.expiresUtc > SYSUTCDATETIME ()
           AND m.tenantId = @root
           AND m.permissionCode = @permissionCode
           AND m.grantRank >= 2
           AND (
                (ss.viewAsUserId IS NOT NULL
                 AND EXISTS (SELECT 1 FROM auth.tvfUserGrantPermissions (ss.viewAsUserId) AS v
                              WHERE v.tenantId = @root AND v.permissionCode = @permissionCode AND v.grantRank >= 2)
                 AND EXISTS (SELECT 1 FROM auth.tvfUserGrantPermissions (ss.appUserId) AS a
                              WHERE a.tenantId = @root AND a.permissionCode = 'tenant.administer'))
                OR (ss.viewAsUserId IS NULL AND ss.userProfileId IS NOT NULL
                    AND EXISTS (SELECT 1 FROM auth.tvfProfileGrantPermissions (ss.userProfileId) AS p
                                 WHERE p.tenantId = @root AND p.permissionCode = @permissionCode AND p.grantRank >= 2))
                OR (ss.viewAsUserId IS NULL AND ss.userProfileId IS NULL)
           ))
        RETURN 1;

    RETURN 0;
END;
GO

/***********************************************************************************************************************
ObjectName:   auth.tvfAdminTenants
Author:       MERIT build
CreateDate:   2026-10-25
========================================================================================================================
Description:

Tenants the current session may administer: whose accounts it may manage and where it may grant roles. Every tenant
for a platform administrator (and dbo); otherwise the session's administration scope.

========================================================================================================================
Requirements and Key Dependencies:

auth.sessionScope, auth.sessionScopeAdminTenant, auth.tenant; SESSION_CONTEXT (N'scopeKey').

========================================================================================================================
Notes:

Administration scope gives no access to records; that comes only from record permissions.

========================================================================================================================
Example Usage and Performance:

IF NOT EXISTS (SELECT 1 FROM auth.tvfAdminTenants () WHERE tenantId = @tenantId) THROW 51403, ...

Inline.

========================================================================================================================
Modification History:

Date:		2026-10-25
Author:		MERIT build
Ticket:		T160
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER FUNCTION auth.tvfAdminTenants ()
RETURNS TABLE
AS
RETURN
    SELECT t.tenantId
      FROM auth.tenant AS t
     WHERE t.isDeleted = 0 AND auth.udfIsPlatformAdmin () = 1
    UNION
    SELECT sa.tenantId
      FROM auth.sessionScope            AS ss
      JOIN auth.sessionScopeAdminTenant AS sa ON sa.sessionScopeId = ss.sessionScopeId AND sa.isDeleted = 0
     WHERE ss.sessionScopeId = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER)
       AND ss.revokedUtc IS NULL
       AND ss.expiresUtc > SYSUTCDATETIME ()
       AND ss.isDeleted = 0;
GO

EXEC util.uspSetObjectDescription @SchemaName = N'auth', @ObjectType = N'FUNCTION', @ObjectName = N'tvfUserGrantPermissions', @Description = N'A user''s master profile expanded to (tenant, permission, kind, highest access rank).';
EXEC util.uspSetObjectDescription @SchemaName = N'auth', @ObjectType = N'FUNCTION', @ObjectName = N'tvfProfileGrantPermissions', @Description = N'A user-made profile expanded to (tenant, permission, kind, highest access rank); not yet clipped to the master.';
EXEC util.uspSetObjectDescription @SchemaName = N'auth', @ObjectType = N'FUNCTION', @ObjectName = N'udfIsPlatformAdmin', @Description = N'1 for a platform administrator''s session (and dbo); fails closed.';
EXEC util.uspSetObjectDescription @SchemaName = N'auth', @ObjectType = N'FUNCTION', @ObjectName = N'udfIsDeidentified', @Description = N'1 when the current session holds only de-identified roles (epidemiologist); 0 for dbo or identified roles.';
EXEC util.uspSetObjectDescription @SchemaName = N'auth', @ObjectType = N'FUNCTION', @ObjectName = N'udfCanReadIdentified', @Description = N'1 when the current active session holds a non-de-identified grant for the specified permission at @owningTenantId; 1 for dbo; fails closed.';
EXEC util.uspSetObjectDescription @SchemaName = N'auth', @ObjectType = N'FUNCTION', @ObjectName = N'udfHasRootPermission', @Description = N'1 when the current active session (master, profile, or view-as) holds write/full permission at the data root.';
EXEC util.uspSetObjectDescription @SchemaName = N'auth', @ObjectType = N'FUNCTION', @ObjectName = N'tvfAdminTenants', @Description = N'Tenants the current session may administer: all for a platform administrator, else its administration scope.';
GO
