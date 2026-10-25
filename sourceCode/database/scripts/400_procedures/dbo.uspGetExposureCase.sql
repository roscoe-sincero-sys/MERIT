-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspGetExposureCase
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

The case page: the case with its person and owner, the person's results for the analyte, activities, linked investigations, open tasks, and the person's primary contact.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission case.view (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspGetExposureCase ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T080
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspGetExposureCase
      @exposureCaseId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspGetExposureCase]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'exposureCaseId=', @exposureCaseId);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'case.view') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (case.view).', 1;
        END;

        IF NOT EXISTS (SELECT 1 FROM dbo.exposureCase WHERE exposureCaseId = @exposureCaseId AND isDeleted = 0)
        BEGIN
            ;THROW 51404, N'The case was not found, or you do not have access to it.', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @personId UNIQUEIDENTIFIER, @analyteId UNIQUEIDENTIFIER;
        SELECT @personId = personId, @analyteId = analyteId FROM dbo.exposureCase WHERE exposureCaseId = @exposureCaseId;

        SELECT c.exposureCaseId, c.caseNumber, c.personId, p.lastName, p.firstName, p.dob, p.sex, c.analyteId,
               a.code AS analyteCode, a.displayName AS analyteName, c.population, c.classification, c.computedClassification,
               c.overrideReason, c.caseStatus, c.exposureCategory, c.workflowTemplate, c.openedDate, c.closedDate,
               c.closureReason, c.assignedToUserId, u.displayName AS assignedToName, c.owningTenantId,
               t.tenantName AS owningTenantName, c.qualifyingLabResultId, c.highestLabResultId,
               h.valueCanonical AS highestValue, h.unitCanonical AS highestUnit, h.collectedDate AS highestCollectedDate,
               c.rowVersion
          FROM dbo.exposureCase AS c
          JOIN dbo.person AS p ON p.personId = c.personId
          JOIN config.analyte AS a ON a.analyteId = c.analyteId
          LEFT JOIN auth.appUser AS u ON u.appUserId = c.assignedToUserId
          LEFT JOIN auth.tenant AS t ON t.tenantId = c.owningTenantId
          LEFT JOIN dbo.labResult AS h ON h.labResultId = c.highestLabResultId
         WHERE c.exposureCaseId = @exposureCaseId;

        SELECT r.labResultId, r.collectedDate, r.operator, r.valueCanonical, r.unitCanonical, r.sampleType, r.testMethod,
               r.resultStatus, r.resultRowStatus, s.specimen, lab.organizationName AS labName
          FROM dbo.labResult AS r
          JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId = r.analyteSpecimenId
          LEFT JOIN dbo.organization AS lab ON lab.organizationId = r.labOrganizationId
         WHERE r.personId = @personId AND s.analyteId = @analyteId AND r.isDeleted = 0
         ORDER BY r.collectedDate DESC;

        SELECT ca.caseActivityId, ca.activityType, ca.activityDate, ca.outcome, ca.note, u.displayName AS workerName,
               cp.lastName AS contactLastName, cp.firstName AS contactFirstName, ca.createdDateUtc
          FROM dbo.caseActivity AS ca
          LEFT JOIN auth.appUser AS u ON u.appUserId = ca.workerUserId
          LEFT JOIN dbo.person AS cp ON cp.personId = ca.contactPersonId
         WHERE ca.exposureCaseId = @exposureCaseId AND ca.isDeleted = 0
         ORDER BY ca.activityDate DESC, ca.createdDateUtc DESC;

        SELECT i.investigationId, i.investigationNumber, i.investigationType, i.investigationStatus, i.requestedDate,
               i.completedDate, COALESCE (l.addressLine1, o.organizationName, es.sourceDescription) AS targetLabel,
               l.city AS targetCity
          FROM dbo.investigationCase AS ic
          JOIN dbo.investigation AS i ON i.investigationId = ic.investigationId AND i.isDeleted = 0
          LEFT JOIN dbo.location AS l ON l.locationId = i.locationId
          LEFT JOIN dbo.organization AS o ON o.organizationId = i.organizationId
          LEFT JOIN dbo.exposureSource AS es ON es.exposureSourceId = i.exposureSourceId
         WHERE ic.exposureCaseId = @exposureCaseId AND ic.isDeleted = 0
         ORDER BY i.requestedDate DESC;

        SELECT w.workTaskId, w.queueCode, w.title, w.detail, w.dueDate, w.isUrgent, w.taskStatus, u.displayName AS assignedToName
          FROM dbo.workTask AS w
          LEFT JOIN auth.appUser AS u ON u.appUserId = w.assignedToUserId
         WHERE w.exposureCaseId = @exposureCaseId AND w.isDeleted = 0 AND w.taskStatus = 'open'
         ORDER BY w.isUrgent DESC, w.dueDate;

        SELECT TOP (1) r.relatedPersonId AS contactPersonId, cp.lastName, cp.firstName, r.role,
               (SELECT TOP (1) contactValue FROM dbo.personContactMethod AS m
                 WHERE m.personId = r.relatedPersonId AND m.contactType = 'phone' AND m.isDeleted = 0
                 ORDER BY m.isPreferred DESC) AS phone
          FROM dbo.personRelationship AS r
          JOIN dbo.person AS cp ON cp.personId = r.relatedPersonId
         WHERE r.personId = @personId AND r.isDeleted = 0 AND r.isPrimaryContact = 1
           AND (r.endDate IS NULL OR r.endDate >= CAST (SYSUTCDATETIME () AS DATE));

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
      @SchemaName  = N'dbo'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspGetExposureCase'
    , @Description = N'The case page: the case with its person and owner, the person''s results for the analyte, activities, linked investigations, open tasks, and the person''s primary contact.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspGetExposureCase TO meritWebRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspGetExposureCase TO meritWorkerRole;
END;
GO
