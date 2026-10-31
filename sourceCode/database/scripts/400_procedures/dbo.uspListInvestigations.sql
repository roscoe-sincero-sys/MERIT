-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspListInvestigations
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

One page of investigations visible to the caller, optionally only open ones or only theirs.

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

exec dbo.uspListInvestigations ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T081
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspListInvestigations
      @openOnly     BIT = 1
      , @assignedToMe BIT = 0
      , @pageNumber   INT = 1
      , @pageSize     INT = 25
      , @searchText NVARCHAR (200) = NULL
      , @assignedFilter VARCHAR (40) = NULL
      , @statusFilter VARCHAR (20) = NULL
      , @typeFilter VARCHAR (30) = NULL
      , @sortBy VARCHAR (20) = 'requested'
      , @sortDescending BIT = 1
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspListInvestigations]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'openOnly=', @openOnly, N', pageNumber=', @pageNumber);

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

        SELECT i.investigationId, i.investigationNumber, i.investigationType, i.investigationStatus, i.requestedDate,
               i.scheduledDate, COALESCE (l.addressLine1, o.organizationName, es.sourceDescription) AS targetLabel,
               u.displayName AS assignedToName, COUNT (*) OVER () AS totalCount
          FROM dbo.investigation AS i
          LEFT JOIN dbo.location AS l ON l.locationId = i.locationId
          LEFT JOIN dbo.organization AS o ON o.organizationId = i.organizationId
          LEFT JOIN dbo.exposureSource AS es ON es.exposureSourceId = i.exposureSourceId
          LEFT JOIN auth.appUser AS u ON u.appUserId = i.assignedToUserId
         WHERE i.isDeleted = 0
           AND (@openOnly = 0 OR i.investigationStatus NOT IN ('cleared', 'closed'))
           AND (@assignedToMe = 0 OR i.assignedToUserId = @me)
           AND (NULLIF (@searchText, N'') IS NULL OR i.investigationNumber LIKE N'%' + @searchText + N'%'
                OR l.addressLine1 LIKE N'%' + @searchText + N'%' OR o.organizationName LIKE N'%' + @searchText + N'%')
           AND (NULLIF (@statusFilter, '') IS NULL OR i.investigationStatus = @statusFilter)
           AND (NULLIF (@typeFilter, '') IS NULL OR i.investigationType = @typeFilter)
           AND (NULLIF (@assignedFilter, '') IS NULL OR (@assignedFilter = 'me' AND i.assignedToUserId = @me)
                OR (@assignedFilter = 'unassigned' AND i.assignedToUserId IS NULL)
                OR i.assignedToUserId = TRY_CAST (@assignedFilter AS UNIQUEIDENTIFIER))
         ORDER BY CASE WHEN @sortBy = 'number' AND @sortDescending = 0 THEN i.investigationNumber END ASC,
                  CASE WHEN @sortBy = 'number' AND @sortDescending = 1 THEN i.investigationNumber END DESC,
                  CASE WHEN @sortBy = 'status' AND @sortDescending = 0 THEN i.investigationStatus END ASC,
                  CASE WHEN @sortBy = 'status' AND @sortDescending = 1 THEN i.investigationStatus END DESC,
                  CASE WHEN @sortBy = 'requested' AND @sortDescending = 0 THEN i.requestedDate END ASC,
                  CASE WHEN @sortBy = 'requested' AND @sortDescending = 1 THEN i.requestedDate END DESC, i.investigationId
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
    , @ObjectName  = N'uspListInvestigations'
    , @Description = N'One page of investigations visible to the caller, optionally only open ones or only theirs.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspListInvestigations TO meritWebRole;
END;
GO
