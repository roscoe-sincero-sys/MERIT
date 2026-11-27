-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   logs.uspListSearchLogEvents
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Scoped logs.searchLog rows for investigation. Raw searchText is returned ONLY when @includeSearchText = 1 and the caller holds security.investigate (#329).

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

exec logs.uspListSearchLogEvents ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		329
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE logs.uspListSearchLogEvents
      @fromUtc           DATETIME2 (3) = NULL
      , @toUtc             DATETIME2 (3) = NULL
      , @appUserId         UNIQUEIDENTIFIER = NULL
      , @includeSearchText BIT = 0
      , @top               INT = 200
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[logs].[uspListSearchLogEvents]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'appUserId=', @appUserId, N', includeSearchText=', @includeSearchText, N', top=', @top);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'security.review') = 0 AND auth.udfHasPermission (N'audit.view') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (security.review or audit.view).', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @canSeeText BIT = CASE WHEN @includeSearchText = 1 AND auth.udfHasPermission (N'security.investigate') = 1 THEN 1 ELSE 0 END;
        DECLARE @limit INT = CASE WHEN @top BETWEEN 1 AND 1000 THEN @top ELSE 200 END;
        DECLARE @all BIT = auth.udfHasPermission (N'security.investigate');
        DECLARE @accounts TABLE (appUserId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @accounts SELECT appUserId FROM auth.tvfReviewAccounts ('security.review');
        DECLARE @auditScope TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @auditScope SELECT tenantId FROM auth.tvfReviewTenants ('audit.view');
        DECLARE @wholeRoots TABLE (rootTenantId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @wholeRoots SELECT t.tenantId FROM auth.tenant AS t JOIN @auditScope AS s ON s.tenantId = t.tenantId WHERE t.tenantId = t.rootTenantId;
        DECLARE @partRoots TABLE (rootTenantId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @partRoots SELECT DISTINCT t.rootTenantId FROM auth.tenant AS t JOIN @auditScope AS s ON s.tenantId = t.tenantId
         WHERE NOT EXISTS (SELECT 1 FROM @wholeRoots AS w WHERE w.rootTenantId = t.rootTenantId);
        DECLARE @auditUsers TABLE (appUserId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @auditUsers SELECT u.appUserId FROM auth.appUser AS u
         WHERE EXISTS (SELECT 1 FROM @partRoots)
           AND (EXISTS (SELECT 1 FROM @auditScope AS s WHERE s.tenantId = u.homeTenantId)
                OR EXISTS (SELECT 1 FROM auth.userAssignment AS ua JOIN @auditScope AS s ON s.tenantId = ua.tenantId WHERE ua.appUserId = u.appUserId AND ua.isDeleted = 0));
        DECLARE @secTenants TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @secTenants SELECT tenantId FROM auth.tvfReviewTenants ('security.review');

        SELECT TOP (@limit)
               s.searchLogId, s.searchedUtc, s.appUserId, u.userName, u.displayName,
               s.rootTenantId, s.actingTenantId, s.sessionReference, s.clientAddress,
               s.resultCount, s.existsElsewhere, s.searchSource,
               CASE WHEN @canSeeText = 1 THEN s.searchText ELSE NULL END AS searchText
          FROM logs.searchLog AS s
          LEFT JOIN auth.appUser AS u ON u.appUserId = s.appUserId
         WHERE s.isDeleted = 0
           AND (@fromUtc IS NULL OR s.searchedUtc >= @fromUtc)
           AND (@toUtc IS NULL OR s.searchedUtc < @toUtc)
           AND (@appUserId IS NULL OR s.appUserId = @appUserId)
           AND (@all = 1
                OR EXISTS (SELECT 1 FROM @wholeRoots AS w WHERE w.rootTenantId = s.rootTenantId)
                OR s.actingTenantId IN (SELECT tenantId FROM @auditScope)
                OR s.actingTenantId IN (SELECT tenantId FROM @secTenants)
                OR (s.actingTenantId IS NULL AND s.appUserId IN (SELECT appUserId FROM @accounts)))
         ORDER BY s.searchedUtc DESC, s.searchLogId DESC;

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
    , @ObjectName  = N'uspListSearchLogEvents'
    , @Description = N'Scoped logs.searchLog rows for investigation. Raw searchText is returned ONLY when @includeSearchText = 1 and the caller holds security.investigate (#329).';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspListSearchLogEvents TO meritWebRole;
END;
GO
