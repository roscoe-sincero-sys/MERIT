-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   auth.uspCreatePasswordReset
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Issues a password-reset link when the account may have one: active, enrolled in the authenticator app (a reset also needs the second factor) and with an email address. Enforces the cooldown and the per-account and per-address limits, supersedes the user's open link and stores the new token's verifier. Returns the outcome and, when created, the address and expiry.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Outcomes: created; cooldown (a link went out less than @cooldownSeconds ago and is still open); limit (@maxPerUser for the account or @maxPerAddress from the address in @windowMinutes); notAvailable. The page that asked says the same thing whatever the outcome, so none of this reaches the requester.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec auth.uspCreatePasswordReset ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T211
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE auth.uspCreatePasswordReset
      @passwordResetId  UNIQUEIDENTIFIER
      , @appUserId        UNIQUEIDENTIFIER
      , @tokenVerifier    VARBINARY (32)
      , @supportReference VARCHAR (16)
      , @correlationId    VARCHAR (64) = NULL
      , @clientAddress    VARCHAR (45) = NULL
      , @lifetimeMinutes  INT = 20
      , @maxAttempts      INT = 5
      , @cooldownSeconds  INT = 60
      , @windowMinutes    INT = 60
      , @maxPerUser       INT = 3
      , @maxPerAddress    INT = 10
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[auth].[uspCreatePasswordReset]')
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
    SET @KeyParameters = CONCAT (N'appUserId=', @appUserId, N', supportReference=', @supportReference);

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        IF DATALENGTH (@tokenVerifier) <> 32
        BEGIN
            ;THROW 50000, N'@tokenVerifier must be a 32-byte HMAC-SHA256 value.', 1;
        END;

        IF @lifetimeMinutes NOT BETWEEN 5 AND 120 OR @maxAttempts NOT BETWEEN 1 AND 10 OR @cooldownSeconds NOT BETWEEN 0 AND 3600
           OR @windowMinutes NOT BETWEEN 1 AND 1440 OR @maxPerUser < 1 OR @maxPerAddress < 1
        BEGIN
            ;THROW 50000, N'A lifetime, attempt or rate-limit argument is out of range.', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @now     DATETIME2 (3) = SYSUTCDATETIME ();
        DECLARE @outcome VARCHAR (20)  = 'created';
        DECLARE @email   NVARCHAR (200), @active BIT, @mfa BIT, @last DATETIME2 (3);

        SELECT @email = email, @active = isActive, @mfa = mfaEnabled
          FROM auth.appUser WITH (UPDLOCK, HOLDLOCK)
         WHERE appUserId = @appUserId AND isDeleted = 0;

        IF @active IS NULL OR @active = 0 OR @mfa = 0 OR NULLIF (LTRIM (RTRIM (@email)), N'') IS NULL
        BEGIN
            SET @outcome = 'notAvailable';
        END
        ELSE
        BEGIN
            SELECT @last = MAX (createdDateUtc)
              FROM auth.passwordReset
             WHERE appUserId = @appUserId AND deliveryStatus <> 'failed' AND consumedUtc IS NULL AND isDeleted = 0;

            IF @last IS NOT NULL AND DATEADD (SECOND, @cooldownSeconds, @last) > @now
            BEGIN
                SET @outcome = 'cooldown';
            END
            ELSE IF (SELECT COUNT (*) FROM auth.passwordReset
                      WHERE appUserId = @appUserId AND createdDateUtc > DATEADD (MINUTE, -@windowMinutes, @now) AND isDeleted = 0) >= @maxPerUser
                 OR (@clientAddress IS NOT NULL
                     AND (SELECT COUNT (*) FROM auth.passwordReset
                           WHERE clientAddress = @clientAddress AND createdDateUtc > DATEADD (MINUTE, -@windowMinutes, @now)
                             AND isDeleted = 0) >= @maxPerAddress)
            BEGIN
                SET @outcome = 'limit';
            END;
        END;

        IF @outcome = 'created'
        BEGIN
            UPDATE auth.passwordReset
               SET supersededUtc = SYSUTCDATETIME ()
             WHERE appUserId = @appUserId AND consumedUtc IS NULL AND supersededUtc IS NULL AND isDeleted = 0;

            INSERT auth.passwordReset (passwordResetId, appUserId, tokenVerifier, expiresUtc, maxAttempts, supportReference,
                                       correlationId, clientAddress)
            VALUES (@passwordResetId, @appUserId, @tokenVerifier, DATEADD (MINUTE, @lifetimeMinutes, @now), @maxAttempts,
                    @supportReference, @correlationId, @clientAddress);
        END;

        SELECT @outcome                                                              AS outcome
             , CASE WHEN @outcome = 'created' THEN @email END                        AS email
             , CASE WHEN @outcome = 'created' THEN DATEADD (MINUTE, @lifetimeMinutes, @now) END AS expiresUtc;

        SET @Comments = CONCAT (N'Password reset ', @outcome, N'.');

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
    , @ObjectName  = N'uspCreatePasswordReset'
    , @Description = N'Issues a password-reset link when the account may have one: active, enrolled in the authenticator app (a reset also needs the second factor) and with an email address. Enforces the cooldown and the per-account and per-address limits, supersedes the user''s open link and stores the new token''s verifier. Returns the outcome and, when created, the address and expiry.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON auth.uspCreatePasswordReset TO meritWebRole;
END;
GO
