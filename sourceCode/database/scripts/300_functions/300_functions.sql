SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   auth.udfHasPermission
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Returns 1 when the current session scope holds the permission, else 0. Stored procedures call it so that permissions are
checked in the database as well as in the application: row-level security is the second wall, not the only one.

========================================================================================================================
Requirements and Key Dependencies:

auth.sessionScope, auth.sessionScopePermission; SESSION_CONTEXT (N'scopeKey').

========================================================================================================================
Notes:

dbo (deployment, maintenance and seed scripts) holds every permission. Any other principal without a valid, unexpired,
unrevoked scope holds none -- the check fails closed exactly as the row-level security predicates do.

========================================================================================================================
Example Usage and Performance:

IF auth.udfHasPermission (N'case.edit') = 0 THROW 51403, N'...', 1;

Two index seeks.

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T040
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER FUNCTION auth.udfHasPermission (@permissionCode VARCHAR (60))
RETURNS BIT
AS
BEGIN
    IF USER_NAME () = N'dbo' RETURN 1;

    RETURN CASE WHEN EXISTS (SELECT 1
                               FROM auth.sessionScope           AS ss
                               JOIN auth.sessionScopePermission AS sp ON sp.sessionScopeId = ss.sessionScopeId
                              WHERE ss.sessionScopeId = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER)
                                AND ss.revokedUtc IS NULL
                                AND ss.expiresUtc > SYSUTCDATETIME ()
                                AND ss.isDeleted = 0
                                AND sp.permissionCode = @permissionCode
                                AND sp.isDeleted = 0)
                THEN CAST (1 AS BIT)
                ELSE CAST (0 AS BIT)
           END;
END;
GO

/***********************************************************************************************************************
ObjectName:   auth.udfSessionUserId
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

The signed-in application user for this connection, from SESSION_CONTEXT (N'appUserId'); NULL when none is set.

========================================================================================================================
Requirements and Key Dependencies:

SESSION_CONTEXT (N'appUserId'), set read-only by Merit.Data.SqlServer on every connection.

========================================================================================================================
Notes:

Procedures use it to stamp who did something (assignedTo, worker, requestedBy) without trusting a parameter.

========================================================================================================================
Example Usage and Performance:

SELECT auth.udfSessionUserId ();

No table access.

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T040
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER FUNCTION auth.udfSessionUserId ()
RETURNS UNIQUEIDENTIFIER
AS
BEGIN
    RETURN TRY_CAST (SESSION_CONTEXT (N'appUserId') AS UNIQUEIDENTIFIER);
END;
GO

/***********************************************************************************************************************
ObjectName:   auth.udfSessionRootTenantId
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

The root tenant of the current session scope; NULL without a valid scope.

========================================================================================================================
Requirements and Key Dependencies:

auth.sessionScope; SESSION_CONTEXT (N'scopeKey').

========================================================================================================================
Notes:

Every insert of a tenant-owned row takes its rootTenantId from here, so a caller cannot write into another root even
before the block predicate would stop it.

========================================================================================================================
Example Usage and Performance:

SELECT auth.udfSessionRootTenantId ();

One clustered seek.

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T040
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER FUNCTION auth.udfSessionRootTenantId ()
RETURNS UNIQUEIDENTIFIER
AS
BEGIN
    RETURN (SELECT ss.rootTenantId
              FROM auth.sessionScope AS ss
             WHERE ss.sessionScopeId = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER)
               AND ss.revokedUtc IS NULL
               AND ss.expiresUtc > SYSUTCDATETIME ()
               AND ss.isDeleted = 0);
END;
GO

/***********************************************************************************************************************
ObjectName:   auth.udfSessionActingTenantId
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

The tenant the signed-in user is acting for (their primary assignment), from SESSION_CONTEXT (N'actingTenantId'). New
tenant-owned rows that have no other owner rule are owned by this tenant.

========================================================================================================================
Requirements and Key Dependencies:

SESSION_CONTEXT (N'actingTenantId').

========================================================================================================================
Notes:

Falls back to the session's root tenant when no acting tenant is set.

========================================================================================================================
Example Usage and Performance:

SELECT auth.udfSessionActingTenantId ();

One clustered seek at most.

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T040
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER FUNCTION auth.udfSessionActingTenantId ()
RETURNS UNIQUEIDENTIFIER
AS
BEGIN
    RETURN COALESCE (TRY_CAST (SESSION_CONTEXT (N'actingTenantId') AS UNIQUEIDENTIFIER), auth.udfSessionRootTenantId ());
END;
GO

/***********************************************************************************************************************
ObjectName:   auth.udfLocalDate
Author:       MERIT build
CreateDate:   2026-10-27
========================================================================================================================
Description:

Today's date in a time zone (a Windows name from sys.time_zone_info, e.g. Eastern Standard Time). NULL means Eastern
Standard Time, the zone of a data root that has none set.

========================================================================================================================
Requirements and Key Dependencies:

AT TIME ZONE; the zone names in sys.time_zone_info.

========================================================================================================================
Notes:

Dates people read, and dates that default to today, follow the jurisdiction, not the server's UTC clock (issue #36):
at 8 p.m. in Maryland the UTC date is already tomorrow.

========================================================================================================================
Example Usage and Performance:

SELECT auth.udfLocalDate ('Pacific Standard Time');

No data access.

========================================================================================================================
Modification History:

Date:		2026-10-27
Author:		MERIT build
Ticket:		T188
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER FUNCTION auth.udfLocalDate (@timeZone VARCHAR (64))
RETURNS DATE
AS
BEGIN
    RETURN CAST (SYSUTCDATETIME () AT TIME ZONE 'UTC' AT TIME ZONE COALESCE (@timeZone, 'Eastern Standard Time') AS DATE);
END;
GO

/***********************************************************************************************************************
ObjectName:   auth.udfSessionToday
Author:       MERIT build
CreateDate:   2026-10-27
========================================================================================================================
Description:

Today's date in the session's jurisdiction, from SESSION_CONTEXT (N'timeZone'), which the application sets from the
session's data root. Every procedure that dates something today uses it.

========================================================================================================================
Requirements and Key Dependencies:

SESSION_CONTEXT (N'timeZone'); auth.udfLocalDate.

========================================================================================================================
Notes:

With no zone in the session (deployment scripts, a sign-in before its scope exists) the date is Eastern time.

========================================================================================================================
Example Usage and Performance:

SELECT auth.udfSessionToday ();

No data access.

========================================================================================================================
Modification History:

Date:		2026-10-27
Author:		MERIT build
Ticket:		T188
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER FUNCTION auth.udfSessionToday ()
RETURNS DATE
AS
BEGIN
    RETURN auth.udfLocalDate (TRY_CAST (SESSION_CONTEXT (N'timeZone') AS VARCHAR (64)));
END;
GO

/***********************************************************************************************************************
ObjectName:   dbo.udfAgeInMonths
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Whole months between a date of birth and a later date, counting a month only once its day has been reached. NULL when
either date is NULL.

========================================================================================================================
Requirements and Key Dependencies:

None.

========================================================================================================================
Notes:

Used for age at collection in extracts and lists. Population itself is derived in Merit.Domain, not here, so the rule
lives in one place; this helper only formats ages for reads.

========================================================================================================================
Example Usage and Performance:

SELECT dbo.udfAgeInMonths ('2024-03-15', '2026-03-14');   -- 23

Scalar, no table access; SQL Server 2019+ inlines it.

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T060
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER FUNCTION dbo.udfAgeInMonths (@dob DATE, @asOf DATE)
RETURNS INT
AS
BEGIN
    RETURN CASE WHEN @dob IS NULL OR @asOf IS NULL THEN NULL
                ELSE DATEDIFF (MONTH, @dob, @asOf) - CASE WHEN DAY (@asOf) < DAY (@dob) THEN 1 ELSE 0 END
           END;
END;
GO

EXEC util.uspSetObjectDescription @SchemaName = N'auth', @ObjectType = N'FUNCTION', @ObjectName = N'udfHasPermission', @Description = N'1 when the current session scope holds the permission, else 0. Fails closed without a valid scope; dbo holds every permission.';
EXEC util.uspSetObjectDescription @SchemaName = N'auth', @ObjectType = N'FUNCTION', @ObjectName = N'udfSessionUserId', @Description = N'Signed-in application user for this connection, from SESSION_CONTEXT appUserId.';
EXEC util.uspSetObjectDescription @SchemaName = N'auth', @ObjectType = N'FUNCTION', @ObjectName = N'udfSessionRootTenantId', @Description = N'Root tenant of the current, valid session scope; NULL otherwise.';
EXEC util.uspSetObjectDescription @SchemaName = N'auth', @ObjectType = N'FUNCTION', @ObjectName = N'udfSessionActingTenantId', @Description = N'Tenant the signed-in user acts for (primary assignment); falls back to the root tenant.';
EXEC util.uspSetObjectDescription @SchemaName = N'auth', @ObjectType = N'FUNCTION', @ObjectName = N'udfLocalDate', @Description = N'Today''s date in a time zone (Windows name); NULL means Eastern Standard Time.';
EXEC util.uspSetObjectDescription @SchemaName = N'auth', @ObjectType = N'FUNCTION', @ObjectName = N'udfSessionToday', @Description = N'Today''s date in the session''s jurisdiction, from SESSION_CONTEXT timeZone.';
EXEC util.uspSetObjectDescription @SchemaName = N'dbo', @ObjectType = N'FUNCTION', @ObjectName = N'udfAgeInMonths', @Description = N'Whole months between a date of birth and a later date; NULL when either is NULL.';
GO
