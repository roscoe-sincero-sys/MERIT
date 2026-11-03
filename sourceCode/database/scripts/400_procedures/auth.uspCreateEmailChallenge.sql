-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   auth.uspCreateEmailChallenge
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Issues an email one-time code when the account may have one: active, not locked, enrolled in the authenticator app, with an email address, allowed by the jurisdiction and (for sign-in) with email codes turned on. Enforces the resend cooldown and the per-account and per-address limits, supersedes the user's open code and stores the new code's verifier. Returns the outcome, the seconds to wait when refused, and the address to send to.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Outcomes: created; cooldown (a code went out less than @cooldownSeconds ago and has been neither used nor failed); limit (@maxPerUser codes for the account, or @maxPerAddress from the address, in @windowMinutes); notAvailable (the account may not use email codes now). A code whose send failed does not start a cooldown, so the person can try again at once, but it still counts toward the limits.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec auth.uspCreateEmailChallenge ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T190
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE auth.uspCreateEmailChallenge
      @mfaEmailChallengeId UNIQUEIDENTIFIER
      , @appUserId           UNIQUEIDENTIFIER
      , @purpose             VARCHAR (10)
      , @codeVerifier        VARBINARY (32)
      , @supportReference    VARCHAR (16)
      , @correlationId       VARCHAR (64) = NULL
      , @clientAddress       VARCHAR (45) = NULL
      , @lifetimeSeconds     INT = 300
      , @maxAttempts         INT = 5
      , @cooldownSeconds     INT = 30
      , @windowMinutes       INT = 15
      , @maxPerUser          INT = 5
      , @maxPerAddress       INT = 20
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[auth].[uspCreateEmailChallenge]')
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
    SET @KeyParameters = CONCAT (N'appUserId=', @appUserId, N', purpose=', @purpose, N', supportReference=', @supportReference);

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        IF @purpose NOT IN ('signIn', 'enroll')
        BEGIN
            ;THROW 50000, N'@purpose must be signIn or enroll.', 1;
        END;

        IF DATALENGTH (@codeVerifier) <> 32
        BEGIN
            ;THROW 50000, N'@codeVerifier must be a 32-byte HMAC-SHA256 value.', 1;
        END;

        IF @lifetimeSeconds NOT BETWEEN 60 AND 900 OR @maxAttempts NOT BETWEEN 1 AND 10 OR @cooldownSeconds NOT BETWEEN 0 AND 600
           OR @windowMinutes NOT BETWEEN 1 AND 1440 OR @maxPerUser < 1 OR @maxPerAddress < 1
        BEGIN
            ;THROW 50000, N'A lifetime, attempt or rate-limit argument is out of range.', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @now        DATETIME2 (3) = SYSUTCDATETIME ();
        DECLARE @outcome    VARCHAR (20)  = 'created';
        DECLARE @retryAfter INT           = 0;
        DECLARE @allowed    BIT           = 0;
        DECLARE @email      NVARCHAR (200), @root UNIQUEIDENTIFIER, @active BIT, @mfa BIT, @verified DATETIME2 (3),
                @lockout    DATETIME2 (3), @last DATETIME2 (3), @oldest DATETIME2 (3);

        -- Serializes every request for this account: concurrent requests take turns here and see each other's rows.
        SELECT @email = email, @root = rootTenantId, @active = isActive, @mfa = mfaEnabled, @verified = mfaEmailVerifiedUtc,
               @lockout = lockoutEndUtc
          FROM auth.appUser WITH (UPDLOCK, HOLDLOCK)
         WHERE appUserId = @appUserId AND isDeleted = 0;

        EXEC auth.uspGetEmailCodePolicy @appUserId = @appUserId, @emailCodesAllowed = @allowed OUTPUT;

        IF @root IS NULL OR @active = 0 OR @mfa = 0 OR @allowed = 0 OR NULLIF (LTRIM (RTRIM (@email)), N'') IS NULL
           OR (@lockout IS NOT NULL AND @lockout > @now) OR (@purpose = 'signIn' AND @verified IS NULL)
        BEGIN
            SET @outcome = 'notAvailable';
        END
        ELSE
        BEGIN
            -- The wait applies to a code still on its way or waiting to be typed; one already used or never sent holds nothing back.
            SELECT @last = MAX (createdDateUtc)
              FROM auth.mfaEmailChallenge
             WHERE appUserId = @appUserId AND deliveryStatus <> 'failed' AND consumedUtc IS NULL AND isDeleted = 0;

            SELECT @oldest = MIN (createdDateUtc)
              FROM auth.mfaEmailChallenge
             WHERE appUserId = @appUserId AND createdDateUtc > DATEADD (MINUTE, -@windowMinutes, @now) AND isDeleted = 0
            HAVING COUNT (*) >= @maxPerUser;

            IF @last IS NOT NULL AND DATEADD (SECOND, @cooldownSeconds, @last) > @now
            BEGIN
                SELECT @outcome = 'cooldown', @retryAfter = DATEDIFF (SECOND, @now, DATEADD (SECOND, @cooldownSeconds, @last)) + 1;
            END
            ELSE IF @oldest IS NOT NULL
            BEGIN
                SELECT @outcome = 'limit', @retryAfter = DATEDIFF (SECOND, @now, DATEADD (MINUTE, @windowMinutes, @oldest)) + 1;
            END
            ELSE IF @clientAddress IS NOT NULL
                AND (SELECT COUNT (*)
                       FROM auth.mfaEmailChallenge
                      WHERE clientAddress = @clientAddress AND createdDateUtc > DATEADD (MINUTE, -@windowMinutes, @now)
                        AND isDeleted = 0) >= @maxPerAddress
            BEGIN
                SELECT @outcome = 'limit', @retryAfter = @windowMinutes * 60;
            END;
        END;

        IF @outcome = 'created'
        BEGIN
            UPDATE auth.mfaEmailChallenge
               SET supersededUtc = SYSUTCDATETIME ()
             WHERE appUserId = @appUserId AND consumedUtc IS NULL AND supersededUtc IS NULL AND isDeleted = 0;

            INSERT auth.mfaEmailChallenge (mfaEmailChallengeId, appUserId, purpose, codeVerifier, expiresUtc, maxAttempts,
                                           supportReference, correlationId, clientAddress)
            VALUES (@mfaEmailChallengeId, @appUserId, @purpose, @codeVerifier, DATEADD (SECOND, @lifetimeSeconds, @now),
                    @maxAttempts, @supportReference, @correlationId, @clientAddress);
        END
        ELSE IF @outcome IN ('cooldown', 'limit')
        BEGIN
            INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId, clientAddress)
            VALUES (@appUserId, @root, 'mfaCodeRefused', 'appUser', @appUserId, @clientAddress);
        END;

        SELECT @outcome                                                                AS outcome
             , @retryAfter                                                             AS retryAfterSeconds
             , CASE WHEN @outcome = 'created' THEN @email END                          AS email
             , CASE WHEN @outcome = 'created' THEN DATEADD (SECOND, @lifetimeSeconds, @now) END AS expiresUtc;

        SET @Comments = CONCAT (N'Email code ', @outcome, N'.');

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
    , @ObjectName  = N'uspCreateEmailChallenge'
    , @Description = N'Issues an email one-time code when the account may have one: active, not locked, enrolled in the authenticator app, with an email address, allowed by the jurisdiction and (for sign-in) with email codes turned on. Enforces the resend cooldown and the per-account and per-address limits, supersedes the user''s open code and stores the new code''s verifier. Returns the outcome, the seconds to wait when refused, and the address to send to.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON auth.uspCreateEmailChallenge TO meritWebRole;
END;
GO
