SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO
-- Request access never grants access to either person's chart. Procedure checks further constrain
-- contact capabilities, relationship validity and review authority per tenant/profile.
CREATE OR ALTER FUNCTION auth.tvfContactCorrectionPredicate
(@rootTenantId UNIQUEIDENTIFIER,@owningTenantId UNIQUEIDENTIFIER,@personId UNIQUEIDENTIFIER,@caregiverId UNIQUEIDENTIFIER)
RETURNS TABLE WITH SCHEMABINDING AS RETURN
 SELECT 1 AS isVisible WHERE EXISTS(SELECT 1 FROM auth.tvfPersonPredicate(@rootTenantId,@owningTenantId,@personId))
 OR EXISTS(SELECT 1 FROM dbo.person g CROSS APPLY auth.tvfPersonPredicate(g.rootTenantId,g.owningTenantId,g.personId) scope
 WHERE g.personId=@caregiverId AND g.rootTenantId=@rootTenantId AND g.isDeleted=0);
GO
CREATE OR ALTER FUNCTION auth.tvfContactCorrectionWritePredicate
(@rootTenantId UNIQUEIDENTIFIER,@owningTenantId UNIQUEIDENTIFIER,@personId UNIQUEIDENTIFIER,@caregiverId UNIQUEIDENTIFIER,@isDeleted BIT)
RETURNS TABLE WITH SCHEMABINDING AS RETURN
 SELECT 1 AS isVisible WHERE EXISTS(SELECT 1 FROM auth.tvfPersonWritePredicate(@rootTenantId,@owningTenantId,@personId,@isDeleted))
 OR EXISTS(SELECT 1 FROM dbo.person g CROSS APPLY auth.tvfPersonWritePredicate(g.rootTenantId,g.owningTenantId,g.personId,@isDeleted) scope
 WHERE g.personId=@caregiverId AND g.rootTenantId=@rootTenantId AND g.isDeleted=0);
GO
EXEC util.uspSetObjectDescription @SchemaName=N'auth',@ObjectType=N'FUNCTION',@ObjectName=N'tvfContactCorrectionPredicate',@Description=N'Correction request reads via patient or caregiver chart access; does not grant either chart.';
EXEC util.uspSetObjectDescription @SchemaName=N'auth',@ObjectType=N'FUNCTION',@ObjectName=N'tvfContactCorrectionWritePredicate',@Description=N'Correction request writes via patient or caregiver write scope; procedures enforce requester and owner review boundaries.';
GO
