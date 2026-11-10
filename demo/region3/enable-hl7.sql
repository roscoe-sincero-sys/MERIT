SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;
IF DB_NAME()<>'MERITRegion3Demo' THROW 51000,'Run only on MERITRegion3Demo.',1;
DECLARE @scope uniqueidentifier=NEWID(),@user uniqueidentifier=(SELECT appUserId FROM auth.appUser WHERE userName='marylanddemo');
DECLARE @expires datetime2=DATEADD(hour,1,SYSUTCDATETIME());
EXEC auth.uspCreateSessionScope @sessionScopeId=@scope,@appUserId=@user,@expiresUtc=@expires;
EXEC sys.sp_set_session_context @key=N'scopeKey',@value=@scope;
EXEC sys.sp_set_session_context @key=N'appUserId',@value=@user;
DECLARE @md uniqueidentifier=(SELECT tenantId FROM auth.tenant WHERE tenantCode='MD-LEAD');
EXEC sys.sp_set_session_context @key=N'actingTenantId',@value=@md;
DECLARE @previous uniqueidentifier,@sender uniqueidentifier,@name nvarchar(120),@id uniqueidentifier;
DECLARE sources CURSOR LOCAL FAST_FORWARD FOR
 SELECT intakeProfileId,senderOrganizationId,profileName FROM dbo.intakeProfile
 WHERE fileType='csv' AND isActive=1 AND isDeleted=0 AND (profileName LIKE N'Region 3 Demo%' OR profileName=N'Chesapeake Reference Laboratory CSV');
OPEN sources;
FETCH NEXT FROM sources INTO @previous,@sender,@name;
WHILE @@FETCH_STATUS=0
BEGIN
 SET @id=CAST(HASHBYTES('MD5',CONCAT(N'region3:hl7:',@previous)) AS uniqueidentifier);
 IF @name=N'Chesapeake Reference Laboratory CSV'
 BEGIN
  SET @name=N'Chesapeake Reference Laboratory HL7';
  SET @id='24b17544-062a-fd3f-4752-d18e85ed6157';
 END
 ELSE SET @name=CONCAT(@name,N' HL7');
 IF NOT EXISTS(SELECT 1 FROM dbo.intakeProfile WHERE intakeProfileId=@id)
  EXEC dbo.uspSaveIntakeProfile @intakeProfileId=@id,@senderOrganizationId=@sender,@profileName=@name,
   @fileType='hl7',@columnMapJson=N'{}',@dateFormats=N'yyyy-MM-dd',@constantsJson=N'{}',@valueTranslationsJson=N'{}';
 FETCH NEXT FROM sources INTO @previous,@sender,@name;
END;
CLOSE sources;
DEALLOCATE sources;
SELECT profileName,fileType FROM dbo.intakeProfile WHERE fileType='hl7' AND isActive=1 AND isDeleted=0;
UPDATE auth.sessionScope SET revokedUtc=SYSUTCDATETIME() WHERE sessionScopeId=@scope;
