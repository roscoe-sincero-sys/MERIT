-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   auth.uspListTenants
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Tenants for pickers and the tenant tree: those the caller administers, those whose records the session covers, and those in the caller's master profile; every tenant for a platform administrator. Depth is counted from the platform tenant.

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

exec auth.uspListTenants ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T160
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE auth.uspListTenants

AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[auth].[uspListTenants]')
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

        DECLARE @ids TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @ids
        SELECT tenantId FROM @admin
        UNION
        SELECT st.tenantId
          FROM auth.sessionScopeTenant AS st
         WHERE st.sessionScopeId = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER) AND st.isDeleted = 0
        UNION
        SELECT tenantId FROM auth.tvfUserGrantPermissions (@me);

        SELECT t.tenantId, t.parentTenantId, t.tenantCode, t.tenantName, t.tenantType, t.rootTenantId, tc.depth
          FROM auth.tenant AS t
          JOIN @ids AS i ON i.tenantId = t.tenantId
          JOIN auth.tenantClosure AS tc ON tc.descendantTenantId = t.tenantId AND tc.ancestorTenantId = @platform AND tc.isDeleted = 0
         WHERE t.isDeleted = 0
         ORDER BY tc.depth, t.tenantName;

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
    , @ObjectName  = N'uspListTenants'
    , @Description = N'Tenants for pickers and the tenant tree: those the caller administers, those whose records the session covers, and those in the caller''s master profile; every tenant for a platform administrator. Depth is counted from the platform tenant.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON auth.uspListTenants TO meritWebRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON auth.uspListTenants TO meritWorkerRole;
END;
GO
