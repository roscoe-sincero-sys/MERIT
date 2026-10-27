-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspRecordMatchDecision
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Records a reviewer's decision on a held row. same: the row links to the chosen person when it is released. different: the pair is remembered and never suggested again. newPerson: the row creates a new person when released. notSure: the row goes to a supervisor through the matchReview queue. The parsed values never change; the decision is stored beside them with the corrections.

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

exec dbo.uspRecordMatchDecision ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T108
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspRecordMatchDecision
      @stagingRowId UNIQUEIDENTIFIER
      , @decision     VARCHAR (12)
      , @personId     UNIQUEIDENTIFIER = NULL
      , @incomingKey  NVARCHAR (200) = NULL
      , @note         NVARCHAR (500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspRecordMatchDecision]')
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
    SET @KeyParameters = CONCAT (N'stagingRowId=', @stagingRowId, N', decision=', @decision, N', personId=', @personId);

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

        IF @decision IS NULL OR @decision NOT IN ('same', 'different', 'newPerson', 'notSure')
        BEGIN
            ;THROW 50000, N'@decision must be same, different, newPerson or notSure.', 1;
        END;
        IF @decision IN ('same', 'different') AND @personId IS NULL
        BEGIN
            ;THROW 51422, N'Choose the candidate person for a Same person or Different person decision.', 1;
        END;
        IF @decision = 'different' AND (@incomingKey IS NULL OR LEN (@incomingKey) = 0)
        BEGIN
            ;THROW 51422, N'A Different person decision needs the incoming record''s name and date of birth.', 1;
        END;
        IF NOT EXISTS (SELECT 1 FROM dbo.stagingRow WHERE stagingRowId = @stagingRowId AND rowStatus = 'held' AND isDeleted = 0)
        BEGIN
            ;THROW 51404, N'The held row was not found, is no longer held, or you do not have access to it.', 1;
        END;
        IF @personId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.person WHERE personId = @personId AND isDeleted = 0
                                                                             AND mergedIntoPersonId IS NULL)
        BEGIN
            ;THROW 51404, N'The candidate person was not found, or you do not have access to them.', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();
        DECLARE @me UNIQUEIDENTIFIER = auth.udfSessionUserId ();
        DECLARE @owner UNIQUEIDENTIFIER = (SELECT owningTenantId FROM dbo.stagingRow WHERE stagingRowId = @stagingRowId);
        DECLARE @corrected NVARCHAR (MAX) = (SELECT COALESCE (correctedJson, N'{}') FROM dbo.stagingRow
                                              WHERE stagingRowId = @stagingRowId);

        IF @decision = 'same'
        BEGIN
            SET @corrected = JSON_MODIFY (JSON_MODIFY (@corrected, '$.matchNewPerson', NULL), '$.matchPersonId',
                                          CONVERT (NVARCHAR (36), @personId));
            UPDATE dbo.stagingRow SET correctedJson = @corrected
             WHERE stagingRowId = @stagingRowId AND rowStatus = 'held';
        END;

        IF @decision = 'newPerson'
        BEGIN
            SET @corrected = JSON_MODIFY (JSON_MODIFY (@corrected, '$.matchPersonId', NULL), '$.matchNewPerson', N'true');
            UPDATE dbo.stagingRow SET correctedJson = @corrected
             WHERE stagingRowId = @stagingRowId AND rowStatus = 'held';
        END;

        IF @decision = 'different'
           AND NOT EXISTS (SELECT 1 FROM dbo.matchExclusion WHERE incomingKey = @incomingKey AND personId = @personId
                                                              AND isDeleted = 0)
        BEGIN
            INSERT dbo.matchExclusion (matchExclusionId, rootTenantId, owningTenantId, personId, incomingKey, stagingRowId,
                                       decisionNote)
            VALUES (CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), @root, COALESCE (auth.udfSessionActingTenantId (), @owner), @personId, @incomingKey,
                    @stagingRowId, @note);
        END;

        IF @decision = 'notSure'
           AND NOT EXISTS (SELECT 1 FROM dbo.workTask WHERE stagingRowId = @stagingRowId AND queueCode = 'matchReview'
                                                        AND taskStatus = 'open' AND isDeleted = 0)
        BEGIN
            INSERT dbo.workTask (workTaskId, rootTenantId, owningTenantId, queueCode, title, detail, stagingRowId, isUrgent,
                                 dueDate)
            SELECT CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), @root, s.owningTenantId, 'matchReview', N'Person match: reviewer not sure',
                   COALESCE (@note, N'A reviewer could not decide whether this record is an existing person.'), s.stagingRowId,
                   s.isUrgent, DATEADD (DAY, CASE WHEN s.isUrgent = 1 THEN 1 ELSE 5 END, auth.udfSessionToday ())
              FROM dbo.stagingRow AS s WHERE s.stagingRowId = @stagingRowId;
        END;

        IF @decision IN ('same', 'newPerson')
        BEGIN
            UPDATE dbo.workTask
               SET taskStatus = 'done', completedUtc = SYSUTCDATETIME (), completedByUserId = @me,
                   resolutionNote = CASE @decision WHEN 'same' THEN N'Linked to an existing person.' ELSE N'Created as a new person.' END
             WHERE stagingRowId = @stagingRowId AND queueCode = 'matchReview' AND taskStatus = 'open' AND isDeleted = 0;
        END;

        SET @Comments = CONCAT (N'Decision ', @decision, N' recorded.');

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
    , @ObjectName  = N'uspRecordMatchDecision'
    , @Description = N'Records a reviewer''s decision on a held row. same: the row links to the chosen person when it is released. different: the pair is remembered and never suggested again. newPerson: the row creates a new person when released. notSure: the row goes to a supervisor through the matchReview queue. The parsed values never change; the decision is stored beside them with the corrections.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspRecordMatchDecision TO meritWebRole;
END;
GO
