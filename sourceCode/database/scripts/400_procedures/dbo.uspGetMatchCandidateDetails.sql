-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspGetMatchCandidateDetails
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

What the review screen shows for each candidate person: identity and aliases, place history and recent results, so the reviewer can compare field by field and open the history in one click.

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

exec dbo.uspGetMatchCandidateDetails ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T108
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspGetMatchCandidateDetails
      @personIdsJson NVARCHAR (MAX)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspGetMatchCandidateDetails]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = N'candidates';

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'person.view') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (person.view).', 1;
        END;

        IF ISJSON (@personIdsJson) = 0
        BEGIN
            ;THROW 50000, N'@personIdsJson must be a JSON array of ids.', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @ids TABLE (personId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @ids SELECT DISTINCT TRY_CAST ([value] AS UNIQUEIDENTIFIER) FROM OPENJSON (@personIdsJson)
                     WHERE TRY_CAST ([value] AS UNIQUEIDENTIFIER) IS NOT NULL;

        SELECT p.personId, p.lastName, p.firstName, p.middleName, p.dob, p.sex, p.isPlaceholderName,
               (SELECT STRING_AGG (CONCAT (a.lastName, N', ', a.firstName), N'; ')
                  FROM dbo.personAlias AS a WHERE a.personId = p.personId AND a.isDeleted = 0
                   AND (a.lastName <> p.lastName OR COALESCE (a.firstName, N'') <> COALESCE (p.firstName, N''))) AS aliases,
               (SELECT COUNT (*) FROM dbo.labResult AS r WHERE r.personId = p.personId AND r.isDeleted = 0
                   AND r.resultRowStatus = 'valid') AS resultCount,
               (SELECT MAX (r.collectedDate) FROM dbo.labResult AS r WHERE r.personId = p.personId AND r.isDeleted = 0) AS lastCollectedDate,
               (SELECT STRING_AGG (c.caseNumber, ', ') FROM dbo.exposureCase AS c
                 WHERE c.personId = p.personId AND c.caseStatus <> 'closed' AND c.isDeleted = 0) AS openCaseNumbers
          FROM dbo.person AS p
          JOIN @ids AS i ON i.personId = p.personId
         WHERE p.isDeleted = 0;

        SELECT pl.personId, pl.role, pl.isPrimary, pl.startDate, pl.endDate, l.addressLine1, l.unit, l.city, l.state, l.zip5,
               l.addressKey
          FROM dbo.personLocation AS pl
          JOIN @ids AS i ON i.personId = pl.personId
          JOIN dbo.location AS l ON l.locationId = pl.locationId
         WHERE pl.isDeleted = 0
         ORDER BY pl.personId, CASE WHEN pl.endDate IS NULL THEN 0 ELSE 1 END, pl.startDate DESC;

        SELECT x.personId, x.collectedDate, x.analyteCode, x.operator, x.valueCanonical, x.unitCanonical, x.sampleType, x.labName
          FROM (SELECT r.personId, r.collectedDate, a.code AS analyteCode, r.operator, r.valueCanonical, r.unitCanonical,
                       r.sampleType, lab.organizationName AS labName,
                       ROW_NUMBER () OVER (PARTITION BY r.personId ORDER BY r.collectedDate DESC) AS rowNo
                  FROM dbo.labResult AS r
                  JOIN @ids AS i ON i.personId = r.personId
                  JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId = r.analyteSpecimenId
                  JOIN config.analyte AS a ON a.analyteId = s.analyteId
                  LEFT JOIN dbo.organization AS lab ON lab.organizationId = r.labOrganizationId
                 WHERE r.isDeleted = 0 AND r.resultRowStatus = 'valid') AS x
         WHERE x.rowNo <= 10
         ORDER BY x.personId, x.collectedDate DESC;

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
    , @ObjectName  = N'uspGetMatchCandidateDetails'
    , @Description = N'What the review screen shows for each candidate person: identity and aliases, place history and recent results, so the reviewer can compare field by field and open the history in one click.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspGetMatchCandidateDetails TO meritWebRole;
END;
GO
