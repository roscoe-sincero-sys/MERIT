-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspGetQueueCounts
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Open task counts for every work queue the caller can see, with urgent and overdue counts, plus held intake rows. Feeds the home dashboard tiles, which poll it.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission task.view (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspGetQueueCounts ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T074
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspGetQueueCounts

AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspGetQueueCounts]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = NULL;

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'task.view') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (task.view).', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @today DATE = auth.udfSessionToday ();
        DECLARE @me UNIQUEIDENTIFIER = auth.udfSessionUserId ();

        SELECT q.queueCode, q.queueName,
               COUNT (w.workTaskId) AS openCount,
               SUM (CASE WHEN w.isUrgent = 1 THEN 1 ELSE 0 END) AS urgentCount,
               SUM (CASE WHEN w.dueDate < @today THEN 1 ELSE 0 END) AS overdueCount,
               SUM (CASE WHEN w.assignedToUserId = @me THEN 1 ELSE 0 END) AS mineCount
          FROM (VALUES ('unmatchedResult', N'Unmatched results'), ('unknownCode', N'Unknown LOINC codes or units'),
                ('pausedBatch', N'Paused intake batches'), ('addressReview', N'Address review'),
                ('unverifiedPrimary', N'Unverified primary residences'), ('detectionLimit', N'Detection limit above level'),
                ('pregnancyPastEnd', N'Pregnancies past expected end'), ('overdueStep', N'Overdue case steps'),
                ('crossLink', N'Cross-link tasks'), ('cluster', N'Cluster alerts'), ('extractMissing', N'Missing extract fields'),
                ('confirmatoryTest', N'Confirmatory tests due'), ('speciationRecommended', N'Speciation recommended'),
                ('populationReview', N'Population unknown (no DOB)'), ('deceasedReview', N'Deceased: close open cases'),
                ('siteReview', N'Other sites to review'), ('investigationRequest', N'Investigation requests'),
                ('prenatalNotification', N'Prenatal provider notifications'), ('newbornTesting', N'Newborn testing'),
                ('letterDue', N'Letters to send'), ('actionLevel', N'Action level follow-up'),
                ('matchReview', N'Person match review (supervisor)')) AS q (queueCode, queueName)
          LEFT JOIN dbo.workTask AS w ON w.queueCode = q.queueCode AND w.taskStatus = 'open' AND w.isDeleted = 0
         GROUP BY q.queueCode, q.queueName
        UNION ALL
        SELECT 'heldRecords', N'Held intake records', COUNT (*), SUM (CASE WHEN isUrgent = 1 THEN 1 ELSE 0 END), 0,
               SUM (CASE WHEN assignedToUserId = @me THEN 1 ELSE 0 END)
          FROM dbo.stagingRow WHERE rowStatus = 'held' AND isDeleted = 0
         ORDER BY urgentCount DESC, openCount DESC;

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
      @SchemaName  = N'dbo'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspGetQueueCounts'
    , @Description = N'Open task counts for every work queue the caller can see, with urgent and overdue counts, plus held intake rows. Feeds the home dashboard tiles, which poll it.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspGetQueueCounts TO meritWebRole;
END;
GO
