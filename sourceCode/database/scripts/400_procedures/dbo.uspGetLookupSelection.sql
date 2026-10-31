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
      @kind VARCHAR (20) = NULL
      , @id UNIQUEIDENTIFIER = NULL
      , @organizationTypeCode VARCHAR (30) = NULL
      , @selectionsJson NVARCHAR (MAX) = NULL
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
    SET @KeyParameters = CONCAT (N'kind=', @kind, N', id=', @id, N', organizationTypeCode=', @organizationTypeCode);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'person.view') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (person.view).', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @requested TABLE (kind VARCHAR (20), id UNIQUEIDENTIFIER, organizationTypeCode VARCHAR (30));
        IF @selectionsJson IS NULL
            INSERT @requested VALUES (@kind, @id, @organizationTypeCode);
        ELSE
            INSERT @requested SELECT kind, id, organizationTypeCode
              FROM OPENJSON (@selectionsJson) WITH (kind VARCHAR (20), id UNIQUEIDENTIFIER, organizationTypeCode VARCHAR (30));

        SELECT r.kind, r.id, CONVERT (NVARCHAR (80), p.personId) AS value, p.lastName AS name,
               p.firstName, CAST (NULL AS NVARCHAR (30)) AS credential, CAST (NULL AS NVARCHAR (30)) AS unit
          FROM @requested AS r JOIN dbo.person AS p ON p.personId = r.id
         WHERE r.kind = 'person' AND p.isDeleted = 0 AND p.mergedIntoPersonId IS NULL
        UNION ALL
        SELECT r.kind, r.id, CONVERT (NVARCHAR (80), o.organizationId), o.organizationName, NULL, NULL, NULL
          FROM @requested AS r JOIN dbo.organization AS o ON o.organizationId = r.id
         WHERE r.kind = 'organization' AND o.isDeleted = 0 AND o.isActive = 1
           AND (r.organizationTypeCode IS NULL OR EXISTS (SELECT 1 FROM dbo.organizationType AS ot
                 WHERE ot.organizationId = o.organizationId AND ot.isDeleted = 0 AND ot.organizationTypeCode = r.organizationTypeCode))
        UNION ALL
        SELECT r.kind, r.id, CONVERT (NVARCHAR (80), p.providerId), p.lastName, p.firstName, p.credential, NULL
          FROM @requested AS r JOIN dbo.provider AS p ON p.providerId = r.id
         WHERE r.kind = 'provider' AND p.isDeleted = 0
        UNION ALL
        SELECT r.kind, r.id, CONVERT (NVARCHAR (80), l.locationId), l.addressLine1, NULL, NULL, l.unit
          FROM @requested AS r JOIN dbo.location AS l ON l.locationId = r.id
         WHERE r.kind = 'location' AND l.isDeleted = 0
        UNION ALL
        SELECT r.kind, r.id, CONCAT (s.organizationSiteId, N':', s.locationId), o.organizationName, NULL, NULL, NULL
          FROM @requested AS r JOIN dbo.organizationSite AS s ON s.organizationSiteId = r.id
          JOIN dbo.organization AS o ON o.organizationId = s.organizationId AND o.isDeleted = 0 AND o.isActive = 1
          JOIN dbo.location AS l ON l.locationId = s.locationId AND l.isDeleted = 0
         WHERE r.kind = 'site' AND s.isDeleted = 0 AND s.endDate IS NULL
           AND (r.organizationTypeCode IS NULL OR EXISTS (SELECT 1 FROM dbo.organizationType AS ot
                 WHERE ot.organizationId = o.organizationId AND ot.isDeleted = 0 AND ot.organizationTypeCode = r.organizationTypeCode));

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
