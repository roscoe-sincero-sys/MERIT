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

        DECLARE @isDbo BIT = CASE WHEN USER_NAME () = N'dbo' THEN 1 ELSE 0 END;
        DECLARE @idTenants TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
        IF @isDbo = 0
        BEGIN
            INSERT @idTenants (tenantId)
            SELECT tenantId
              FROM auth.tvfIdentifiedTenants ('case.view');
        END;

        DECLARE @invIdentified BIT = CASE WHEN @isDbo = 1
                                            OR EXISTS (SELECT 1 FROM dbo.investigation AS iv
                                                         JOIN @idTenants AS idt ON idt.tenantId = iv.owningTenantId
                                                        WHERE iv.investigationId = @investigationId)
                                          THEN 1 ELSE 0 END;

        SELECT i.investigationId, i.investigationNumber, i.investigationType,
               CASE WHEN @invIdentified = 1 THEN i.locationId ELSE NULL END AS locationId,
               CASE WHEN @invIdentified = 1 THEN i.organizationId ELSE NULL END AS organizationId,
               CASE WHEN @invIdentified = 1 THEN i.worksiteLocationId ELSE NULL END AS worksiteLocationId,
               CASE WHEN @invIdentified = 1 THEN i.exposureSourceId ELSE NULL END AS exposureSourceId,
               i.investigationStatus, i.assignedToUserId, u.displayName AS assignedToName,
               i.requestedDate, i.scheduledDate, i.completedDate,
               CASE WHEN @invIdentified = 1 THEN i.findings ELSE NULL END AS findings,
               i.regulatoryAction, i.referralDate,
               i.referralOutcome, i.rowVersion, i.owningTenantId, t.tenantName AS owningTenantName,
               CASE WHEN @invIdentified = 1 THEN l.addressLine1 ELSE NULL END AS addressLine1,
               CASE WHEN @invIdentified = 1 THEN l.unit ELSE NULL END AS unit,
               CASE WHEN @invIdentified = 1 THEN l.city ELSE NULL END AS city,
               CASE WHEN @invIdentified = 1 THEN l.state ELSE NULL END AS state,
               CASE WHEN @invIdentified = 1 THEN l.zip5 ELSE NULL END AS zip5,
               l.yearBuilt,
               CASE WHEN @invIdentified = 1 THEN o.organizationName ELSE NULL END AS organizationName,
               CASE WHEN @invIdentified = 1 THEN w.addressLine1 ELSE NULL END AS worksiteAddress,
               CASE WHEN @invIdentified = 1 THEN w.city ELSE NULL END AS worksiteCity,
               es.sourceType,
               CASE WHEN @invIdentified = 1 THEN es.sourceDescription ELSE NULL END AS sourceDescription,
               CAST (@invIdentified AS BIT) AS isIdentified
          FROM dbo.investigation AS i
          LEFT JOIN auth.appUser AS u ON u.appUserId = i.assignedToUserId
          LEFT JOIN auth.tenant AS t ON t.tenantId = i.owningTenantId
          LEFT JOIN dbo.location AS l ON l.locationId = i.locationId
          LEFT JOIN dbo.organization AS o ON o.organizationId = i.organizationId
          LEFT JOIN dbo.location AS w ON w.locationId = i.worksiteLocationId
          LEFT JOIN dbo.exposureSource AS es ON es.exposureSourceId = i.exposureSourceId
         WHERE i.investigationId = @investigationId;

        SELECT c.exposureCaseId, c.caseNumber,
               CASE WHEN c.exposureCaseId IS NULL THEN NULL
                    WHEN @isDbo = 1 OR idt.tenantId IS NOT NULL THEN c.personId
                    ELSE CAST (0x0 AS UNIQUEIDENTIFIER) END AS personId,
               CASE WHEN c.exposureCaseId IS NULL THEN NULL
                    WHEN @isDbo = 1 OR idt.tenantId IS NOT NULL THEN p.lastName
                    ELSE N'De-identified' END AS lastName,
               CASE WHEN @isDbo = 1 OR idt.tenantId IS NOT NULL THEN p.firstName ELSE NULL END AS firstName,
               a.code AS analyteCode, c.classification,
               c.caseStatus,
               CASE WHEN @isDbo = 1 OR idt.tenantId IS NOT NULL THEN e.jobTitle ELSE NULL END AS jobTitle
          FROM dbo.investigationCase AS ic
          LEFT JOIN dbo.exposureCase AS c ON c.exposureCaseId = ic.exposureCaseId
          LEFT JOIN @idTenants AS idt ON idt.tenantId = c.owningTenantId
          LEFT JOIN dbo.person AS p ON p.personId = c.personId
          LEFT JOIN config.analyte AS a ON a.analyteId = c.analyteId
          LEFT JOIN dbo.employment AS e ON e.employmentId = ic.employmentId
         WHERE ic.investigationId = @investigationId AND ic.isDeleted = 0;

        SELECT es.environmentalSampleId, es.sampleType, es.roomOrComponent, es.surface, es.sampleValue, es.unit, es.sampleDate,
               es.exceedsStandard, st.standardValue, st.unit AS standardUnit, st.authority,
               CASE WHEN @invIdentified = 1 THEN es.notes ELSE NULL END AS notes,
               es.modifiedDateUtc
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
