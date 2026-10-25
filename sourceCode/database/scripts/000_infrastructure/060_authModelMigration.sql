SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
GO

/*
    Authorization model, version 2. Runs before the table scripts on every deployment and does nothing once applied.

    1. auth.profile becomes auth.role (and profilePermission becomes rolePermission): a role is a named bundle of
       permissions. "Profile" now means what a person signs in with: their master profile (their assignments) and the
       profiles they make for themselves (auth.userProfile). Tables, columns, constraints and indexes are renamed in
       place, so existing assignments keep working; the audit triggers are dropped and the table scripts recreate them.
    2. Tenant types become platform, region, state, tribe, program and jurisdiction, and a state postal code is optional
       (the platform and regions have none).
    3. The row-level security predicates gain write rules (per-tenant read, write and full access). Their functions are
       schema-bound to the policy, so when the old version is found the policy is dropped here; 200_security recreates
       the functions and 210_securityPolicy the policy with every predicate. Open sessions are ended, because their
       stored scopes predate the new access levels.
*/

-- ------------------------------------------------------------------------------------------------ 1. profile -> role
IF OBJECT_ID (N'auth.profile', N'U') IS NOT NULL AND OBJECT_ID (N'auth.role', N'U') IS NULL
BEGIN
    BEGIN TRANSACTION;

    DECLARE @sql NVARCHAR (MAX) = N'';

    -- Audit triggers are rebuilt by the table scripts under the new names.
    SELECT @sql += N'DROP TRIGGER ' + QUOTENAME (SCHEMA_NAME (o.schema_id)) + N'.' + QUOTENAME (tr.name) + N';'
      FROM sys.triggers AS tr
      JOIN sys.objects AS o ON o.object_id = tr.parent_id
     WHERE o.object_id IN (OBJECT_ID (N'auth.profile'), OBJECT_ID (N'auth.profilePermission'), OBJECT_ID (N'auth.userAssignment'));
    EXEC sys.sp_executesql @sql;

    EXEC sys.sp_rename N'auth.profile', N'role';
    EXEC sys.sp_rename N'auth.profilePermission', N'rolePermission';
    EXEC sys.sp_rename N'auth.role.profileId', N'roleId', N'COLUMN';
    EXEC sys.sp_rename N'auth.role.profileCode', N'roleCode', N'COLUMN';
    EXEC sys.sp_rename N'auth.role.profileName', N'roleName', N'COLUMN';
    EXEC sys.sp_rename N'auth.rolePermission.profilePermissionId', N'rolePermissionId', N'COLUMN';
    EXEC sys.sp_rename N'auth.rolePermission.profileId', N'roleId', N'COLUMN';
    EXEC sys.sp_rename N'auth.userAssignment.profileId', N'roleId', N'COLUMN';

    -- Constraint and index names follow the convention, so rename them the same way.
    DECLARE @renames TABLE (objectName SYSNAME, newName SYSNAME, kind VARCHAR (10));
    INSERT @renames
    SELECT QUOTENAME (SCHEMA_NAME (o.schema_id)) + N'.' + QUOTENAME (o.name), x.newName, 'OBJECT'
      FROM sys.objects AS o
     CROSS APPLY (SELECT REPLACE (REPLACE (REPLACE (REPLACE (REPLACE (o.name, N'auth_profilePermission', N'auth_rolePermission'),
                         N'auth_profile', N'auth_role'), N'profileId', N'roleId'), N'profileCode', N'roleCode'),
                         N'profileName', N'roleName') AS newName) AS x
     WHERE o.type IN ('PK', 'UQ', 'F', 'D', 'C') AND SCHEMA_NAME (o.schema_id) = N'auth' AND x.newName <> o.name
       AND o.parent_object_id IN (OBJECT_ID (N'auth.role'), OBJECT_ID (N'auth.rolePermission'), OBJECT_ID (N'auth.userAssignment'));
    INSERT @renames
    SELECT N'auth.' + QUOTENAME (OBJECT_NAME (i.object_id)) + N'.' + QUOTENAME (i.name), x.newName, 'INDEX'
      FROM sys.indexes AS i
     CROSS APPLY (SELECT REPLACE (REPLACE (REPLACE (REPLACE (i.name, N'auth_profilePermission', N'auth_rolePermission'),
                         N'auth_profile', N'auth_role'), N'profileId', N'roleId'), N'profileCode', N'roleCode') AS newName) AS x
     WHERE i.object_id IN (OBJECT_ID (N'auth.role'), OBJECT_ID (N'auth.rolePermission'), OBJECT_ID (N'auth.userAssignment'))
       AND i.is_primary_key = 0 AND i.name IS NOT NULL AND x.newName <> i.name;

    DECLARE @name SYSNAME, @new SYSNAME, @kind VARCHAR (10);
    DECLARE renames CURSOR LOCAL FAST_FORWARD FOR SELECT objectName, newName, kind FROM @renames;
    OPEN renames;
    FETCH NEXT FROM renames INTO @name, @new, @kind;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF @kind = 'INDEX'
            EXEC sys.sp_rename @name, @new, N'INDEX';
        ELSE
            EXEC sys.sp_rename @name, @new, N'OBJECT';
        FETCH NEXT FROM renames INTO @name, @new, @kind;
    END;
    CLOSE renames;
    DEALLOCATE renames;

    COMMIT TRANSACTION;
    PRINT N'auth.profile renamed to auth.role.';
END;
GO

IF OBJECT_ID (N'auth.uspListProfiles', N'P') IS NOT NULL
    DROP PROCEDURE auth.uspListProfiles;
GO

-- ------------------------------------------------------------------------------------------------ 2. tenant types
IF EXISTS (SELECT 1 FROM sys.check_constraints
            WHERE name = N'CK_auth_tenant_tenantType' AND definition NOT LIKE N'%jurisdiction%')
BEGIN
    ALTER TABLE auth.tenant DROP CONSTRAINT CK_auth_tenant_tenantType;
    UPDATE auth.tenant SET tenantType = CASE tenantType WHEN 'stateProgram' THEN 'state'
                                                        WHEN 'localHealthDepartment' THEN 'jurisdiction'
                                                        ELSE tenantType END
     WHERE tenantType IN ('stateProgram', 'localHealthDepartment');
    PRINT N'Tenant types migrated.';
END;
GO

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID (N'auth.tenant') AND name = N'statePostalCode'
                                       AND is_nullable = 0)
BEGIN
    ALTER TABLE auth.tenant ALTER COLUMN statePostalCode CHAR (2) NULL;
END;
GO

-- ------------------------------------------------------------------------------------------------ 3. predicates v2
IF EXISTS (SELECT 1 FROM sys.security_policies WHERE name = N'tenantIsolationPolicy' AND SCHEMA_NAME (schema_id) = N'auth')
   AND OBJECT_ID (N'auth.tvfTenantWritePredicate') IS NULL
BEGIN
    DROP SECURITY POLICY auth.tenantIsolationPolicy;
    UPDATE auth.sessionScope SET revokedUtc = SYSUTCDATETIME () WHERE revokedUtc IS NULL;
    PRINT N'Row-level security policy dropped for the version 2 predicates; open sessions ended.';
END;
GO
