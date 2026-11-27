-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   logs.uspPurgeSignInEvents
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Purges sign-in events older than @retentionDays (at least 30), in independently committed 5,000-row batches (#336, #340): the personal/correlation data (address, forwarded chain, user agent, session reference, trace ID, and both copies of an attempted name) is removed and the row is marked deleted, leaving only an anonymous count. This database never hard-deletes. The worker runs it daily (SignInLog:RetentionDays).

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

exec logs.uspPurgeSignInEvents ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T213
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE logs.uspPurgeSignInEvents
      @retentionDays INT = 365
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[logs].[uspPurgeSignInEvents]')
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
    SET @KeyParameters = CONCAT (N'retentionDays=', @retentionDays);

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        IF @retentionDays < 30
        BEGIN
            ;THROW 50000, N'@retentionDays must be at least 30.', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @cutoff DATETIME2 (3) = DATEADD (DAY, -@retentionDays, SYSUTCDATETIME ());
        DECLARE @deleted INT = 0, @batch INT = 1;

        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) logs.signInEvent
               SET clientAddress = NULL, forwardedFor = NULL, userAgent = NULL, attemptedNameHash = NULL,
                   attemptedNameProtected = NULL, sessionReference = NULL, traceId = NULL, isDeleted = 1
             WHERE eventUtc < @cutoff AND isDeleted = 0;
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @deleted += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;

        -- Idempotent batched cleanup for previously soft-deleted rows that still retain correlation IDs (#336, Codex #4).
        -- Split into two single-predicate loops so SQL Server seeks the filtered indexes (0 rows once backlog is cleared).
        SET @batch = 1;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) logs.signInEvent
               SET sessionReference = NULL, traceId = NULL
             WHERE eventUtc < @cutoff AND isDeleted = 1 AND sessionReference IS NOT NULL;
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @deleted += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;

        SET @batch = 1;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) logs.signInEvent
               SET traceId = NULL
             WHERE eventUtc < @cutoff AND isDeleted = 1 AND traceId IS NOT NULL;
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @deleted += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;

        SELECT @deleted AS purged;

        SET @Comments = CONCAT (@deleted, N' sign-in event(s) purged.');

        -- =========================================================================================
        -- ===== End of the procedure's own work. ===================================================
        -- =========================================================================================

        -- Completion. Deliberately after the COMMIT.
        SET @EndTimeUtc = SYSUTCDATETIME ();

        IF @ExecutionId IS NOT NULL
        BEGIN
            UPDATE logs.ExecutionLog
               SET EndDateUtc          = @EndTimeUtc
                 , ElapsedMilliseconds = CAST (LEAST (DATEDIFF_BIG (MILLISECOND, @StartTimeUtc, @EndTimeUtc)
                                                    , CAST (2147483647 AS BIGINT)) AS INT)
                 , Successful          = 1
                 , Comments            = LEFT (@Comments, 500)
                 , modifiedBy          = COALESCE (NULLIF (CAST (SESSION_CONTEXT (N'AppUser') AS NVARCHAR (255)), N''), ORIGINAL_LOGIN ())
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
      @SchemaName  = N'logs'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspPurgeSignInEvents'
    , @Description = N'Purges sign-in events older than @retentionDays (at least 30), in independently committed 5,000-row batches (#336, #340): the personal/correlation data (address, forwarded chain, user agent, session reference, trace ID, and both copies of an attempted name) is removed and the row is marked deleted, leaving only an anonymous count. This database never hard-deletes. The worker runs it daily (SignInLog:RetentionDays).';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspPurgeSignInEvents TO meritWorkerRole;
END;
GO
