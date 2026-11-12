-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   logs.uspListAuditUsers
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Lists the users who appear in the caller's root tenant's audit ledger, by name, for the audit log's user filter.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission audit.view (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec logs.uspListAuditUsers ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T051
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE logs.uspListAuditUsers

AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[logs].[uspListAuditUsers]')
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

        IF auth.udfHasPermission (N'audit.view') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (audit.view).', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        -- Issue 270: scope to what the caller reviews, not to the whole data root. A data root is visible in full only
        -- where the caller reviews its root tenant (a state administrator; a platform or region administrator for the
        -- roots below it). Elsewhere: events by users homed or granted (now or before) in the reviewed tenants, and
        -- events on records those tenants owned when the event was written.
        DECLARE @scope TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @scope SELECT tenantId FROM auth.tvfReviewTenants ('audit.view');
        DECLARE @wholeRoots TABLE (rootTenantId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @wholeRoots SELECT t.tenantId FROM auth.tenant AS t JOIN @scope AS s ON s.tenantId = t.tenantId WHERE t.tenantId = t.rootTenantId;
        DECLARE @partRoots TABLE (rootTenantId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @partRoots SELECT DISTINCT t.rootTenantId FROM auth.tenant AS t JOIN @scope AS s ON s.tenantId = t.tenantId
         WHERE NOT EXISTS (SELECT 1 FROM @wholeRoots AS w WHERE w.rootTenantId = t.rootTenantId);
        DECLARE @users TABLE (appUserId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @users SELECT u.appUserId FROM auth.appUser AS u
         WHERE EXISTS (SELECT 1 FROM @partRoots)
           AND (EXISTS (SELECT 1 FROM @scope AS s WHERE s.tenantId = u.homeTenantId)
                OR EXISTS (SELECT 1 FROM auth.userAssignment AS ua JOIN @scope AS s ON s.tenantId = ua.tenantId WHERE ua.appUserId = u.appUserId AND ua.isDeleted = 0));

        SELECT u.appUserId, u.displayName
          FROM auth.appUser AS u
         WHERE EXISTS (SELECT 1
                         FROM logs.auditEvent AS e
                        WHERE e.appUserId = u.appUserId
                          AND (EXISTS (SELECT 1 FROM @wholeRoots AS w WHERE w.rootTenantId = e.rootTenantId)
                OR (EXISTS (SELECT 1 FROM @partRoots AS p WHERE p.rootTenantId = e.rootTenantId)
                    AND ((e.owningTenantId IS NULL AND EXISTS (SELECT 1 FROM @users AS x WHERE x.appUserId = e.appUserId))
                         OR EXISTS (SELECT 1 FROM @scope AS s WHERE s.tenantId = e.owningTenantId)))))
         ORDER BY u.displayName, u.appUserId;

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
    , @ObjectName  = N'uspListAuditUsers'
    , @Description = N'Lists the users who appear in the caller''s root tenant''s audit ledger, by name, for the audit log''s user filter.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspListAuditUsers TO meritWebRole;
END;
GO
