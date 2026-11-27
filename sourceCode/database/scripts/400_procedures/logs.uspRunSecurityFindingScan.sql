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

Scheduled worker scan that evaluates behavioral anomaly rules across all active tenants, deduplicates by fingerprint within the window, persists new findings into logs.securityFinding, and returns newly inserted rows (#327).

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

        DECLARE @winHours INT = CASE WHEN @hours BETWEEN 1 AND 168 THEN @hours ELSE 24 END;
        DECLARE @now DATETIME2 (3) = SYSUTCDATETIME ();
        DECLARE @since DATETIME2 (3) = DATEADD (HOUR, -@winHours, @now);
        DECLARE @dayBucket VARCHAR (10) = CONVERT (VARCHAR (10), @now, 23);

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

        INSERT @candidates
        SELECT e.rootTenantId, MAX (e.actingTenantId), e.appUserId, NULL, e.sessionReference,
               'bulkViewVelocity', CASE WHEN COUNT (DISTINCT e.entityId) >= @viewVelocityThreshold * 2 THEN 'high' ELSE 'medium' END,
               CONCAT (N'High distinct record view velocity (', COUNT (DISTINCT e.entityId), N' records in ', @winHours, N'h)'),
               (SELECT COUNT (DISTINCT e.entityId) AS distinctEntities, COUNT (*) AS totalViews FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
               CONCAT ('bulkViewVelocity|', COALESCE (CONVERT (VARCHAR (36), e.appUserId), 'none'), '|', COALESCE (CONVERT (VARCHAR (36), e.sessionReference), @dayBucket))
          FROM logs.auditEvent AS e
         WHERE e.eventUtc >= @since AND e.eventAction = 'view' AND e.entityId IS NOT NULL
         GROUP BY e.rootTenantId, e.appUserId, e.sessionReference
        HAVING COUNT (DISTINCT e.entityId) >= @viewVelocityThreshold;

        INSERT @candidates
        SELECT e.rootTenantId, MAX (e.actingTenantId), e.appUserId, NULL, NULL,
               'bulkExportSpike', CASE WHEN SUM (COALESCE (e.recordCount, 1)) >= @exportRowsThreshold * 2 THEN 'high' ELSE 'medium' END,
               CONCAT (N'Bulk export spike (', COUNT (*), N' exports, ', SUM (COALESCE (e.recordCount, 0)), N' rows)'),
               (SELECT COUNT (*) AS exportCount, SUM (COALESCE (e.recordCount, 0)) AS totalRows FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
               CONCAT ('bulkExportSpike|', COALESCE (CONVERT (VARCHAR (36), e.appUserId), 'none'), '|', @dayBucket)
          FROM logs.auditEvent AS e
         WHERE e.eventUtc >= @since AND e.eventAction = 'export'
         GROUP BY e.rootTenantId, e.appUserId
        HAVING COUNT (*) >= @exportSpikeThreshold OR SUM (COALESCE (e.recordCount, 0)) >= @exportRowsThreshold;

        INSERT @candidates
        SELECT e.rootTenantId, MAX (e.actingTenantId), e.appUserId, MAX (e.clientAddress), NULL,
               'probingDeniedOrOutsideScope', CASE WHEN COUNT (*) >= @probingThreshold * 2 THEN 'high' ELSE 'medium' END,
               CONCAT (N'Repeated permission-denied or outside-scope probing (', COUNT (*), N' events)'),
               (SELECT COUNT (*) AS events FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
               CONCAT ('probingDeniedOrOutsideScope|', COALESCE (CONVERT (VARCHAR (36), e.appUserId), 'none'), '|', @dayBucket)
          FROM logs.auditEvent AS e
         WHERE e.eventUtc >= @since AND (e.eventAction = 'permissionDenied' OR e.entityType = 'personOutsideScope')
         GROUP BY e.rootTenantId, e.appUserId
        HAVING COUNT (*) >= @probingThreshold;

        INSERT @candidates
        SELECT e.rootTenantId, MAX (e.actingTenantId), e.appUserId, NULL, e.sessionReference,
               'midSessionIpDrift', 'high',
               CONCAT (N'Mid-session IP address change (', COUNT (DISTINCT e.clientAddress), N' addresses)'),
               (SELECT COUNT (DISTINCT e.clientAddress) AS distinctAddresses, COUNT (*) AS events FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
               CONCAT ('midSessionIpDrift|', CONVERT (VARCHAR (36), e.sessionReference))
          FROM logs.auditEvent AS e
         WHERE e.eventUtc >= @since AND e.sessionReference IS NOT NULL AND e.clientAddress IS NOT NULL
         GROUP BY e.rootTenantId, e.appUserId, e.sessionReference
        HAVING COUNT (DISTINCT e.clientAddress) > 1;

        INSERT @candidates
        SELECT s.rootTenantId, MAX (s.actingTenantId), s.appUserId, MAX (s.clientAddress), NULL,
               'searchVelocitySpike', CASE WHEN COUNT (*) >= @searchVelocityThreshold * 2 THEN 'high' ELSE 'medium' END,
               CONCAT (N'High-velocity searching (', COUNT (*), N' queries)'),
               (SELECT COUNT (*) AS searches, SUM (CASE WHEN s.resultCount = 0 THEN 1 ELSE 0 END) AS zeroResults FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
               CONCAT ('searchVelocitySpike|', CONVERT (VARCHAR (36), s.appUserId), '|', @dayBucket)
          FROM logs.searchLog AS s
         WHERE s.isDeleted = 0 AND s.searchedUtc >= @since
         GROUP BY s.rootTenantId, s.appUserId
        HAVING COUNT (*) >= @searchVelocityThreshold;

        INSERT @candidates
        SELECT COALESCE (e.rootTenantId, '00000000-0000-0000-0000-000000000000'), NULL, e.appUserId, MAX (e.clientAddress), NULL,
               'mfaOrResetAbuse', 'high',
               CONCAT (N'Repeated MFA or password-reset attempts (', COUNT (*), N' events)'),
               (SELECT COUNT (*) AS events, COUNT (DISTINCT e.clientAddress) AS distinctAddresses FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
               CONCAT ('mfaOrResetAbuse|', COALESCE (CONVERT (VARCHAR (36), e.appUserId), 'none'), '|', @dayBucket)
          FROM logs.signInEvent AS e
         WHERE e.isDeleted = 0 AND e.eventUtc >= @since AND e.eventType IN ('mfaFailed', 'mfaEmailCodeSent', 'passwordResetRequested')
         GROUP BY e.rootTenantId, e.appUserId
        HAVING COUNT (*) >= @mfaAbuseThreshold;

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

        INSERT logs.securityFinding (securityFindingId, rootTenantId, owningTenantId, appUserId, clientAddress, sessionReference,
                                     ruleCode, severity, title, detailJson, windowStartUtc, windowEndUtc, detectedUtc, status, fingerprint)
        OUTPUT INSERTED.securityFindingId, INSERTED.rootTenantId, INSERTED.appUserId, INSERTED.ruleCode,
               INSERTED.severity, INSERTED.title, INSERTED.detailJson, INSERTED.detectedUtc
          INTO @inserted
        SELECT CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), c.rootTenantId, c.owningTenantId, c.appUserId, c.clientAddress, c.sessionReference,
               c.ruleCode, c.severity, c.title, c.detailJson, @since, @now, @now, 'open', c.fingerprint
          FROM @candidates AS c
         WHERE NOT EXISTS (SELECT 1 FROM logs.securityFinding AS existing
                            WHERE existing.fingerprint = c.fingerprint AND existing.isDeleted = 0);

        SELECT securityFindingId, rootTenantId, appUserId, ruleCode, severity, title, detailJson, detectedUtc
          FROM @inserted
         ORDER BY detectedUtc DESC;

        SET @Comments = CONCAT (N'Inserted ', (SELECT COUNT (*) FROM @inserted), N' new security finding(s).');

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
    , @Description = N'Scheduled worker scan that evaluates behavioral anomaly rules across all active tenants, deduplicates by fingerprint within the window, persists new findings into logs.securityFinding, and returns newly inserted rows (#327).';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspRunSecurityFindingScan TO meritWorkerRole;
END;
GO
