SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO
/*
ObjectName: dbo.udfPatientWorkOwner
Author: MERIT build
CreateDate: 2026-11-08
Description: Current serving jurisdiction for patient-only work, with the visible patient owner as fallback.
Requirements: Scoped person/residence tables and active jurisdiction membership.
Notes: No elevated execution or arbitrary caller-supplied tenant. Overlapping jurisdictions are resolved deterministically.
Example Usage: SELECT dbo.udfPatientWorkOwner(@personId);
Modification History: 2026-11-08, issue 252, initial version.
*/
CREATE OR ALTER FUNCTION dbo.udfPatientWorkOwner(@personId UNIQUEIDENTIFIER)
RETURNS UNIQUEIDENTIFIER
AS
BEGIN
 DECLARE @root UNIQUEIDENTIFIER,@owner UNIQUEIDENTIFIER,@today DATE=auth.udfSessionToday();
 SELECT @root=rootTenantId,@owner=owningTenantId FROM dbo.person WHERE personId=@personId AND isDeleted=0;
 SELECT TOP(1) @owner=t.tenantId FROM dbo.personLocation pl JOIN dbo.location l ON l.locationId=pl.locationId AND l.isDeleted=0
 JOIN config.subJurisdiction sj ON sj.rootTenantId=@root AND sj.countyFips=l.countyFips AND sj.isDeleted=0
 JOIN auth.tenantJurisdiction tj ON tj.subJurisdictionId=sj.subJurisdictionId AND tj.isDeleted=0
 JOIN auth.tenant t ON t.tenantId=tj.tenantId AND t.rootTenantId=@root AND t.tenantType='jurisdiction' AND t.isActive=1 AND t.isDeleted=0
 WHERE pl.personId=@personId AND pl.isDeleted=0 AND pl.role='residence' AND pl.isPrimary=1
 AND pl.startDate<=@today AND (pl.endDate IS NULL OR pl.endDate>=@today) ORDER BY t.tenantCode;
 RETURN @owner;
END;
GO
EXEC util.uspSetObjectDescription @SchemaName=N'dbo',@ObjectType=N'FUNCTION',@ObjectName=N'udfPatientWorkOwner',
 @Description=N'Current scoped serving jurisdiction for patient-only clinical work. Does not change case ownership or grant access.';
GO
