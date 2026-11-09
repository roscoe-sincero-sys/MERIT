SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO
IF OBJECT_ID(N'auth.tvfRelatedContactPredicate',N'IF') IS NULL
 EXEC(N'CREATE FUNCTION auth.tvfRelatedContactPredicate(@rootTenantId UNIQUEIDENTIFIER,@owningTenantId UNIQUEIDENTIFIER,@personId UNIQUEIDENTIFIER) RETURNS TABLE WITH SCHEMABINDING AS RETURN SELECT 1 AS isVisible WHERE 1=0;');
GO
IF NOT EXISTS(SELECT 1 FROM sys.security_predicates WHERE predicate_definition LIKE N'%tvfRelatedContactPredicate%')
EXEC(N'ALTER FUNCTION auth.tvfRelatedContactPredicate(@rootTenantId UNIQUEIDENTIFIER,@owningTenantId UNIQUEIDENTIFIER,@personId UNIQUEIDENTIFIER)
RETURNS TABLE WITH SCHEMABINDING AS RETURN
 SELECT 1 AS isVisible WHERE EXISTS(SELECT 1 FROM auth.tvfPersonPredicate(@rootTenantId,@owningTenantId,@personId))
 OR EXISTS(SELECT 1 FROM dbo.personRelationship r
 JOIN auth.sessionScope ss ON ss.sessionScopeId=TRY_CAST(SESSION_CONTEXT(N''scopeKey'') AS UNIQUEIDENTIFIER)
 JOIN auth.sessionScopeTenant st ON st.sessionScopeId=ss.sessionScopeId AND st.isDeleted=0 AND st.accessLevel IN(''write'',''full'') AND st.canReadRelatedContacts=1
 JOIN auth.sessionScopePermission sp ON sp.sessionScopeId=ss.sessionScopeId AND sp.permissionCode=''person.edit'' AND sp.isDeleted=0
 WHERE r.relatedPersonId=@personId AND r.rootTenantId=@rootTenantId AND r.isDeleted=0
 AND (r.startDate IS NULL OR r.startDate<=CAST(SYSUTCDATETIME() AT TIME ZONE ''UTC'' AT TIME ZONE COALESCE(TRY_CAST(SESSION_CONTEXT(N''timeZone'') AS VARCHAR(64)),''Eastern Standard Time'') AS DATE))
 AND (r.endDate IS NULL OR r.endDate>CAST(SYSUTCDATETIME() AT TIME ZONE ''UTC'' AT TIME ZONE COALESCE(TRY_CAST(SESSION_CONTEXT(N''timeZone'') AS VARCHAR(64)),''Eastern Standard Time'') AS DATE))
 AND ss.isDeleted=0 AND ss.revokedUtc IS NULL AND ss.expiresUtc>SYSUTCDATETIME()
 AND (r.isPrimaryContact=1 OR r.hasLegalCustody=1 OR r.role IN(''parent'',''legalGuardian'',''fosterParent'',''caregiver''))
 AND (r.owningTenantId=st.tenantId OR EXISTS(SELECT 1 FROM dbo.personAccess pa WHERE pa.personId=r.personId AND pa.rootTenantId=@rootTenantId AND pa.tenantId=st.tenantId AND pa.isDeleted=0)));
');
GO
EXEC util.uspSetObjectDescription @SchemaName=N'auth',@ObjectType=N'FUNCTION',@ObjectName=N'tvfRelatedContactPredicate',
 @Description=N'Contact-only read projection through an active patient relationship and scoped clinical access. Bound only to person identity, contact methods and residences; never grants access to caregiver laboratory results, cases, documents or clinical activities.';
GO
