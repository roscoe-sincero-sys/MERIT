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

Ungranted owner-context helper for config.uspExportRootTenant: returns the 71-table manifest (including active and soft-deleted counts) or streams one table page without credential columns (#346, I17).

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
      @rootTenantId UNIQUEIDENTIFIER
      , @tableName    NVARCHAR (128) = NULL
      , @pageNumber   INT = 1
      , @pageSize     INT = 1000
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
    SET @KeyParameters = CONCAT (N'rootTenantId=', @rootTenantId, N', pageNumber=', @pageNumber, N', pageSize=', @pageSize);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @limit INT = CASE WHEN @pageSize BETWEEN 1 AND 5000 THEN @pageSize ELSE 1000 END;
        DECLARE @offset INT = (CASE WHEN @pageNumber >= 1 THEN @pageNumber ELSE 1 END - 1) * @limit;

        IF @tableName IS NULL
        BEGIN
            DECLARE @manifest TABLE (
                tableName   NVARCHAR (128) NOT NULL,
                activeRows  INT NOT NULL,
                deletedRows INT NOT NULL,
                totalRows   INT NOT NULL,
                disposition VARCHAR (30) NOT NULL
            );
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'auth.mfaRecoveryCode',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM auth.mfaRecoveryCode WHERE appUserId IN (SELECT appUserId FROM auth.appUser WHERE rootTenantId = @rootTenantId);
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'auth.passwordReset',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM auth.passwordReset WHERE appUserId IN (SELECT appUserId FROM auth.appUser WHERE rootTenantId = @rootTenantId);
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'auth.mfaEmailChallenge',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM auth.mfaEmailChallenge WHERE appUserId IN (SELECT appUserId FROM auth.appUser WHERE rootTenantId = @rootTenantId);
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'auth.userProfileGrant',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM auth.userProfileGrant WHERE userProfileId IN (SELECT up.userProfileId FROM auth.userProfile AS up JOIN auth.appUser AS u ON u.appUserId = up.appUserId WHERE u.rootTenantId = @rootTenantId);
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'auth.userProfile',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM auth.userProfile WHERE appUserId IN (SELECT appUserId FROM auth.appUser WHERE rootTenantId = @rootTenantId);
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'auth.userAssignment',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM auth.userAssignment WHERE appUserId IN (SELECT appUserId FROM auth.appUser WHERE rootTenantId = @rootTenantId) OR tenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId);
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'auth.sessionScopeTenant',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM auth.sessionScopeTenant WHERE sessionScopeId IN (SELECT sessionScopeId FROM auth.sessionScope WHERE rootTenantId = @rootTenantId) OR tenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId);
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'auth.sessionScopePermission',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM auth.sessionScopePermission WHERE sessionScopeId IN (SELECT sessionScopeId FROM auth.sessionScope WHERE rootTenantId = @rootTenantId);
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'auth.sessionScopeAdminTenant',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM auth.sessionScopeAdminTenant WHERE sessionScopeId IN (SELECT sessionScopeId FROM auth.sessionScope WHERE rootTenantId = @rootTenantId) OR tenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId);
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'auth.rolePermission',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM auth.rolePermission WHERE roleId IN (SELECT roleId FROM auth.role WHERE rootTenantId = @rootTenantId);
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'auth.tenantJurisdiction',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM auth.tenantJurisdiction WHERE tenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId);
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'auth.tenantClosure',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM auth.tenantClosure WHERE ancestorTenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId) OR descendantTenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId);
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'logs.auditEventChange',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM logs.auditEventChange WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'logs.auditEventRecord',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM logs.auditEventRecord WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'logs.securityFinding',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM logs.securityFinding WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'logs.searchLog',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM logs.searchLog WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'logs.screenVisit',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM logs.screenVisit WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'logs.signInEvent',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM logs.signInEvent WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.contactCorrection',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.contactCorrection WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.caseResultLink',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.caseResultLink WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.notificationObligation',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.notificationObligation WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.personDocument',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.personDocument WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.personActivity',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.personActivity WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.matchExclusion',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.matchExclusion WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.extractRun',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.extractRun WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.jobQueue',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.jobQueue WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.workTask',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.workTask WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.caseCluster',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.caseCluster WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.cluster',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.cluster WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.environmentalSample',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.environmentalSample WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.investigationCase',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.investigationCase WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.investigation',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.investigation WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.exposureSource',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.exposureSource WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.caseActivity',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.caseActivity WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.exposureCase',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.exposureCase WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.stagingHoldReason',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.stagingHoldReason WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.stagingRow',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.stagingRow WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.labResult',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.labResult WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.intakeBatch',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.intakeBatch WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.intakeProfile',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.intakeProfile WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.provider',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.provider WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.product',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.product WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.employmentAnalyte',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.employmentAnalyte WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.employment',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.employment WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.personLocation',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.personLocation WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.locationOwner',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.locationOwner WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.organizationSite',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.organizationSite WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.location',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.location WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.organizationType',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.organizationType WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.organization',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.organization WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.personRelationship',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.personRelationship WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.pregnancyEpisode',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.pregnancyEpisode WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.personIdentifier',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.personIdentifier WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.personContactMethod',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.personContactMethod WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.personRace',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.personRace WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.personAlias',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.personAlias WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.personMatchKey',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.personMatchKey WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.personAccess',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.personAccess WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'dbo.person',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM dbo.person WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'config.workflowDefinition',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM config.workflowDefinition WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'config.notificationRule',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM config.notificationRule WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'config.environmentalStandard',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM config.environmentalStandard WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'config.threshold',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM config.threshold WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'config.jurisdictionSetting',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM config.jurisdictionSetting WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'config.subJurisdiction',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM config.subJurisdiction WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'auth.sessionScopeRoot',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM auth.sessionScopeRoot WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'auth.sessionScope',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM auth.sessionScope WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'auth.appUser',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM auth.appUser WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'auth.role',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM auth.role WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'auth.tenant',
                   COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),
                   COUNT (*), 'exportable'
              FROM auth.tenant WHERE rootTenantId = @rootTenantId;
            INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)
            SELECT N'logs.auditEvent', COUNT (*), 0, COUNT (*), 'ledgerReadOnly'
              FROM logs.auditEvent WHERE rootTenantId = @rootTenantId;
            SELECT tableName, COALESCE (activeRows, 0) AS activeRows, COALESCE (deletedRows, 0) AS deletedRows, totalRows, disposition FROM @manifest ORDER BY tableName;
            RETURN;
        END;

        IF @tableName = N'auth.mfaRecoveryCode'
        BEGIN
            SELECT mfaRecoveryCodeId, appUserId, usedUtc, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM auth.mfaRecoveryCode
             WHERE appUserId IN (SELECT appUserId FROM auth.appUser WHERE rootTenantId = @rootTenantId)
             ORDER BY mfaRecoveryCodeId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'auth.passwordReset'
        BEGIN
            SELECT passwordResetId, appUserId, expiresUtc, failedAttempts, maxAttempts, consumedUtc, supersededUtc, deliveryStatus, deliveryUpdatedUtc, deliveryAttempts, failureKind, smtpReplyCode, supportReference, correlationId, clientAddress, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM auth.passwordReset
             WHERE appUserId IN (SELECT appUserId FROM auth.appUser WHERE rootTenantId = @rootTenantId)
             ORDER BY passwordResetId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'auth.mfaEmailChallenge'
        BEGIN
            SELECT mfaEmailChallengeId, appUserId, purpose, expiresUtc, failedAttempts, maxAttempts, consumedUtc, supersededUtc, deliveryStatus, deliveryUpdatedUtc, deliveryAttempts, failureKind, smtpReplyCode, supportReference, correlationId, clientAddress, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM auth.mfaEmailChallenge
             WHERE appUserId IN (SELECT appUserId FROM auth.appUser WHERE rootTenantId = @rootTenantId)
             ORDER BY mfaEmailChallengeId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'auth.userProfileGrant'
        BEGIN
            SELECT userProfileGrantId, userProfileId, roleId, tenantId, includeDescendants, accessLevel, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM auth.userProfileGrant
             WHERE userProfileId IN (SELECT up.userProfileId FROM auth.userProfile AS up JOIN auth.appUser AS u ON u.appUserId = up.appUserId WHERE u.rootTenantId = @rootTenantId)
             ORDER BY userProfileGrantId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'auth.userProfile'
        BEGIN
            SELECT userProfileId, appUserId, profileName, profileDescription, isMaster, isActive, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM auth.userProfile
             WHERE appUserId IN (SELECT appUserId FROM auth.appUser WHERE rootTenantId = @rootTenantId)
             ORDER BY userProfileId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'auth.userAssignment'
        BEGIN
            SELECT userAssignmentId, appUserId, roleId, tenantId, includeDescendants, accessLevel, startDate, endDate, revokedUtc, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM auth.userAssignment
             WHERE appUserId IN (SELECT appUserId FROM auth.appUser WHERE rootTenantId = @rootTenantId) OR tenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId)
             ORDER BY userAssignmentId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'auth.sessionScopeTenant'
        BEGIN
            SELECT sessionScopeTenantId, tenantId, accessLevel, canReadRelatedContacts, canReadIdentifiedResults, canReadIdentifiedCases, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM auth.sessionScopeTenant
             WHERE sessionScopeId IN (SELECT sessionScopeId FROM auth.sessionScope WHERE rootTenantId = @rootTenantId) OR tenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId)
             ORDER BY sessionScopeTenantId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'auth.sessionScopePermission'
        BEGIN
            SELECT sessionScopePermissionId, permissionCode, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM auth.sessionScopePermission
             WHERE sessionScopeId IN (SELECT sessionScopeId FROM auth.sessionScope WHERE rootTenantId = @rootTenantId)
             ORDER BY sessionScopePermissionId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'auth.sessionScopeAdminTenant'
        BEGIN
            SELECT sessionScopeAdminTenantId, tenantId, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM auth.sessionScopeAdminTenant
             WHERE sessionScopeId IN (SELECT sessionScopeId FROM auth.sessionScope WHERE rootTenantId = @rootTenantId) OR tenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId)
             ORDER BY sessionScopeAdminTenantId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'auth.rolePermission'
        BEGIN
            SELECT rolePermissionId, roleId, permissionId, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM auth.rolePermission
             WHERE roleId IN (SELECT roleId FROM auth.role WHERE rootTenantId = @rootTenantId)
             ORDER BY rolePermissionId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'auth.tenantJurisdiction'
        BEGIN
            SELECT tenantJurisdictionId, tenantId, subJurisdictionId, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM auth.tenantJurisdiction
             WHERE tenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId)
             ORDER BY tenantJurisdictionId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'auth.tenantClosure'
        BEGIN
            SELECT tenantClosureId, ancestorTenantId, descendantTenantId, depth, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM auth.tenantClosure
             WHERE ancestorTenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId) OR descendantTenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId)
             ORDER BY tenantClosureId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'logs.auditEventChange'
        BEGIN
            SELECT auditEventChangeId, auditEventId, rootTenantId, owningTenantId, entityType, entityId, changedUtc, beforeJson, afterJson, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM logs.auditEventChange
             WHERE rootTenantId = @rootTenantId
             ORDER BY auditEventChangeId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'logs.auditEventRecord'
        BEGIN
            SELECT auditEventRecordId, auditEventId, rootTenantId, entityType, entityId, owningTenantId, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM logs.auditEventRecord
             WHERE rootTenantId = @rootTenantId
             ORDER BY auditEventRecordId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'logs.securityFinding'
        BEGIN
            SELECT securityFindingId, rootTenantId, owningTenantId, appUserId, clientAddress, sessionReference, ruleCode, severity, title, detailJson, windowStartUtc, windowEndUtc, detectedUtc, status, acknowledgedByUserId, acknowledgedUtc, fingerprint, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM logs.securityFinding
             WHERE rootTenantId = @rootTenantId
             ORDER BY securityFindingId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'logs.searchLog'
        BEGIN
            SELECT searchLogId, appUserId, rootTenantId, searchText, resultCount, searchedUtc, sessionReference, clientAddress, actingTenantId, existsElsewhere, searchSource, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM logs.searchLog
             WHERE rootTenantId = @rootTenantId
             ORDER BY searchLogId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'logs.screenVisit'
        BEGIN
            SELECT screenVisitId, eventUtc, rootTenantId, actingTenantId, appUserId, sessionReference, profileId, viewAsUserId, roleCodes, screenCode, navigationType, tabId, previousVisitId, durationMs, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM logs.screenVisit
             WHERE rootTenantId = @rootTenantId
             ORDER BY screenVisitId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'logs.signInEvent'
        BEGIN
            SELECT signInEventId, eventUtc, eventType, step, method, reason, appUserId, actorUserId, rootTenantId, attemptedNameHash, clientAddress, forwardedFor, userAgent, sessionReference, attemptId, traceId, failedSignInCount, lockoutEndUtc, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM logs.signInEvent
             WHERE rootTenantId = @rootTenantId
             ORDER BY signInEventId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.contactCorrection'
        BEGIN
            SELECT contactCorrectionId, rootTenantId, owningTenantId, personId, caregiverId, caregiverName, responsibleTenantId, contactType, proposedValue, city, state, zip5, unitAction, unit, proposedAddressKey, sourceNote, beforeJson, afterJson, requestStatus, reviewNote, reviewedByUserId, reviewedUtc, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.contactCorrection
             WHERE rootTenantId = @rootTenantId
             ORDER BY contactCorrectionId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.caseResultLink'
        BEGIN
            SELECT caseResultLinkId, rootTenantId, owningTenantId, personId, exposureCaseId, labResultId, openingActivityId, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.caseResultLink
             WHERE rootTenantId = @rootTenantId
             ORDER BY caseResultLinkId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.notificationObligation'
        BEGIN
            SELECT notificationObligationId, rootTenantId, owningTenantId, personId, exposureCaseId, labResultId, notificationRuleId, workTaskId, policySnapshotJson, ruleName, recipientKind, triggerEvent, anchorDate, dueDate, timeZoneId, evaluationState, reviewReason, completedActivityId, completedDate, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.notificationObligation
             WHERE rootTenantId = @rootTenantId
             ORDER BY notificationObligationId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.personDocument'
        BEGIN
            SELECT personDocumentId, rootTenantId, owningTenantId, personId, exposureCaseId, supersedesDocumentId, title, fileName, contentType, category, documentDate, source, description, uploadedByUserId, contentHash, content, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.personDocument
             WHERE rootTenantId = @rootTenantId
             ORDER BY personDocumentId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.personActivity'
        BEGIN
            SELECT personActivityId, rootTenantId, owningTenantId, personId, exposureCaseId, labResultId, activityType, activityDate, audience, channel, contactPersonId, recipientLabel, noticeFormReference, ownerScopeConfirmed, outcome, note, workerUserId, isVoided, voidReason, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.personActivity
             WHERE rootTenantId = @rootTenantId
             ORDER BY personActivityId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.matchExclusion'
        BEGIN
            SELECT matchExclusionId, rootTenantId, owningTenantId, personId, incomingKey, stagingRowId, decisionNote, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.matchExclusion
             WHERE rootTenantId = @rootTenantId
             ORDER BY matchExclusionId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.extractRun'
        BEGIN
            SELECT extractRunId, rootTenantId, owningTenantId, extractType, periodStart, periodEnd, extractLayoutId, runStatus, jobId, recordCount, errorCount, validationReportJson, fileName, fileContent, requestedByUserId, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.extractRun
             WHERE rootTenantId = @rootTenantId
             ORDER BY extractRunId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.jobQueue'
        BEGIN
            SELECT jobId, rootTenantId, owningTenantId, jobType, payloadJson, payloadVersion, jobStatus, attempts, maxAttempts, availableUtc, claimedBy, claimedUtc, claimToken, completedUtc, lastError, requestedByUserId, requestedProfileId, requestedViewAsUserId, requestedActingTenantId, resultJson, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.jobQueue
             WHERE rootTenantId = @rootTenantId
             ORDER BY jobId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.workTask'
        BEGIN
            SELECT workTaskId, rootTenantId, owningTenantId, queueCode, title, detail, personId, exposureCaseId, labResultId, investigationId, clusterId, stagingRowId, isUrgent, dueDate, assignedToUserId, taskStatus, completedUtc, completedByUserId, resolutionNote, testingPlanJson, workflowDefinitionId, workflowSnapshotJson, workflowOccurrenceKey, completionActivityId, resolutionKind, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.workTask
             WHERE rootTenantId = @rootTenantId
             ORDER BY workTaskId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.caseCluster'
        BEGIN
            SELECT caseClusterId, rootTenantId, owningTenantId, clusterId, exposureCaseId, joinedDate, leftDate, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.caseCluster
             WHERE rootTenantId = @rootTenantId
             ORDER BY caseClusterId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.cluster'
        BEGIN
            SELECT clusterId, rootTenantId, owningTenantId, clusterType, sharedKey, title, clusterStatus, windowStart, windowEnd, notes, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.cluster
             WHERE rootTenantId = @rootTenantId
             ORDER BY clusterId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.environmentalSample'
        BEGIN
            SELECT environmentalSampleId, rootTenantId, owningTenantId, investigationId, sampleType, roomOrComponent, surface, sampleValue, unit, sampleDate, environmentalStandardId, exceedsStandard, notes, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.environmentalSample
             WHERE rootTenantId = @rootTenantId
             ORDER BY environmentalSampleId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.investigationCase'
        BEGIN
            SELECT investigationCaseId, rootTenantId, owningTenantId, investigationId, exposureCaseId, employmentId, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.investigationCase
             WHERE rootTenantId = @rootTenantId
             ORDER BY investigationCaseId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.investigation'
        BEGIN
            SELECT investigationId, rootTenantId, owningTenantId, investigationNumber, investigationType, locationId, organizationId, worksiteLocationId, exposureSourceId, investigationStatus, assignedToUserId, requestedDate, scheduledDate, completedDate, findings, regulatoryAction, referralDate, referralOutcome, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.investigation
             WHERE rootTenantId = @rootTenantId
             ORDER BY investigationId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.exposureSource'
        BEGIN
            SELECT exposureSourceId, rootTenantId, owningTenantId, personId, exposureCaseId, sourceType, sourceDescription, productId, locationId, startDate, endDate, isConfirmed, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.exposureSource
             WHERE rootTenantId = @rootTenantId
             ORDER BY exposureSourceId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.caseActivity'
        BEGIN
            SELECT caseActivityId, lifecycleKind, isVoided, voidReason, rootTenantId, owningTenantId, exposureCaseId, activityType, activityDate, workerUserId, contactPersonId, outcome, note, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.caseActivity
             WHERE rootTenantId = @rootTenantId
             ORDER BY caseActivityId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.exposureCase'
        BEGIN
            SELECT exposureCaseId, rootTenantId, owningTenantId, caseNumber, personId, analyteId, population, classification, computedClassification, overrideReason, caseStatus, qualifyingLabResultId, highestLabResultId, exposureCategory, workflowTemplate, assignedToUserId, openedDate, closedDate, closureReason, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.exposureCase
             WHERE rootTenantId = @rootTenantId
             ORDER BY exposureCaseId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.stagingHoldReason'
        BEGIN
            SELECT stagingHoldReasonId, rootTenantId, owningTenantId, stagingRowId, reasonCode, fieldName, reasonMessage, reportedValue, isResolved, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.stagingHoldReason
             WHERE rootTenantId = @rootTenantId
             ORDER BY stagingHoldReasonId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.stagingRow'
        BEGIN
            SELECT stagingRowId, rootTenantId, owningTenantId, intakeBatchId, rowNumber, parsedJson, correctedJson, rowStatus, labResultId, rejectReason, assignedToUserId, isUrgent, heldSinceUtc, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.stagingRow
             WHERE rootTenantId = @rootTenantId
             ORDER BY stagingRowId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.labResult'
        BEGIN
            SELECT labResultId, rootTenantId, owningTenantId, personId, analyteSpecimenId, collectedDate, analyzedDate, reportedDate, reportingFinalDateVerified, reportingEligibilityVerified, reportingPolicyExcluded, reportingSameDayChecked, reportingLateConfirmed, reportingVerifiedPolicyId, reportingVerifiedByUserId, reportingVerifiedUtc, receivedUtc, resultStatus, supersedesLabResultId, correctionReason, correctionByUserId, correctionUtc, classificationSnapshotJson, valueReported, valueText, operator, unitReported, valueCanonical, unitCanonical, sampleType, testMethod, deviceModel, deviceSerial, kitLot, speciation, creatinineValue, valueCreatinineAdj, reasonForTest, payerType, labOrganizationId, orderingProviderId, orderingFacilityId, accessionNumber, loincReported, reportedSnapshotJson, reportedLocationId, ageAtCollectionMonths, population, isIndeterminate, resultRowStatus, intakeBatchId, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.labResult
             WHERE rootTenantId = @rootTenantId
             ORDER BY labResultId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.intakeBatch'
        BEGIN
            SELECT intakeBatchId, rootTenantId, owningTenantId, intakeProfileId, senderOrganizationId, sourceChannel, fileName, rawContent, contentSha256, receivedUtc, departmentReceivedUtc, receiptEvidence, receiptTimeZone, batchStatus, pauseReason, receivedCount, loadedCount, heldCount, rejectedCount, submittedByUserId, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.intakeBatch
             WHERE rootTenantId = @rootTenantId
             ORDER BY intakeBatchId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.intakeProfile'
        BEGIN
            SELECT intakeProfileId, rootTenantId, owningTenantId, senderOrganizationId, profileName, profileVersion, fileType, headerRow, dateFormats, columnMapJson, valueTranslationsJson, constantsJson, headerSignature, isActive, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.intakeProfile
             WHERE rootTenantId = @rootTenantId
             ORDER BY intakeProfileId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.provider'
        BEGIN
            SELECT providerId, rootTenantId, owningTenantId, npi, lastName, firstName, credential, primaryTaxonomy, practiceLocationId, registryStatus, registryCheckedUtc, localOverridesJson, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.provider
             WHERE rootTenantId = @rootTenantId
             ORDER BY providerId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.product'
        BEGIN
            SELECT productId, rootTenantId, owningTenantId, brand, productName, countryOfOrigin, productType, notes, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.product
             WHERE rootTenantId = @rootTenantId
             ORDER BY productId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.employmentAnalyte'
        BEGIN
            SELECT employmentAnalyteId, rootTenantId, owningTenantId, personId, employmentId, analyteId, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.employmentAnalyte
             WHERE rootTenantId = @rootTenantId
             ORDER BY employmentAnalyteId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.employment'
        BEGIN
            SELECT employmentId, rootTenantId, owningTenantId, personId, organizationId, worksiteLocationId, jobTitle, industryText, occupationCode, occupationCodeVersion, naicsOverride, metalTasks, ppeNotes, hoursPerWeek, startDate, endDate, source, verifiedDate, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.employment
             WHERE rootTenantId = @rootTenantId
             ORDER BY employmentId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.personLocation'
        BEGIN
            SELECT personLocationId, rootTenantId, owningTenantId, personId, locationId, organizationSiteId, role, isPrimary, primaryReason, scheduleType, hoursPerWeek, scheduleNote, startDate, endDate, source, verifiedDate, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.personLocation
             WHERE rootTenantId = @rootTenantId
             ORDER BY personLocationId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.locationOwner'
        BEGIN
            SELECT locationOwnerId, rootTenantId, owningTenantId, locationId, ownerPersonId, ownerOrganizationId, startDate, endDate, source, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.locationOwner
             WHERE rootTenantId = @rootTenantId
             ORDER BY locationOwnerId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.organizationSite'
        BEGIN
            SELECT organizationSiteId, rootTenantId, owningTenantId, organizationId, locationId, siteName, startDate, endDate, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.organizationSite
             WHERE rootTenantId = @rootTenantId
             ORDER BY organizationSiteId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.location'
        BEGIN
            SELECT locationId, rootTenantId, owningTenantId, country, addressLine1, unit, city, state, zip5, region, postalCode, countyFips, stdStatus, latitude, longitude, geocodeQuality, censusTract, censusVintage, buildingLocationId, yearBuilt, yearBuiltSource, housingType, tenure, isCityOnly, addressKey, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.location
             WHERE rootTenantId = @rootTenantId
             ORDER BY locationId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.organizationType'
        BEGIN
            SELECT organizationTypeId, rootTenantId, owningTenantId, organizationId, organizationTypeCode, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.organizationType
             WHERE rootTenantId = @rootTenantId
             ORDER BY organizationTypeId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.organization'
        BEGIN
            SELECT organizationId, rootTenantId, owningTenantId, organizationName, organizationNameKey, naicsCode, naicsVersion, npi, cliaNumber, parentOrganizationId, contactName, phone, email, isActive, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.organization
             WHERE rootTenantId = @rootTenantId
             ORDER BY organizationId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.personRelationship'
        BEGIN
            SELECT personRelationshipId, rootTenantId, owningTenantId, personId, relatedPersonId, role, isPrimaryContact, hasLegalCustody, startDate, endDate, source, verifiedDate, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.personRelationship
             WHERE rootTenantId = @rootTenantId
             ORDER BY personRelationshipId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.pregnancyEpisode'
        BEGIN
            SELECT pregnancyEpisodeId, rootTenantId, owningTenantId, personId, startDate, expectedEndDate, actualEndDate, outcome, source, newbornPersonId, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.pregnancyEpisode
             WHERE rootTenantId = @rootTenantId
             ORDER BY pregnancyEpisodeId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.personIdentifier'
        BEGIN
            SELECT personIdentifierId, rootTenantId, owningTenantId, personId, identifierType, identifierValue, assigningFacility, source, reportedDate, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.personIdentifier
             WHERE rootTenantId = @rootTenantId
             ORDER BY personIdentifierId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.personContactMethod'
        BEGIN
            SELECT personContactMethodId, rootTenantId, owningTenantId, personId, contactType, contactValue, isPreferred, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.personContactMethod
             WHERE rootTenantId = @rootTenantId
             ORDER BY personContactMethodId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.personRace'
        BEGIN
            SELECT personRaceId, rootTenantId, owningTenantId, personId, raceCode, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.personRace
             WHERE rootTenantId = @rootTenantId
             ORDER BY personRaceId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.personAlias'
        BEGIN
            SELECT personAliasId, rootTenantId, owningTenantId, personId, lastName, firstName, middleName, lastNameKey, firstNameKey, aliasType, source, sourceLabResultId, firstSeen, lastSeen, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.personAlias
             WHERE rootTenantId = @rootTenantId
             ORDER BY personAliasId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.personMatchKey'
        BEGIN
            SELECT personMatchKeyId, rootTenantId, personId, lastNameKey, firstNameKey, dob, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.personMatchKey
             WHERE rootTenantId = @rootTenantId
             ORDER BY personMatchKeyId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.personAccess'
        BEGIN
            SELECT personAccessId, rootTenantId, personId, tenantId, accessReason, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.personAccess
             WHERE rootTenantId = @rootTenantId
             ORDER BY personAccessId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'dbo.person'
        BEGIN
            SELECT personId, rootTenantId, owningTenantId, lastName, firstName, middleName, lastNameKey, firstNameKey, isPlaceholderName, dob, dobVerified, sex, ethnicity, preferredLanguage, deceasedDate, mergedIntoPersonId, legacyId, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM dbo.person
             WHERE rootTenantId = @rootTenantId
             ORDER BY personId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'config.workflowDefinition'
        BEGIN
            SELECT workflowDefinitionId, rootTenantId, owningTenantId, name, version, definitionStatus, description, triggerEvent, population, analyteSpecimenId, minimumAgeMonths, maximumAgeMonths, minimumValue, maximumValue, requiresEligibilityReview, completionKind, evidenceFieldsJson, assignmentRoleCode, routingPolicy, dueInDays, recurrenceDays, dayBasis, timeZoneId, holidayDatesJson, blocksClosure, closureReason, minimumContactAttempts, allowWaiver, effectiveFrom, effectiveTo, approvalReference, publishedByUserId, publishedUtc, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM config.workflowDefinition
             WHERE rootTenantId = @rootTenantId
             ORDER BY workflowDefinitionId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'config.notificationRule'
        BEGIN
            SELECT notificationRuleId, rootTenantId, owningTenantId, ruleName, analyteSpecimenId, population, rulePurpose, eligibilityMode, requiredFieldsCsv, minimumAgeMonths, maximumAgeMonths, minimumValue, maximumValue, recipientKind, triggerEvent, dayBasis, dueInDays, timeZoneId, calendarName, holidayDatesJson, effectiveFrom, effectiveTo, approvalReference, approvedByUserId, isActive, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM config.notificationRule
             WHERE rootTenantId = @rootTenantId
             ORDER BY notificationRuleId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'config.environmentalStandard'
        BEGIN
            SELECT environmentalStandardId, rootTenantId, sampleType, surface, standardValue, unit, authority, effectiveFrom, effectiveTo, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM config.environmentalStandard
             WHERE rootTenantId = @rootTenantId
             ORDER BY environmentalStandardId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'config.threshold'
        BEGIN
            SELECT thresholdId, rootTenantId, analyteSpecimenId, population, ageMinMonths, ageMaxMonths, speciation, sampleType, thresholdType, actionCodeId, dueIntervalDays, lowerValue, upperValue, unit, comparator, effectiveFrom, effectiveTo, authority, isActive, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM config.threshold
             WHERE rootTenantId = @rootTenantId
             ORDER BY thresholdId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'config.jurisdictionSetting'
        BEGIN
            SELECT jurisdictionSettingId, rootTenantId, settingKey, settingValue, effectiveFrom, effectiveTo, settingDescription, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM config.jurisdictionSetting
             WHERE rootTenantId = @rootTenantId
             ORDER BY jurisdictionSettingId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'config.subJurisdiction'
        BEGIN
            SELECT subJurisdictionId, rootTenantId, countyFips, subJurisdictionName, jurisdictionType, boundary, labelLatitude, labelLongitude, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM config.subJurisdiction
             WHERE rootTenantId = @rootTenantId
             ORDER BY subJurisdictionId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'auth.sessionScopeRoot'
        BEGIN
            SELECT sessionScopeRootId, rootTenantId, accessLevel, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM auth.sessionScopeRoot
             WHERE rootTenantId = @rootTenantId
             ORDER BY sessionScopeRootId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'auth.sessionScope'
        BEGIN
            SELECT appUserId, sessionReference, rootTenantId, actingTenantId, userProfileId, viewAsUserId, isPlatformAdmin, isDeidentified, expiresUtc, revokedUtc, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM auth.sessionScope
             WHERE rootTenantId = @rootTenantId
             ORDER BY sessionScopeId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'auth.appUser'
        BEGIN
            SELECT appUserId, rootTenantId, homeTenantId, userName, displayName, email, mfaEnabled, failedSignInCount, lockoutEndUtc, lastSignInUtc, isActive, mfaEmailVerifiedUtc, mfaPreferredMethod, passwordChangedUtc, passwordChangeRequired, lastTotpStep, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM auth.appUser
             WHERE rootTenantId = @rootTenantId
             ORDER BY appUserId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'auth.role'
        BEGIN
            SELECT roleId, rootTenantId, roleCode, roleName, isAdministrator, isDeidentified, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM auth.role
             WHERE rootTenantId = @rootTenantId
             ORDER BY roleId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'auth.tenant'
        BEGIN
            SELECT tenantId, parentTenantId, rootTenantId, tenantCode, tenantName, tenantType, statePostalCode, timeZone, isActive, isDeleted, createdBy, createdDateUtc, modifiedBy, modifiedDateUtc, deletedBy, deletedDateUtc
              FROM auth.tenant
             WHERE rootTenantId = @rootTenantId
             ORDER BY tenantId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE IF @tableName = N'logs.auditEvent'
        BEGIN
            SELECT auditEventId, eventUtc, appUserId, rootTenantId, owningTenantId, eventAction, entityType, entityId, recordCount, clientAddress, userAgentHash, sessionReference, traceId, actingTenantId, viewAsUserId, viewKind, filterJson, administrationJson, createdBy, createdDateUtc
              FROM logs.auditEvent
             WHERE rootTenantId = @rootTenantId
             ORDER BY auditEventId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
        END
        ELSE
        BEGIN
            ;THROW 51422, N'Unknown or non-tenant table name for root export.', 1;
        END;

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
    , @Description = N'Ungranted owner-context helper for config.uspExportRootTenant: returns the 71-table manifest (including active and soft-deleted counts) or streams one table page without credential columns (#346, I17).';
GO
