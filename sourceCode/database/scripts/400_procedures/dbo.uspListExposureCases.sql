-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspListExposureCases
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

One page of cases visible to the caller, optionally only open ones, only theirs, or one analyte.

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

exec dbo.uspListExposureCases ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T080
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspListExposureCases
      @openOnly       BIT = 1
      , @assignedToMe   BIT = 0
      , @analyteId      UNIQUEIDENTIFIER = NULL
      , @pageNumber     INT = 1
      , @pageSize       INT = 25
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspListExposureCases]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'openOnly=', @openOnly, N', assignedToMe=', @assignedToMe, N', pageNumber=', @pageNumber);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'case.view') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (case.view).', 1;
        END;

        IF @pageNumber < 1 OR @pageSize NOT BETWEEN 1 AND 500
        BEGIN
            ;THROW 50000, N'@pageNumber must be 1 or more and @pageSize between 1 and 500.', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @me UNIQUEIDENTIFIER = auth.udfSessionUserId ();

        SELECT c.exposureCaseId, c.caseNumber, c.personId, p.lastName, p.firstName, p.dob, a.code AS analyteCode,
               c.population, c.classification, c.caseStatus, c.openedDate, u.displayName AS assignedToName,
               t.tenantName AS owningTenantName, COUNT (*) OVER () AS totalCount
          FROM dbo.exposureCase AS c
          JOIN dbo.person AS p ON p.personId = c.personId
          JOIN config.analyte AS a ON a.analyteId = c.analyteId
          LEFT JOIN auth.appUser AS u ON u.appUserId = c.assignedToUserId
          LEFT JOIN auth.tenant AS t ON t.tenantId = c.owningTenantId
         WHERE c.isDeleted = 0
           AND (@openOnly = 0 OR c.caseStatus <> 'closed')
           AND (@assignedToMe = 0 OR c.assignedToUserId = @me)
           AND (@analyteId IS NULL OR c.analyteId = @analyteId)
         ORDER BY c.openedDate DESC, c.exposureCaseId
        OFFSET (@pageNumber - 1) * @pageSize ROWS
         FETCH NEXT @pageSize ROWS ONLY;

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
    , @ObjectName  = N'uspListExposureCases'
    , @Description = N'One page of cases visible to the caller, optionally only open ones, only theirs, or one analyte.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspListExposureCases TO meritWebRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspListExposureCases TO meritWorkerRole;
END;
GO
