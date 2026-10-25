-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspSearchOrganizations
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Server-side type-ahead for organizations by normalized name, optionally of one type, with their first site address so the user can tell branches apart.

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

exec dbo.uspSearchOrganizations ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T085
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspSearchOrganizations
      @nameKey              NVARCHAR (120)
      , @organizationTypeCode VARCHAR (30) = NULL
      , @maxRows              INT = 10
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspSearchOrganizations]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'organizationTypeCode=', @organizationTypeCode, N', maxRows=', @maxRows);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'person.view') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (person.view).', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        SELECT TOP (@maxRows) o.organizationId, o.organizationName, o.naicsCode, o.cliaNumber, o.npi,
               (SELECT STRING_AGG (ot.organizationTypeCode, ', ') FROM dbo.organizationType AS ot
                 WHERE ot.organizationId = o.organizationId AND ot.isDeleted = 0) AS organizationTypes,
               site.organizationSiteId, site.locationId AS siteLocationId, site.siteName,
               site.addressLine1 AS siteAddress, site.city AS siteCity
          FROM dbo.organization AS o
          OUTER APPLY (SELECT TOP (1) os.organizationSiteId, os.locationId, os.siteName, l.addressLine1, l.city
                         FROM dbo.organizationSite AS os JOIN dbo.location AS l ON l.locationId = os.locationId
                        WHERE os.organizationId = o.organizationId AND os.isDeleted = 0 AND os.endDate IS NULL
                        ORDER BY os.startDate) AS site
         WHERE o.isDeleted = 0 AND o.isActive = 1
           AND o.organizationNameKey LIKE N'%' + @nameKey + N'%'
           AND (@organizationTypeCode IS NULL OR EXISTS (SELECT 1 FROM dbo.organizationType AS ot
                                                          WHERE ot.organizationId = o.organizationId AND ot.isDeleted = 0
                                                            AND ot.organizationTypeCode = @organizationTypeCode))
         ORDER BY CASE WHEN o.organizationNameKey LIKE @nameKey + N'%' THEN 0 ELSE 1 END, o.organizationName;

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
    , @ObjectName  = N'uspSearchOrganizations'
    , @Description = N'Server-side type-ahead for organizations by normalized name, optionally of one type, with their first site address so the user can tell branches apart.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspSearchOrganizations TO meritWebRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspSearchOrganizations TO meritWorkerRole;
END;
GO
