SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO
/*
ObjectName: auth.tvfReviewAccounts
Author: MERIT build
CreateDate: 2026-11-11
Description: The accounts a reviewer of the tenants in auth.tvfReviewTenants (@permissionCode) sees in security reports
             (users and access, sign-in activity, account sign-in history): everyone who CAN reach those tenants' data
             and everyone who DID.
Requirements: auth.tvfReviewTenants, auth.userAssignment, logs.auditEvent.owningTenantId (#270).
Notes: Issue 278. Can reach: homed in a reviewed tenant, or a current grant at a reviewed tenant (the reviewed set
       already includes descendants). Did reach: the actor of an audit event in the last 365 days on a record a reviewed tenant owned, so a
       statewide worker who opened a county case appears in that county's review. A statewide grant alone does not list
       every statewide worker in every county: observed access is the test, which keeps county lists bounded and keeps the
       county administrator's Users page to its own people plus actual outside actors. Platform accounts see no records
       and so never appear through the audit clause for record events. Staff of sibling jurisdictions only are not
       included. Seeing an account here grants nothing: account changes stay with its home tenant's administrators.
Example Usage: SELECT appUserId FROM auth.tvfReviewAccounts ('security.review');
Modification History: 2026-11-11, issue 278, initial version.
*/
CREATE OR ALTER FUNCTION auth.tvfReviewAccounts (@permissionCode VARCHAR (60))
RETURNS TABLE
AS
RETURN
    WITH reviewed AS (SELECT tenantId FROM auth.tvfReviewTenants (@permissionCode))
    SELECT u.appUserId
      FROM auth.appUser AS u
     WHERE u.isDeleted = 0 AND EXISTS (SELECT 1 FROM reviewed AS r WHERE r.tenantId = u.homeTenantId)
    UNION
    SELECT ua.appUserId
      FROM auth.userAssignment AS ua
     WHERE ua.isDeleted = 0
       AND (ua.endDate IS NULL OR ua.endDate >= auth.udfSessionToday ())
       AND EXISTS (SELECT 1 FROM reviewed AS r WHERE r.tenantId = ua.tenantId)
    UNION
    SELECT e.appUserId
      FROM logs.auditEvent AS e
     WHERE e.appUserId IS NOT NULL
       AND e.owningTenantId IS NOT NULL
       AND e.eventUtc >= DATEADD (DAY, -365, SYSUTCDATETIME ())
       AND EXISTS (SELECT 1 FROM reviewed AS r WHERE r.tenantId = e.owningTenantId);
GO
EXEC util.uspSetObjectDescription @SchemaName=N'auth',@ObjectType=N'FUNCTION',@ObjectName=N'tvfReviewAccounts',
 @Description=N'Accounts a security reviewer sees for the reviewed tenants: homed or granted there, plus actors (for example statewide staff) with audited access to those tenants'' records in the last year (#278).';
GO
