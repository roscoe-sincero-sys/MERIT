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

Unified per-user behavioral summary across sign-ins, active sessions, audit ledger actions, search volume, screen usage, and open security findings within the caller's review scope (#330).

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
        DECLARE @all BIT = auth.udfHasPermission (N'security.investigate');
        DECLARE @accounts TABLE (appUserId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @accounts SELECT appUserId FROM auth.tvfReviewAccounts ('security.review');
        DECLARE @allAudit BIT = auth.udfHasPermission (N'security.investigate');
        DECLARE @auditTenants TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @auditTenants SELECT tenantId FROM auth.tvfReviewTenants ('audit.view');

        IF @all = 0 AND @allAudit = 0
           AND NOT EXISTS (SELECT 1 FROM @accounts WHERE appUserId = @appUserId)
           AND NOT EXISTS (SELECT 1 FROM auth.appUser AS u WHERE u.appUserId = @appUserId AND u.homeTenantId IN (SELECT tenantId FROM @auditTenants))
        BEGIN
            ;THROW 51404, N'The user was not found, or you do not have access to review this account.', 1;
        END;

        -- 1. Account overview & sign-in posture
        SELECT u.appUserId, u.userName, u.displayName, u.isActive, u.mfaEnabled, u.lastSignInUtc,
               (SELECT COUNT (*) FROM auth.sessionScope AS ss
                 WHERE ss.appUserId = u.appUserId AND ss.revokedUtc IS NULL AND ss.expiresUtc > SYSUTCDATETIME ()) AS activeSessions,
               (SELECT COUNT (*) FROM logs.signInEvent AS se WHERE se.appUserId = u.appUserId AND se.isDeleted = 0 AND se.eventUtc >= @since AND se.eventType = 'sessionStarted') AS signInSuccesses,
               (SELECT COUNT (*) FROM logs.signInEvent AS se WHERE se.appUserId = u.appUserId AND se.isDeleted = 0 AND se.eventUtc >= @since AND se.eventType IN ('passwordFailed', 'unknownUser', 'mfaFailed', 'attemptWhileLocked', 'accountInactive')) AS signInFailures,
               (SELECT COUNT (*) FROM logs.signInEvent AS se WHERE se.appUserId = u.appUserId AND se.isDeleted = 0 AND se.eventUtc >= @since AND se.eventType = 'accountLocked') AS lockouts,
               (SELECT COUNT (DISTINCT se.clientAddress) FROM logs.signInEvent AS se WHERE se.appUserId = u.appUserId AND se.isDeleted = 0 AND se.eventUtc >= @since AND se.clientAddress IS NOT NULL) AS distinctAddresses,
               (SELECT COUNT (*) FROM logs.searchLog AS sl WHERE sl.appUserId = u.appUserId AND sl.isDeleted = 0 AND sl.searchedUtc >= @since) AS totalSearches,
               (SELECT COUNT (*) FROM logs.searchLog AS sl WHERE sl.appUserId = u.appUserId AND sl.isDeleted = 0 AND sl.searchedUtc >= @since AND sl.resultCount = 0) AS zeroResultSearches,
               (SELECT COUNT (*) FROM logs.searchLog AS sl WHERE sl.appUserId = u.appUserId AND sl.isDeleted = 0 AND sl.searchedUtc >= @since AND sl.existsElsewhere = 1) AS outsideScopeSearches,
               (SELECT COUNT (DISTINCT sv.sessionReference) FROM logs.screenVisit AS sv WHERE sv.appUserId = u.appUserId AND sv.isDeleted = 0 AND sv.eventUtc >= @since) AS usageSessions,
               (SELECT COUNT (*) FROM logs.screenVisit AS sv WHERE sv.appUserId = u.appUserId AND sv.isDeleted = 0 AND sv.eventUtc >= @since) AS screenVisits
          FROM auth.appUser AS u
         WHERE u.appUserId = @appUserId AND u.isDeleted = 0;

        -- 2. Audit ledger action breakdown
        SELECT e.eventAction, e.entityType, COUNT (*) AS eventCount,
               SUM (COALESCE (e.recordCount, 1)) AS totalRecords,
               MAX (e.eventUtc) AS lastUtc
          FROM logs.auditEvent AS e
         WHERE e.appUserId = @appUserId AND e.eventUtc >= @since
         GROUP BY e.eventAction, e.entityType
         ORDER BY eventCount DESC, lastUtc DESC;

        -- 3. Recent security findings for this user
        SELECT TOP (25) f.securityFindingId, f.ruleCode, f.severity, f.title, f.status, f.detectedUtc
          FROM logs.securityFinding AS f
         WHERE f.appUserId = @appUserId AND f.isDeleted = 0 AND f.detectedUtc >= @since
         ORDER BY f.detectedUtc DESC;

        -- 4. Recent screen-usage sessions for this user
        SELECT TOP (25) v.sessionReference, MIN (v.eventUtc) AS firstUtc, MAX (v.eventUtc) AS lastUtc, COUNT (*) AS visits
          FROM logs.screenVisit AS v
         WHERE v.appUserId = @appUserId AND v.isDeleted = 0 AND v.eventUtc >= @since
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
    , @Description = N'Unified per-user behavioral summary across sign-ins, active sessions, audit ledger actions, search volume, screen usage, and open security findings within the caller''s review scope (#330).';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspGetUserBehaviorSummary TO meritWebRole;
END;
GO
