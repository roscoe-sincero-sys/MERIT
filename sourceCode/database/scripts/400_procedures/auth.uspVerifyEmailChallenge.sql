-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   auth.uspVerifyEmailChallenge
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Checks a typed email code against the user's open challenge for the purpose, in one UPDATE so two submissions of the same code cannot both succeed: a match consumes the challenge, a miss counts an attempt. An enroll match also turns email codes on for the account. Returns verified, wrong (with the attempts left), locked (no attempts left), expired, used, superseded (a newer code was sent, or this one could not be sent) or none.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

The verifier is compared inside SQL Server. That comparison is not constant-time, but it compares HMAC outputs: without the web application's key, timing reveals nothing about the code.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec auth.uspVerifyEmailChallenge ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T190
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE auth.uspVerifyEmailChallenge
      @appUserId     UNIQUEIDENTIFIER
      , @purpose       VARCHAR (10)
      , @codeVerifier  VARBINARY (32)
      , @clientAddress VARCHAR (45) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[auth].[uspVerifyEmailChallenge]')
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
    SET @KeyParameters = CONCAT (N'appUserId=', @appUserId, N', purpose=', @purpose);

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

        DECLARE @now       DATETIME2 (3) = SYSUTCDATETIME ();
        DECLARE @outcome   VARCHAR (20);
        DECLARE @remaining INT = 0;
        DECLARE @root      UNIQUEIDENTIFIER = (SELECT rootTenantId FROM auth.appUser WHERE appUserId = @appUserId);
        DECLARE @result    TABLE (matched BIT NOT NULL, failedAttempts INT NOT NULL, maxAttempts INT NOT NULL);

        -- An older code (replaced, or already used) is recognised as such and costs no attempt against the open one.
        IF NOT EXISTS (SELECT 1 FROM auth.mfaEmailChallenge
                        WHERE appUserId = @appUserId AND purpose = @purpose AND codeVerifier = @codeVerifier AND consumedUtc IS NULL
                          AND supersededUtc IS NULL AND isDeleted = 0)
        BEGIN
            SELECT TOP (1) @outcome = CASE WHEN consumedUtc IS NOT NULL THEN 'used' ELSE 'superseded' END
              FROM auth.mfaEmailChallenge
             WHERE appUserId = @appUserId AND purpose = @purpose AND codeVerifier = @codeVerifier AND isDeleted = 0
               AND (consumedUtc IS NOT NULL OR supersededUtc IS NOT NULL)
             ORDER BY createdDateUtc DESC;
        END;

        IF @outcome IS NULL
        UPDATE auth.mfaEmailChallenge
           SET consumedUtc    = CASE WHEN codeVerifier = @codeVerifier THEN @now END
             , failedAttempts = failedAttempts + CASE WHEN codeVerifier = @codeVerifier THEN 0 ELSE 1 END
        OUTPUT CASE WHEN inserted.consumedUtc IS NULL THEN 0 ELSE 1 END, inserted.failedAttempts, inserted.maxAttempts
          INTO @result (matched, failedAttempts, maxAttempts)
         WHERE appUserId = @appUserId AND purpose = @purpose AND consumedUtc IS NULL AND supersededUtc IS NULL
           AND deliveryStatus <> 'failed' AND expiresUtc > @now AND failedAttempts < maxAttempts AND isDeleted = 0;

        IF @outcome IS NOT NULL
        BEGIN
            SET @remaining = 0;
        END
        ELSE IF EXISTS (SELECT 1 FROM @result WHERE matched = 1)
        BEGIN
            SET @outcome = 'verified';
        END
        ELSE IF EXISTS (SELECT 1 FROM @result)
        BEGIN
            SELECT @remaining = maxAttempts - failedAttempts FROM @result;
            SET @outcome = CASE WHEN @remaining > 0 THEN 'wrong' ELSE 'locked' END;
        END
        ELSE
        BEGIN
            SELECT TOP (1) @outcome = CASE WHEN consumedUtc IS NOT NULL THEN 'used'
                                           WHEN supersededUtc IS NOT NULL THEN 'superseded'
                                           WHEN failedAttempts >= maxAttempts THEN 'locked'
                                           WHEN expiresUtc <= @now THEN 'expired'
                                           ELSE 'none' END
              FROM auth.mfaEmailChallenge
             WHERE appUserId = @appUserId AND purpose = @purpose AND isDeleted = 0
             ORDER BY createdDateUtc DESC;

            SET @outcome = COALESCE (@outcome, 'none');
        END;

        IF @outcome = 'verified' AND @purpose = 'enroll'
        BEGIN
            UPDATE auth.appUser SET mfaEmailVerifiedUtc = @now WHERE appUserId = @appUserId AND isDeleted = 0;

            INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId, clientAddress)
            VALUES (@appUserId, @root, 'mfaEmailEnabled', 'appUser', @appUserId, @clientAddress);
        END
        ELSE IF @outcome = 'locked' AND EXISTS (SELECT 1 FROM @result)
        BEGIN
            INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId, clientAddress)
            VALUES (@appUserId, @root, 'mfaCodeLocked', 'appUser', @appUserId, @clientAddress);
        END;

        SELECT @outcome AS outcome, @remaining AS remainingAttempts;

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
    , @ObjectName  = N'uspVerifyEmailChallenge'
    , @Description = N'Checks a typed email code against the user''s open challenge for the purpose, in one UPDATE so two submissions of the same code cannot both succeed: a match consumes the challenge, a miss counts an attempt. An enroll match also turns email codes on for the account. Returns verified, wrong (with the attempts left), locked (no attempts left), expired, used, superseded (a newer code was sent, or this one could not be sent) or none.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON auth.uspVerifyEmailChallenge TO meritWebRole;
END;
GO
