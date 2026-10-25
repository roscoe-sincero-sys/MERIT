SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO

/*
    Row-level security predicate functions, version 2.

    The policy in 210_securityPolicy.sql is SCHEMABINDING = ON, which is what lets an application login with
    EXECUTE-only permissions be filtered without holding SELECT on auth tables. The price is that a function a
    policy references cannot be altered. So each function below is created when missing and SKIPPED, with a
    message, once the policy binds it: SET NOEXEC ON turns the CREATE OR ALTER batch into a no-op. To change a
    predicate on a live database, drop the policy in a change script (see 000_infrastructure/060), re-run this file,
    and re-run 210_securityPolicy.sql, which adds every predicate back.

    Session context, set read-only by the application on every connection (Merit.Data.SqlServer):
        scopeKey   auth.sessionScope.sessionScopeId resolved at sign-in or at a profile switch
        appUserId  signed-in user (the administrator, when viewing as another user)
        AppUser    user name, stamped into the audit columns

    Three kinds of row, each with a read (filter) and a write (block) predicate:
        tenant  rows owned by a tenant (cases, tasks, intake): owning tenant in the session's tenant set
        person  persons and their child rows: owning tenant, or a tenant given access through dbo.personAccess
        root    shared reference rows of a data root (addresses, organizations, configuration): root in the session's roots
    Write predicates need write or full access at that tenant (or root); a row whose isDeleted becomes 1 needs full.

    Fail closed: with no scopeKey the predicates return nothing for every principal except dbo, which is the
    deployment and maintenance identity (seed scripts run as dbo). Application logins are never dbo.
*/

IF EXISTS (SELECT 1 FROM sys.security_predicates WHERE predicate_definition LIKE N'%tvfTenantPredicate%')
BEGIN
    PRINT N'auth.tvfTenantPredicate is bound by auth.tenantIsolationPolicy; left unchanged.';
    SET NOEXEC ON;
END;
GO

/***********************************************************************************************************************
ObjectName:   auth.tvfTenantPredicate
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Row-level security filter for tenant-owned rows (cases, investigations, tasks, intake). A row is visible when its owning
tenant is in the caller's session tenant set, at any access level.

========================================================================================================================
Requirements and Key Dependencies:

auth.sessionScopeTenant, auth.sessionScope; SESSION_CONTEXT (N'scopeKey').

========================================================================================================================
Notes:

The scope table already holds descendants for grants that include them, so the test is one seek on
UX_auth_sessionScopeTenant_sessionScopeId_tenantId. A revoked or expired scope sees nothing. Tenant ids are unique across
data roots, so a session that spans several roots (a region) needs no root test here.

========================================================================================================================
Example Usage and Performance:

Used only by auth.tenantIsolationPolicy. One index seek per row tested.

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T031
Description:	Initial version.

Date:		2026-10-25
Author:		MERIT build
Ticket:		T160
Description:	Version 2: membership only, so sessions can span several data roots.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER FUNCTION auth.tvfTenantPredicate (@rootTenantId UNIQUEIDENTIFIER, @owningTenantId UNIQUEIDENTIFIER)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN
    SELECT 1 AS isVisible
     WHERE USER_NAME () = N'dbo'
        OR EXISTS (SELECT 1
                     FROM auth.sessionScope       AS ss
                     JOIN auth.sessionScopeTenant AS st ON st.sessionScopeId = ss.sessionScopeId
                    WHERE ss.sessionScopeId = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER)
                      AND ss.revokedUtc IS NULL
                      AND ss.expiresUtc > SYSUTCDATETIME ()
                      AND st.tenantId       = @owningTenantId
                      AND st.isDeleted      = 0
                      AND @rootTenantId IS NOT NULL);
GO

SET NOEXEC OFF;
GO

IF EXISTS (SELECT 1 FROM sys.security_predicates WHERE predicate_definition LIKE N'%tvfTenantWritePredicate%')
BEGIN
    PRINT N'auth.tvfTenantWritePredicate is bound by auth.tenantIsolationPolicy; left unchanged.';
    SET NOEXEC ON;
END;
GO

/***********************************************************************************************************************
ObjectName:   auth.tvfTenantWritePredicate
Author:       MERIT build
CreateDate:   2026-10-25
========================================================================================================================
Description:

Row-level security block predicate for tenant-owned rows: an insert or update is allowed when the session has write or
full access at the row's owning tenant, and a soft delete (isDeleted = 1) only with full access.

========================================================================================================================
Requirements and Key Dependencies:

auth.sessionScopeTenant, auth.sessionScope; SESSION_CONTEXT (N'scopeKey').

========================================================================================================================
Notes:

This is what makes a read-only profile read-only and a no-delete profile unable to delete, whatever procedure is
called: the database refuses the write.

========================================================================================================================
Example Usage and Performance:

Used only by auth.tenantIsolationPolicy, AFTER INSERT and AFTER UPDATE. One index seek per row written.

========================================================================================================================
Modification History:

Date:		2026-10-25
Author:		MERIT build
Ticket:		T160
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER FUNCTION auth.tvfTenantWritePredicate (@rootTenantId UNIQUEIDENTIFIER, @owningTenantId UNIQUEIDENTIFIER,
                                                       @isDeleted BIT)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN
    SELECT 1 AS isAllowed
     WHERE USER_NAME () = N'dbo'
        OR EXISTS (SELECT 1
                     FROM auth.sessionScope       AS ss
                     JOIN auth.sessionScopeTenant AS st ON st.sessionScopeId = ss.sessionScopeId
                    WHERE ss.sessionScopeId = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER)
                      AND ss.revokedUtc IS NULL
                      AND ss.expiresUtc > SYSUTCDATETIME ()
                      AND st.tenantId       = @owningTenantId
                      AND st.isDeleted      = 0
                      AND @rootTenantId IS NOT NULL
                      AND (st.accessLevel = 'full' OR (st.accessLevel = 'write' AND @isDeleted = 0)));
GO

SET NOEXEC OFF;
GO

IF EXISTS (SELECT 1 FROM sys.security_predicates WHERE predicate_definition LIKE N'%tvfRootPredicate%')
BEGIN
    PRINT N'auth.tvfRootPredicate is bound by auth.tenantIsolationPolicy; left unchanged.';
    SET NOEXEC ON;
END;
GO

/***********************************************************************************************************************
ObjectName:   auth.tvfRootPredicate
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Row-level security filter for shared reference rows of a data root: locations, organizations, providers and
configuration (thresholds, settings, sub-jurisdictions). Visible when the root is one of the session's data roots.

========================================================================================================================
Requirements and Key Dependencies:

auth.sessionScopeRoot, auth.sessionScope; SESSION_CONTEXT (N'scopeKey').

========================================================================================================================
Notes:

A state or tribe is its own data root, so Pennsylvania users never see Delaware's addresses or settings; a region user
whose grants cover both states sees both. A platform administrator's session has no data roots and sees none.

========================================================================================================================
Example Usage and Performance:

Used only by auth.tenantIsolationPolicy. One index seek per row tested.

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T031
Description:	Initial version.

Date:		2026-10-25
Author:		MERIT build
Ticket:		T160
Description:	Version 2: the session's set of data roots instead of a single root.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER FUNCTION auth.tvfRootPredicate (@rootTenantId UNIQUEIDENTIFIER)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN
    SELECT 1 AS isVisible
     WHERE USER_NAME () = N'dbo'
        OR EXISTS (SELECT 1
                     FROM auth.sessionScope     AS ss
                     JOIN auth.sessionScopeRoot AS sr ON sr.sessionScopeId = ss.sessionScopeId
                    WHERE ss.sessionScopeId = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER)
                      AND ss.revokedUtc IS NULL
                      AND ss.expiresUtc > SYSUTCDATETIME ()
                      AND sr.rootTenantId   = @rootTenantId
                      AND sr.isDeleted      = 0);
GO

SET NOEXEC OFF;
GO

IF EXISTS (SELECT 1 FROM sys.security_predicates WHERE predicate_definition LIKE N'%tvfRootWritePredicate%')
BEGIN
    PRINT N'auth.tvfRootWritePredicate is bound by auth.tenantIsolationPolicy; left unchanged.';
    SET NOEXEC ON;
END;
GO

/***********************************************************************************************************************
ObjectName:   auth.tvfRootWritePredicate
Author:       MERIT build
CreateDate:   2026-10-25
========================================================================================================================
Description:

Row-level security block predicate for shared reference rows: write or full access somewhere in the data root to create
or change one, full access to soft delete one.

========================================================================================================================
Requirements and Key Dependencies:

auth.sessionScopeRoot, auth.sessionScope; SESSION_CONTEXT (N'scopeKey').

========================================================================================================================
Notes:

Which shared rows a user may change is further limited by permissions in the procedures (config.edit for
configuration); this predicate stops a read-only session changing any of them.

========================================================================================================================
Example Usage and Performance:

Used only by auth.tenantIsolationPolicy, AFTER INSERT and AFTER UPDATE. One index seek per row written.

========================================================================================================================
Modification History:

Date:		2026-10-25
Author:		MERIT build
Ticket:		T160
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER FUNCTION auth.tvfRootWritePredicate (@rootTenantId UNIQUEIDENTIFIER, @isDeleted BIT)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN
    SELECT 1 AS isAllowed
     WHERE USER_NAME () = N'dbo'
        OR EXISTS (SELECT 1
                     FROM auth.sessionScope     AS ss
                     JOIN auth.sessionScopeRoot AS sr ON sr.sessionScopeId = ss.sessionScopeId
                    WHERE ss.sessionScopeId = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER)
                      AND ss.revokedUtc IS NULL
                      AND ss.expiresUtc > SYSUTCDATETIME ()
                      AND sr.rootTenantId   = @rootTenantId
                      AND sr.isDeleted      = 0
                      AND (sr.accessLevel = 'full' OR (sr.accessLevel = 'write' AND @isDeleted = 0)));
GO

SET NOEXEC OFF;
GO

IF EXISTS (SELECT 1 FROM sys.security_predicates WHERE predicate_definition LIKE N'%tvfPersonPredicate%')
BEGIN
    PRINT N'auth.tvfPersonPredicate is bound by auth.tenantIsolationPolicy; left unchanged.';
    SET NOEXEC ON;
END;
GO

/***********************************************************************************************************************
ObjectName:   auth.tvfPersonPredicate
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Row-level security filter for persons and their child rows (aliases, places, results, jobs): visible through the
owning tenant or through any tenant dbo.personAccess gives access, when that tenant is in the session's tenant set.

========================================================================================================================
Requirements and Key Dependencies:

auth.sessionScope, auth.sessionScopeTenant, dbo.personAccess; SESSION_CONTEXT (N'scopeKey').

========================================================================================================================
Notes:

personAccess rows are maintained by uspRefreshPersonAccess from residence, case ownership and investigations.

========================================================================================================================
Example Usage and Performance:

Used only by auth.tenantIsolationPolicy. A seek on the scope tenants and, when needed, on personAccess.

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T031
Description:	Initial version.

Date:		2026-10-25
Author:		MERIT build
Ticket:		T160
Description:	Version 2: membership only, so sessions can span several data roots.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER FUNCTION auth.tvfPersonPredicate (@rootTenantId UNIQUEIDENTIFIER, @owningTenantId UNIQUEIDENTIFIER,
                                                  @personId UNIQUEIDENTIFIER)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN
    SELECT 1 AS isVisible
     WHERE USER_NAME () = N'dbo'
        OR EXISTS (SELECT 1
                     FROM auth.sessionScope       AS ss
                     JOIN auth.sessionScopeTenant AS st ON st.sessionScopeId = ss.sessionScopeId
                    WHERE ss.sessionScopeId = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER)
                      AND ss.revokedUtc IS NULL
                      AND ss.expiresUtc > SYSUTCDATETIME ()
                      AND st.isDeleted      = 0
                      AND (st.tenantId = @owningTenantId
                           OR EXISTS (SELECT 1
                                        FROM dbo.personAccess AS pa
                                       WHERE pa.rootTenantId = @rootTenantId
                                         AND pa.personId     = @personId
                                         AND pa.tenantId     = st.tenantId
                                         AND pa.isDeleted    = 0)));
GO

SET NOEXEC OFF;
GO

IF EXISTS (SELECT 1 FROM sys.security_predicates WHERE predicate_definition LIKE N'%tvfPersonWritePredicate%')
BEGIN
    PRINT N'auth.tvfPersonWritePredicate is bound by auth.tenantIsolationPolicy; left unchanged.';
    SET NOEXEC ON;
END;
GO

/***********************************************************************************************************************
ObjectName:   auth.tvfPersonWritePredicate
Author:       MERIT build
CreateDate:   2026-10-25
========================================================================================================================
Description:

Row-level security block predicate for persons and their child rows: the session needs write or full access at the
owning tenant or at a tenant with dbo.personAccess to the person, and full access to soft delete.

========================================================================================================================
Requirements and Key Dependencies:

auth.sessionScope, auth.sessionScopeTenant, dbo.personAccess; SESSION_CONTEXT (N'scopeKey').

========================================================================================================================
Notes:

A new person is owned by the tenant whose user creates them, which therefore needs write access there.

========================================================================================================================
Example Usage and Performance:

Used only by auth.tenantIsolationPolicy, AFTER INSERT and AFTER UPDATE.

========================================================================================================================
Modification History:

Date:		2026-10-25
Author:		MERIT build
Ticket:		T160
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER FUNCTION auth.tvfPersonWritePredicate (@rootTenantId UNIQUEIDENTIFIER, @owningTenantId UNIQUEIDENTIFIER,
                                                       @personId UNIQUEIDENTIFIER, @isDeleted BIT)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN
    SELECT 1 AS isAllowed
     WHERE USER_NAME () = N'dbo'
        OR EXISTS (SELECT 1
                     FROM auth.sessionScope       AS ss
                     JOIN auth.sessionScopeTenant AS st ON st.sessionScopeId = ss.sessionScopeId
                    WHERE ss.sessionScopeId = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER)
                      AND ss.revokedUtc IS NULL
                      AND ss.expiresUtc > SYSUTCDATETIME ()
                      AND st.isDeleted      = 0
                      AND (st.accessLevel = 'full' OR (st.accessLevel = 'write' AND @isDeleted = 0))
                      AND (st.tenantId = @owningTenantId
                           OR EXISTS (SELECT 1
                                        FROM dbo.personAccess AS pa
                                       WHERE pa.rootTenantId = @rootTenantId
                                         AND pa.personId     = @personId
                                         AND pa.tenantId     = st.tenantId
                                         AND pa.isDeleted    = 0)));
GO

SET NOEXEC OFF;
GO

-- Descriptions are set only while the function is unbound or newly created; util.uspSetObjectDescription
-- adds or updates, so running these on every deployment is harmless.
EXEC util.uspSetObjectDescription @SchemaName = N'auth', @ObjectType = N'FUNCTION', @ObjectName = N'tvfTenantPredicate',
     @Description = N'Row-level security filter for tenant-owned rows: visible when the owning tenant is in the session''s tenant set. Fails closed without SESSION_CONTEXT scopeKey.';
EXEC util.uspSetObjectDescription @SchemaName = N'auth', @ObjectType = N'FUNCTION', @ObjectName = N'tvfTenantWritePredicate',
     @Description = N'Row-level security block predicate for tenant-owned rows: write or full access at the owning tenant; full access to soft delete. Fails closed.';
EXEC util.uspSetObjectDescription @SchemaName = N'auth', @ObjectType = N'FUNCTION', @ObjectName = N'tvfRootPredicate',
     @Description = N'Row-level security filter for shared reference rows of a data root (locations, organizations, configuration): visible when the root is one of the session''s data roots. Fails closed.';
EXEC util.uspSetObjectDescription @SchemaName = N'auth', @ObjectType = N'FUNCTION', @ObjectName = N'tvfRootWritePredicate',
     @Description = N'Row-level security block predicate for shared reference rows: write or full access in the data root; full access to soft delete. Fails closed.';
EXEC util.uspSetObjectDescription @SchemaName = N'auth', @ObjectType = N'FUNCTION', @ObjectName = N'tvfPersonPredicate',
     @Description = N'Row-level security filter for persons and their child rows: visible through the owning tenant or dbo.personAccess. Fails closed.';
EXEC util.uspSetObjectDescription @SchemaName = N'auth', @ObjectType = N'FUNCTION', @ObjectName = N'tvfPersonWritePredicate',
     @Description = N'Row-level security block predicate for persons and their child rows: write or full access through the owning tenant or dbo.personAccess; full access to soft delete. Fails closed.';
GO
