SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO
-- Release the schema binding so the projection definition can be upgraded on every deployment.
-- Deployment must finish before runtime access resumes; 210_securityPolicy restores all predicates.
IF EXISTS(SELECT 1 FROM sys.security_policies WHERE object_id=OBJECT_ID(N'auth.tenantIsolationPolicy'))
 DROP SECURITY POLICY auth.tenantIsolationPolicy;
GO
-- Release the dependency on tvfPersonPredicate before 200_rlsFunctions upgrades it.
IF OBJECT_ID(N'auth.tvfContactCorrectionPredicate',N'IF') IS NOT NULL
 EXEC(N'ALTER FUNCTION auth.tvfContactCorrectionPredicate(@rootTenantId UNIQUEIDENTIFIER,@owningTenantId UNIQUEIDENTIFIER,@personId UNIQUEIDENTIFIER,@caregiverId UNIQUEIDENTIFIER) RETURNS TABLE WITH SCHEMABINDING AS RETURN SELECT 1 AS isVisible WHERE 1=0;');
IF OBJECT_ID(N'auth.tvfContactCorrectionWritePredicate',N'IF') IS NOT NULL
 EXEC(N'ALTER FUNCTION auth.tvfContactCorrectionWritePredicate(@rootTenantId UNIQUEIDENTIFIER,@owningTenantId UNIQUEIDENTIFIER,@personId UNIQUEIDENTIFIER,@caregiverId UNIQUEIDENTIFIER,@isDeleted BIT) RETURNS TABLE WITH SCHEMABINDING AS RETURN SELECT 1 AS isVisible WHERE 1=0;');
IF OBJECT_ID(N'auth.tvfRelatedContactPredicate',N'IF') IS NOT NULL
 EXEC(N'ALTER FUNCTION auth.tvfRelatedContactPredicate(@rootTenantId UNIQUEIDENTIFIER,@owningTenantId UNIQUEIDENTIFIER,@personId UNIQUEIDENTIFIER) RETURNS TABLE WITH SCHEMABINDING AS RETURN SELECT 1 AS isVisible WHERE 1=0;');
GO
