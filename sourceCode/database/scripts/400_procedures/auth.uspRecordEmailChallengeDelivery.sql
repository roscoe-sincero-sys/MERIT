-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   auth.uspRecordEmailChallengeDelivery
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Records how sending an email code ended. Submitted means the mail server accepted the message, not that it reached the mailbox. A failed send closes the challenge, so a message that arrives late never works after the person was told it could not be sent, and writes the failure category and SMTP reply code (never the server's text) for support.

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

exec auth.uspRecordEmailChallengeDelivery ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T190
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE auth.uspRecordEmailChallengeDelivery
      @mfaEmailChallengeId UNIQUEIDENTIFIER
      , @submitted           BIT
      , @deliveryAttempts    INT
      , @failureKind         VARCHAR (30) = NULL
      , @smtpReplyCode       INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[auth].[uspRecordEmailChallengeDelivery]')
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
    SET @KeyParameters = CONCAT (N'mfaEmailChallengeId=', @mfaEmailChallengeId, N', submitted=', @submitted, N', attempts=', @deliveryAttempts, N', failureKind=', @failureKind, N', smtpReplyCode=', @smtpReplyCode);

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
        DECLARE @user UNIQUEIDENTIFIER, @root UNIQUEIDENTIFIER, @address VARCHAR (45);

        UPDATE auth.mfaEmailChallenge
           SET deliveryStatus     = CASE WHEN @submitted = 1 THEN 'submitted' ELSE 'failed' END
             , deliveryUpdatedUtc = @now
             , deliveryAttempts   = @deliveryAttempts
             , failureKind        = CASE WHEN @submitted = 1 THEN NULL ELSE @failureKind END
             , smtpReplyCode      = @smtpReplyCode
             , supersededUtc      = CASE WHEN @submitted = 0 THEN COALESCE (supersededUtc, @now) ELSE supersededUtc END
         WHERE mfaEmailChallengeId = @mfaEmailChallengeId AND deliveryStatus = 'pending' AND isDeleted = 0;

        IF @@ROWCOUNT > 0
        BEGIN
            SELECT @user = c.appUserId, @root = u.rootTenantId, @address = c.clientAddress
              FROM auth.mfaEmailChallenge AS c
              JOIN auth.appUser           AS u ON u.appUserId = c.appUserId
             WHERE c.mfaEmailChallengeId = @mfaEmailChallengeId;

            INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId, clientAddress)
            VALUES (@user, @root, CASE WHEN @submitted = 1 THEN 'mfaCodeSent' ELSE 'mfaCodeNotSent' END, 'appUser', @user,
                    @address);
        END;

        SET @Comments = CASE WHEN @submitted = 1 THEN N'Email code submitted.' ELSE N'Email code not sent.' END;

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
    , @ObjectName  = N'uspRecordEmailChallengeDelivery'
    , @Description = N'Records how sending an email code ended. Submitted means the mail server accepted the message, not that it reached the mailbox. A failed send closes the challenge, so a message that arrives late never works after the person was told it could not be sent, and writes the failure category and SMTP reply code (never the server''s text) for support.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON auth.uspRecordEmailChallengeDelivery TO meritWebRole;
END;
GO
