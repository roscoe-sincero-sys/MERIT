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

One page of cases visible to the caller, filtered by status, analyte, classification, population, owning tenant, assignee (me, a person, or nobody) and a case number or last name, sorted by opened date, case number, classification or person.

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
      @openOnly         BIT = 1
      , @assignedToMe     BIT = 0
      , @analyteId        UNIQUEIDENTIFIER = NULL
      , @classification   VARCHAR (12) = NULL
      , @population       VARCHAR (10) = NULL
      , @owningTenantId   UNIQUEIDENTIFIER = NULL
      , @assignedToUserId UNIQUEIDENTIFIER = NULL
      , @unassignedOnly   BIT = 0
      , @search           NVARCHAR (60) = NULL
      , @sort             VARCHAR (20) = 'opened'
      , @descending       BIT = 1
      , @pageNumber       INT = 1
      , @pageSize         INT = 25
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
    SET @KeyParameters = CONCAT (N'openOnly=', @openOnly, N', assignedToMe=', @assignedToMe, N', unassignedOnly=', @unassignedOnly, N', classification=', @classification, N', population=', @population, N', sort=', @sort, N', pageNumber=', @pageNumber);

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
        IF @sort NOT IN ('opened', 'case', 'classification', 'person')
        BEGIN
            ;THROW 50000, N'@sort must be opened, case, classification or person.', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @me UNIQUEIDENTIFIER = auth.udfSessionUserId ();
        -- The search text matches literally: LIKE wildcards in it are escaped.
        DECLARE @like NVARCHAR (200) = REPLACE (REPLACE (REPLACE (NULLIF (LTRIM (RTRIM (@search)), N''), N'[', N'[[]'), N'%', N'[%]'), N'_', N'[_]');

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
           AND (@unassignedOnly = 0 OR c.assignedToUserId IS NULL)
           AND (@assignedToUserId IS NULL OR c.assignedToUserId = @assignedToUserId)
           AND (@analyteId IS NULL OR c.analyteId = @analyteId)
           AND (@classification IS NULL OR c.classification = @classification)
           AND (@population IS NULL OR c.population = @population)
           AND (@owningTenantId IS NULL OR c.owningTenantId = @owningTenantId)
           AND (@like IS NULL OR c.caseNumber LIKE N'%' + @like + N'%' OR p.lastName LIKE @like + N'%')
         ORDER BY CASE WHEN @sort = 'opened' AND @descending = 0 THEN c.openedDate END ASC,
                  CASE WHEN @sort = 'opened' AND @descending = 1 THEN c.openedDate END DESC,
                  CASE WHEN @sort = 'case' AND @descending = 0 THEN c.caseNumber END ASC,
                  CASE WHEN @sort = 'case' AND @descending = 1 THEN c.caseNumber END DESC,
                  -- Most serious first when ascending.
                  CASE WHEN @sort = 'classification' AND @descending = 0
                       THEN CASE c.classification WHEN 'confirmed' THEN 1 WHEN 'suspect' THEN 2 ELSE 3 END END ASC,
                  CASE WHEN @sort = 'classification' AND @descending = 1
                       THEN CASE c.classification WHEN 'confirmed' THEN 1 WHEN 'suspect' THEN 2 ELSE 3 END END DESC,
                  CASE WHEN @sort = 'person' AND @descending = 0 THEN p.lastName END ASC,
                  CASE WHEN @sort = 'person' AND @descending = 0 THEN p.firstName END ASC,
                  CASE WHEN @sort = 'person' AND @descending = 1 THEN p.lastName END DESC,
                  CASE WHEN @sort = 'person' AND @descending = 1 THEN p.firstName END DESC,
                  c.openedDate DESC, c.exposureCaseId
        OFFSET (@pageNumber - 1) * @pageSize ROWS
         FETCH NEXT @pageSize ROWS ONLY
        OPTION (RECOMPILE);

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
    , @Description = N'One page of cases visible to the caller, filtered by status, analyte, classification, population, owning tenant, assignee (me, a person, or nobody) and a case number or last name, sorted by opened date, case number, classification or person.';
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
