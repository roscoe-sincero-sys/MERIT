-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspGetDashboard
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

The home dashboard: the caller's counts, urgent items (elevated held results first, because a result sitting in a queue delays follow-up) and their most recent open cases.

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

exec dbo.uspGetDashboard ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T074
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspGetDashboard

AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspGetDashboard]')
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

        SELECT (SELECT COUNT (*) FROM dbo.exposureCase WHERE assignedToUserId = @me AND caseStatus <> 'closed' AND isDeleted = 0) AS myOpenCases,
               (SELECT COUNT (*) FROM dbo.workTask WHERE assignedToUserId = @me AND taskStatus = 'open' AND isDeleted = 0) AS myOpenTasks,
               (SELECT COUNT (*) FROM dbo.workTask WHERE taskStatus = 'open' AND dueDate < @today AND isDeleted = 0) AS overdueTasks,
               (SELECT COUNT (*) FROM dbo.workTask WHERE taskStatus = 'open' AND isUrgent = 1 AND isDeleted = 0) AS urgentTasks,
               (SELECT COUNT (*) FROM dbo.stagingRow WHERE rowStatus = 'held' AND isUrgent = 1 AND isDeleted = 0) AS urgentHeld,
               (SELECT COUNT (*) FROM dbo.exposureCase WHERE caseStatus <> 'closed' AND isDeleted = 0) AS openCasesInScope;

        -- A held result is named by the patient and the result as reported, corrections applied, so each row says which record
        -- it is (issue #40); its batch and sender narrow the held records it links to.
        SELECT TOP (10) x.itemType, x.itemId, x.title, x.detail, x.dueDate, x.waitingSinceUtc, x.personId, x.exposureCaseId,
               x.intakeBatchId, x.senderName
          FROM (SELECT 'heldRecord' AS itemType, s.stagingRowId AS itemId,
                       COALESCE (NULLIF (CONCAT_WS (N': ', NULLIF (CONCAT_WS (N', ', f.lastName, f.firstName), N''),
                                                    NULLIF (CONCAT_WS (N' ', a.displayName, f.resultValue, f.resultUnit), N'')), N''),
                                 N'Elevated result held at intake') AS title,
                       (SELECT TOP (1) reasonMessage FROM dbo.stagingHoldReason AS h WHERE h.stagingRowId = s.stagingRowId
                          AND h.isDeleted = 0 AND h.isResolved = 0) AS detail, CAST (NULL AS DATE) AS dueDate,
                       s.heldSinceUtc AS waitingSinceUtc, CAST (NULL AS UNIQUEIDENTIFIER) AS personId,
                       CAST (NULL AS UNIQUEIDENTIFIER) AS exposureCaseId, s.intakeBatchId, o.organizationName AS senderName,
                       0 AS sortKey
                  FROM dbo.stagingRow AS s
                  JOIN dbo.intakeBatch AS b ON b.intakeBatchId = s.intakeBatchId
                  LEFT JOIN dbo.organization AS o ON o.organizationId = b.senderOrganizationId
                 CROSS APPLY (SELECT COALESCE (JSON_VALUE (s.correctedJson, '$.lastName'), JSON_VALUE (s.parsedJson, '$.lastName')) AS lastName,
                                     COALESCE (JSON_VALUE (s.correctedJson, '$.firstName'), JSON_VALUE (s.parsedJson, '$.firstName')) AS firstName,
                                     COALESCE (JSON_VALUE (s.correctedJson, '$.loinc'), JSON_VALUE (s.parsedJson, '$.loinc')) AS loinc,
                                     COALESCE (JSON_VALUE (s.correctedJson, '$.value'), JSON_VALUE (s.parsedJson, '$.value')) AS resultValue,
                                     COALESCE (JSON_VALUE (s.correctedJson, '$.unit'), JSON_VALUE (s.parsedJson, '$.unit')) AS resultUnit) AS f
                  LEFT JOIN config.loincCode AS l ON l.loincCode = f.loinc AND l.isDeleted = 0
                  LEFT JOIN config.analyteSpecimen AS sp ON sp.analyteSpecimenId = l.analyteSpecimenId
                  LEFT JOIN config.analyte AS a ON a.analyteId = sp.analyteId
                 WHERE s.rowStatus = 'held' AND s.isUrgent = 1 AND s.isDeleted = 0
                UNION ALL
                SELECT 'task', w.workTaskId, w.title, w.detail, w.dueDate, w.createdDateUtc, w.personId, w.exposureCaseId,
                       CAST (NULL AS UNIQUEIDENTIFIER), CAST (NULL AS NVARCHAR (120)), 1
                  FROM dbo.workTask AS w
                 WHERE w.taskStatus = 'open' AND w.isDeleted = 0 AND (w.isUrgent = 1 OR w.dueDate < @today)) AS x
         ORDER BY x.sortKey, x.waitingSinceUtc;

        SELECT TOP (10) c.exposureCaseId, c.caseNumber, c.personId, p.lastName, p.firstName, a.code AS analyteCode,
               c.classification, c.openedDate
          FROM dbo.exposureCase AS c
          JOIN dbo.person AS p ON p.personId = c.personId
          JOIN config.analyte AS a ON a.analyteId = c.analyteId
         WHERE c.assignedToUserId = @me AND c.caseStatus <> 'closed' AND c.isDeleted = 0
         ORDER BY c.openedDate DESC;

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
    , @ObjectName  = N'uspGetDashboard'
    , @Description = N'The home dashboard: the caller''s counts, urgent items (elevated held results first, because a result sitting in a queue delays follow-up) and their most recent open cases.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspGetDashboard TO meritWebRole;
END;
GO
