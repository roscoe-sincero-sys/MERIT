-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   config.uspExportRootTenantInternal
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Ungranted owner-context helper for config.uspExportRootTenant: reads all rootTenantId tables across RLS (#346).

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec config.uspExportRootTenantInternal ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		346
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE config.uspExportRootTenantInternal
      @rootTenantId    UNIQUEIDENTIFIER
      , @includePayloads BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[config].[uspExportRootTenantInternal]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'rootTenantId=', @rootTenantId, N', includePayloads=', @includePayloads);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @manifest TABLE (
            tableName    NVARCHAR (128) NOT NULL,
            exportedRows INT NOT NULL,
            disposition  VARCHAR (30) NOT NULL,
            payloadJson  NVARCHAR (MAX) NULL
        );

        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'logs.auditEventChange', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM logs.auditEventChange WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM logs.auditEventChange WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'logs.auditEventRecord', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM logs.auditEventRecord WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM logs.auditEventRecord WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'logs.securityFinding', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM logs.securityFinding WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM logs.securityFinding WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'logs.searchLog', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM logs.searchLog WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM logs.searchLog WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'logs.screenVisit', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM logs.screenVisit WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM logs.screenVisit WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'logs.signInEvent', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM logs.signInEvent WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM logs.signInEvent WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.contactCorrection', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.contactCorrection WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.contactCorrection WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.caseResultLink', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.caseResultLink WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.caseResultLink WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.notificationObligation', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.notificationObligation WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.notificationObligation WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.personDocument', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.personDocument WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.personDocument WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.personActivity', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.personActivity WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.personActivity WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.matchExclusion', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.matchExclusion WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.matchExclusion WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.extractRun', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.extractRun WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.extractRun WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.jobQueue', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.jobQueue WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.jobQueue WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.workTask', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.workTask WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.workTask WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.caseCluster', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.caseCluster WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.caseCluster WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.cluster', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.cluster WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.cluster WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.environmentalSample', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.environmentalSample WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.environmentalSample WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.investigationCase', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.investigationCase WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.investigationCase WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.investigation', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.investigation WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.investigation WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.exposureSource', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.exposureSource WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.exposureSource WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.caseActivity', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.caseActivity WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.caseActivity WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.exposureCase', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.exposureCase WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.exposureCase WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.stagingHoldReason', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.stagingHoldReason WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.stagingHoldReason WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.stagingRow', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.stagingRow WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.stagingRow WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.labResult', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.labResult WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.labResult WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.intakeBatch', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.intakeBatch WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.intakeBatch WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.intakeProfile', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.intakeProfile WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.intakeProfile WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.provider', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.provider WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.provider WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.product', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.product WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.product WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.employmentAnalyte', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.employmentAnalyte WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.employmentAnalyte WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.employment', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.employment WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.employment WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.personLocation', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.personLocation WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.personLocation WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.locationOwner', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.locationOwner WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.locationOwner WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.organizationSite', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.organizationSite WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.organizationSite WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.location', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.location WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.location WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.organizationType', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.organizationType WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.organizationType WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.organization', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.organization WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.organization WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.personRelationship', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.personRelationship WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.personRelationship WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.pregnancyEpisode', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.pregnancyEpisode WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.pregnancyEpisode WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.personIdentifier', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.personIdentifier WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.personIdentifier WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.personContactMethod', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.personContactMethod WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.personContactMethod WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.personRace', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.personRace WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.personRace WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.personAlias', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.personAlias WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.personAlias WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.personMatchKey', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.personMatchKey WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.personMatchKey WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.personAccess', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.personAccess WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.personAccess WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'dbo.person', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM dbo.person WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM dbo.person WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'config.workflowDefinition', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM config.workflowDefinition WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM config.workflowDefinition WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'config.notificationRule', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM config.notificationRule WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM config.notificationRule WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'config.environmentalStandard', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM config.environmentalStandard WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM config.environmentalStandard WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'config.threshold', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM config.threshold WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM config.threshold WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'config.jurisdictionSetting', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM config.jurisdictionSetting WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM config.jurisdictionSetting WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'config.subJurisdiction', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM config.subJurisdiction WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM config.subJurisdiction WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'auth.sessionScopeRoot', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM auth.sessionScopeRoot WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM auth.sessionScopeRoot WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'auth.sessionScope', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM auth.sessionScope WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM auth.sessionScope WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'auth.appUser', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM auth.appUser WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM auth.appUser WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'auth.role', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM auth.role WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM auth.role WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'auth.tenant', COUNT (*), 'exported',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM auth.tenant WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)
                    ELSE NULL END
          FROM auth.tenant WHERE rootTenantId = @rootTenantId AND isDeleted = 0;
        INSERT @manifest (tableName, exportedRows, disposition, payloadJson)
        SELECT N'logs.auditEvent', COUNT (*), 'ledgerReadOnly',
               CASE WHEN @includePayloads = 1 AND COUNT (*) > 0
                    THEN (SELECT * FROM logs.auditEvent WHERE rootTenantId = @rootTenantId FOR JSON PATH)
                    ELSE NULL END
          FROM logs.auditEvent WHERE rootTenantId = @rootTenantId;

        SELECT tableName, exportedRows, disposition, payloadJson FROM @manifest ORDER BY tableName;

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
      @SchemaName  = N'config'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspExportRootTenantInternal'
    , @Description = N'Ungranted owner-context helper for config.uspExportRootTenant: reads all rootTenantId tables across RLS (#346).';
GO
