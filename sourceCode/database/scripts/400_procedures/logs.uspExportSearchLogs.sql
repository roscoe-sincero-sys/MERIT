-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   logs.uspExportSearchLogs
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Cursor-paginated canonical export of logs.searchLog metadata (never raw searchText) for a UTC range (#328/#329).

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission security.review or audit.view (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec logs.uspExportSearchLogs ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		328
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE logs.uspExportSearchLogs
      @fromUtc          DATETIME2 (3)
      , @toUtc            DATETIME2 (3)
      , @afterSearchedUtc DATETIME2 (3) = NULL
      , @afterSearchLogId UNIQUEIDENTIFIER = NULL
      , @batchSize        INT = 2000
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[logs].[uspExportSearchLogs]')
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

        IF auth.udfHasPermission (N'security.review') = 0 AND auth.udfHasPermission (N'audit.view') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (security.review or audit.view).', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @limit INT = CASE WHEN @batchSize BETWEEN 1 AND 5000 THEN @batchSize ELSE 2000 END;
        DECLARE @all BIT = auth.udfHasPermission (N'security.investigate');
        DECLARE @accounts TABLE (appUserId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @accounts SELECT appUserId FROM auth.tvfReviewAccounts ('security.review');
        DECLARE @allAudit BIT = auth.udfHasPermission (N'security.investigate');
        DECLARE @auditTenants TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @auditTenants SELECT tenantId FROM auth.tvfReviewTenants ('audit.view');

        SELECT TOP (@limit)
               s.searchLogId, s.searchedUtc, s.appUserId, u.userName, u.displayName,
               s.rootTenantId, s.actingTenantId, s.sessionReference, s.clientAddress,
               s.resultCount, s.existsElsewhere, s.searchSource
          FROM logs.searchLog AS s
          LEFT JOIN auth.appUser AS u ON u.appUserId = s.appUserId
         WHERE s.isDeleted = 0 AND s.searchedUtc >= @fromUtc AND s.searchedUtc <= @toUtc
           AND (@afterSearchLogId IS NULL
                OR s.searchedUtc > @afterSearchedUtc
                OR (s.searchedUtc = @afterSearchedUtc AND s.searchLogId > @afterSearchLogId))
           AND (@all = 1 OR @allAudit = 1
                OR s.appUserId IN (SELECT appUserId FROM @accounts)
                OR s.actingTenantId IN (SELECT tenantId FROM @auditTenants)
                OR s.rootTenantId IN (SELECT tenantId FROM @auditTenants))
         ORDER BY s.searchedUtc ASC, s.searchLogId ASC;

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
    , @ObjectName  = N'uspExportSearchLogs'
    , @Description = N'Cursor-paginated canonical export of logs.searchLog metadata (never raw searchText) for a UTC range (#328/#329).';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspExportSearchLogs TO meritWebRole;
END;
GO
