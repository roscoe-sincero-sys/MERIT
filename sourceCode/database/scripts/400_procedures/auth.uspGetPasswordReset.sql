-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   auth.uspGetPasswordReset
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Checks a reset link: its id and the verifier of its token. Only a matching, open, unexpired link returns the account, its user name, whether it has a second factor, the password rule and the attempts left. Otherwise the status alone: expired, used, superseded or none.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

The token is 32 random bytes, so guessing it is not practical and a wrong token costs no attempt. Attempts count wrong second-factor codes.

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec auth.uspGetPasswordReset ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T211
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE auth.uspGetPasswordReset
      @passwordResetId UNIQUEIDENTIFIER
      , @tokenVerifier   VARBINARY (32)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[auth].[uspGetPasswordReset]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'passwordResetId=', @passwordResetId);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @now DATETIME2 (3) = SYSUTCDATETIME ();
        DECLARE @user UNIQUEIDENTIFIER, @status VARCHAR (20), @minLength INT = 15;

        SELECT @user   = r.appUserId
             , @status = CASE WHEN r.consumedUtc IS NOT NULL THEN 'used'
                              WHEN r.supersededUtc IS NOT NULL THEN 'superseded'
                              WHEN r.expiresUtc <= @now THEN 'expired'
                              WHEN r.failedAttempts >= r.maxAttempts THEN 'superseded'
                              ELSE 'valid' END
          FROM auth.passwordReset AS r
         WHERE r.passwordResetId = @passwordResetId AND r.tokenVerifier = @tokenVerifier AND r.isDeleted = 0;

        IF @status = 'valid'
        BEGIN
            EXEC auth.uspGetPasswordPolicy @appUserId = @user, @passwordMinLength = @minLength OUTPUT;
        END;

        SELECT COALESCE (@status, 'none')                                    AS status
             , CASE WHEN @status = 'valid' THEN u.appUserId END              AS appUserId
             , CASE WHEN @status = 'valid' THEN u.userName END               AS userName
             , CASE WHEN @status = 'valid' THEN u.mfaEnabled ELSE 0 END      AS mfaEnabled
             , CASE WHEN @status = 'valid' THEN u.lockoutEndUtc END          AS lockoutEndUtc
             , @minLength                                                    AS passwordMinLength
             , CASE WHEN @status = 'valid' THEN r.maxAttempts - r.failedAttempts ELSE 0 END AS remainingAttempts
          FROM (SELECT 1 AS one) AS x
          LEFT JOIN auth.passwordReset AS r ON r.passwordResetId = @passwordResetId AND @status = 'valid'
          LEFT JOIN auth.appUser       AS u ON u.appUserId = @user AND @status = 'valid';

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
    , @ObjectName  = N'uspGetPasswordReset'
    , @Description = N'Checks a reset link: its id and the verifier of its token. Only a matching, open, unexpired link returns the account, its user name, whether it has a second factor, the password rule and the attempts left. Otherwise the status alone: expired, used, superseded or none.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON auth.uspGetPasswordReset TO meritWebRole;
END;
GO
