-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspGetPersonCases
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Cases and investigations section: one row per case with its workflow, assignment and counts of open tasks and linked investigations.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission person.view (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspGetPersonCases ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T076
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspGetPersonCases
      @personId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspGetPersonCases]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'personId=', @personId);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'person.view') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (person.view).', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        SELECT c.exposureCaseId, c.caseNumber, a.code AS analyteCode, a.displayName AS analyteName, c.population,
               c.classification, c.computedClassification, c.caseStatus, c.exposureCategory, c.workflowTemplate,
               c.openedDate, c.closedDate, c.closureReason, u.displayName AS assignedToName, t.tenantName AS owningTenantName,
               hr.valueCanonical AS highestValue, hr.unitCanonical AS highestUnit,
               (SELECT COUNT (*) FROM dbo.workTask AS w WHERE w.exposureCaseId = c.exposureCaseId AND w.isDeleted = 0
                   AND w.taskStatus = 'open') AS openTasks,
               (SELECT COUNT (*) FROM dbo.investigationCase AS ic WHERE ic.exposureCaseId = c.exposureCaseId
                   AND ic.isDeleted = 0) AS investigations
          FROM dbo.exposureCase AS c
          JOIN config.analyte AS a ON a.analyteId = c.analyteId
          LEFT JOIN auth.appUser AS u ON u.appUserId = c.assignedToUserId
          LEFT JOIN auth.tenant AS t ON t.tenantId = c.owningTenantId
          LEFT JOIN dbo.labResult AS hr ON hr.labResultId = c.highestLabResultId
         WHERE c.personId = @personId AND c.isDeleted = 0
         ORDER BY CASE c.caseStatus WHEN 'closed' THEN 1 ELSE 0 END, c.openedDate DESC;

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
    , @ObjectName  = N'uspGetPersonCases'
    , @Description = N'Cases and investigations section: one row per case with its workflow, assignment and counts of open tasks and linked investigations.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspGetPersonCases TO meritWebRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspGetPersonCases TO meritWorkerRole;
END;
GO
