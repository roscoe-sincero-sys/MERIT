-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspReleaseStagingRows
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Releases held rows: they rerun validation and matching in a background job per batch.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission result.release_hold (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspReleaseStagingRows ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T105
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspReleaseStagingRows
      @stagingRowIdsJson NVARCHAR (MAX)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspReleaseStagingRows]')
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
    SET @KeyParameters = N'release';

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        IF auth.udfHasPermission (N'result.release_hold') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (result.release_hold).', 1;
        END;

        IF ISJSON (@stagingRowIdsJson) = 0
        BEGIN
            ;THROW 50000, N'@stagingRowIdsJson must be a JSON array of ids.', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @ids TABLE (stagingRowId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @ids SELECT DISTINCT TRY_CAST ([value] AS UNIQUEIDENTIFIER) FROM OPENJSON (@stagingRowIdsJson)
                     WHERE TRY_CAST ([value] AS UNIQUEIDENTIFIER) IS NOT NULL;

        DECLARE @released TABLE (stagingRowId UNIQUEIDENTIFIER PRIMARY KEY);
        DECLARE @batches TABLE (intakeBatchId UNIQUEIDENTIFIER PRIMARY KEY);

        UPDATE s SET rowStatus = 'released'
        OUTPUT inserted.stagingRowId INTO @released
          FROM dbo.stagingRow AS s
          JOIN @ids AS i ON i.stagingRowId = s.stagingRowId
         WHERE s.rowStatus = 'held' AND s.isDeleted = 0
           AND (NOT EXISTS (SELECT 1 FROM dbo.stagingHoldReason AS h
                             WHERE h.stagingRowId = s.stagingRowId AND h.reasonCode = 'uncertainMatch'
                               AND h.isResolved = 0 AND h.isDeleted = 0)
                OR TRY_CAST (JSON_VALUE (s.correctedJson, '$.matchPersonId') AS UNIQUEIDENTIFIER) IS NOT NULL
                OR JSON_VALUE (s.correctedJson, '$.matchNewPerson') = N'true');

        INSERT @batches (intakeBatchId)
        SELECT DISTINCT s.intakeBatchId FROM dbo.stagingRow AS s JOIN @released AS i ON i.stagingRowId = s.stagingRowId
         WHERE s.rowStatus = 'released';

        DECLARE @jobProfile UNIQUEIDENTIFIER, @jobViewAs UNIQUEIDENTIFIER, @jobActing UNIQUEIDENTIFIER;
        SELECT @jobProfile = userProfileId, @jobViewAs = viewAsUserId, @jobActing = actingTenantId
          FROM auth.sessionScope
         WHERE sessionScopeId = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER) AND isDeleted = 0;

        INSERT dbo.jobQueue (jobId, rootTenantId, owningTenantId, jobType, payloadJson, payloadVersion, availableUtc,
                             requestedByUserId, requestedProfileId, requestedViewAsUserId, requestedActingTenantId)
        SELECT CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), b.rootTenantId, b.owningTenantId, 'intakeBatch',
               CONCAT (N'{"intakeBatchId":"', b.intakeBatchId, N'","releasedOnly":true}'), 1, SYSUTCDATETIME (),
               auth.udfSessionUserId (), @jobProfile, @jobViewAs, @jobActing
          FROM dbo.intakeBatch AS b JOIN @batches AS x ON x.intakeBatchId = b.intakeBatchId;

        SELECT COUNT (*) AS releasedCount FROM @released;

        SET @Comments = N'Rows released.';

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
    , @ObjectName  = N'uspReleaseStagingRows'
    , @Description = N'Releases held rows: they rerun validation and matching in a background job per batch.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspReleaseStagingRows TO meritWebRole;
END;
GO
