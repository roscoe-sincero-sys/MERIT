-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   auth.uspListUsers
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

The accounts an administrator can see: people whose home tenant they administer, and people holding a grant at a tenant they administer (whose accounts they cannot change). Every account for a platform administrator.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission tenant.administer (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec auth.uspListUsers ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T160
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE auth.uspListUsers

AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[auth].[uspListUsers]')
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

        IF auth.udfHasPermission (N'tenant.administer') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (tenant.administer).', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @admin TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @admin SELECT tenantId FROM auth.tvfAdminTenants ();

        SELECT u.appUserId, u.userName, u.displayName, u.email, u.isActive, u.mfaEnabled, u.lastSignInUtc, u.lockoutEndUtc
             , h.tenantName AS homeTenantName, h.tenantType AS homeTenantType
             , CAST (CASE WHEN EXISTS (SELECT 1 FROM @admin AS a WHERE a.tenantId = u.homeTenantId) THEN 1 ELSE 0 END AS BIT)
                   AS canManageAccount
             , (SELECT STRING_AGG (CONCAT (r.roleName, N' @ ', t.tenantName,
                                           CASE ua.accessLevel WHEN 'full' THEN N'' ELSE CONCAT (N' (', ua.accessLevel, N' only)') END),
                                   N'; ') WITHIN GROUP (ORDER BY r.roleName)
                  FROM auth.userAssignment AS ua
                  JOIN auth.role   AS r ON r.roleId = ua.roleId
                  JOIN auth.tenant AS t ON t.tenantId = ua.tenantId
                 WHERE ua.appUserId = u.appUserId AND ua.isDeleted = 0
                   AND (ua.endDate IS NULL OR ua.endDate >= CAST (SYSUTCDATETIME () AS DATE))) AS assignments
          FROM auth.appUser AS u
          LEFT JOIN auth.tenant AS h ON h.tenantId = u.homeTenantId
         WHERE u.isDeleted = 0
           AND (EXISTS (SELECT 1 FROM @admin AS a WHERE a.tenantId = u.homeTenantId)
                OR EXISTS (SELECT 1 FROM auth.userAssignment AS ua JOIN @admin AS a ON a.tenantId = ua.tenantId
                            WHERE ua.appUserId = u.appUserId AND ua.isDeleted = 0
                              AND (ua.endDate IS NULL OR ua.endDate >= CAST (SYSUTCDATETIME () AS DATE))))
         ORDER BY u.displayName;

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
    , @ObjectName  = N'uspListUsers'
    , @Description = N'The accounts an administrator can see: people whose home tenant they administer, and people holding a grant at a tenant they administer (whose accounts they cannot change). Every account for a platform administrator.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON auth.uspListUsers TO meritWebRole;
END;
GO
