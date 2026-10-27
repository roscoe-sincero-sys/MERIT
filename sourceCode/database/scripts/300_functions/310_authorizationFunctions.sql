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
CREATE OR ALTER FUNCTION auth.tvfUserGrantPermissions (@appUserId UNIQUEIDENTIFIER)
RETURNS TABLE
AS
RETURN
    SELECT tc.descendantTenantId AS tenantId, pm.permissionCode, pm.accessLevel AS permissionLevel,
           MAX (CASE ua.accessLevel WHEN 'full' THEN 3 WHEN 'write' THEN 2 ELSE 1 END) AS grantRank
      FROM auth.userAssignment AS ua
      JOIN auth.tenantClosure  AS tc ON tc.ancestorTenantId = ua.tenantId
                                    AND (ua.includeDescendants = 1 OR tc.depth = 0)
                                    AND tc.isDeleted = 0
      JOIN auth.tenant         AS t  ON t.tenantId = tc.descendantTenantId AND t.isActive = 1 AND t.isDeleted = 0
      JOIN auth.rolePermission AS rp ON rp.roleId = ua.roleId AND rp.isDeleted = 0
      JOIN auth.permission     AS pm ON pm.permissionId = rp.permissionId AND pm.isDeleted = 0
     WHERE ua.appUserId = @appUserId
       AND ua.isDeleted = 0
       AND ua.startDate <= auth.udfSessionToday ()
       AND (ua.endDate IS NULL OR ua.endDate >= auth.udfSessionToday ())
       AND (ua.accessLevel <> 'read' OR pm.accessLevel = 'read')
     GROUP BY tc.descendantTenantId, pm.permissionCode, pm.accessLevel;
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
           MAX (CASE g.accessLevel WHEN 'full' THEN 3 WHEN 'write' THEN 2 ELSE 1 END) AS grantRank
      FROM auth.userProfile      AS up
      JOIN auth.userProfileGrant AS g  ON g.userProfileId = up.userProfileId AND g.isDeleted = 0
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
EXEC util.uspSetObjectDescription @SchemaName = N'auth', @ObjectType = N'FUNCTION', @ObjectName = N'tvfAdminTenants', @Description = N'Tenants the current session may administer: all for a platform administrator, else its administration scope.';
GO
