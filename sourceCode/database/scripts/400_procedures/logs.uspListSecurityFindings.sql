-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   logs.uspListSecurityFindings
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Lists persisted behavioral anomaly findings from logs.securityFinding within the caller's review scope (#327).

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission security.review or tenant.administer (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec logs.uspListSecurityFindings ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		327
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE logs.uspListSecurityFindings
      @days      INT = 30
      , @status    VARCHAR (20) = NULL
      , @severity  VARCHAR (15) = NULL
      , @appUserId UNIQUEIDENTIFIER = NULL
      , @top       INT = 200
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[logs].[uspListSecurityFindings]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'days=', @days, N', status=', @status, N', severity=', @severity);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'security.review') = 0 AND auth.udfHasPermission (N'tenant.administer') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (security.review or tenant.administer).', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @since DATETIME2 (3) = DATEADD (DAY, -CASE WHEN @days BETWEEN 1 AND 365 THEN @days ELSE 30 END, SYSUTCDATETIME ());
        DECLARE @limit INT = CASE WHEN @top BETWEEN 1 AND 1000 THEN @top ELSE 200 END;
        DECLARE @all BIT = auth.udfHasPermission (N'security.investigate');
        DECLARE @accounts TABLE (appUserId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @accounts SELECT appUserId FROM auth.tvfReviewAccounts ('security.review');
        DECLARE @reviewTenants TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @reviewTenants SELECT tenantId FROM auth.tvfReviewTenants ('security.review');

        SELECT TOP (@limit)
               f.securityFindingId, f.rootTenantId, f.owningTenantId, f.appUserId,
               u.userName, u.displayName, f.clientAddress, f.sessionReference,
               f.ruleCode, f.severity, f.title, f.detailJson,
               f.windowStartUtc, f.windowEndUtc, f.detectedUtc, f.status,
               f.acknowledgedByUserId, ack.displayName AS acknowledgedByName, f.acknowledgedUtc
          FROM logs.securityFinding AS f
          LEFT JOIN auth.appUser AS u ON u.appUserId = f.appUserId
          LEFT JOIN auth.appUser AS ack ON ack.appUserId = f.acknowledgedByUserId
         WHERE f.isDeleted = 0 AND f.detectedUtc >= @since
           AND (@status IS NULL OR f.status = @status)
           AND (@severity IS NULL OR f.severity = @severity)
           AND (@appUserId IS NULL OR f.appUserId = @appUserId)
           AND (@all = 1
                OR f.appUserId IN (SELECT appUserId FROM @accounts)
                OR f.owningTenantId IN (SELECT tenantId FROM @reviewTenants)
                OR f.rootTenantId IN (SELECT tenantId FROM @reviewTenants))
         ORDER BY CASE f.severity WHEN 'critical' THEN 1 WHEN 'high' THEN 2 WHEN 'medium' THEN 3 ELSE 4 END,
                  f.detectedUtc DESC;

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
    , @ObjectName  = N'uspListSecurityFindings'
    , @Description = N'Lists persisted behavioral anomaly findings from logs.securityFinding within the caller''s review scope (#327).';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspListSecurityFindings TO meritWebRole;
END;
GO
