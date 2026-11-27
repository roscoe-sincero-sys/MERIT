-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   config.uspPurgeRootTenantInternal
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Ungranted owner-context helper for config.uspPurgeRootTenant: scrubs every PHI, free-text, binary, geocode, and credential column across all 70 mutable tenant/auth tables (including previously soft-deleted rows) in independently committed 5,000-row batches with post-purge verification (#346, B4, I16).

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

exec config.uspPurgeRootTenantInternal ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		346
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE config.uspPurgeRootTenantInternal
      @rootTenantId        UNIQUEIDENTIFIER
      , @backupRetentionDays INT = 35
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[config].[uspPurgeRootTenantInternal]')
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
    SET @KeyParameters = CONCAT (N'rootTenantId=', @rootTenantId, N', backupRetentionDays=', @backupRetentionDays);

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @report TABLE (
            tableName           NVARCHAR (128) NOT NULL,
            rowsAffected        INT NOT NULL,
            remainingUnscrubbed INT NOT NULL,
            disposition         VARCHAR (40) NOT NULL
        );
        DECLARE @n INT = 0, @batch INT = 0, @rem INT = 0;

        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) auth.mfaRecoveryCode
               SET codeHash = N'[purged]', usedUtc = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (appUserId IN (SELECT appUserId FROM auth.appUser WHERE rootTenantId = @rootTenantId))
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM auth.mfaRecoveryCode WHERE (appUserId IN (SELECT appUserId FROM auth.appUser WHERE rootTenantId = @rootTenantId)) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'auth.mfaRecoveryCode', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) auth.passwordReset
               SET tokenVerifier = CAST (0x00 AS VARBINARY (32)), consumedUtc = NULL, supersededUtc = NULL, deliveryUpdatedUtc = NULL, failureKind = NULL, smtpReplyCode = NULL, supportReference = 'PURGED', correlationId = NULL, clientAddress = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (appUserId IN (SELECT appUserId FROM auth.appUser WHERE rootTenantId = @rootTenantId))
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM auth.passwordReset WHERE (appUserId IN (SELECT appUserId FROM auth.appUser WHERE rootTenantId = @rootTenantId)) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'auth.passwordReset', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) auth.mfaEmailChallenge
               SET codeVerifier = CAST (0x00 AS VARBINARY (32)), consumedUtc = NULL, supersededUtc = NULL, deliveryUpdatedUtc = NULL, failureKind = NULL, smtpReplyCode = NULL, supportReference = 'PURGED', correlationId = NULL, clientAddress = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (appUserId IN (SELECT appUserId FROM auth.appUser WHERE rootTenantId = @rootTenantId))
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM auth.mfaEmailChallenge WHERE (appUserId IN (SELECT appUserId FROM auth.appUser WHERE rootTenantId = @rootTenantId)) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'auth.mfaEmailChallenge', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) auth.userProfileGrant
               SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (userProfileId IN (SELECT up.userProfileId FROM auth.userProfile AS up JOIN auth.appUser AS u ON u.appUserId = up.appUserId WHERE u.rootTenantId = @rootTenantId))
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM auth.userProfileGrant WHERE (userProfileId IN (SELECT up.userProfileId FROM auth.userProfile AS up JOIN auth.appUser AS u ON u.appUserId = up.appUserId WHERE u.rootTenantId = @rootTenantId)) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'auth.userProfileGrant', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) auth.userProfile
               SET profileName = N'[purged]', profileDescription = NULL, isActive = 0, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (appUserId IN (SELECT appUserId FROM auth.appUser WHERE rootTenantId = @rootTenantId))
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM auth.userProfile WHERE (appUserId IN (SELECT appUserId FROM auth.appUser WHERE rootTenantId = @rootTenantId)) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'auth.userProfile', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) auth.userAssignment
               SET endDate = NULL, revokedUtc = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (appUserId IN (SELECT appUserId FROM auth.appUser WHERE rootTenantId = @rootTenantId) OR tenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId))
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM auth.userAssignment WHERE (appUserId IN (SELECT appUserId FROM auth.appUser WHERE rootTenantId = @rootTenantId) OR tenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId)) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'auth.userAssignment', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) auth.sessionScopeTenant
               SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (sessionScopeId IN (SELECT sessionScopeId FROM auth.sessionScope WHERE rootTenantId = @rootTenantId))
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM auth.sessionScopeTenant WHERE (sessionScopeId IN (SELECT sessionScopeId FROM auth.sessionScope WHERE rootTenantId = @rootTenantId)) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'auth.sessionScopeTenant', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) auth.sessionScopePermission
               SET permissionCode = '[purged]', isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (sessionScopeId IN (SELECT sessionScopeId FROM auth.sessionScope WHERE rootTenantId = @rootTenantId))
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM auth.sessionScopePermission WHERE (sessionScopeId IN (SELECT sessionScopeId FROM auth.sessionScope WHERE rootTenantId = @rootTenantId)) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'auth.sessionScopePermission', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) auth.sessionScopeAdminTenant
               SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (sessionScopeId IN (SELECT sessionScopeId FROM auth.sessionScope WHERE rootTenantId = @rootTenantId))
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM auth.sessionScopeAdminTenant WHERE (sessionScopeId IN (SELECT sessionScopeId FROM auth.sessionScope WHERE rootTenantId = @rootTenantId)) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'auth.sessionScopeAdminTenant', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) auth.rolePermission
               SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (roleId IN (SELECT roleId FROM auth.role WHERE rootTenantId = @rootTenantId))
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM auth.rolePermission WHERE (roleId IN (SELECT roleId FROM auth.role WHERE rootTenantId = @rootTenantId)) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'auth.rolePermission', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) auth.tenantJurisdiction
               SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (tenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId))
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM auth.tenantJurisdiction WHERE (tenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId)) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'auth.tenantJurisdiction', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) auth.tenantClosure
               SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (ancestorTenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId) OR descendantTenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId))
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM auth.tenantClosure WHERE (ancestorTenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId) OR descendantTenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId)) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'auth.tenantClosure', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) logs.auditEventChange
               SET entityType = '[purged]', beforeJson = NULL, afterJson = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM logs.auditEventChange WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'logs.auditEventChange', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) logs.auditEventRecord
               SET entityType = '[purged]', entityId = '00000000-0000-0000-0000-000000000000', isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM logs.auditEventRecord WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'logs.auditEventRecord', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) logs.securityFinding
               SET appUserId = NULL, clientAddress = NULL, sessionReference = NULL, title = N'[purged]', detailJson = NULL, acknowledgedByUserId = NULL, acknowledgedUtc = NULL, fingerprint = CONCAT ('purged|', CONVERT (VARCHAR (36), securityFindingId)), isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM logs.securityFinding WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'logs.securityFinding', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) logs.searchLog
               SET searchText = N'', sessionReference = NULL, clientAddress = NULL, actingTenantId = NULL, existsElsewhere = NULL, searchSource = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM logs.searchLog WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'logs.searchLog', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) logs.screenVisit
               SET actingTenantId = NULL, appUserId = NULL, sessionReference = NULL, profileId = NULL, viewAsUserId = NULL, roleCodes = NULL, tabId = NULL, previousVisitId = NULL, durationMs = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM logs.screenVisit WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'logs.screenVisit', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) logs.signInEvent
               SET step = NULL, method = NULL, reason = NULL, appUserId = NULL, actorUserId = NULL, attemptedNameHash = NULL, attemptedNameProtected = NULL, clientAddress = NULL, forwardedFor = NULL, userAgent = NULL, sessionReference = NULL, attemptId = NULL, traceId = NULL, failedSignInCount = NULL, lockoutEndUtc = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM logs.signInEvent WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'logs.signInEvent', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.contactCorrection
               SET caregiverName = N'[purged]', proposedValue = N'[purged]', city = NULL, state = NULL, zip5 = NULL, unit = NULL, proposedAddressKey = NULL, sourceNote = N'[purged]', beforeJson = N'{}', afterJson = NULL, reviewNote = NULL, reviewedByUserId = NULL, reviewedUtc = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.contactCorrection WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.contactCorrection', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.caseResultLink
               SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.caseResultLink WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.caseResultLink', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.notificationObligation
               SET policySnapshotJson = N'{}', ruleName = N'[purged]', recipientKind = '[purged]', triggerEvent = '[purged]', timeZoneId = '[purged]', reviewReason = NULL, completedDate = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.notificationObligation WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.notificationObligation', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.personDocument
               SET title = N'[purged]', fileName = N'purged', contentType = '[purged]', category = '[purged]', source = N'[purged]', description = NULL, contentHash = CAST (0x00 AS BINARY (32)), content = 0x00, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.personDocument WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.personDocument', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.personActivity
               SET recipientLabel = NULL, noticeFormReference = NULL, note = NULL, voidReason = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.personActivity WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.personActivity', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.matchExclusion
               SET incomingKey = N'[purged]', decisionNote = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.matchExclusion WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.matchExclusion', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.extractRun
               SET recordCount = NULL, errorCount = NULL, validationReportJson = NULL, fileName = NULL, fileContent = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.extractRun WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.extractRun', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.jobQueue
               SET jobType = '[purged]', payloadJson = N'{}', claimedBy = NULL, claimedUtc = NULL, claimToken = NULL, completedUtc = NULL, lastError = NULL, resultJson = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.jobQueue WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.jobQueue', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.workTask
               SET queueCode = '[purged]', title = N'[purged]', detail = NULL, dueDate = NULL, completedUtc = NULL, completedByUserId = NULL, resolutionNote = NULL, testingPlanJson = NULL, workflowDefinitionId = NULL, workflowSnapshotJson = NULL, workflowOccurrenceKey = NULL, completionActivityId = NULL, resolutionKind = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.workTask WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.workTask', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.caseCluster
               SET leftDate = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.caseCluster WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.caseCluster', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.cluster
               SET clusterType = '[purged]', title = N'[purged]', notes = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.cluster WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.cluster', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.environmentalSample
               SET sampleType = '[purged]', roomOrComponent = NULL, surface = NULL, unit = N'[purged]', exceedsStandard = NULL, notes = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.environmentalSample WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.environmentalSample', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.investigationCase
               SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.investigationCase WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.investigationCase', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.investigation
               SET investigationNumber = '[purged]', scheduledDate = NULL, completedDate = NULL, findings = NULL, regulatoryAction = NULL, referralDate = NULL, referralOutcome = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.investigation WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.investigation', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.exposureSource
               SET sourceType = '[purged]', sourceDescription = NULL, startDate = NULL, endDate = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.exposureSource WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.exposureSource', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.caseActivity
               SET lifecycleKind = NULL, voidReason = NULL, activityType = '[purged]', outcome = NULL, note = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.caseActivity WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.caseActivity', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.exposureCase
               SET caseNumber = '[purged]', overrideReason = NULL, workflowTemplate = '[purged]', closedDate = NULL, closureReason = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.exposureCase WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.exposureCase', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.stagingHoldReason
               SET fieldName = NULL, reasonMessage = N'[purged]', reportedValue = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.stagingHoldReason WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.stagingHoldReason', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.stagingRow
               SET parsedJson = N'{}', correctedJson = NULL, rejectReason = NULL, assignedToUserId = NULL, heldSinceUtc = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.stagingRow WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.stagingRow', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.labResult
               SET collectedDate = '1900-01-01', analyzedDate = NULL, reportedDate = NULL, reportingFinalDateVerified = NULL, reportingVerifiedPolicyId = NULL, reportingVerifiedByUserId = NULL, reportingVerifiedUtc = NULL, correctionReason = NULL, correctionByUserId = NULL, correctionUtc = NULL, classificationSnapshotJson = NULL, valueReported = NULL, valueText = NULL, unitReported = N'[purged]', valueCanonical = NULL, unitCanonical = N'[purged]', sampleType = NULL, testMethod = NULL, deviceModel = NULL, deviceSerial = NULL, kitLot = NULL, speciation = NULL, creatinineValue = NULL, valueCreatinineAdj = NULL, reasonForTest = NULL, payerType = NULL, accessionNumber = NULL, loincReported = NULL, reportedSnapshotJson = N'{}', ageAtCollectionMonths = NULL, population = 'unknown', isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.labResult WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.labResult', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.intakeBatch
               SET fileName = NULL, rawContent = NULL, contentSha256 = NULL, departmentReceivedUtc = NULL, receiptEvidence = NULL, receiptTimeZone = NULL, pauseReason = NULL, submittedByUserId = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.intakeBatch WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.intakeBatch', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.intakeProfile
               SET profileName = N'[purged]', dateFormats = NULL, columnMapJson = N'{}', valueTranslationsJson = NULL, constantsJson = NULL, headerSignature = NULL, isActive = 0, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.intakeProfile WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.intakeProfile', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.provider
               SET npi = NULL, lastName = N'[purged]', firstName = NULL, credential = NULL, primaryTaxonomy = NULL, registryStatus = '[purged]', registryCheckedUtc = NULL, localOverridesJson = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.provider WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.provider', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.product
               SET brand = NULL, productName = N'[purged]', countryOfOrigin = NULL, productType = NULL, notes = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.product WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.product', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.employmentAnalyte
               SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.employmentAnalyte WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.employmentAnalyte', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.employment
               SET jobTitle = NULL, industryText = NULL, occupationCode = NULL, occupationCodeVersion = NULL, naicsOverride = NULL, metalTasks = NULL, ppeNotes = NULL, hoursPerWeek = NULL, startDate = NULL, endDate = NULL, source = '[purged]', verifiedDate = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.employment WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.employment', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.personLocation
               SET primaryReason = NULL, scheduleType = NULL, hoursPerWeek = NULL, scheduleNote = NULL, endDate = NULL, source = '[purged]', verifiedDate = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.personLocation WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.personLocation', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.locationOwner
               SET startDate = NULL, endDate = NULL, source = '[purged]', isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.locationOwner WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.locationOwner', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.organizationSite
               SET siteName = NULL, startDate = NULL, endDate = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.organizationSite WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.organizationSite', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.location
               SET country = 'XX', addressLine1 = NULL, unit = NULL, city = N'[purged]', state = NULL, zip5 = NULL, region = NULL, postalCode = NULL, countyFips = NULL, stdStatus = '[purged]', latitude = NULL, longitude = NULL, geocodeQuality = NULL, censusTract = NULL, censusVintage = NULL, yearBuilt = NULL, yearBuiltSource = NULL, housingType = NULL, tenure = NULL, addressKey = CONCAT (N'purged-', CAST (locationId AS NVARCHAR (36))), isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.location WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.location', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.organizationType
               SET organizationTypeCode = '[purged]', isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.organizationType WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.organizationType', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.organization
               SET organizationName = N'[purged]', organizationNameKey = N'[purged]', naicsCode = NULL, naicsVersion = NULL, npi = NULL, cliaNumber = NULL, contactName = NULL, phone = NULL, email = NULL, isActive = 0, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.organization WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.organization', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.personRelationship
               SET role = '[purged]', startDate = NULL, endDate = NULL, source = '[purged]', verifiedDate = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.personRelationship WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.personRelationship', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.pregnancyEpisode
               SET expectedEndDate = NULL, actualEndDate = NULL, outcome = NULL, source = '[purged]', isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.pregnancyEpisode WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.pregnancyEpisode', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.personIdentifier
               SET identifierType = '[purged]', identifierValue = N'[purged]', assigningFacility = NULL, source = '[purged]', isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.personIdentifier WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.personIdentifier', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.personContactMethod
               SET contactValue = N'[purged]', isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.personContactMethod WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.personContactMethod', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.personRace
               SET raceCode = '[purged]', isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.personRace WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.personRace', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.personAlias
               SET lastName = N'[purged]', firstName = NULL, middleName = NULL, lastNameKey = N'PURGED', firstNameKey = NULL, aliasType = '[purged]', source = '[purged]', sourceLabResultId = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.personAlias WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.personAlias', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.personMatchKey
               SET lastNameKey = N'PURGED', firstNameKey = NULL, dob = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.personMatchKey WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.personMatchKey', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.personAccess
               SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.personAccess WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.personAccess', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) dbo.person
               SET lastName = N'[purged]', firstName = NULL, middleName = NULL, lastNameKey = N'PURGED', firstNameKey = NULL, dob = NULL, sex = 'unknown', ethnicity = 'unknown', preferredLanguage = NULL, deceasedDate = NULL, legacyId = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM dbo.person WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'dbo.person', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) config.workflowDefinition
               SET name = N'[purged]', description = N'[purged]', minimumAgeMonths = NULL, maximumAgeMonths = NULL, minimumValue = NULL, maximumValue = NULL, evidenceFieldsJson = N'{}', assignmentRoleCode = NULL, dueInDays = NULL, recurrenceDays = NULL, timeZoneId = '[purged]', holidayDatesJson = N'{}', closureReason = '[purged]', effectiveTo = NULL, approvalReference = NULL, publishedByUserId = NULL, publishedUtc = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM config.workflowDefinition WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'config.workflowDefinition', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) config.notificationRule
               SET ruleName = N'[purged]', population = '[purged]', requiredFieldsCsv = NULL, minimumAgeMonths = NULL, maximumAgeMonths = NULL, maximumValue = NULL, timeZoneId = '[purged]', calendarName = N'[purged]', holidayDatesJson = N'{}', effectiveTo = NULL, approvalReference = N'[purged]', isActive = 0, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM config.notificationRule WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'config.notificationRule', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) config.environmentalStandard
               SET sampleType = '[purged]', surface = NULL, unit = N'[purged]', authority = NULL, effectiveTo = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM config.environmentalStandard WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'config.environmentalStandard', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) config.threshold
               SET ageMinMonths = NULL, ageMaxMonths = NULL, speciation = NULL, sampleType = NULL, dueIntervalDays = NULL, upperValue = NULL, unit = N'[purged]', effectiveTo = NULL, authority = NULL, isActive = 0, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM config.threshold WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'config.threshold', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) config.jurisdictionSetting
               SET settingKey = '[purged]', settingValue = N'[purged]', effectiveTo = NULL, settingDescription = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM config.jurisdictionSetting WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'config.jurisdictionSetting', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) config.subJurisdiction
               SET subJurisdictionName = N'[purged]', boundary = NULL, labelLatitude = NULL, labelLongitude = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM config.subJurisdiction WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'config.subJurisdiction', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) auth.sessionScopeRoot
               SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM auth.sessionScopeRoot WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'auth.sessionScopeRoot', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) auth.sessionScope
               SET actingTenantId = NULL, userProfileId = NULL, viewAsUserId = NULL, revokedUtc = COALESCE (revokedUtc, SYSUTCDATETIME ()), isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM auth.sessionScope WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'auth.sessionScope', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) auth.appUser
               SET userName = CONCAT (N'purged-', CAST (appUserId AS NVARCHAR (36))), displayName = N'[purged]', email = NULL, passwordHash = N'[purged]', securityStamp = '00000000-0000-0000-0000-000000000000', mfaSecretProtected = NULL, mfaEnabled = 0, failedSignInCount = 0, lockoutEndUtc = NULL, lastSignInUtc = NULL, isActive = 0, mfaEmailVerifiedUtc = NULL, passwordChangedUtc = NULL, lastTotpStep = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM auth.appUser WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'auth.appUser', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) auth.role
               SET roleName = N'[purged]', isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM auth.role WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'auth.role', @n, @rem, 'scrubbedAndSoftDeleted');
        SET @batch = 1; SET @n = 0;
        WHILE @batch > 0
        BEGIN
            BEGIN TRANSACTION;
            UPDATE TOP (5000) auth.tenant
               SET tenantName = N'[purged]', statePostalCode = NULL, timeZone = NULL, isActive = 0, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
             WHERE (rootTenantId = @rootTenantId)
               AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
            SET @batch = @@ROWCOUNT;
            COMMIT TRANSACTION;
            SET @n += @batch;
            IF @batch = 5000
            BEGIN
                WAITFOR DELAY '00:00:00.050';
            END;
        END;
        SELECT @rem = COUNT (*) FROM auth.tenant WHERE (rootTenantId = @rootTenantId) AND (isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge');
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'auth.tenant', @n, @rem, 'scrubbedAndSoftDeleted');
        DECLARE @ledgerRows INT = (SELECT COUNT (*) FROM logs.auditEvent WHERE rootTenantId = @rootTenantId);
        INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'logs.auditEvent', @ledgerRows, 0, 'retainedAppendOnlyLedger');

        SELECT tableName, rowsAffected, remainingUnscrubbed, disposition FROM @report ORDER BY tableName;
        SELECT @rootTenantId AS rootTenantId,
               (SELECT tenantCode FROM auth.tenant WHERE tenantId = @rootTenantId) AS tenantCode,
               (SELECT SUM (rowsAffected) FROM @report WHERE disposition = 'scrubbedAndSoftDeleted') AS totalRowsPurged,
               (SELECT SUM (remainingUnscrubbed) FROM @report WHERE disposition = 'scrubbedAndSoftDeleted') AS totalRemainingUnscrubbed,
               @ledgerRows AS ledgerRowsRetained,
               DATEADD (DAY, @backupRetentionDays, SYSUTCDATETIME ()) AS backupAgeOutUtc;
        SET @Comments = CONCAT (N'Purged rootTenantId=', @rootTenantId);

        -- =========================================================================================
        -- ===== End of the procedure's own work. ===================================================
        -- =========================================================================================

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
      @SchemaName  = N'config'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspPurgeRootTenantInternal'
    , @Description = N'Ungranted owner-context helper for config.uspPurgeRootTenant: scrubs every PHI, free-text, binary, geocode, and credential column across all 70 mutable tenant/auth tables (including previously soft-deleted rows) in independently committed 5,000-row batches with post-purge verification (#346, B4, I16).';
GO
