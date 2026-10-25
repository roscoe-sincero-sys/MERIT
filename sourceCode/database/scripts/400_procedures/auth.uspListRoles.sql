-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   auth.uspListRoles
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Roles the caller can work with: for administrators, roles usable at the tenants they administer; for anyone, the roles in their own master profile (for building their own profiles). Platform roles only for a platform administrator.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec auth.uspListRoles ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T160
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE auth.uspListRoles

AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[auth].[uspListRoles]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = NULL;

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @admin TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @admin SELECT tenantId FROM auth.tvfAdminTenants ();
        DECLARE @platform UNIQUEIDENTIFIER = CAST (HASHBYTES ('MD5', N'tenant:PLATFORM') AS UNIQUEIDENTIFIER);
        DECLARE @me UNIQUEIDENTIFIER = auth.udfSessionUserId ();

        SELECT r.roleId, r.roleCode, r.roleName, r.isAdministrator, r.isDeidentified, r.rootTenantId, rt.tenantName AS rootTenantName
             , (SELECT STRING_AGG (pm.permissionCode, ', ') WITHIN GROUP (ORDER BY pm.permissionCode)
                  FROM auth.rolePermission AS rp JOIN auth.permission AS pm ON pm.permissionId = rp.permissionId
                 WHERE rp.roleId = r.roleId AND rp.isDeleted = 0) AS permissions
          FROM auth.role   AS r
          JOIN auth.tenant AS rt ON rt.tenantId = r.rootTenantId
         WHERE r.isDeleted = 0
           AND (r.rootTenantId <> @platform OR auth.udfIsPlatformAdmin () = 1)
           AND (auth.udfIsPlatformAdmin () = 1
                OR EXISTS (SELECT 1 FROM @admin AS a
                             JOIN auth.tenantClosure AS c ON c.descendantTenantId = a.tenantId AND c.ancestorTenantId = r.rootTenantId
                                                         AND c.isDeleted = 0)
                OR EXISTS (SELECT 1 FROM auth.userAssignment AS ua WHERE ua.appUserId = @me AND ua.roleId = r.roleId
                                                                     AND ua.isDeleted = 0))
         ORDER BY rt.tenantName, r.roleName;

        -- =========================================================================================
        -- ===== End of the procedure's own work. ===================================================
        -- =========================================================================================

    END TRY
    BEGIN CATCH

        SELECT @ErrorNumber = ERROR_NUMBER ()
             , @ErrorProc   = ERROR_PROCEDURE ()
             , @ErrorLine   = ERROR_LINE ()
             , @ErrorMsg    = ERROR_MESSAGE ()
                            + N' (error '  + CAST (ERROR_NUMBER () AS NVARCHAR (11))
                            + N', line '   + CAST (ERROR_LINE ()   AS NVARCHAR (11)) + N')';

        EXEC logs.uspRecordExecutionError
              @ProcedureName   = @ProcName
            , @KeyParameters   = @KeyParameters
            , @ExecutionLogId  = NULL
            , @ErrorMessage    = @ErrorMsg
            , @ErrorProcedure  = @ErrorProc
            , @ErrorNumber     = @ErrorNumber
            , @ErrorLine       = @ErrorLine
            , @DynamicSql      = @DynamicSql
            , @ContextMessage  = @ContextMessage;

        ;THROW;

    END CATCH;

    RETURN 0;
END;
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'auth'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspListRoles'
    , @Description = N'Roles the caller can work with: for administrators, roles usable at the tenants they administer; for anyone, the roles in their own master profile (for building their own profiles). Platform roles only for a platform administrator.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON auth.uspListRoles TO meritWebRole;
END;
GO
