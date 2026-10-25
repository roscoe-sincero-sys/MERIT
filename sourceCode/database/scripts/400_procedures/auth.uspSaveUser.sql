-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   auth.uspSaveUser
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Creates or updates an account. New accounts work for a tenant the administrator administers and get an empty master profile; an existing account can be changed only by an administrator of its home tenant. Nobody can deactivate their own account. A new account must enroll in MFA at first sign-in.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission tenant.administer (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec auth.uspSaveUser ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T160
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE auth.uspSaveUser
      @appUserId    UNIQUEIDENTIFIER
      , @userName     NVARCHAR (100)
      , @displayName  NVARCHAR (120)
      , @email        NVARCHAR (200) = NULL
      , @isActive     BIT = 1
      , @passwordHash NVARCHAR (500) = NULL
      , @homeTenantId UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[auth].[uspSaveUser]')
          , @StartTimeUtc   DATETIME2 (3)  = SYSUTCDATETIME ()
          , @EndTimeUtc     DATETIME2 (3)  = NULL
          , @ExecutionId    BIGINT         = NULL
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @Comments       NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never a credential, a payload, or patient data.
    SET @KeyParameters = CONCAT (N'appUserId=', @appUserId, N', homeTenantId=', @homeTenantId);

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        IF auth.udfHasPermission (N'tenant.administer') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (tenant.administer).', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @admin TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @admin SELECT tenantId FROM auth.tvfAdminTenants ();
        DECLARE @me UNIQUEIDENTIFIER = auth.udfSessionUserId ();
        DECLARE @root UNIQUEIDENTIFIER = (SELECT rootTenantId FROM auth.tenant WHERE tenantId = @homeTenantId AND isDeleted = 0);

        IF @homeTenantId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM @admin WHERE tenantId = @homeTenantId)
        BEGIN
            ;THROW 51403, N'You can place people only in tenants you administer.', 1;
        END;

        IF EXISTS (SELECT 1 FROM auth.appUser WHERE appUserId = @appUserId)
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM auth.appUser AS u JOIN @admin AS a ON a.tenantId = u.homeTenantId
                            WHERE u.appUserId = @appUserId AND u.isDeleted = 0)
            BEGIN
                ;THROW 51403, N'Only an administrator of the tenant this person works for can change their account.', 1;
            END;

            IF @appUserId = @me AND @isActive = 0
            BEGIN
                ;THROW 51422, N'You cannot deactivate your own account.', 1;
            END;

            UPDATE auth.appUser
               SET displayName   = @displayName
                 , email         = @email
                 , isActive      = @isActive
                 , homeTenantId  = COALESCE (@homeTenantId, homeTenantId)
                 , rootTenantId  = COALESCE (@root, rootTenantId)
                 , passwordHash  = COALESCE (@passwordHash, passwordHash)
                 , securityStamp = CASE WHEN @passwordHash IS NOT NULL OR @isActive = 0 THEN NEWID () ELSE securityStamp END
             WHERE appUserId = @appUserId AND isDeleted = 0;

            IF @isActive = 0
            BEGIN
                UPDATE auth.sessionScope SET revokedUtc = SYSUTCDATETIME () WHERE appUserId = @appUserId AND revokedUtc IS NULL;
            END;
        END
        ELSE
        BEGIN
            IF @homeTenantId IS NULL OR @root IS NULL
            BEGIN
                ;THROW 51422, N'Choose the tenant this person works for.', 1;
            END;

            IF @passwordHash IS NULL
            BEGIN
                ;THROW 51422, N'A new account needs an initial password.', 1;
            END;

            IF EXISTS (SELECT 1 FROM auth.appUser WHERE userName = @userName AND isDeleted = 0)
            BEGIN
                ;THROW 51422, N'That user name is already taken.', 1;
            END;

            INSERT auth.appUser (appUserId, rootTenantId, homeTenantId, userName, displayName, email, passwordHash, securityStamp,
                                 isActive)
            VALUES (@appUserId, @root, @homeTenantId, @userName, @displayName, @email, @passwordHash, NEWID (), @isActive);

            INSERT auth.userProfile (userProfileId, appUserId, profileName, profileDescription, isMaster, isActive)
            VALUES (CAST (HASHBYTES ('MD5', CONCAT (N'masterProfile:', @appUserId)) AS UNIQUEIDENTIFIER), @appUserId,
                    N'Master profile', N'Everything you have been given. Managed by your administrator.', 1, 1);
        END;

        SET @Comments = N'User saved.';

        -- =========================================================================================
        -- ===== End of the procedure's own work. ===================================================
        -- =========================================================================================

        IF @@TRANCOUNT > 0
        BEGIN
            COMMIT TRANSACTION;
        END;

        -- Completion. Deliberately after the COMMIT.
        SET @EndTimeUtc = SYSUTCDATETIME ();

        IF @ExecutionId IS NOT NULL
        BEGIN
            UPDATE logs.ExecutionLog
               SET EndDateUtc          = @EndTimeUtc
                 , ElapsedMilliseconds = CAST (LEAST (DATEDIFF_BIG (MILLISECOND, @StartTimeUtc, @EndTimeUtc)
                                                    , CAST (2147483647 AS BIGINT)) AS INT)
                 , Successful          = 1
                 , Comments            = @Comments
                 , modifiedBy          = ORIGINAL_LOGIN ()
                 , modifiedDateUtc     = @EndTimeUtc
             WHERE ExecutionLogId = @ExecutionId;
        END;

    END TRY
    BEGIN CATCH

        SELECT @ErrorNumber = ERROR_NUMBER ()
             , @ErrorProc   = ERROR_PROCEDURE ()
             , @ErrorLine   = ERROR_LINE ()
             , @ErrorMsg    = ERROR_MESSAGE ()
                            + N' (error '  + CAST (ERROR_NUMBER () AS NVARCHAR (11))
                            + N', line '   + CAST (ERROR_LINE ()   AS NVARCHAR (11)) + N')';

        IF XACT_STATE () <> 0
        BEGIN
            ROLLBACK TRANSACTION;
        END;

        BEGIN TRY
            IF @ExecutionId IS NULL
               OR NOT EXISTS (SELECT 1
                                FROM logs.ExecutionLog
                               WHERE ExecutionLogId = @ExecutionId)
            BEGIN
                EXEC logs.uspStartExecutionLogging
                      @ProcedureName          = @ProcName
                    , @KeyParameters          = @KeyParameters
                    , @StartDateUtc           = @StartTimeUtc
                    , @ReCreatedAfterRollback = 1
                    , @ExecutionLogId         = @ExecutionId OUTPUT;
            END;
        END TRY
        BEGIN CATCH
            SET @ExecutionId = NULL;
        END CATCH;

        EXEC logs.uspRecordExecutionError
              @ProcedureName   = @ProcName
            , @KeyParameters   = @KeyParameters
            , @ExecutionLogId  = @ExecutionId
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
    , @ObjectName  = N'uspSaveUser'
    , @Description = N'Creates or updates an account. New accounts work for a tenant the administrator administers and get an empty master profile; an existing account can be changed only by an administrator of its home tenant. Nobody can deactivate their own account. A new account must enroll in MFA at first sign-in.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON auth.uspSaveUser TO meritWebRole;
END;
GO
