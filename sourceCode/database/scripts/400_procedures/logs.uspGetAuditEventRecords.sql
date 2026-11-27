-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   logs.uspGetAuditEventRecords
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Returns the manifest of record identifiers associated with one list, export or batch audit event, checking both parent-event review scope (#333) and per-record tenant ownership (#341), plus the count of manifest rows outside the caller's review scope.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission audit.view (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec logs.uspGetAuditEventRecords ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		#333
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE logs.uspGetAuditEventRecords
      @auditEventId BIGINT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[logs].[uspGetAuditEventRecords]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'auditEventId=', @auditEventId);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'audit.view') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (audit.view).', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @scope TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @scope SELECT tenantId FROM auth.tvfReviewTenants ('audit.view');
        DECLARE @wholeRoots TABLE (rootTenantId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @wholeRoots SELECT t.tenantId FROM auth.tenant AS t JOIN @scope AS s ON s.tenantId = t.tenantId WHERE t.tenantId = t.rootTenantId;
        DECLARE @partRoots TABLE (rootTenantId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @partRoots SELECT DISTINCT t.rootTenantId FROM auth.tenant AS t JOIN @scope AS s ON s.tenantId = t.tenantId
         WHERE NOT EXISTS (SELECT 1 FROM @wholeRoots AS w WHERE w.rootTenantId = t.rootTenantId);
        DECLARE @users TABLE (appUserId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @users SELECT u.appUserId FROM auth.appUser AS u
         WHERE EXISTS (SELECT 1 FROM @partRoots)
           AND (EXISTS (SELECT 1 FROM @scope AS s WHERE s.tenantId = u.homeTenantId)
                OR EXISTS (SELECT 1 FROM auth.userAssignment AS ua JOIN @scope AS s ON s.tenantId = ua.tenantId WHERE ua.appUserId = u.appUserId AND ua.isDeleted = 0));

        DECLARE @eventRoot UNIQUEIDENTIFIER;
        SELECT @eventRoot = e.rootTenantId
          FROM logs.auditEvent AS e
         WHERE e.auditEventId = @auditEventId
           AND (EXISTS (SELECT 1 FROM @wholeRoots AS w WHERE w.rootTenantId = e.rootTenantId)
                OR (EXISTS (SELECT 1 FROM @partRoots AS p WHERE p.rootTenantId = e.rootTenantId)
                    AND ((e.owningTenantId IS NULL AND EXISTS (SELECT 1 FROM @users AS x WHERE x.appUserId = e.appUserId))
                         OR EXISTS (SELECT 1 FROM @scope AS s WHERE s.tenantId = e.owningTenantId)
                         OR (e.owningTenantId = e.rootTenantId AND e.entityType IN ('jurisdictionSetting', 'threshold', 'notificationRule')))));

        IF @eventRoot IS NULL
        BEGIN
            ;THROW 51404, N'The audit event was not found, or you do not have access to it.', 1;
        END;

        DECLARE @isWholeRoot BIT = CASE WHEN EXISTS (SELECT 1 FROM @wholeRoots AS w WHERE w.rootTenantId = @eventRoot) THEN 1 ELSE 0 END;

        DECLARE @manifest TABLE (
            entityType VARCHAR (30) NOT NULL,
            entityId   UNIQUEIDENTIFIER NOT NULL,
            isVisible  BIT NOT NULL
        );

        INSERT @manifest (entityType, entityId, isVisible)
        SELECT r.entityType, r.entityId,
               CASE
                   WHEN @isWholeRoot = 1 THEN 1
                   WHEN r.entityType IN ('person', 'patientCare')
                        THEN CASE WHEN COALESCE (r.owningTenantId, dbo.udfPatientWorkOwner (r.entityId)) IN (SELECT tenantId FROM @scope) THEN 1 ELSE 0 END
                   WHEN r.entityType = 'labResult'
                        THEN CASE WHEN COALESCE (r.owningTenantId, dbo.udfPatientWorkOwner ((SELECT lr.personId FROM dbo.labResult AS lr WHERE lr.labResultId = r.entityId))) IN (SELECT tenantId FROM @scope) THEN 1 ELSE 0 END
                   WHEN r.entityType = 'exposureCase'
                        THEN CASE WHEN COALESCE (r.owningTenantId, (SELECT ec.owningTenantId FROM dbo.exposureCase AS ec WHERE ec.exposureCaseId = r.entityId)) IN (SELECT tenantId FROM @scope) THEN 1 ELSE 0 END
                   WHEN r.entityType = 'investigation'
                        THEN CASE WHEN COALESCE (r.owningTenantId, (SELECT inv.owningTenantId FROM dbo.investigation AS inv WHERE inv.investigationId = r.entityId)) IN (SELECT tenantId FROM @scope) THEN 1 ELSE 0 END
                   WHEN r.entityType = 'stagingRow'
                        THEN CASE WHEN COALESCE (r.owningTenantId, (SELECT sr.owningTenantId FROM dbo.stagingRow AS sr WHERE sr.stagingRowId = r.entityId)) IN (SELECT tenantId FROM @scope) THEN 1 ELSE 0 END
                   WHEN r.entityType IN ('workTask', 'testingPlan')
                        THEN CASE WHEN COALESCE (r.owningTenantId, (SELECT wt.owningTenantId FROM dbo.workTask AS wt WHERE wt.workTaskId = r.entityId)) IN (SELECT tenantId FROM @scope) THEN 1 ELSE 0 END
                   WHEN r.entityType = 'notificationObligation'
                        THEN CASE WHEN COALESCE (r.owningTenantId, (SELECT no2.owningTenantId FROM dbo.notificationObligation AS no2 WHERE no2.notificationObligationId = r.entityId)) IN (SELECT tenantId FROM @scope) THEN 1 ELSE 0 END
                   ELSE 1
               END
          FROM logs.auditEventRecord AS r
         WHERE r.auditEventId = @auditEventId AND r.isDeleted = 0;

        SELECT entityType, entityId
          FROM @manifest
         WHERE isVisible = 1
         ORDER BY entityType, entityId;

        SELECT COUNT (*) AS hiddenCount
          FROM @manifest
         WHERE isVisible = 0;

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
    , @ObjectName  = N'uspGetAuditEventRecords'
    , @Description = N'Returns the manifest of record identifiers associated with one list, export or batch audit event, checking both parent-event review scope (#333) and per-record tenant ownership (#341), plus the count of manifest rows outside the caller''s review scope.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspGetAuditEventRecords TO meritWebRole;
END;
GO
