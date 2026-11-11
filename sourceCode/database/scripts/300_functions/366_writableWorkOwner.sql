SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO
/*
ObjectName: dbo.udfWritableWorkOwner
Author: MERIT build
CreateDate: 2026-11-11
Description: The owner for work generated in the caller's session: the routed (preferred) tenant when the session can
             write there, otherwise the session's acting tenant.
Requirements: auth.tvfTenantWritePredicate (the same rule the row-level-security block predicates apply).
Notes: Intake and result follow-up (cases, receipt-quality, notification and testing tasks) route work to a county or to
       the data root. A county-scoped session cannot write either when the patient lives elsewhere or the work is
       root-owned, and the block predicate then aborts the whole intake row (issue 268). This keeps the routed owner
       whenever it is writable, so root- and state-level sessions behave exactly as before, and otherwise keeps the
       work where the caller works. No elevated execution; the caller cannot name an arbitrary tenant.
Example Usage: SELECT dbo.udfWritableWorkOwner(@rootTenantId, dbo.udfPatientWorkOwner(@personId));
Modification History: 2026-11-11, issue 268, initial version.
*/
CREATE OR ALTER FUNCTION dbo.udfWritableWorkOwner(@rootTenantId UNIQUEIDENTIFIER, @preferredTenantId UNIQUEIDENTIFIER)
RETURNS UNIQUEIDENTIFIER
AS
BEGIN
 IF @preferredTenantId IS NOT NULL AND EXISTS (SELECT 1 FROM auth.tvfTenantWritePredicate (@rootTenantId, @preferredTenantId, 0))
  RETURN @preferredTenantId;
 RETURN COALESCE (auth.udfSessionActingTenantId (), @preferredTenantId);
END;
GO
EXEC util.uspSetObjectDescription @SchemaName=N'dbo',@ObjectType=N'FUNCTION',@ObjectName=N'udfWritableWorkOwner',
 @Description=N'Owner for session-generated work: the routed tenant when the session can write it, otherwise the acting tenant (issue 268). Grants no access.';
GO
