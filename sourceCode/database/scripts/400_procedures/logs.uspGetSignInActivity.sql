-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   logs.uspGetSignInActivity
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Suspicious sign-in patterns over the last @hours: failures by address and by account, one address trying many accounts (spraying), many addresses trying one account (stuffing), a full sign-in after repeated failures, and lockouts and rate-limit refusals. Tenant administrators see their accounts; holders of security.investigate also see unknown names (hashed) and rate-limit refusals.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission tenant.administer or security.review (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec logs.uspGetSignInActivity ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T213
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE logs.uspGetSignInActivity
      @hours              INT = 24
      , @sprayThreshold     INT = 5
      , @stuffingThreshold  INT = 3
      , @failuresBefore     INT = 3
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[logs].[uspGetSignInActivity]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'hours=', @hours);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'tenant.administer') = 0 AND auth.udfHasPermission (N'security.review') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (tenant.administer or security.review).', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @all BIT = auth.udfHasPermission (N'security.investigate');
        -- Issues 270/271: accounts homed or holding a current grant in the tenants the caller administers or reviews.
        DECLARE @admin TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @admin SELECT tenantId FROM auth.tvfReviewTenants ('security.review');
        DECLARE @accounts TABLE (appUserId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @accounts SELECT u.appUserId FROM auth.appUser AS u
         WHERE u.isDeleted = 0
           AND (EXISTS (SELECT 1 FROM @admin AS a WHERE a.tenantId = u.homeTenantId)
                OR EXISTS (SELECT 1 FROM auth.userAssignment AS ua JOIN @admin AS a ON a.tenantId = ua.tenantId
                            WHERE ua.appUserId = u.appUserId AND ua.isDeleted = 0
                              AND (ua.endDate IS NULL OR ua.endDate >= auth.udfSessionToday ())));
        DECLARE @since DATETIME2 (3) = DATEADD (HOUR, -CASE WHEN @hours BETWEEN 1 AND 2160 THEN @hours ELSE 24 END, SYSUTCDATETIME ());

        DECLARE @e TABLE (eventUtc DATETIME2 (3), eventType VARCHAR (40), reason VARCHAR (60), appUserId UNIQUEIDENTIFIER,
                          attemptedNameHash VARBINARY (32), clientAddress VARCHAR (45));
        INSERT @e
        SELECT e.eventUtc, e.eventType, e.reason, e.appUserId, e.attemptedNameHash, e.clientAddress
          FROM logs.signInEvent AS e
         WHERE e.eventUtc >= @since AND e.isDeleted = 0
           AND (@all = 1 OR e.appUserId IN (SELECT appUserId FROM @accounts));

        -- 1. Failures by address.
        SELECT TOP (50) clientAddress
             , SUM (CASE WHEN eventType IN ('passwordFailed', 'unknownUser', 'mfaFailed', 'attemptWhileLocked', 'accountInactive') THEN 1 ELSE 0 END)            AS failures
             , COUNT (DISTINCT CASE WHEN eventType IN ('passwordFailed', 'unknownUser', 'mfaFailed', 'attemptWhileLocked', 'accountInactive') THEN appUserId END) AS distinctAccounts
             , COUNT (DISTINCT CASE WHEN eventType = 'unknownUser' THEN attemptedNameHash END) AS distinctUnknownNames
             , SUM (CASE WHEN eventType = 'accountLocked' THEN 1 ELSE 0 END)            AS lockouts
             , SUM (CASE WHEN eventType = 'rateLimited' THEN 1 ELSE 0 END)              AS rateLimited
             , MIN (eventUtc) AS firstUtc, MAX (eventUtc) AS lastUtc
          FROM @e
         WHERE clientAddress IS NOT NULL
         GROUP BY clientAddress
        HAVING SUM (CASE WHEN eventType IN ('passwordFailed', 'unknownUser', 'mfaFailed', 'attemptWhileLocked', 'accountInactive', 'accountLocked', 'rateLimited') THEN 1 ELSE 0 END) > 0
         ORDER BY failures DESC, lastUtc DESC;

        -- 2. Failures by account.
        SELECT TOP (50) e.appUserId, u.userName, u.displayName
             , SUM (CASE WHEN e.eventType IN ('passwordFailed', 'unknownUser', 'mfaFailed', 'attemptWhileLocked', 'accountInactive') THEN 1 ELSE 0 END)  AS failures
             , COUNT (DISTINCT CASE WHEN e.eventType IN ('passwordFailed', 'unknownUser', 'mfaFailed', 'attemptWhileLocked', 'accountInactive') THEN e.clientAddress END) AS distinctAddresses
             , SUM (CASE WHEN e.eventType = 'accountLocked' THEN 1 ELSE 0 END) AS lockouts
             , MAX (e.eventUtc) AS lastUtc
          FROM @e AS e
          JOIN auth.appUser AS u ON u.appUserId = e.appUserId
         GROUP BY e.appUserId, u.userName, u.displayName
        HAVING SUM (CASE WHEN e.eventType IN ('passwordFailed', 'unknownUser', 'mfaFailed', 'attemptWhileLocked', 'accountInactive', 'accountLocked') THEN 1 ELSE 0 END) > 0
         ORDER BY failures DESC, lastUtc DESC;

        -- 3. Spraying: one address, many different accounts or unknown names.
        SELECT clientAddress
             , COUNT (DISTINCT COALESCE (CONVERT (VARCHAR (36), appUserId), CONVERT (VARCHAR (64), attemptedNameHash, 2))) AS targets
             , COUNT (*) AS failures, MIN (eventUtc) AS firstUtc, MAX (eventUtc) AS lastUtc
          FROM @e
         WHERE eventType IN ('passwordFailed', 'unknownUser', 'mfaFailed', 'attemptWhileLocked', 'accountInactive') AND clientAddress IS NOT NULL
         GROUP BY clientAddress
        HAVING COUNT (DISTINCT COALESCE (CONVERT (VARCHAR (36), appUserId), CONVERT (VARCHAR (64), attemptedNameHash, 2))) >= @sprayThreshold
         ORDER BY targets DESC;

        -- 4. Stuffing: one account (or one unknown name) tried from many addresses.
        SELECT e.appUserId, u.userName, u.displayName, CONVERT (VARCHAR (64), e.attemptedNameHash, 2) AS attemptedNameHash
             , COUNT (DISTINCT e.clientAddress) AS addresses, COUNT (*) AS failures, MAX (e.eventUtc) AS lastUtc
          FROM @e AS e
          LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
         WHERE e.eventType IN ('passwordFailed', 'unknownUser', 'mfaFailed', 'attemptWhileLocked', 'accountInactive')
         GROUP BY e.appUserId, u.userName, u.displayName, e.attemptedNameHash
        HAVING COUNT (DISTINCT e.clientAddress) >= @stuffingThreshold
         ORDER BY addresses DESC;

        -- 5. A full sign-in after repeated failures on the same account within the hour before it.
        SELECT s.appUserId, u.userName, u.displayName, s.eventUtc AS successUtc, s.clientAddress AS successAddress
             , f.failures, f.addresses
          FROM @e AS s
          JOIN auth.appUser AS u ON u.appUserId = s.appUserId
         CROSS APPLY (SELECT COUNT (*) AS failures, COUNT (DISTINCT x.clientAddress) AS addresses
                        FROM @e AS x
                       WHERE x.appUserId = s.appUserId AND x.eventType IN ('passwordFailed', 'unknownUser', 'mfaFailed', 'attemptWhileLocked', 'accountInactive')
                         AND x.eventUtc < s.eventUtc AND x.eventUtc >= DATEADD (HOUR, -1, s.eventUtc)) AS f
         WHERE s.eventType = 'sessionStarted' AND f.failures >= @failuresBefore
         ORDER BY s.eventUtc DESC;

        -- 6. Lockouts and rate-limit refusals.
        SELECT TOP (100) e.eventUtc, e.eventType, e.reason, e.appUserId, u.userName, e.clientAddress
          FROM @e AS e
          LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
         WHERE e.eventType IN ('accountLocked', 'rateLimited')
         ORDER BY e.eventUtc DESC;

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
    , @ObjectName  = N'uspGetSignInActivity'
    , @Description = N'Suspicious sign-in patterns over the last @hours: failures by address and by account, one address trying many accounts (spraying), many addresses trying one account (stuffing), a full sign-in after repeated failures, and lockouts and rate-limit refusals. Tenant administrators see their accounts; holders of security.investigate also see unknown names (hashed) and rate-limit refusals.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspGetSignInActivity TO meritWebRole;
END;
GO
