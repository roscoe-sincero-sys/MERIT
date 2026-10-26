-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspListReporters
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

The reporter registry: laboratories and facilities (organizations with a CLIA number or NPI) and individual providers, filtered by name or identifier and by kind, for the Reporters screen.

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

exec dbo.uspListReporters ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T163
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspListReporters
      @text    NVARCHAR (120) = NULL
      , @kind    VARCHAR (20) = NULL
      , @maxRows INT = 200
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspListReporters]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'kind=', @kind, N', maxRows=', @maxRows);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'person.view') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (person.view).', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @t NVARCHAR (120) = NULLIF (LTRIM (RTRIM (@text)), N'');

        SELECT TOP (@maxRows) x.reporterKind, x.reporterId, x.reporterName, x.cliaNumber, x.npi, x.credential, x.phone,
               x.registryStatus, x.modifiedDateUtc
          FROM (SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.organizationType AS ot WHERE ot.organizationId = o.organizationId
                                                                                 AND ot.organizationTypeCode = 'laboratory'
                                                                                 AND ot.isDeleted = 0)
                            THEN 'laboratory' ELSE 'facility' END AS reporterKind,
                       o.organizationId AS reporterId, o.organizationName AS reporterName, o.cliaNumber, o.npi,
                       CAST (NULL AS NVARCHAR (20)) AS credential, o.phone, CAST (NULL AS VARCHAR (20)) AS registryStatus,
                       o.modifiedDateUtc
                  FROM dbo.organization AS o
                 WHERE o.isDeleted = 0 AND o.isActive = 1 AND (o.cliaNumber IS NOT NULL OR o.npi IS NOT NULL)
                   AND (@t IS NULL OR o.organizationName LIKE N'%' + @t + N'%' OR o.cliaNumber = @t OR o.npi = @t)
                UNION ALL
                SELECT 'provider', pv.providerId, CONCAT (pv.lastName, CASE WHEN pv.firstName IS NULL THEN N'' ELSE N', ' + pv.firstName END),
                       NULL, pv.npi, pv.credential, NULL, pv.registryStatus, pv.modifiedDateUtc
                  FROM dbo.provider AS pv
                 WHERE pv.isDeleted = 0
                   AND (@t IS NULL OR pv.lastName LIKE @t + N'%' OR pv.firstName LIKE @t + N'%' OR pv.npi = @t)) AS x
         WHERE @kind IS NULL OR x.reporterKind = @kind
         ORDER BY x.reporterKind, x.reporterName;

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
    , @ObjectName  = N'uspListReporters'
    , @Description = N'The reporter registry: laboratories and facilities (organizations with a CLIA number or NPI) and individual providers, filtered by name or identifier and by kind, for the Reporters screen.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspListReporters TO meritWebRole;
END;
GO
