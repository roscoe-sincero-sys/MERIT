-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   logs.uspRunSecurityFindingScan
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Scheduled worker scan evaluating all 10 behavioral anomaly rules across active tenants (and platform-scoped unattributed events), serialized via sp_getapplock, deduplicated by unique fingerprint (#327).

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec logs.uspRunSecurityFindingScan ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		327
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE logs.uspRunSecurityFindingScan
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
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[logs].[uspRunSecurityFindingScan]')
          , @StartTimeUtc   DATETIME2 (3)  = SYSUTCDATETIME ()
          , @EndTimeUtc     DATETIME2 (3)  = NULL
          , @ExecutionId    BIGINT         = NULL
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @Comments       NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never a credential, a payload, or patient data.
    SET @KeyParameters = CONCAT (N'hours=', @hours);

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @scanLock INT;
        EXEC @scanLock = sys.sp_getapplock @Resource = N'MERIT.securityFindingScan', @LockMode = 'Exclusive',
             @LockOwner = 'Transaction', @LockTimeout = 5000;
        IF @scanLock < 0
        BEGIN
            SELECT TOP (0) securityFindingId, rootTenantId, appUserId, ruleCode, severity, title, detailJson, detectedUtc
              FROM logs.securityFinding;
            SET @Comments = N'Another worker instance is running the scan.';
        END
        ELSE
        BEGIN
        DECLARE @winHours INT = CASE WHEN @hours BETWEEN 1 AND 168 THEN @hours ELSE 24 END;
        DECLARE @now DATETIME2 (3) = SYSUTCDATETIME ();
        DECLARE @since DATETIME2 (3) = DATEADD (HOUR, -@winHours, @now);
        DECLARE @dayBucket VARCHAR (10) = CONVERT (VARCHAR (10), @now, 23);
        DECLARE @platformRoot UNIQUEIDENTIFIER = '00000000-0000-0000-0000-000000000000';

        DECLARE @candidates TABLE (
            rootTenantId     UNIQUEIDENTIFIER NOT NULL,
            owningTenantId   UNIQUEIDENTIFIER NULL,
            appUserId        UNIQUEIDENTIFIER NULL,
            clientAddress    VARCHAR (45) NULL,
            sessionReference UNIQUEIDENTIFIER NULL,
            ruleCode         VARCHAR (50) NOT NULL,
            severity         VARCHAR (15) NOT NULL,
            title            NVARCHAR (200) NOT NULL,
            detailJson       NVARCHAR (2000) NULL,
            fingerprint      VARCHAR (128) NOT NULL
        );

        -- Rule 1: bulkViewVelocity (owningTenantId derived from source event ownership + acting tenant, NULL when mixed, Codex #2)
        INSERT @candidates
        SELECT COALESCE (e.rootTenantId, @platformRoot),
               CASE WHEN SUM (CASE WHEN COALESCE (e.owningTenantId, e.actingTenantId) IS NULL THEN 1 ELSE 0 END) = 0 AND COUNT (DISTINCT COALESCE (e.owningTenantId, e.actingTenantId)) = 1 AND COUNT (DISTINCT e.actingTenantId) <= 1 THEN MAX (COALESCE (e.owningTenantId, e.actingTenantId)) END,
               e.appUserId, NULL, e.sessionReference,
               'bulkViewVelocity', CASE WHEN COUNT (DISTINCT e.entityId) >= @viewVelocityThreshold * 2 THEN 'high' ELSE 'medium' END,
               CONCAT (N'High distinct record view velocity (', COUNT (DISTINCT e.entityId), N' records in ', @winHours, N'h)'),
               (SELECT COUNT (DISTINCT e.entityId) AS distinctEntities, COUNT (*) AS totalViews FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
               CONCAT ('bulkViewVelocity|', COALESCE (CONVERT (VARCHAR (36), e.appUserId), 'none'), '|', COALESCE (CONVERT (VARCHAR (36), e.sessionReference), @dayBucket))
          FROM logs.auditEvent AS e
         WHERE e.eventUtc >= @since AND e.eventAction = 'view' AND e.entityId IS NOT NULL
         GROUP BY COALESCE (e.rootTenantId, @platformRoot), e.appUserId, e.sessionReference
        HAVING COUNT (DISTINCT e.entityId) >= @viewVelocityThreshold;

        -- Rule 2: bulkExportSpike (structuredLogExport paired by entityId; single-entry downloads by auditEventId, I13 / Codex #3)
        INSERT @candidates
        SELECT COALESCE (e.rootTenantId, @platformRoot),
               CASE WHEN SUM (CASE WHEN COALESCE (e.owningTenantId, e.actingTenantId) IS NULL THEN 1 ELSE 0 END) = 0 AND COUNT (DISTINCT COALESCE (e.owningTenantId, e.actingTenantId)) = 1 AND COUNT (DISTINCT e.actingTenantId) <= 1 THEN MAX (COALESCE (e.owningTenantId, e.actingTenantId)) END,
               e.appUserId, NULL, NULL,
               'bulkExportSpike',
               CASE WHEN SUM (COALESCE (e.recordCount, 1)) >= @exportRowsThreshold * 2
                         OR COUNT (DISTINCT CASE WHEN e.entityType = 'structuredLogExport' AND e.entityId IS NOT NULL THEN CAST (e.entityId AS VARCHAR (36)) ELSE CAST (e.auditEventId AS VARCHAR (20)) END) >= @exportSpikeThreshold * 2
                    THEN 'high' ELSE 'medium' END,
               CONCAT (N'Bulk export spike (', COUNT (DISTINCT CASE WHEN e.entityType = 'structuredLogExport' AND e.entityId IS NOT NULL THEN CAST (e.entityId AS VARCHAR (36)) ELSE CAST (e.auditEventId AS VARCHAR (20)) END), N' exports, ', SUM (COALESCE (e.recordCount, 0)), N' rows)'),
               (SELECT COUNT (DISTINCT CASE WHEN e.entityType = 'structuredLogExport' AND e.entityId IS NOT NULL THEN CAST (e.entityId AS VARCHAR (36)) ELSE CAST (e.auditEventId AS VARCHAR (20)) END) AS exportCount, SUM (COALESCE (e.recordCount, 0)) AS totalRows FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
               CONCAT ('bulkExportSpike|', CONVERT (VARCHAR (36), COALESCE (e.rootTenantId, @platformRoot)), '|', COALESCE (CONVERT (VARCHAR (36), e.appUserId), 'none'), '|', @dayBucket)
          FROM logs.auditEvent AS e
         WHERE e.eventUtc >= @since AND e.eventAction = 'export'
         GROUP BY COALESCE (e.rootTenantId, @platformRoot), e.appUserId
        HAVING COUNT (DISTINCT CASE WHEN e.entityType = 'structuredLogExport' AND e.entityId IS NOT NULL THEN CAST (e.entityId AS VARCHAR (36)) ELSE CAST (e.auditEventId AS VARCHAR (20)) END) >= @exportSpikeThreshold
            OR SUM (COALESCE (e.recordCount, 0)) >= @exportRowsThreshold;

        -- Rule 3: probingDeniedOrOutsideScope (handles NULL rootTenantId safely under platform-only scope, B1 / Codex #3)
        INSERT @candidates
        SELECT COALESCE (e.rootTenantId, @platformRoot),
               CASE WHEN SUM (CASE WHEN COALESCE (e.owningTenantId, e.actingTenantId) IS NULL THEN 1 ELSE 0 END) = 0 AND COUNT (DISTINCT COALESCE (e.owningTenantId, e.actingTenantId)) = 1 AND COUNT (DISTINCT e.actingTenantId) <= 1 THEN MAX (COALESCE (e.owningTenantId, e.actingTenantId)) END,
               e.appUserId, MAX (e.clientAddress), NULL,
               'probingDeniedOrOutsideScope', CASE WHEN COUNT (*) >= @probingThreshold * 2 THEN 'high' ELSE 'medium' END,
               CONCAT (N'Repeated permission-denied or outside-scope probing (', COUNT (*), N' events)'),
               (SELECT COUNT (*) AS events FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
               CONCAT ('probingDeniedOrOutsideScope|', CONVERT (VARCHAR (36), COALESCE (e.rootTenantId, @platformRoot)), '|', COALESCE (CONVERT (VARCHAR (36), e.appUserId), 'none'), '|', @dayBucket)
          FROM logs.auditEvent AS e
         WHERE e.eventUtc >= @since AND (e.eventAction = 'permissionDenied' OR e.entityType = 'personOutsideScope')
         GROUP BY COALESCE (e.rootTenantId, @platformRoot), e.appUserId
        HAVING COUNT (*) >= @probingThreshold;

        -- Rule 4: searchVelocitySpike (county-owned ONLY when every contributing row has the same non-NULL actingTenantId, Codex #3)
        INSERT @candidates
        SELECT COALESCE (s.rootTenantId, @platformRoot),
               CASE WHEN SUM (CASE WHEN s.actingTenantId IS NULL THEN 1 ELSE 0 END) = 0 AND COUNT (DISTINCT s.actingTenantId) = 1 THEN MAX (s.actingTenantId) END,
               s.appUserId, MAX (s.clientAddress), NULL,
               'searchVelocitySpike', CASE WHEN COUNT (*) >= @searchVelocityThreshold * 2 THEN 'high' ELSE 'medium' END,
               CONCAT (N'High-velocity searching (', COUNT (*), N' queries)'),
               (SELECT COUNT (*) AS searches, SUM (CASE WHEN s.resultCount = 0 THEN 1 ELSE 0 END) AS zeroResults FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
               CONCAT ('searchVelocitySpike|', CONVERT (VARCHAR (36), COALESCE (s.rootTenantId, @platformRoot)), '|', CONVERT (VARCHAR (36), s.appUserId), '|', @dayBucket)
          FROM logs.searchLog AS s
         WHERE s.isDeleted = 0 AND s.searchedUtc >= @since
         GROUP BY COALESCE (s.rootTenantId, @platformRoot), s.appUserId
        HAVING COUNT (*) >= @searchVelocityThreshold;

        -- Rule 5: midSessionIpDrift (source-event ownership, Codex #1/#3; network prefix comparison, Claude I5 / I11)
        INSERT @candidates
        SELECT COALESCE (e.rootTenantId, @platformRoot),
               CASE WHEN SUM (CASE WHEN COALESCE (e.owningTenantId, e.actingTenantId) IS NULL THEN 1 ELSE 0 END) = 0 AND COUNT (DISTINCT COALESCE (e.owningTenantId, e.actingTenantId)) = 1 AND COUNT (DISTINCT e.actingTenantId) <= 1 THEN MAX (COALESCE (e.owningTenantId, e.actingTenantId)) END,
               e.appUserId, NULL, e.sessionReference,
               'midSessionIpDrift', CASE WHEN COUNT (DISTINCT CASE
            WHEN CHARINDEX ('.', e.clientAddress) > 0
                 THEN LEFT (e.clientAddress, LEN (e.clientAddress) - CHARINDEX ('.', REVERSE (e.clientAddress)))
            WHEN CHARINDEX (':', e.clientAddress) > 0
                 THEN COALESCE (
                      LEFT (REPLACE (CASE WHEN LEFT (e.clientAddress, 2) = '::' THEN '0' + e.clientAddress ELSE e.clientAddress END, '::', ':0:0:0:'),
                            NULLIF (CHARINDEX (':', REPLACE (CASE WHEN LEFT (e.clientAddress, 2) = '::' THEN '0' + e.clientAddress ELSE e.clientAddress END, '::', ':0:0:0:'),
                            NULLIF (CHARINDEX (':', REPLACE (CASE WHEN LEFT (e.clientAddress, 2) = '::' THEN '0' + e.clientAddress ELSE e.clientAddress END, '::', ':0:0:0:'),
                            NULLIF (CHARINDEX (':', REPLACE (CASE WHEN LEFT (e.clientAddress, 2) = '::' THEN '0' + e.clientAddress ELSE e.clientAddress END, '::', ':0:0:0:')), 0) + 1), 0) + 1), 0) - 1),
                      e.clientAddress)
            ELSE e.clientAddress
        END) >= 3 THEN 'high' ELSE 'medium' END,
               CONCAT (N'Mid-session network change (', COUNT (DISTINCT CASE
            WHEN CHARINDEX ('.', e.clientAddress) > 0
                 THEN LEFT (e.clientAddress, LEN (e.clientAddress) - CHARINDEX ('.', REVERSE (e.clientAddress)))
            WHEN CHARINDEX (':', e.clientAddress) > 0
                 THEN COALESCE (
                      LEFT (REPLACE (CASE WHEN LEFT (e.clientAddress, 2) = '::' THEN '0' + e.clientAddress ELSE e.clientAddress END, '::', ':0:0:0:'),
                            NULLIF (CHARINDEX (':', REPLACE (CASE WHEN LEFT (e.clientAddress, 2) = '::' THEN '0' + e.clientAddress ELSE e.clientAddress END, '::', ':0:0:0:'),
                            NULLIF (CHARINDEX (':', REPLACE (CASE WHEN LEFT (e.clientAddress, 2) = '::' THEN '0' + e.clientAddress ELSE e.clientAddress END, '::', ':0:0:0:'),
                            NULLIF (CHARINDEX (':', REPLACE (CASE WHEN LEFT (e.clientAddress, 2) = '::' THEN '0' + e.clientAddress ELSE e.clientAddress END, '::', ':0:0:0:')), 0) + 1), 0) + 1), 0) - 1),
                      e.clientAddress)
            ELSE e.clientAddress
        END), N' distinct network prefixes)'),
               (SELECT COUNT (DISTINCT CASE
            WHEN CHARINDEX ('.', e.clientAddress) > 0
                 THEN LEFT (e.clientAddress, LEN (e.clientAddress) - CHARINDEX ('.', REVERSE (e.clientAddress)))
            WHEN CHARINDEX (':', e.clientAddress) > 0
                 THEN COALESCE (
                      LEFT (REPLACE (CASE WHEN LEFT (e.clientAddress, 2) = '::' THEN '0' + e.clientAddress ELSE e.clientAddress END, '::', ':0:0:0:'),
                            NULLIF (CHARINDEX (':', REPLACE (CASE WHEN LEFT (e.clientAddress, 2) = '::' THEN '0' + e.clientAddress ELSE e.clientAddress END, '::', ':0:0:0:'),
                            NULLIF (CHARINDEX (':', REPLACE (CASE WHEN LEFT (e.clientAddress, 2) = '::' THEN '0' + e.clientAddress ELSE e.clientAddress END, '::', ':0:0:0:'),
                            NULLIF (CHARINDEX (':', REPLACE (CASE WHEN LEFT (e.clientAddress, 2) = '::' THEN '0' + e.clientAddress ELSE e.clientAddress END, '::', ':0:0:0:')), 0) + 1), 0) + 1), 0) - 1),
                      e.clientAddress)
            ELSE e.clientAddress
        END) AS distinctPrefixes, COUNT (DISTINCT e.clientAddress) AS distinctAddresses, COUNT (*) AS events FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
               CONCAT ('midSessionIpDrift|', CONVERT (VARCHAR (36), e.sessionReference))
          FROM logs.auditEvent AS e
         WHERE e.eventUtc >= @since AND e.sessionReference IS NOT NULL AND e.clientAddress IS NOT NULL
         GROUP BY COALESCE (e.rootTenantId, @platformRoot), e.appUserId, e.sessionReference
        HAVING COUNT (DISTINCT CASE
            WHEN CHARINDEX ('.', e.clientAddress) > 0
                 THEN LEFT (e.clientAddress, LEN (e.clientAddress) - CHARINDEX ('.', REVERSE (e.clientAddress)))
            WHEN CHARINDEX (':', e.clientAddress) > 0
                 THEN COALESCE (
                      LEFT (REPLACE (CASE WHEN LEFT (e.clientAddress, 2) = '::' THEN '0' + e.clientAddress ELSE e.clientAddress END, '::', ':0:0:0:'),
                            NULLIF (CHARINDEX (':', REPLACE (CASE WHEN LEFT (e.clientAddress, 2) = '::' THEN '0' + e.clientAddress ELSE e.clientAddress END, '::', ':0:0:0:'),
                            NULLIF (CHARINDEX (':', REPLACE (CASE WHEN LEFT (e.clientAddress, 2) = '::' THEN '0' + e.clientAddress ELSE e.clientAddress END, '::', ':0:0:0:'),
                            NULLIF (CHARINDEX (':', REPLACE (CASE WHEN LEFT (e.clientAddress, 2) = '::' THEN '0' + e.clientAddress ELSE e.clientAddress END, '::', ':0:0:0:')), 0) + 1), 0) + 1), 0) - 1),
                      e.clientAddress)
            ELSE e.clientAddress
        END) > 1;

        -- Rule 6: automatedUserAgent (Codex #6 parity)
        INSERT @candidates
        SELECT COALESCE (e.rootTenantId, @platformRoot), NULL, e.appUserId, MAX (e.clientAddress), MAX (e.sessionReference),
               'automatedUserAgent', 'medium',
               CONCAT (N'Automated or headless client user-agent detected (', COUNT (*), N' sign-in events)'),
               (SELECT COUNT (*) AS events, LEFT (MAX (e.userAgent), 120) AS sampleUserAgent FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
               CONCAT ('automatedUserAgent|', CONVERT (VARCHAR (36), COALESCE (e.rootTenantId, @platformRoot)), '|', COALESCE (CONVERT (VARCHAR (36), e.appUserId), 'none'), '|', @dayBucket)
          FROM logs.signInEvent AS e
         WHERE e.isDeleted = 0 AND e.eventUtc >= @since AND e.userAgent IS NOT NULL
           AND (   e.userAgent LIKE N'%curl/%'
             OR e.userAgent LIKE N'%python-requests%'
             OR e.userAgent LIKE N'%HeadlessChrome%'
             OR e.userAgent LIKE N'%Playwright%'
             OR e.userAgent LIKE N'%Selenium%'
             OR e.userAgent LIKE N'%Scrapy%'
             OR e.userAgent LIKE N'%wget%'
             OR e.userAgent LIKE N'%Go-http-client%')
         GROUP BY COALESCE (e.rootTenantId, @platformRoot), e.appUserId;

        -- Rule 7: privilegedActionBurst (source-event ownership, Codex #1/#3)
        INSERT @candidates
        SELECT COALESCE (e.rootTenantId, @platformRoot),
               CASE WHEN SUM (CASE WHEN COALESCE (e.owningTenantId, e.actingTenantId) IS NULL THEN 1 ELSE 0 END) = 0 AND COUNT (DISTINCT COALESCE (e.owningTenantId, e.actingTenantId)) = 1 AND COUNT (DISTINCT e.actingTenantId) <= 1 THEN MAX (COALESCE (e.owningTenantId, e.actingTenantId)) END,
               e.appUserId, MAX (e.clientAddress), NULL,
               'privilegedActionBurst', 'high',
               CONCAT (N'Privileged security/configuration burst (', COUNT (*), N' actions)'),
               (SELECT COUNT (*) AS actions, @privBurstThreshold AS threshold FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
               CONCAT ('privilegedActionBurst|', CONVERT (VARCHAR (36), COALESCE (e.rootTenantId, @platformRoot)), '|', COALESCE (CONVERT (VARCHAR (36), e.appUserId), 'none'), '|', @dayBucket)
          FROM logs.auditEvent AS e
         WHERE e.eventUtc >= @since
           AND (   e.eventAction IN ('grant', 'revoke', 'deactivate', 'mfaResetByAdmin', 'passwordSetByAdmin')
                OR (e.entityType IN ('threshold', 'jurisdictionSetting', 'userAssignment', 'role') AND e.eventAction IN ('create', 'update', 'delete')))
         GROUP BY COALESCE (e.rootTenantId, @platformRoot), e.appUserId
        HAVING COUNT (*) >= @privBurstThreshold;

        -- Rule 8: mfaOrResetAbuse (Claude I2 canonical event types)
        INSERT @candidates
        SELECT COALESCE (e.rootTenantId, @platformRoot), NULL, e.appUserId, MAX (e.clientAddress), NULL,
               'mfaOrResetAbuse', 'high',
               CONCAT (N'Repeated MFA or password-reset attempts (', COUNT (*), N' events)'),
               (SELECT COUNT (*) AS events, COUNT (DISTINCT e.clientAddress) AS distinctAddresses FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
               CONCAT ('mfaOrResetAbuse|', CONVERT (VARCHAR (36), COALESCE (e.rootTenantId, @platformRoot)), '|', COALESCE (CONVERT (VARCHAR (36), e.appUserId), 'none'), '|', @dayBucket)
          FROM logs.signInEvent AS e
         WHERE e.isDeleted = 0 AND e.eventUtc >= @since AND e.eventType IN ('mfaFailed', 'emailCodeSent', 'emailCodeRefused', 'passwordResetRequested', 'recoveryCodeUsed')
         GROUP BY COALESCE (e.rootTenantId, @platformRoot), e.appUserId
        HAVING COUNT (*) >= @mfaAbuseThreshold;

        -- Rule 9: distributedLowAndSlowFailures (Codex #6 parity)
        INSERT @candidates
        SELECT COALESCE (e.rootTenantId, @platformRoot), NULL, e.appUserId, NULL, NULL,
               'distributedLowAndSlowFailures', 'medium',
               CONCAT (N'Distributed low-and-slow sign-in failures (', COUNT (*), N' failures across ', COUNT (DISTINCT e.clientAddress), N' addresses)'),
               (SELECT COUNT (*) AS failures, COUNT (DISTINCT e.clientAddress) AS distinctAddresses, @distributedFailThreshold AS threshold FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
               CONCAT ('distributedLowAndSlowFailures|', CONVERT (VARCHAR (36), COALESCE (e.rootTenantId, @platformRoot)), '|', COALESCE (CONVERT (VARCHAR (36), e.appUserId), 'none'), '|', @dayBucket)
          FROM logs.signInEvent AS e
         WHERE e.isDeleted = 0 AND e.eventUtc >= @since AND e.eventType IN ('passwordFailed', 'unknownUser', 'mfaFailed', 'attemptWhileLocked', 'accountInactive')
         GROUP BY COALESCE (e.rootTenantId, @platformRoot), e.appUserId
        HAVING COUNT (*) >= @distributedFailThreshold AND COUNT (DISTINCT e.clientAddress) >= 4;

        -- Rule 10: apiOnlyNoScreenVisits (Codex #3 logical event count + #2 source ownership)
        INSERT @candidates
        SELECT COALESCE (e.rootTenantId, @platformRoot),
               CASE WHEN SUM (CASE WHEN COALESCE (e.owningTenantId, e.actingTenantId) IS NULL THEN 1 ELSE 0 END) = 0 AND COUNT (DISTINCT COALESCE (e.owningTenantId, e.actingTenantId)) = 1 AND COUNT (DISTINCT e.actingTenantId) <= 1 THEN MAX (COALESCE (e.owningTenantId, e.actingTenantId)) END,
               e.appUserId, MAX (e.clientAddress), e.sessionReference,
               'apiOnlyNoScreenVisits', 'medium',
               CONCAT (N'Data-access session with zero UI screen visits (', COUNT (DISTINCT CASE WHEN e.entityType = 'structuredLogExport' AND e.entityId IS NOT NULL THEN CAST (e.entityId AS VARCHAR (36)) ELSE CAST (e.auditEventId AS VARCHAR (20)) END), N' reads)'),
               (SELECT COUNT (DISTINCT CASE WHEN e.entityType = 'structuredLogExport' AND e.entityId IS NOT NULL THEN CAST (e.entityId AS VARCHAR (36)) ELSE CAST (e.auditEventId AS VARCHAR (20)) END) AS readEvents, COUNT (DISTINCT e.entityId) AS distinctEntities FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
               CONCAT ('apiOnlyNoScreenVisits|', CONVERT (VARCHAR (36), e.sessionReference))
          FROM logs.auditEvent AS e
         WHERE e.eventUtc >= @since AND e.sessionReference IS NOT NULL
           AND e.eventAction IN ('view', 'list', 'export')
           AND NOT EXISTS (SELECT 1 FROM logs.screenVisit AS v
                            WHERE v.sessionReference = e.sessionReference AND v.isDeleted = 0)
         GROUP BY COALESCE (e.rootTenantId, @platformRoot), e.appUserId, e.sessionReference
        HAVING COUNT (DISTINCT CASE WHEN e.entityType = 'structuredLogExport' AND e.entityId IS NOT NULL THEN CAST (e.entityId AS VARCHAR (36)) ELSE CAST (e.auditEventId AS VARCHAR (20)) END) >= 25;

        -- Deduplicate candidates by fingerprint within this scan batch before inserting against the unique index.
        DECLARE @deduped TABLE (
            rootTenantId     UNIQUEIDENTIFIER NOT NULL,
            owningTenantId   UNIQUEIDENTIFIER NULL,
            appUserId        UNIQUEIDENTIFIER NULL,
            clientAddress    VARCHAR (45) NULL,
            sessionReference UNIQUEIDENTIFIER NULL,
            ruleCode         VARCHAR (50) NOT NULL,
            severity         VARCHAR (15) NOT NULL,
            title            NVARCHAR (200) NOT NULL,
            detailJson       NVARCHAR (2000) NULL,
            fingerprint      VARCHAR (128) NOT NULL PRIMARY KEY
        );
        -- Codex #2: Include effective ownership scope ('root' vs county GUID) in the deduplication fingerprint so a prior
        -- county-level disposition (including falsePositive) can never suppress a newly root-scoped finding.
        INSERT @deduped
        SELECT rootTenantId, owningTenantId, appUserId, clientAddress, sessionReference, ruleCode, severity, title, detailJson, scopedFingerprint
          FROM (SELECT *,
                       LEFT (CONCAT (fingerprint, '|', COALESCE (CONVERT (VARCHAR (36), owningTenantId), 'root')), 128) AS scopedFingerprint,
                       ROW_NUMBER () OVER (PARTITION BY LEFT (CONCAT (fingerprint, '|', COALESCE (CONVERT (VARCHAR (36), owningTenantId), 'root')), 128)
                                               ORDER BY CASE severity WHEN 'critical' THEN 1 WHEN 'high' THEN 2 WHEN 'medium' THEN 3 ELSE 4 END) AS rn
                  FROM @candidates) AS x
         WHERE rn = 1;

        DECLARE @inserted TABLE (
            securityFindingId UNIQUEIDENTIFIER,
            rootTenantId      UNIQUEIDENTIFIER,
            appUserId         UNIQUEIDENTIFIER NULL,
            ruleCode          VARCHAR (50),
            severity          VARCHAR (15),
            title             NVARCHAR (200),
            detailJson        NVARCHAR (2000),
            detectedUtc       DATETIME2 (3)
        );

        -- Codex #6 / Claude S8: Persist upward severity escalations on active non-falsePositive findings, reopening resolved ones.
        UPDATE f
           SET f.severity     = c.severity
             , f.title        = c.title
             , f.detailJson   = c.detailJson
             , f.windowEndUtc = @now
             , f.status       = CASE WHEN f.status = 'resolved' THEN 'open' ELSE f.status END
             , f.acknowledgedByUserId = CASE WHEN f.status = 'resolved' THEN NULL ELSE f.acknowledgedByUserId END
             , f.acknowledgedUtc      = CASE WHEN f.status = 'resolved' THEN NULL ELSE f.acknowledgedUtc END
             , f.owningTenantId = CASE WHEN f.owningTenantId = c.owningTenantId THEN f.owningTenantId ELSE NULL END
        OUTPUT INSERTED.securityFindingId, INSERTED.rootTenantId, INSERTED.appUserId, INSERTED.ruleCode,
               INSERTED.severity, INSERTED.title, INSERTED.detailJson, INSERTED.detectedUtc
          INTO @inserted
          FROM logs.securityFinding AS f
          JOIN @deduped AS c ON c.fingerprint = f.fingerprint
         WHERE f.isDeleted = 0 AND f.status <> 'falsePositive'
           AND (CASE c.severity WHEN 'critical' THEN 4 WHEN 'high' THEN 3 WHEN 'medium' THEN 2 ELSE 1 END)
               > (CASE f.severity WHEN 'critical' THEN 4 WHEN 'high' THEN 3 WHEN 'medium' THEN 2 ELSE 1 END);

        INSERT logs.securityFinding (securityFindingId, rootTenantId, owningTenantId, appUserId, clientAddress, sessionReference,
                                     ruleCode, severity, title, detailJson, windowStartUtc, windowEndUtc, detectedUtc, status, fingerprint)
        OUTPUT INSERTED.securityFindingId, INSERTED.rootTenantId, INSERTED.appUserId, INSERTED.ruleCode,
               INSERTED.severity, INSERTED.title, INSERTED.detailJson, INSERTED.detectedUtc
          INTO @inserted
        SELECT CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), c.rootTenantId, c.owningTenantId, c.appUserId, c.clientAddress, c.sessionReference,
               c.ruleCode, c.severity, c.title, c.detailJson, @since, @now, @now, 'open', c.fingerprint
          FROM @deduped AS c
         WHERE NOT EXISTS (SELECT 1 FROM logs.securityFinding AS existing
                            WHERE existing.fingerprint = c.fingerprint AND existing.isDeleted = 0);

        SELECT securityFindingId, rootTenantId, appUserId, ruleCode, severity, title, detailJson, detectedUtc
          FROM @inserted
         ORDER BY detectedUtc DESC;

        SET @Comments = CONCAT (N'Persisted/escalated ', (SELECT COUNT (*) FROM @inserted), N' security finding(s).');
        END;

        -- =========================================================================================
        -- ===== End of the procedure's own work. ===================================================
        -- =========================================================================================

        IF @@TRANCOUNT > 0
        BEGIN
            COMMIT TRANSACTION;
        END;

        -- Completion. Deliberately after the COMMIT.
        SET @EndTimeUtc = SYSUTCDATETIME ();

        IF @ExecutionId IS NOT NULL
        BEGIN
            UPDATE logs.ExecutionLog
               SET EndDateUtc          = @EndTimeUtc
                 , ElapsedMilliseconds = CAST (LEAST (DATEDIFF_BIG (MILLISECOND, @StartTimeUtc, @EndTimeUtc)
                                                    , CAST (2147483647 AS BIGINT)) AS INT)
                 , Successful          = 1
                 , Comments            = LEFT (@Comments, 500)
                 , modifiedBy          = COALESCE (NULLIF (CAST (SESSION_CONTEXT (N'AppUser') AS NVARCHAR (255)), N''), ORIGINAL_LOGIN ())
                 , modifiedDateUtc     = @EndTimeUtc
             WHERE ExecutionLogId = @ExecutionId;
        END;

    END TRY
    BEGIN CATCH

        SELECT @ErrorNumber = ERROR_NUMBER ()
             , @ErrorProc   = ERROR_PROCEDURE ()
             , @ErrorLine   = ERROR_LINE ()
             , @ErrorMsg    = ERROR_MESSAGE ()
                            + N' (error '  + CAST (ERROR_NUMBER () AS NVARCHAR (11))
                            + N', line '   + CAST (ERROR_LINE ()   AS NVARCHAR (11)) + N')';

        IF XACT_STATE () <> 0
        BEGIN
            ROLLBACK TRANSACTION;
        END;

        BEGIN TRY
            IF @ExecutionId IS NULL
               OR NOT EXISTS (SELECT 1
                                FROM logs.ExecutionLog
                               WHERE ExecutionLogId = @ExecutionId)
            BEGIN
                EXEC logs.uspStartExecutionLogging
                      @ProcedureName          = @ProcName
                    , @KeyParameters          = @KeyParameters
                    , @StartDateUtc           = @StartTimeUtc
                    , @ReCreatedAfterRollback = 1
                    , @ExecutionLogId         = @ExecutionId OUTPUT;
            END;
        END TRY
        BEGIN CATCH
            SET @ExecutionId = NULL;
        END CATCH;

        EXEC logs.uspRecordExecutionError
              @ProcedureName   = @ProcName
            , @KeyParameters   = @KeyParameters
            , @ExecutionLogId  = @ExecutionId
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
    , @ObjectName  = N'uspRunSecurityFindingScan'
    , @Description = N'Scheduled worker scan evaluating all 10 behavioral anomaly rules across active tenants (and platform-scoped unattributed events), serialized via sp_getapplock, deduplicated by unique fingerprint (#327).';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspRunSecurityFindingScan TO meritWorkerRole;
END;
GO
