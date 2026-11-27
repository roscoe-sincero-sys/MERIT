-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   logs.uspExportScreenVisits
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Cursor-paginated canonical export of logs.screenVisit rows over [@fromUtc, @toUtc) within the caller's usage.view scope (#328).

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

exec logs.uspExportScreenVisits ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		328
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE logs.uspExportScreenVisits
      @fromUtc            DATETIME2 (3)
      , @toUtc              DATETIME2 (3)
      , @afterEventUtc      DATETIME2 (3) = NULL
      , @afterScreenVisitId UNIQUEIDENTIFIER = NULL
      , @batchSize          INT = 2000
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[logs].[uspExportScreenVisits]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'batchSize=', @batchSize);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'usage.view') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (usage.view).', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @limit INT = CASE WHEN @batchSize BETWEEN 1 AND 5000 THEN @batchSize ELSE 2000 END;
        DECLARE @review TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @review SELECT tenantId FROM auth.tvfReviewTenants ('usage.view');

        SELECT TOP (@limit)
               v.screenVisitId, v.eventUtc, v.rootTenantId, v.actingTenantId,
               v.appUserId, u.userName, u.displayName,
               v.sessionReference, v.profileId, v.viewAsUserId,
               v.roleCodes, v.screenCode, v.navigationType, v.tabId, v.previousVisitId, v.durationMs
          FROM logs.screenVisit AS v
          LEFT JOIN auth.appUser AS u ON u.appUserId = v.appUserId
         WHERE v.isDeleted = 0 AND v.eventUtc >= @fromUtc AND v.eventUtc < @toUtc
           AND (@afterScreenVisitId IS NULL
                OR v.eventUtc > @afterEventUtc
                OR (v.eventUtc = @afterEventUtc AND v.screenVisitId > @afterScreenVisitId))
           AND EXISTS (SELECT 1 FROM @review AS r WHERE r.tenantId = v.actingTenantId)
         ORDER BY v.eventUtc ASC, v.screenVisitId ASC;

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
    , @ObjectName  = N'uspExportScreenVisits'
    , @Description = N'Cursor-paginated canonical export of logs.screenVisit rows over [@fromUtc, @toUtc) within the caller''s usage.view scope (#328).';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspExportScreenVisits TO meritWebRole;
END;
GO
