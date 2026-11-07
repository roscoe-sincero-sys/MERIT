-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   auth.uspGetPasswordPolicy
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

The effective password minimum for a user's or selected home tenant's data root (15 to 128). Runs as its owner, because a reset page has no session yet and row-level security hides the setting. Granted to no role; reached only through the password procedures, which pass the user they are working on.

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

DECLARE @n INT; exec auth.uspGetPasswordPolicy @appUserId = '...', @passwordMinLength = @n OUTPUT

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T211
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE auth.uspGetPasswordPolicy
      @appUserId         UNIQUEIDENTIFIER = NULL
      , @passwordMinLength INT OUTPUT
      , @homeTenantId      UNIQUEIDENTIFIER = NULL
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[auth].[uspGetPasswordPolicy]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'appUserId=', @appUserId);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @root UNIQUEIDENTIFIER = CASE WHEN @homeTenantId IS NOT NULL
            THEN (SELECT rootTenantId FROM auth.tenant WHERE tenantId = @homeTenantId AND isDeleted = 0)
            ELSE (SELECT rootTenantId FROM auth.appUser WHERE appUserId = @appUserId AND isDeleted = 0) END;
        DECLARE @today DATE = CAST (SYSUTCDATETIME () AS DATE);
        SET @passwordMinLength = NULL;

        SELECT TOP (1) @passwordMinLength = TRY_CAST (s.settingValue AS INT)
          FROM config.jurisdictionSetting AS s
         WHERE s.rootTenantId = @root AND s.settingKey = 'passwordMinLength' AND s.isDeleted = 0
           AND s.effectiveFrom <= @today AND (s.effectiveTo IS NULL OR s.effectiveTo >= @today)
         ORDER BY s.effectiveFrom DESC;

        SET @passwordMinLength = CASE WHEN @passwordMinLength IS NULL OR @passwordMinLength < 15 THEN 15
                                     WHEN @passwordMinLength > 128 THEN 128 ELSE @passwordMinLength END;

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
    , @ObjectName  = N'uspGetPasswordPolicy'
    , @Description = N'The effective password minimum for a user''s or selected home tenant''s data root (15 to 128). Runs as its owner, because a reset page has no session yet and row-level security hides the setting. Granted to no role; reached only through the password procedures, which pass the user they are working on.';
GO
