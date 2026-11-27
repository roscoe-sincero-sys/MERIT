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

Ungranted owner-context helper for config.uspPurgeRootTenant: scrubs and soft-deletes all mutable rootTenantId tables across RLS while leaving append-only logs.auditEvent intact (#346).

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

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @report TABLE (
            tableName    NVARCHAR (128) NOT NULL,
            rowsAffected INT NOT NULL,
            disposition  VARCHAR (40) NOT NULL
        );
        DECLARE @n INT = 0;

        UPDATE logs.auditEventChange
           SET beforeJson = NULL, afterJson = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'logs.auditEventChange', @n, 'scrubbedAndSoftDeleted');
        UPDATE logs.auditEventRecord
           SET entityId = '00000000-0000-0000-0000-000000000000', owningTenantId = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'logs.auditEventRecord', @n, 'scrubbedAndSoftDeleted');
        UPDATE logs.securityFinding
           SET clientAddress = NULL, sessionReference = NULL, detailJson = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'logs.securityFinding', @n, 'scrubbedAndSoftDeleted');
        UPDATE logs.searchLog
           SET searchText = N'', clientAddress = NULL, sessionReference = NULL, actingTenantId = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'logs.searchLog', @n, 'scrubbedAndSoftDeleted');
        UPDATE logs.screenVisit
           SET appUserId = NULL, sessionReference = NULL, profileId = NULL, viewAsUserId = NULL, actingTenantId = NULL, roleCodes = NULL, tabId = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'logs.screenVisit', @n, 'scrubbedAndSoftDeleted');
        UPDATE logs.signInEvent
           SET clientAddress = NULL, forwardedFor = NULL, userAgent = NULL, attemptedNameHash = NULL, attemptedNameProtected = NULL, sessionReference = NULL, traceId = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'logs.signInEvent', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.contactCorrection
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.contactCorrection', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.caseResultLink
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.caseResultLink', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.notificationObligation
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.notificationObligation', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.personDocument
           SET title = N'[purged]', fileName = N'purged', content = 0x00, contentHash = CAST (0x00 AS BINARY (32)), description = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.personDocument', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.personActivity
           SET note = N'[purged]', recipientLabel = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.personActivity', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.matchExclusion
           SET incomingKey = N'[purged]', decisionNote = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.matchExclusion', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.extractRun
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.extractRun', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.jobQueue
           SET payloadJson = N'{}', isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.jobQueue', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.workTask
           SET title = N'[purged]', detail = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.workTask', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.caseCluster
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.caseCluster', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.cluster
           SET notes = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.cluster', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.environmentalSample
           SET notes = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.environmentalSample', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.investigationCase
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.investigationCase', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.investigation
           SET findings = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.investigation', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.exposureSource
           SET sourceDescription = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.exposureSource', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.caseActivity
           SET note = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.caseActivity', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.exposureCase
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.exposureCase', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.stagingHoldReason
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.stagingHoldReason', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.stagingRow
           SET parsedJson = N'{}', correctedJson = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.stagingRow', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.labResult
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.labResult', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.intakeBatch
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.intakeBatch', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.intakeProfile
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.intakeProfile', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.provider
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.provider', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.product
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.product', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.employmentAnalyte
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.employmentAnalyte', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.employment
           SET jobTitle = NULL, metalTasks = NULL, ppeNotes = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.employment', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.personLocation
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.personLocation', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.locationOwner
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.locationOwner', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.organizationSite
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.organizationSite', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.location
           SET addressLine1 = N'[purged]', unit = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.location', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.organizationType
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.organizationType', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.organization
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.organization', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.personRelationship
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.personRelationship', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.pregnancyEpisode
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.pregnancyEpisode', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.personIdentifier
           SET identifierValue = N'[purged]', isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.personIdentifier', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.personContactMethod
           SET contactValue = N'[purged]', isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.personContactMethod', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.personRace
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.personRace', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.personAlias
           SET lastName = N'[purged]', firstName = NULL, middleName = NULL, lastNameKey = N'PURGED', firstNameKey = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.personAlias', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.personMatchKey
           SET lastNameKey = N'PURGED', firstNameKey = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.personMatchKey', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.personAccess
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.personAccess', @n, 'scrubbedAndSoftDeleted');
        UPDATE dbo.person
           SET lastName = N'[purged]', firstName = NULL, middleName = NULL, lastNameKey = N'PURGED', firstNameKey = NULL, dob = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'dbo.person', @n, 'scrubbedAndSoftDeleted');
        UPDATE config.workflowDefinition
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'config.workflowDefinition', @n, 'scrubbedAndSoftDeleted');
        UPDATE config.notificationRule
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'config.notificationRule', @n, 'scrubbedAndSoftDeleted');
        UPDATE config.environmentalStandard
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'config.environmentalStandard', @n, 'scrubbedAndSoftDeleted');
        UPDATE config.threshold
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'config.threshold', @n, 'scrubbedAndSoftDeleted');
        UPDATE config.jurisdictionSetting
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'config.jurisdictionSetting', @n, 'scrubbedAndSoftDeleted');
        UPDATE config.subJurisdiction
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'config.subJurisdiction', @n, 'scrubbedAndSoftDeleted');
        UPDATE auth.sessionScopeRoot
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'auth.sessionScopeRoot', @n, 'scrubbedAndSoftDeleted');
        UPDATE auth.sessionScope
           SET revokedUtc = COALESCE (revokedUtc, SYSUTCDATETIME ()), isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'auth.sessionScope', @n, 'scrubbedAndSoftDeleted');
        UPDATE auth.appUser
           SET isActive = 0, email = NULL, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'auth.appUser', @n, 'scrubbedAndSoftDeleted');
        UPDATE auth.role
           SET isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'auth.role', @n, 'scrubbedAndSoftDeleted');
        UPDATE auth.tenant
           SET isActive = 0, isDeleted = 1, modifiedBy = N'MERIT tenant purge'
         WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        SET @n = @@ROWCOUNT;
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'auth.tenant', @n, 'scrubbedAndSoftDeleted');
        DECLARE @ledgerRows INT = (SELECT COUNT (*) FROM logs.auditEvent WHERE rootTenantId = @rootTenantId);
        INSERT @report (tableName, rowsAffected, disposition) VALUES (N'logs.auditEvent', @ledgerRows, 'retainedAppendOnlyLedger');

        SELECT tableName, rowsAffected, disposition FROM @report ORDER BY tableName;
        SELECT @rootTenantId AS rootTenantId,
               (SELECT tenantCode FROM auth.tenant WHERE tenantId = @rootTenantId) AS tenantCode,
               (SELECT SUM (rowsAffected) FROM @report WHERE disposition = 'scrubbedAndSoftDeleted') AS totalRowsPurged,
               @ledgerRows AS ledgerRowsRetained,
               DATEADD (DAY, @backupRetentionDays, SYSUTCDATETIME ()) AS backupAgeOutUtc;
        SET @Comments = CONCAT (N'Purged rootTenantId=', @rootTenantId);

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
      @SchemaName  = N'config'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspPurgeRootTenantInternal'
    , @Description = N'Ungranted owner-context helper for config.uspPurgeRootTenant: scrubs and soft-deletes all mutable rootTenantId tables across RLS while leaving append-only logs.auditEvent intact (#346).';
GO
