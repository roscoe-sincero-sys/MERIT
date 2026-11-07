-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   logs.uspGetScreenUsage
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Scoped usage aggregates, five paths into manual result entry, back-and-forth loops, strict observed search/person/entry/saved elapsed flow and the latest 100 sessions. Counts cover the full period; only the displayed session list is capped. Predecessors outside the reader's scope are not exposed.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission usage.view (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec logs.uspGetScreenUsage ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		227
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE logs.uspGetScreenUsage
      @days INT = 30
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[logs].[uspGetScreenUsage]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'days=', @days);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'usage.view') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (usage.view).', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        IF @days NOT BETWEEN 1 AND 90
        BEGIN
            ;THROW 51422, N'Choose a period from 1 to 90 days.', 1;
        END;DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();
        DECLARE @scope UNIQUEIDENTIFIER = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER);
        DECLARE @from DATETIME2 (3) = DATEADD (DAY, -@days, SYSUTCDATETIME ());
        SELECT v.* INTO #visits
          FROM logs.screenVisit AS v
         WHERE v.isDeleted = 0 AND v.rootTenantId = @root AND v.eventUtc >= @from
           AND (EXISTS (SELECT 1 FROM auth.sessionScopeTenant AS st WHERE st.sessionScopeId = @scope AND st.tenantId = v.actingTenantId)
             OR EXISTS (SELECT 1 FROM auth.sessionScopeAdminTenant AS st WHERE st.sessionScopeId = @scope AND st.tenantId = v.actingTenantId));
        CREATE UNIQUE CLUSTERED INDEX IX_visits_id ON #visits (screenVisitId);
        SELECT v.screenCode, CAST (NULL AS VARCHAR (1000)) AS roleCode, COUNT (*) AS visits,
               COUNT (DISTINCT v.sessionReference) AS sessions, COUNT (v.durationMs) AS timedVisits,
               AVG (CAST (v.durationMs AS FLOAT)) AS averageVisibleMs
          FROM #visits AS v GROUP BY v.screenCode
        UNION ALL
        SELECT v.screenCode, r.value AS roleCode, COUNT (*), COUNT (DISTINCT v.sessionReference), COUNT (v.durationMs), AVG (CAST (v.durationMs AS FLOAT))
          FROM #visits AS v CROSS APPLY STRING_SPLIT (v.roleCodes, ',') AS r GROUP BY v.screenCode, r.value;

        SELECT v.screenVisitId, v.screenCode, v.sessionReference, v.eventUtc,
               p.screenCode AS p1, p.eventUtc AS t1, p2.screenCode AS p2, p2.eventUtc AS t2, p3.screenCode AS p3, p3.eventUtc AS t3
          INTO #paths FROM #visits AS v
          LEFT JOIN #visits AS p ON p.screenVisitId = v.previousVisitId
          LEFT JOIN #visits AS p2 ON p2.screenVisitId = p.previousVisitId
          LEFT JOIN #visits AS p3 ON p3.screenVisitId = p2.previousVisitId;

        SELECT * FROM (
            SELECT TOP (5) 'intoResult' AS kind,
                   CONCAT_WS (' > ', p3, p2, p1, screenCode) AS pathCodes, COUNT (*) AS occurrences,
                   COUNT (DISTINCT sessionReference) AS sessions
              FROM #paths WHERE screenCode = 'result.new'
             GROUP BY CONCAT_WS (' > ', p3, p2, p1, screenCode) ORDER BY COUNT (*) DESC, pathCodes
        ) AS a
        UNION ALL
        SELECT * FROM (
            SELECT TOP (10) 'loop' AS kind, CONCAT_WS (' > ', p2, p1, screenCode) AS pathCodes, COUNT (*) AS occurrences,
                   COUNT (DISTINCT sessionReference) AS sessions
              FROM #paths WHERE p2 = screenCode AND p1 <> screenCode
             GROUP BY CONCAT_WS (' > ', p2, p1, screenCode) ORDER BY COUNT (*) DESC, pathCodes
        ) AS b;

        SELECT COUNT (*) AS completions, AVG (CAST (DATEDIFF (SECOND, t3, eventUtc) AS FLOAT)) AS averageElapsedSeconds
          FROM #paths WHERE screenCode = 'result.saved' AND p1 = 'result.new' AND p2 = 'person' AND p3 = 'people.search'
           AND DATEDIFF (SECOND, t3, eventUtc) BETWEEN 0 AND 86400;

        SELECT TOP (100) v.sessionReference, v.appUserId, MAX (u.displayName) AS displayName,
               MIN (v.eventUtc) AS firstUtc, MAX (v.eventUtc) AS lastUtc, COUNT (*) AS visits, COUNT (*) OVER () AS totalCount
          FROM #visits AS v LEFT JOIN auth.appUser AS u ON u.appUserId = v.appUserId
         GROUP BY v.sessionReference, v.appUserId ORDER BY MAX (v.eventUtc) DESC, v.sessionReference;

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
      @SchemaName  = N'logs'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspGetScreenUsage'
    , @Description = N'Scoped usage aggregates, five paths into manual result entry, back-and-forth loops, strict observed search/person/entry/saved elapsed flow and the latest 100 sessions. Counts cover the full period; only the displayed session list is capped. Predecessors outside the reader''s scope are not exposed.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspGetScreenUsage TO meritWebRole;
END;
GO
