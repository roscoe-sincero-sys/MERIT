-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   logs.uspGetScreenSession
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

One non-secret session reference, ordered and paged by 100. Reads only in-scope visits and lifecycle events for that same actor/reference. No addresses, agents, scope keys or unscoped visits are returned.

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

exec logs.uspGetScreenSession ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		227
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE logs.uspGetScreenSession
      @sessionReference UNIQUEIDENTIFIER
      , @days INT = 30
      , @pageNumber INT = 1
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[logs].[uspGetScreenSession]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = N'scoped screen sequence';

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
        END;
        IF @pageNumber NOT BETWEEN 1 AND 100000
        BEGIN
            ;THROW 51422, N'Choose a valid page.', 1;
        END;
        DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();
        DECLARE @scope UNIQUEIDENTIFIER = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER);
        DECLARE @from DATETIME2 (3) = DATEADD (DAY, -@days, SYSUTCDATETIME ());
        SELECT v.* INTO #visits
          FROM logs.screenVisit AS v
         WHERE v.isDeleted = 0 AND v.rootTenantId = @root AND v.eventUtc >= @from
           AND (EXISTS (SELECT 1 FROM auth.sessionScopeTenant AS st WHERE st.sessionScopeId = @scope AND st.tenantId = v.actingTenantId)
             OR EXISTS (SELECT 1 FROM auth.sessionScopeAdminTenant AS st WHERE st.sessionScopeId = @scope AND st.tenantId = v.actingTenantId));
        CREATE UNIQUE CLUSTERED INDEX IX_visits_id ON #visits (screenVisitId);
        SELECT screenVisitId, eventUtc, screenCode, navigationType, tabId,
               CASE WHEN EXISTS (SELECT 1 FROM #visits AS p WHERE p.screenVisitId = v.previousVisitId) THEN previousVisitId END AS previousVisitId, roleCodes,
               actingTenantId, profileId, viewAsUserId, durationMs, COUNT (*) OVER () AS totalCount
          FROM #visits AS v WHERE sessionReference = @sessionReference
         ORDER BY eventUtc, screenVisitId OFFSET (@pageNumber - 1) * 100 ROWS FETCH NEXT 100 ROWS ONLY;

        SELECT e.eventUtc, e.eventType, e.reason FROM logs.signInEvent AS e
         WHERE e.isDeleted = 0 AND e.sessionReference = @sessionReference
           AND e.eventType IN ('sessionStarted', 'signedOut', 'sessionExpired', 'sessionRevoked')
           AND EXISTS (SELECT 1 FROM #visits AS v WHERE v.sessionReference = @sessionReference AND v.appUserId = e.appUserId)
         ORDER BY e.eventUtc, e.signInEventId;

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
    , @ObjectName  = N'uspGetScreenSession'
    , @Description = N'One non-secret session reference, ordered and paged by 100. Reads only in-scope visits and lifecycle events for that same actor/reference. No addresses, agents, scope keys or unscoped visits are returned.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspGetScreenSession TO meritWebRole;
END;
GO
