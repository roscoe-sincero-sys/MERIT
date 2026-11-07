-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   auth.uspGetAdminPasswordPolicy
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

The effective password minimum for an account's selected home tenant. Checks authority over both the existing account and the selected tenant before the private owner-executed policy lookup; never uses the administrator's own active root as a substitute for the account's policy.

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

exec auth.uspGetAdminPasswordPolicy ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		198
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE auth.uspGetAdminPasswordPolicy
      @appUserId    UNIQUEIDENTIFIER = NULL
      , @homeTenantId UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[auth].[uspGetAdminPasswordPolicy]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'appUserId=', @appUserId, N', homeTenantId=', @homeTenantId);

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
        IF @appUserId IS NOT NULL AND NOT EXISTS
            (SELECT 1 FROM auth.appUser AS u JOIN @admin AS a ON a.tenantId = u.homeTenantId WHERE u.appUserId = @appUserId AND u.isDeleted = 0)
        BEGIN
            ;THROW 51403, N'Only an administrator of the account home tenant can check its password policy.', 1;
        END;
        SET @homeTenantId = COALESCE (@homeTenantId,
            (SELECT homeTenantId FROM auth.appUser WHERE appUserId = @appUserId AND isDeleted = 0),
            TRY_CAST (SESSION_CONTEXT (N'actingTenantId') AS UNIQUEIDENTIFIER));
        IF @homeTenantId IS NULL OR NOT EXISTS (SELECT 1 FROM @admin WHERE tenantId = @homeTenantId)
        BEGIN
            ;THROW 51403, N'Choose a home tenant you administer.', 1;
        END;
        DECLARE @minimum INT = 15;
        EXEC auth.uspGetPasswordPolicy @appUserId = @appUserId, @passwordMinLength = @minimum OUTPUT, @homeTenantId = @homeTenantId;
        SELECT @minimum AS passwordMinLength;

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
    , @ObjectName  = N'uspGetAdminPasswordPolicy'
    , @Description = N'The effective password minimum for an account''s selected home tenant. Checks authority over both the existing account and the selected tenant before the private owner-executed policy lookup; never uses the administrator''s own active root as a substitute for the account''s policy.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON auth.uspGetAdminPasswordPolicy TO meritWebRole;
END;
GO
