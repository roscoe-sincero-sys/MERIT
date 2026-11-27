-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   logs.uspGetUserBehaviorSummary
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Unified per-user behavioral summary applying strict event-level tenant scope to audit, search, usage, and security-finding aggregates (#330, Codex #1).

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission security.review or audit.view or tenant.administer (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec logs.uspGetUserBehaviorSummary ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		330
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE logs.uspGetUserBehaviorSummary
      @appUserId UNIQUEIDENTIFIER
      , @days      INT = 30
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[logs].[uspGetUserBehaviorSummary]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'appUserId=', @appUserId, N', days=', @days);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'security.review') = 0 AND auth.udfHasPermission (N'audit.view') = 0 AND auth.udfHasPermission (N'tenant.administer') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (security.review or audit.view or tenant.administer).', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @winDays INT = CASE WHEN @days BETWEEN 1 AND 365 THEN @days ELSE 30 END;
        DECLARE @since DATETIME2 (3) = DATEADD (DAY, -@winDays, SYSUTCDATETIME ());
        DECLARE @canAudit BIT = auth.udfHasPermission (N'audit.view');
        DECLARE @canUsage BIT = auth.udfHasPermission (N'usage.view');
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
        DECLARE @wholeSecRoots TABLE (rootTenantId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @wholeSecRoots SELECT t.tenantId FROM auth.tenant AS t JOIN @secTenants AS r ON r.tenantId = t.tenantId WHERE t.tenantId = t.rootTenantId;
        DECLARE @usageTenants TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
        IF @canUsage = 1 INSERT @usageTenants SELECT tenantId FROM auth.tvfReviewTenants ('usage.view');

        IF @all = 0
           AND NOT EXISTS (SELECT 1 FROM @accounts WHERE appUserId = @appUserId)
           AND NOT EXISTS (SELECT 1 FROM @auditUsers WHERE appUserId = @appUserId)
           AND NOT EXISTS (SELECT 1 FROM auth.appUser AS u WHERE u.appUserId = @appUserId AND u.rootTenantId IN (SELECT rootTenantId FROM @wholeRoots))
        BEGIN
            ;THROW 51404, N'The user was not found, or you do not have access to review this account.', 1;
        END;

        -- 1. Account overview & event-scoped posture
        SELECT u.appUserId, u.userName, u.displayName, u.isActive, u.mfaEnabled, u.lastSignInUtc,
               (SELECT COUNT (*) FROM auth.sessionScope AS ss
                 WHERE ss.appUserId = u.appUserId AND ss.revokedUtc IS NULL AND ss.expiresUtc > SYSUTCDATETIME ()) AS activeSessions,
               (SELECT COUNT (*) FROM logs.signInEvent AS se WHERE se.appUserId = u.appUserId AND se.isDeleted = 0 AND se.eventUtc >= @since AND se.eventType = 'sessionStarted') AS signInSuccesses,
               (SELECT COUNT (*) FROM logs.signInEvent AS se WHERE se.appUserId = u.appUserId AND se.isDeleted = 0 AND se.eventUtc >= @since AND se.eventType IN ('passwordFailed', 'unknownUser', 'mfaFailed', 'attemptWhileLocked', 'accountInactive')) AS signInFailures,
               (SELECT COUNT (*) FROM logs.signInEvent AS se WHERE se.appUserId = u.appUserId AND se.isDeleted = 0 AND se.eventUtc >= @since AND se.eventType = 'accountLocked') AS lockouts,
               (SELECT COUNT (DISTINCT se.clientAddress) FROM logs.signInEvent AS se WHERE se.appUserId = u.appUserId AND se.isDeleted = 0 AND se.eventUtc >= @since AND se.clientAddress IS NOT NULL) AS distinctAddresses,
               (SELECT COUNT (*) FROM logs.searchLog AS sl
                 WHERE sl.appUserId = u.appUserId AND sl.isDeleted = 0 AND sl.searchedUtc >= @since
                   AND (@all = 1 OR EXISTS (SELECT 1 FROM @wholeRoots AS w WHERE w.rootTenantId = sl.rootTenantId)
                        OR sl.actingTenantId IN (SELECT tenantId FROM @auditScope)
                        OR sl.actingTenantId IN (SELECT tenantId FROM @secTenants))) AS totalSearches,
               (SELECT COUNT (*) FROM logs.searchLog AS sl
                 WHERE sl.appUserId = u.appUserId AND sl.isDeleted = 0 AND sl.searchedUtc >= @since AND sl.resultCount = 0
                   AND (@all = 1 OR EXISTS (SELECT 1 FROM @wholeRoots AS w WHERE w.rootTenantId = sl.rootTenantId)
                        OR sl.actingTenantId IN (SELECT tenantId FROM @auditScope)
                        OR sl.actingTenantId IN (SELECT tenantId FROM @secTenants))) AS zeroResultSearches,
               (SELECT COUNT (*) FROM logs.searchLog AS sl
                 WHERE sl.appUserId = u.appUserId AND sl.isDeleted = 0 AND sl.searchedUtc >= @since AND sl.existsElsewhere = 1
                   AND (@all = 1 OR EXISTS (SELECT 1 FROM @wholeRoots AS w WHERE w.rootTenantId = sl.rootTenantId)
                        OR sl.actingTenantId IN (SELECT tenantId FROM @auditScope)
                        OR sl.actingTenantId IN (SELECT tenantId FROM @secTenants))) AS outsideScopeSearches,
               (SELECT COUNT (DISTINCT sv.sessionReference) FROM logs.screenVisit AS sv
                 WHERE @canUsage = 1 AND sv.appUserId = u.appUserId AND sv.isDeleted = 0 AND sv.eventUtc >= @since
                   AND sv.actingTenantId IN (SELECT tenantId FROM @usageTenants)) AS usageSessions,
               (SELECT COUNT (*) FROM logs.screenVisit AS sv
                 WHERE @canUsage = 1 AND sv.appUserId = u.appUserId AND sv.isDeleted = 0 AND sv.eventUtc >= @since
                   AND sv.actingTenantId IN (SELECT tenantId FROM @usageTenants)) AS screenVisits
          FROM auth.appUser AS u
         WHERE u.appUserId = @appUserId AND u.isDeleted = 0;

        -- 2. Event-scoped audit ledger action breakdown (requires audit.view + #270 event ownership scope)
        SELECT e.eventAction, e.entityType, COUNT (*) AS eventCount,
               SUM (COALESCE (e.recordCount, 1)) AS totalRecords,
               MAX (e.eventUtc) AS lastUtc
          FROM logs.auditEvent AS e
         WHERE @canAudit = 1 AND e.appUserId = @appUserId AND e.eventUtc >= @since
           AND (EXISTS (SELECT 1 FROM @wholeRoots AS w WHERE w.rootTenantId = e.rootTenantId)
            OR (EXISTS (SELECT 1 FROM @partRoots AS p WHERE p.rootTenantId = e.rootTenantId)
                AND ((e.owningTenantId IS NULL AND EXISTS (SELECT 1 FROM @auditUsers AS x WHERE x.appUserId = e.appUserId))
                     OR EXISTS (SELECT 1 FROM @auditScope AS s WHERE s.tenantId = e.owningTenantId)
                     OR (e.owningTenantId = e.rootTenantId AND e.entityType IN ('jurisdictionSetting', 'threshold', 'notificationRule')))))
         GROUP BY e.eventAction, e.entityType
         ORDER BY eventCount DESC, lastUtc DESC;

        -- 3. Event-scoped recent security findings for this user
        SELECT TOP (25) f.securityFindingId, f.ruleCode, f.severity, f.title, f.status, f.detectedUtc
          FROM logs.securityFinding AS f
         WHERE f.appUserId = @appUserId AND f.isDeleted = 0 AND f.detectedUtc >= @since
           AND (@all = 1
                OR f.owningTenantId IN (SELECT tenantId FROM @secTenants)
                OR (f.owningTenantId IS NULL AND f.rootTenantId IN (SELECT rootTenantId FROM @wholeSecRoots)))
         ORDER BY f.detectedUtc DESC;

        -- 4. Event-scoped recent screen-usage sessions (requires usage.view + actingTenantId scope)
        SELECT TOP (25) v.sessionReference, MIN (v.eventUtc) AS firstUtc, MAX (v.eventUtc) AS lastUtc, COUNT (*) AS visits
          FROM logs.screenVisit AS v
         WHERE @canUsage = 1 AND v.appUserId = @appUserId AND v.isDeleted = 0 AND v.eventUtc >= @since
           AND v.actingTenantId IN (SELECT tenantId FROM @usageTenants)
         GROUP BY v.sessionReference
         ORDER BY MAX (v.eventUtc) DESC;

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
    , @ObjectName  = N'uspGetUserBehaviorSummary'
    , @Description = N'Unified per-user behavioral summary applying strict event-level tenant scope to audit, search, usage, and security-finding aggregates (#330, Codex #1).';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspGetUserBehaviorSummary TO meritWebRole;
END;
GO
