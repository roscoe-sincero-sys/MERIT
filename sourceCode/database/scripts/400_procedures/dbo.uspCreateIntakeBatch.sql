-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspCreateIntakeBatch
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Stores a received file unchanged and queues the intake job that parses, validates, matches and loads it. Returns at once; the upload screen polls the batch.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission intake.upload (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspCreateIntakeBatch ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T083
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspCreateIntakeBatch
      @intakeBatchId   UNIQUEIDENTIFIER
      , @intakeProfileId UNIQUEIDENTIFIER
      , @fileName        NVARCHAR (260)
      , @rawContent      NVARCHAR (MAX)
      , @jobId           UNIQUEIDENTIFIER
      , @contentSha256   CHAR (64) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspCreateIntakeBatch]')
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
    SET @KeyParameters = CONCAT (N'intakeBatchId=', @intakeBatchId, N', intakeProfileId=', @intakeProfileId, N', jobId=', @jobId);

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        IF auth.udfHasPermission (N'intake.upload') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (intake.upload).', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();
        DECLARE @acting UNIQUEIDENTIFIER = auth.udfSessionActingTenantId ();
        DECLARE @user UNIQUEIDENTIFIER = auth.udfSessionUserId ();
        DECLARE @sender UNIQUEIDENTIFIER = (SELECT senderOrganizationId FROM dbo.intakeProfile WHERE intakeProfileId = @intakeProfileId
                                              AND isDeleted = 0);

        IF @sender IS NULL
        BEGIN
            ;THROW 51404, N'The intake profile was not found.', 1;
        END;

        INSERT dbo.intakeBatch (intakeBatchId, rootTenantId, owningTenantId, intakeProfileId, senderOrganizationId, sourceChannel,
                                fileName, rawContent, contentSha256, receivedUtc, batchStatus, submittedByUserId)
        VALUES (@intakeBatchId, @root, @acting, @intakeProfileId, @sender, 'upload', @fileName, @rawContent, @contentSha256,
                SYSUTCDATETIME (), 'received', @user);

        DECLARE @jobProfile UNIQUEIDENTIFIER, @jobViewAs UNIQUEIDENTIFIER, @jobActing UNIQUEIDENTIFIER;
        SELECT @jobProfile = userProfileId, @jobViewAs = viewAsUserId, @jobActing = actingTenantId
          FROM auth.sessionScope
         WHERE sessionScopeId = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER) AND isDeleted = 0;

        INSERT dbo.jobQueue (jobId, rootTenantId, owningTenantId, jobType, payloadJson, payloadVersion, availableUtc,
                             requestedByUserId, requestedProfileId, requestedViewAsUserId, requestedActingTenantId)
        VALUES (@jobId, @root, @acting, 'intakeBatch',
                CONCAT (N'{"intakeBatchId":"', @intakeBatchId, N'","releasedOnly":false}'), 1, SYSUTCDATETIME (), @user,
                @jobProfile, @jobViewAs, @jobActing);

        SET @Comments = N'Batch received and queued.';

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
      @SchemaName  = N'dbo'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspCreateIntakeBatch'
    , @Description = N'Stores a received file unchanged and queues the intake job that parses, validates, matches and loads it. Returns at once; the upload screen polls the batch.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspCreateIntakeBatch TO meritWebRole;
END;
GO
