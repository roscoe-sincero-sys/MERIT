-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   logs.uspGetBehaviorAnomalies
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Evaluates post-authentication and sign-in behavioral anomaly rules over the last @hours within the caller's review scope, with configurable thresholds (#327).

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

exec logs.uspGetBehaviorAnomalies ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		327
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE logs.uspGetBehaviorAnomalies
      @hours                    INT = 24
      , @viewVelocityThreshold    INT = 50
      , @exportSpikeThreshold     INT = 5
      , @exportRowsThreshold      INT = 500
      , @probingThreshold         INT = 10
      , @searchVelocityThreshold  INT = 40
      , @privBurstThreshold       INT = 5
      , @mfaAbuseThreshold        INT = 5
      , @distributedFailThreshold INT = 10
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[logs].[uspGetBehaviorAnomalies]')
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

        IF auth.udfHasPermission (N'security.review') = 0 AND auth.udfHasPermission (N'tenant.administer') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (security.review or tenant.administer).', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @winHours INT = CASE WHEN @hours BETWEEN 1 AND 2160 THEN @hours ELSE 24 END;
        DECLARE @windowEnd DATETIME2 (3) = SYSUTCDATETIME ();
        DECLARE @since DATETIME2 (3) = DATEADD (HOUR, -@winHours, @windowEnd);
        DECLARE @all BIT = auth.udfHasPermission (N'security.investigate');
        DECLARE @accounts TABLE (appUserId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @accounts SELECT appUserId FROM auth.tvfReviewAccounts ('security.review');

        DECLARE @findings TABLE (
            ruleCode         VARCHAR (50),
            severity         VARCHAR (15),
            appUserId        UNIQUEIDENTIFIER NULL,
            userName         NVARCHAR (100) NULL,
            displayName      NVARCHAR (150) NULL,
            clientAddress    VARCHAR (45) NULL,
            sessionReference UNIQUEIDENTIFIER NULL,
            eventCount       INT,
            title            NVARCHAR (200),
            detailJson       NVARCHAR (2000),
            firstUtc         DATETIME2 (3),
            lastUtc          DATETIME2 (3)
        );

        -- Rule 1: Bulk patient/result/case viewing velocity
        INSERT @findings
        SELECT 'bulkViewVelocity',
               CASE WHEN COUNT (DISTINCT e.entityId) >= @viewVelocityThreshold * 2 THEN 'high' ELSE 'medium' END,
               e.appUserId, u.userName, u.displayName, NULL, e.sessionReference,
               COUNT (DISTINCT e.entityId),
               CONCAT (N'High distinct record view velocity (', COUNT (DISTINCT e.entityId), N' records in ', @winHours, N'h)'),
               (SELECT COUNT (DISTINCT e.entityId) AS distinctEntities, COUNT (*) AS totalViews, @viewVelocityThreshold AS threshold FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
               MIN (e.eventUtc), MAX (e.eventUtc)
          FROM logs.auditEvent AS e
          LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
         WHERE e.eventUtc >= @since AND e.eventAction = 'view' AND e.entityId IS NOT NULL
           AND (@all = 1 OR e.appUserId IN (SELECT appUserId FROM @accounts))
         GROUP BY e.appUserId, u.userName, u.displayName, e.sessionReference
        HAVING COUNT (DISTINCT e.entityId) >= @viewVelocityThreshold;

        -- Rule 2: Bulk or unusual data exports
        INSERT @findings
        SELECT 'bulkExportSpike',
               CASE WHEN SUM (COALESCE (e.recordCount, 1)) >= @exportRowsThreshold * 2 OR COUNT (*) >= @exportSpikeThreshold * 2 THEN 'high' ELSE 'medium' END,
               e.appUserId, u.userName, u.displayName, NULL, NULL,
               COUNT (*),
               CONCAT (N'Bulk export spike (', COUNT (*), N' exports, ', SUM (COALESCE (e.recordCount, 0)), N' rows)'),
               (SELECT COUNT (*) AS exportCount, SUM (COALESCE (e.recordCount, 0)) AS totalRows, @exportSpikeThreshold AS countThreshold, @exportRowsThreshold AS rowsThreshold FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
               MIN (e.eventUtc), MAX (e.eventUtc)
          FROM logs.auditEvent AS e
          LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
         WHERE e.eventUtc >= @since AND e.eventAction = 'export'
           AND (@all = 1 OR e.appUserId IN (SELECT appUserId FROM @accounts))
         GROUP BY e.appUserId, u.userName, u.displayName
        HAVING COUNT (*) >= @exportSpikeThreshold OR SUM (COALESCE (e.recordCount, 0)) >= @exportRowsThreshold;

        -- Rule 3: Repeated permissionDenied or outside-scope probing
        INSERT @findings
        SELECT 'probingDeniedOrOutsideScope',
               CASE WHEN COUNT (*) >= @probingThreshold * 2 THEN 'high' ELSE 'medium' END,
               e.appUserId, u.userName, u.displayName, MAX (e.clientAddress), NULL,
               COUNT (*),
               CONCAT (N'Repeated permission-denied or outside-scope probing (', COUNT (*), N' events)'),
               (SELECT SUM (CASE WHEN e.eventAction = 'permissionDenied' THEN 1 ELSE 0 END) AS permissionDenied,
                       SUM (CASE WHEN e.entityType = 'personOutsideScope' THEN 1 ELSE 0 END) AS outsideScope,
                       @probingThreshold AS threshold FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
               MIN (e.eventUtc), MAX (e.eventUtc)
          FROM logs.auditEvent AS e
          LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
         WHERE e.eventUtc >= @since
           AND (e.eventAction = 'permissionDenied' OR e.entityType = 'personOutsideScope')
           AND (@all = 1 OR e.appUserId IN (SELECT appUserId FROM @accounts))
         GROUP BY e.appUserId, u.userName, u.displayName
        HAVING COUNT (*) >= @probingThreshold;

        -- Rule 4: High-velocity searchLog querying
        INSERT @findings
        SELECT 'searchVelocitySpike',
               CASE WHEN COUNT (*) >= @searchVelocityThreshold * 2 THEN 'high' ELSE 'medium' END,
               s.appUserId, u.userName, u.displayName, MAX (s.clientAddress), NULL,
               COUNT (*),
               CONCAT (N'High-velocity searching (', COUNT (*), N' queries, ', SUM (CASE WHEN s.resultCount = 0 THEN 1 ELSE 0 END), N' zero-result)'),
               (SELECT COUNT (*) AS searches, SUM (CASE WHEN s.resultCount = 0 THEN 1 ELSE 0 END) AS zeroResults,
                       SUM (CASE WHEN s.existsElsewhere = 1 THEN 1 ELSE 0 END) AS outsideScope, @searchVelocityThreshold AS threshold FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
               MIN (s.searchedUtc), MAX (s.searchedUtc)
          FROM logs.searchLog AS s
          LEFT JOIN auth.appUser AS u ON u.appUserId = s.appUserId
         WHERE s.isDeleted = 0 AND s.searchedUtc >= @since
           AND (@all = 1 OR s.appUserId IN (SELECT appUserId FROM @accounts))
         GROUP BY s.appUserId, u.userName, u.displayName
        HAVING COUNT (*) >= @searchVelocityThreshold;

        -- Rule 5: Mid-session IP drift (possible session hijacking)
        INSERT @findings
        SELECT 'midSessionIpDrift', 'high',
               e.appUserId, u.userName, u.displayName, NULL, e.sessionReference,
               COUNT (DISTINCT e.clientAddress),
               CONCAT (N'Mid-session IP address change (', COUNT (DISTINCT e.clientAddress), N' distinct addresses in one session)'),
               (SELECT COUNT (DISTINCT e.clientAddress) AS distinctAddresses, COUNT (*) AS events FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
               MIN (e.eventUtc), MAX (e.eventUtc)
          FROM logs.auditEvent AS e
          LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
         WHERE e.eventUtc >= @since AND e.sessionReference IS NOT NULL AND e.clientAddress IS NOT NULL
           AND (@all = 1 OR e.appUserId IN (SELECT appUserId FROM @accounts))
         GROUP BY e.appUserId, u.userName, u.displayName, e.sessionReference
        HAVING COUNT (DISTINCT e.clientAddress) > 1;

        -- Rule 6: Automated / headless User-Agent strings
        INSERT @findings
        SELECT 'automatedUserAgent', 'medium',
               e.appUserId, u.userName, u.displayName, MAX (e.clientAddress), MAX (e.sessionReference),
               COUNT (*),
               CONCAT (N'Automated or headless client user-agent detected (', COUNT (*), N' sign-in events)'),
               (SELECT COUNT (*) AS events, LEFT (MAX (e.userAgent), 120) AS sampleUserAgent FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
               MIN (e.eventUtc), MAX (e.eventUtc)
          FROM logs.signInEvent AS e
          LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
         WHERE e.isDeleted = 0 AND e.eventUtc >= @since AND e.userAgent IS NOT NULL
           AND (   e.userAgent LIKE N'%curl/%'
                OR e.userAgent LIKE N'%python-requests%'
                OR e.userAgent LIKE N'%HeadlessChrome%'
                OR e.userAgent LIKE N'%Playwright%'
                OR e.userAgent LIKE N'%Selenium%'
                OR e.userAgent LIKE N'%Scrapy%'
                OR e.userAgent LIKE N'%wget%'
                OR e.userAgent LIKE N'%Go-http-client%')
           AND (@all = 1 OR e.appUserId IN (SELECT appUserId FROM @accounts))
         GROUP BY e.appUserId, u.userName, u.displayName;

        -- Rule 7: Privileged / security-admin action bursts
        INSERT @findings
        SELECT 'privilegedActionBurst', 'high',
               e.appUserId, u.userName, u.displayName, MAX (e.clientAddress), NULL,
               COUNT (*),
               CONCAT (N'Privileged security/configuration burst (', COUNT (*), N' actions)'),
               (SELECT COUNT (*) AS actions, @privBurstThreshold AS threshold FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
               MIN (e.eventUtc), MAX (e.eventUtc)
          FROM logs.auditEvent AS e
          LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
         WHERE e.eventUtc >= @since
           AND (   e.eventAction IN ('grant', 'revoke', 'deactivate', 'mfaResetByAdmin', 'passwordSetByAdmin')
                OR (e.entityType IN ('threshold', 'jurisdictionSetting', 'userAssignment', 'role') AND e.eventAction IN ('create', 'update', 'delete')))
           AND (@all = 1 OR e.appUserId IN (SELECT appUserId FROM @accounts))
         GROUP BY e.appUserId, u.userName, u.displayName
        HAVING COUNT (*) >= @privBurstThreshold;

        -- Rule 8: MFA fatigue / password-reset abuse
        INSERT @findings
        SELECT 'mfaOrResetAbuse', 'high',
               e.appUserId, u.userName, u.displayName, MAX (e.clientAddress), NULL,
               COUNT (*),
               CONCAT (N'Repeated MFA or password-reset attempts (', COUNT (*), N' events)'),
               (SELECT COUNT (*) AS events, COUNT (DISTINCT e.clientAddress) AS distinctAddresses, @mfaAbuseThreshold AS threshold FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
               MIN (e.eventUtc), MAX (e.eventUtc)
          FROM logs.signInEvent AS e
          LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
         WHERE e.isDeleted = 0 AND e.eventUtc >= @since
           AND e.eventType IN ('mfaFailed', 'mfaEmailCodeSent', 'passwordResetRequested')
           AND (@all = 1 OR e.appUserId IN (SELECT appUserId FROM @accounts))
         GROUP BY e.appUserId, u.userName, u.displayName
        HAVING COUNT (*) >= @mfaAbuseThreshold;

        -- Rule 9: Distributed low-and-slow failures across IP addresses
        INSERT @findings
        SELECT 'distributedLowAndSlowFailures', 'medium',
               e.appUserId, u.userName, u.displayName, NULL, NULL,
               COUNT (*),
               CONCAT (N'Distributed low-and-slow sign-in failures (', COUNT (*), N' failures across ', COUNT (DISTINCT e.clientAddress), N' addresses)'),
               (SELECT COUNT (*) AS failures, COUNT (DISTINCT e.clientAddress) AS distinctAddresses, @distributedFailThreshold AS threshold FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
               MIN (e.eventUtc), MAX (e.eventUtc)
          FROM logs.signInEvent AS e
          LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
         WHERE e.isDeleted = 0 AND e.eventUtc >= @since
           AND e.eventType IN ('passwordFailed', 'unknownUser', 'mfaFailed', 'attemptWhileLocked', 'accountInactive')
           AND (@all = 1 OR e.appUserId IN (SELECT appUserId FROM @accounts))
         GROUP BY e.appUserId, u.userName, u.displayName
        HAVING COUNT (*) >= @distributedFailThreshold AND COUNT (DISTINCT e.clientAddress) >= 4;

        -- Rule 10: API-only data access with zero screen visits (machine-speed / automated scraping)
        INSERT @findings
        SELECT 'apiOnlyNoScreenVisits', 'medium',
               e.appUserId, u.userName, u.displayName, MAX (e.clientAddress), e.sessionReference,
               COUNT (*),
               CONCAT (N'Data-access session with zero UI screen visits (', COUNT (*), N' reads)'),
               (SELECT COUNT (*) AS readEvents, COUNT (DISTINCT e.entityId) AS distinctEntities FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
               MIN (e.eventUtc), MAX (e.eventUtc)
          FROM logs.auditEvent AS e
          LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
         WHERE e.eventUtc >= @since AND e.sessionReference IS NOT NULL
           AND e.eventAction IN ('view', 'list', 'export')
           AND (@all = 1 OR e.appUserId IN (SELECT appUserId FROM @accounts))
           AND NOT EXISTS (SELECT 1 FROM logs.screenVisit AS v
                            WHERE v.sessionReference = e.sessionReference AND v.isDeleted = 0)
         GROUP BY e.appUserId, u.userName, u.displayName, e.sessionReference
        HAVING COUNT (*) >= 25;

        SELECT ruleCode, severity, appUserId, userName, displayName, clientAddress, sessionReference,
               eventCount, title, detailJson, firstUtc, lastUtc
          FROM @findings
         ORDER BY CASE severity WHEN 'critical' THEN 1 WHEN 'high' THEN 2 WHEN 'medium' THEN 3 ELSE 4 END,
                  lastUtc DESC;

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
    , @ObjectName  = N'uspGetBehaviorAnomalies'
    , @Description = N'Evaluates post-authentication and sign-in behavioral anomaly rules over the last @hours within the caller''s review scope, with configurable thresholds (#327).';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspGetBehaviorAnomalies TO meritWebRole;
END;
GO
