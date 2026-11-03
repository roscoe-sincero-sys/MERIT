-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   auth.uspSaveMyMfaMethods
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

The signed-in user turns email codes off or chooses which second factor sign-in offers first. Turning email codes on is not done here: only a confirmed enroll code does that (auth.uspVerifyEmailChallenge). The authenticator app cannot be removed.

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

exec auth.uspSaveMyMfaMethods ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T190
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE auth.uspSaveMyMfaMethods
      @emailCodesOn    BIT
      , @preferredMethod VARCHAR (10)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[auth].[uspSaveMyMfaMethods]')
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
    SET @KeyParameters = CONCAT (N'emailCodesOn=', @emailCodesOn, N', preferredMethod=', @preferredMethod);

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        IF @preferredMethod NOT IN ('totp', 'email')
        BEGIN
            ;THROW 50000, N'@preferredMethod must be totp or email.', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @appUserId UNIQUEIDENTIFIER = auth.udfSessionUserId ();
        DECLARE @wasOn BIT, @wasPreferred VARCHAR (10), @root UNIQUEIDENTIFIER;

        IF @appUserId IS NULL
        BEGIN
            ;THROW 51403, N'Sign in to change your sign-in methods.', 1;
        END;

        SELECT @wasOn = CASE WHEN mfaEmailVerifiedUtc IS NULL THEN 0 ELSE 1 END, @wasPreferred = mfaPreferredMethod,
               @root = rootTenantId
          FROM auth.appUser WITH (UPDLOCK, HOLDLOCK)
         WHERE appUserId = @appUserId AND isDeleted = 0;

        IF @emailCodesOn = 1 AND @wasOn = 0
        BEGIN
            ;THROW 51422, N'Confirm a code sent to your email before turning on email codes.', 1;
        END;

        IF @emailCodesOn = 0
        BEGIN
            SET @preferredMethod = 'totp';
        END;

        UPDATE auth.appUser
           SET mfaEmailVerifiedUtc = CASE WHEN @emailCodesOn = 0 THEN NULL ELSE mfaEmailVerifiedUtc END
             , mfaPreferredMethod  = @preferredMethod
         WHERE appUserId = @appUserId AND isDeleted = 0;

        IF @emailCodesOn = 0 AND @wasOn = 1
        BEGIN
            UPDATE auth.mfaEmailChallenge
               SET supersededUtc = SYSUTCDATETIME ()
             WHERE appUserId = @appUserId AND consumedUtc IS NULL AND supersededUtc IS NULL AND isDeleted = 0;

            INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId)
            VALUES (@appUserId, @root, 'mfaEmailDisabled', 'appUser', @appUserId);
        END
        ELSE IF @preferredMethod <> @wasPreferred
        BEGIN
            INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId)
            VALUES (@appUserId, @root, 'mfaMethodChanged', 'appUser', @appUserId);
        END;

        SET @Comments = N'Sign-in methods saved.';

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
    , @ObjectName  = N'uspSaveMyMfaMethods'
    , @Description = N'The signed-in user turns email codes off or chooses which second factor sign-in offers first. Turning email codes on is not done here: only a confirmed enroll code does that (auth.uspVerifyEmailChallenge). The authenticator app cannot be removed.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON auth.uspSaveMyMfaMethods TO meritWebRole;
END;
GO
