-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspGetInvestigation
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

The investigation page: the investigation and its target, the cases it serves, and its environmental samples with computed exceedance.

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

exec dbo.uspGetInvestigation ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T081
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspGetInvestigation
      @investigationId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspGetInvestigation]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'investigationId=', @investigationId);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'case.view') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (case.view).', 1;
        END;

        IF NOT EXISTS (SELECT 1 FROM dbo.investigation WHERE investigationId = @investigationId AND isDeleted = 0)
        BEGIN
            ;THROW 51404, N'The investigation was not found, or you do not have access to it.', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        SELECT i.investigationId, i.investigationNumber, i.investigationType, i.locationId, i.organizationId,
               i.worksiteLocationId, i.exposureSourceId, i.investigationStatus, i.assignedToUserId, u.displayName AS assignedToName,
               i.requestedDate, i.scheduledDate, i.completedDate, i.findings, i.regulatoryAction, i.referralDate,
               i.referralOutcome, i.rowVersion, t.tenantName AS owningTenantName,
               l.addressLine1, l.unit, l.city, l.state, l.zip5, l.yearBuilt, o.organizationName,
               w.addressLine1 AS worksiteAddress, w.city AS worksiteCity, es.sourceType, es.sourceDescription
          FROM dbo.investigation AS i
          LEFT JOIN auth.appUser AS u ON u.appUserId = i.assignedToUserId
          LEFT JOIN auth.tenant AS t ON t.tenantId = i.owningTenantId
          LEFT JOIN dbo.location AS l ON l.locationId = i.locationId
          LEFT JOIN dbo.organization AS o ON o.organizationId = i.organizationId
          LEFT JOIN dbo.location AS w ON w.locationId = i.worksiteLocationId
          LEFT JOIN dbo.exposureSource AS es ON es.exposureSourceId = i.exposureSourceId
         WHERE i.investigationId = @investigationId;

        SELECT c.exposureCaseId, c.caseNumber, c.personId, p.lastName, p.firstName, a.code AS analyteCode, c.classification,
               c.caseStatus, e.jobTitle
          FROM dbo.investigationCase AS ic
          LEFT JOIN dbo.exposureCase AS c ON c.exposureCaseId = ic.exposureCaseId
          LEFT JOIN dbo.person AS p ON p.personId = c.personId
          LEFT JOIN config.analyte AS a ON a.analyteId = c.analyteId
          LEFT JOIN dbo.employment AS e ON e.employmentId = ic.employmentId
         WHERE ic.investigationId = @investigationId AND ic.isDeleted = 0;

        SELECT es.environmentalSampleId, es.sampleType, es.roomOrComponent, es.surface, es.sampleValue, es.unit, es.sampleDate,
               es.exceedsStandard, st.standardValue, st.unit AS standardUnit, st.authority, es.notes, es.modifiedDateUtc
          FROM dbo.environmentalSample AS es
          LEFT JOIN config.environmentalStandard AS st ON st.environmentalStandardId = es.environmentalStandardId
         WHERE es.investigationId = @investigationId AND es.isDeleted = 0
         ORDER BY es.sampleDate DESC, es.sampleType;

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
    , @ObjectName  = N'uspGetInvestigation'
    , @Description = N'The investigation page: the investigation and its target, the cases it serves, and its environmental samples with computed exceedance.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspGetInvestigation TO meritWebRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspGetInvestigation TO meritWorkerRole;
END;
GO
