-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   auth.uspCompletePasswordReset
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Sets a new password with a reset link, after the web application has checked the second factor. The link is consumed in the same statement that checks it, so it works once. The security stamp changes and every session ends, the lockout clears, other open links close, and passwordReset is audited.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec auth.uspCompletePasswordReset ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T211
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE auth.uspCompletePasswordReset
      @passwordResetId UNIQUEIDENTIFIER
      , @tokenVerifier   VARBINARY (32)
      , @passwordHash    NVARCHAR (500)
      , @clientAddress   VARCHAR (45) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[auth].[uspCompletePasswordReset]')
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
    SET @KeyParameters = CONCAT (N'passwordResetId=', @passwordResetId);

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @now DATETIME2 (3) = SYSUTCDATETIME ();
        DECLARE @used TABLE (appUserId UNIQUEIDENTIFIER NOT NULL);

        UPDATE auth.passwordReset
           SET consumedUtc = @now
        OUTPUT inserted.appUserId INTO @used (appUserId)
         WHERE passwordResetId = @passwordResetId AND tokenVerifier = @tokenVerifier AND consumedUtc IS NULL
           AND supersededUtc IS NULL AND expiresUtc > @now AND failedAttempts < maxAttempts AND isDeleted = 0;

        DECLARE @appUserId UNIQUEIDENTIFIER = (SELECT TOP (1) appUserId FROM @used);

        IF @appUserId IS NOT NULL
        BEGIN
            UPDATE auth.appUser
               SET passwordHash = @passwordHash, securityStamp = NEWID (), failedSignInCount = 0, lockoutEndUtc = NULL
                 , passwordChangedUtc = @now, passwordChangeRequired = 0
             WHERE appUserId = @appUserId AND isDeleted = 0;

            UPDATE auth.passwordReset
               SET supersededUtc = SYSUTCDATETIME ()
             WHERE appUserId = @appUserId AND consumedUtc IS NULL AND supersededUtc IS NULL AND isDeleted = 0;

            UPDATE auth.sessionScope SET revokedUtc = @now WHERE appUserId = @appUserId AND revokedUtc IS NULL;

            INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId, clientAddress)
            SELECT @appUserId, rootTenantId, 'passwordReset', 'appUser', @appUserId, @clientAddress
              FROM auth.appUser WHERE appUserId = @appUserId;
        END;

        SELECT CASE WHEN @appUserId IS NULL THEN 'invalid' ELSE 'reset' END AS outcome, @appUserId AS appUserId;

        SET @Comments = CASE WHEN @appUserId IS NULL THEN N'Reset link not valid.' ELSE N'Password reset.' END;

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
    , @ObjectName  = N'uspCompletePasswordReset'
    , @Description = N'Sets a new password with a reset link, after the web application has checked the second factor. The link is consumed in the same statement that checks it, so it works once. The security stamp changes and every session ends, the lockout clears, other open links close, and passwordReset is audited.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON auth.uspCompletePasswordReset TO meritWebRole;
END;
GO
