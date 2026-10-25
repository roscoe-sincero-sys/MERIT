-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspGetPerson
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

The person page header: identity, current primary residence county, open case chips per analyte, aliases, race values, contact methods and, with person.view_identifiers, external identifiers.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission person.view (auth.udfHasPermission); refuses with 51403 otherwise.

Six result sets. Throws 51404 when the person does not exist or is outside the caller's scope -- the two are indistinguishable on purpose.

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspGetPerson ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T076
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspGetPerson
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
                                                    , N'[dbo].[uspGetPerson]')
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

        IF NOT EXISTS (SELECT 1 FROM dbo.person WHERE personId = @personId AND isDeleted = 0)
        BEGIN
            ;THROW 51404, N'The person was not found, or you do not have access to it.', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @today DATE = CAST (SYSUTCDATETIME () AS DATE);

        SELECT p.personId, p.rootTenantId, p.lastName, p.firstName, p.middleName, p.dob, p.dobVerified, p.sex, p.ethnicity,
               p.preferredLanguage, p.deceasedDate, p.mergedIntoPersonId, p.isPlaceholderName, p.rowVersion,
               cur.countyFips AS currentCountyFips, cur.countyName AS currentCountyName, cur.locationId AS currentPrimaryLocationId,
               CAST (CASE WHEN EXISTS (SELECT 1 FROM dbo.labResult AS r WHERE r.personId = p.personId AND r.isDeleted = 0)
                          THEN 0 ELSE 1 END AS BIT) AS isContactOnly,
               CAST (CASE WHEN EXISTS (SELECT 1 FROM dbo.pregnancyEpisode AS e WHERE e.personId = p.personId AND e.isDeleted = 0)
                          THEN 1 ELSE 0 END AS BIT) AS hasPregnancyEpisodes
          FROM dbo.person AS p
          OUTER APPLY (SELECT TOP (1) l.locationId, l.countyFips, sj.subJurisdictionName AS countyName
                         FROM dbo.personLocation AS pl
                         JOIN dbo.location AS l ON l.locationId = pl.locationId
                         LEFT JOIN config.subJurisdiction AS sj ON sj.countyFips = l.countyFips AND sj.rootTenantId = p.rootTenantId
                        WHERE pl.personId = p.personId AND pl.isDeleted = 0 AND pl.role = 'residence' AND pl.isPrimary = 1
                          AND pl.startDate <= @today AND (pl.endDate IS NULL OR pl.endDate >= @today)
                        ORDER BY pl.startDate DESC) AS cur
         WHERE p.personId = @personId;

        SELECT personAliasId, lastName, firstName, middleName, aliasType, source, firstSeen, lastSeen
          FROM dbo.personAlias WHERE personId = @personId AND isDeleted = 0 ORDER BY lastSeen DESC;

        SELECT r.raceCode, lv.displayName
          FROM dbo.personRace AS r
          LEFT JOIN config.lookupValue AS lv ON lv.lookupType = 'race' AND lv.code = r.raceCode
         WHERE r.personId = @personId AND r.isDeleted = 0;

        SELECT personContactMethodId, contactType, contactValue, isPreferred
          FROM dbo.personContactMethod WHERE personId = @personId AND isDeleted = 0 ORDER BY contactType, isPreferred DESC;

        SELECT personIdentifierId, identifierType, identifierValue, assigningFacility, source, reportedDate
          FROM dbo.personIdentifier
         WHERE personId = @personId AND isDeleted = 0 AND auth.udfHasPermission (N'person.view_identifiers') = 1;

        SELECT c.exposureCaseId, c.caseNumber, a.code AS analyteCode, a.displayName AS analyteName, c.classification,
               c.caseStatus, c.population
          FROM dbo.exposureCase AS c
          JOIN config.analyte AS a ON a.analyteId = c.analyteId
         WHERE c.personId = @personId AND c.isDeleted = 0 AND c.caseStatus <> 'closed'
         ORDER BY a.sortOrder;

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
    , @ObjectName  = N'uspGetPerson'
    , @Description = N'The person page header: identity, current primary residence county, open case chips per analyte, aliases, race values, contact methods and, with person.view_identifiers, external identifiers.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspGetPerson TO meritWebRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspGetPerson TO meritWorkerRole;
END;
GO
