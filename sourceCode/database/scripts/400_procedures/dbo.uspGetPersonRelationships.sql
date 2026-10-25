-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspGetPersonRelationships
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Relationships section: links in both directions. A row where the person is the related person shows the role's inverse label, so a parent's page reads 'child of'.

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

exec dbo.uspGetPersonRelationships ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T076
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspGetPersonRelationships
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
                                                    , N'[dbo].[uspGetPersonRelationships]')
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

        SELECT r.personRelationshipId, CAST (0 AS BIT) AS isInverse, r.relatedPersonId AS otherPersonId,
               o.lastName AS otherLastName, o.firstName AS otherFirstName, o.dob AS otherDob,
               r.role, COALESCE (lv.displayName, r.role) AS roleLabel, r.isPrimaryContact, r.hasLegalCustody,
               r.startDate, r.endDate, r.source, r.verifiedDate, r.rowVersion,
               CAST (CASE WHEN EXISTS (SELECT 1 FROM dbo.labResult AS x WHERE x.personId = o.personId AND x.isDeleted = 0)
                          THEN 0 ELSE 1 END AS BIT) AS otherIsContactOnly
          FROM dbo.personRelationship AS r
          JOIN dbo.person AS o ON o.personId = r.relatedPersonId
          LEFT JOIN config.lookupValue AS lv ON lv.lookupType = 'relationshipRole' AND lv.code = r.role
         WHERE r.personId = @personId AND r.isDeleted = 0
        UNION ALL
        SELECT r.personRelationshipId, CAST (1 AS BIT), r.personId, o.lastName, o.firstName, o.dob,
               r.role, COALESCE (lv.inverseLabel, r.role), CAST (0 AS BIT), CAST (0 AS BIT),
               r.startDate, r.endDate, r.source, r.verifiedDate, r.rowVersion,
               CAST (CASE WHEN EXISTS (SELECT 1 FROM dbo.labResult AS x WHERE x.personId = o.personId AND x.isDeleted = 0)
                          THEN 0 ELSE 1 END AS BIT)
          FROM dbo.personRelationship AS r
          JOIN dbo.person AS o ON o.personId = r.personId
          LEFT JOIN config.lookupValue AS lv ON lv.lookupType = 'relationshipRole' AND lv.code = r.role
         WHERE r.relatedPersonId = @personId AND r.isDeleted = 0
         ORDER BY isInverse, endDate, roleLabel;

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
    , @ObjectName  = N'uspGetPersonRelationships'
    , @Description = N'Relationships section: links in both directions. A row where the person is the related person shows the role''s inverse label, so a parent''s page reads ''child of''.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspGetPersonRelationships TO meritWebRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspGetPersonRelationships TO meritWorkerRole;
END;
GO
