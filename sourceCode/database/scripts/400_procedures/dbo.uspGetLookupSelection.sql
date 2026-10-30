-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspGetLookupSelection
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Rehydrates one selected lookup from its authorized identifier; RLS applies to every source.

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

exec dbo.uspGetLookupSelection ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		#66
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspGetLookupSelection
      @kind VARCHAR (20)
      , @id UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspGetLookupSelection]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'kind=', @kind, N', id=', @id);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'person.view') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (person.view).', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        SELECT CONVERT (NVARCHAR (80), p.personId) AS value, CONCAT (p.lastName, N', ', p.firstName) AS label
          FROM dbo.person AS p WHERE @kind = 'person' AND p.personId = @id AND p.isDeleted = 0
        UNION ALL
        SELECT CONVERT (NVARCHAR (80), o.organizationId), o.organizationName
          FROM dbo.organization AS o WHERE @kind = 'organization' AND o.organizationId = @id AND o.isDeleted = 0
        UNION ALL
        SELECT CONVERT (NVARCHAR (80), p.providerId), RTRIM (CONCAT (p.lastName, N', ', p.firstName, N' ', p.credential))
          FROM dbo.provider AS p WHERE @kind = 'provider' AND p.providerId = @id AND p.isDeleted = 0
        UNION ALL
        SELECT CONVERT (NVARCHAR (80), l.locationId), CONCAT (l.addressLine1, CASE WHEN l.unit IS NULL THEN N'' ELSE N' ' + l.unit END)
          FROM dbo.location AS l WHERE @kind = 'location' AND l.locationId = @id AND l.isDeleted = 0
        UNION ALL
        SELECT CONCAT (s.organizationSiteId, N':', s.locationId), CONCAT (o.organizationName, N' - ', l.addressLine1, N', ', l.city)
          FROM dbo.organizationSite AS s
          JOIN dbo.organization AS o ON o.organizationId = s.organizationId AND o.isDeleted = 0
          JOIN dbo.location AS l ON l.locationId = s.locationId AND l.isDeleted = 0
         WHERE @kind = 'site' AND s.organizationSiteId = @id AND s.isDeleted = 0;

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
    , @ObjectName  = N'uspGetLookupSelection'
    , @Description = N'Rehydrates one selected lookup from its authorized identifier; RLS applies to every source.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspGetLookupSelection TO meritWebRole;
END;
GO
