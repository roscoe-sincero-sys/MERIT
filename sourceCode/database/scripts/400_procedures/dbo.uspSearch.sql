-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspSearch
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Global search by name (with aliases), date of birth, address, organization, accession number or case number, within the caller's access scope. Writes the search text to the purgeable logs.searchLog and, on an exact name and DOB match outside scope, reports only that a record exists elsewhere.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission person.view (auth.udfHasPermission); refuses with 51403 otherwise.

The application normalizes the text into name keys (upper case, letters only) and an address key; the raw text is used as-is for accession and case numbers. Search text never reaches @KeyParameters or the audit ledger. Result sets: persons, cases, organizations, then one row with existsElsewhere.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspSearch ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T075
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspSearch
      @searchText      NVARCHAR (200)
      , @nameKey1        NVARCHAR (60) = NULL
      , @nameKey2        NVARCHAR (60) = NULL
      , @dob             DATE = NULL
      , @addressKey      NVARCHAR (300) = NULL
      , @maxRows         INT = 50
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspSearch]')
          , @StartTimeUtc   DATETIME2 (3)  = SYSUTCDATETIME ()
          , @EndTimeUtc     DATETIME2 (3)  = NULL
          , @ExecutionId    BIGINT         = NULL
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @Comments       NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never a credential, a payload, or patient data.
    SET @KeyParameters = CONCAT (N'maxRows=', @maxRows, N', hasDob=', IIF (@dob IS NULL, 0, 1));

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        IF auth.udfHasPermission (N'person.view') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (person.view).', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @found INT = 0, @elsewhere BIT = 0;
        DECLARE @term NVARCHAR (200) = LTRIM (RTRIM (@searchText));
        DECLARE @persons TABLE (personId UNIQUEIDENTIFIER PRIMARY KEY, matchedOn NVARCHAR (40));

        IF @nameKey1 IS NOT NULL
        BEGIN
            -- A person found by name and by an alias is one row; 'name' wins (MAX), since it is the stronger reason.
            INSERT @persons (personId, matchedOn)
            SELECT TOP (@maxRows) x.personId, MAX (x.matchedOn)
              FROM (SELECT p.personId, N'name' AS matchedOn
                      FROM dbo.person AS p
                     WHERE p.isDeleted = 0 AND p.mergedIntoPersonId IS NULL
                       AND ((p.lastNameKey LIKE @nameKey1 + N'%' AND (@nameKey2 IS NULL OR p.firstNameKey LIKE @nameKey2 + N'%'))
                         OR (@nameKey2 IS NOT NULL AND p.lastNameKey LIKE @nameKey2 + N'%' AND p.firstNameKey LIKE @nameKey1 + N'%'))
                       AND (@dob IS NULL OR p.dob = @dob)
                    UNION
                    SELECT a.personId, N'alias'
                      FROM dbo.personAlias AS a
                      JOIN dbo.person AS p ON p.personId = a.personId AND p.isDeleted = 0 AND p.mergedIntoPersonId IS NULL
                     WHERE a.isDeleted = 0
                       AND ((a.lastNameKey LIKE @nameKey1 + N'%' AND (@nameKey2 IS NULL OR a.firstNameKey LIKE @nameKey2 + N'%'))
                         OR (@nameKey2 IS NOT NULL AND a.lastNameKey LIKE @nameKey2 + N'%' AND a.firstNameKey LIKE @nameKey1 + N'%'))
                       AND (@dob IS NULL OR p.dob = @dob)) AS x
             GROUP BY x.personId;
        END
        ELSE IF @dob IS NOT NULL
        BEGIN
            INSERT @persons (personId, matchedOn)
            SELECT TOP (@maxRows) p.personId, N'dob' FROM dbo.person AS p
             WHERE p.isDeleted = 0 AND p.dob = @dob AND p.mergedIntoPersonId IS NULL;
        END;

        INSERT @persons (personId, matchedOn)
        SELECT DISTINCT r.personId, N'accession'
          FROM dbo.labResult AS r
         WHERE r.isDeleted = 0 AND r.personId IS NOT NULL AND r.accessionNumber = @term
           AND NOT EXISTS (SELECT 1 FROM @persons AS x WHERE x.personId = r.personId);

        IF @addressKey IS NOT NULL AND LEN (@addressKey) >= 4
        BEGIN
            INSERT @persons (personId, matchedOn)
            SELECT DISTINCT TOP (@maxRows) pl.personId, N'address'
              FROM dbo.location AS l
              JOIN dbo.personLocation AS pl ON pl.locationId = l.locationId AND pl.isDeleted = 0
             WHERE l.isDeleted = 0 AND l.addressKey LIKE @addressKey + N'%'
               AND NOT EXISTS (SELECT 1 FROM @persons AS x WHERE x.personId = pl.personId);
        END;

        SELECT TOP (@maxRows) p.personId, p.lastName, p.firstName, p.middleName, p.dob, p.sex, x.matchedOn,
               cur.countyName AS currentCounty,
               (SELECT STRING_AGG (CONCAT (a.code, N':', c.classification), N', ')
                  FROM dbo.exposureCase AS c JOIN config.analyte AS a ON a.analyteId = c.analyteId
                 WHERE c.personId = p.personId AND c.isDeleted = 0 AND c.caseStatus <> 'closed') AS openCases
          FROM @persons AS x
          JOIN dbo.person AS p ON p.personId = x.personId
          OUTER APPLY (SELECT TOP (1) sj.subJurisdictionName AS countyName
                         FROM dbo.personLocation AS pl
                         JOIN dbo.location AS l ON l.locationId = pl.locationId
                         LEFT JOIN config.subJurisdiction AS sj ON sj.countyFips = l.countyFips AND sj.rootTenantId = p.rootTenantId
                        WHERE pl.personId = p.personId AND pl.isDeleted = 0 AND pl.role = 'residence' AND pl.isPrimary = 1
                          AND pl.startDate <= CAST (SYSUTCDATETIME () AS DATE)
                          AND (pl.endDate IS NULL OR pl.endDate >= CAST (SYSUTCDATETIME () AS DATE))) AS cur
         ORDER BY p.lastName, p.firstName, p.dob;

        SET @found = @@ROWCOUNT;

        SELECT c.exposureCaseId, c.caseNumber, c.personId, p.lastName, p.firstName, a.code AS analyteCode, c.classification,
               c.caseStatus, c.openedDate
          FROM dbo.exposureCase AS c
          JOIN dbo.person AS p ON p.personId = c.personId
          JOIN config.analyte AS a ON a.analyteId = c.analyteId
         WHERE c.isDeleted = 0 AND c.caseNumber = @term;

        SET @found = @found + @@ROWCOUNT;

        SELECT TOP (@maxRows) o.organizationId, o.organizationName, o.naicsCode, o.cliaNumber,
               (SELECT STRING_AGG (ot.organizationTypeCode, ', ') FROM dbo.organizationType AS ot
                 WHERE ot.organizationId = o.organizationId AND ot.isDeleted = 0) AS organizationTypes
          FROM dbo.organization AS o
         WHERE o.isDeleted = 0 AND @nameKey1 IS NOT NULL
           AND o.organizationNameKey LIKE @nameKey1 + N'%' + COALESCE (@nameKey2 + N'%', N'')
         ORDER BY o.organizationName;

        SET @found = @found + @@ROWCOUNT;

        -- Outside scope: only on an exact name and DOB match, through the match-key table, and every hit is audited.
        IF @found = 0 AND @nameKey1 IS NOT NULL AND @nameKey2 IS NOT NULL AND @dob IS NOT NULL
           AND EXISTS (SELECT 1 FROM dbo.personMatchKey AS mk
                        WHERE mk.rootTenantId = auth.udfSessionRootTenantId () AND mk.isDeleted = 0 AND mk.dob = @dob
                          AND ((mk.lastNameKey = @nameKey1 AND mk.firstNameKey = @nameKey2)
                            OR (mk.lastNameKey = @nameKey2 AND mk.firstNameKey = @nameKey1)))
        BEGIN
            SET @elsewhere = 1;
            INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId, recordCount)
            VALUES (auth.udfSessionUserId (), auth.udfSessionRootTenantId (), 'search', 'personOutsideScope', NULL, 1);
        END;

        SELECT @elsewhere AS existsElsewhere;

        INSERT logs.searchLog (searchLogId, appUserId, rootTenantId, searchText, resultCount, searchedUtc)
        VALUES (CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), COALESCE (auth.udfSessionUserId (), '00000000-0000-0000-0000-000000000000'),
                COALESCE (auth.udfSessionRootTenantId (), '00000000-0000-0000-0000-000000000000'), LEFT (@searchText, 400),
                @found, SYSUTCDATETIME ());

        SET @Comments = CONCAT (@found, N' match(es).');

        -- =========================================================================================
        -- ===== End of the procedure's own work. ===================================================
        -- =========================================================================================

        IF @@TRANCOUNT > 0
        BEGIN
            COMMIT TRANSACTION;
        END;

        -- Completion. Deliberately after the COMMIT.
        SET @EndTimeUtc = SYSUTCDATETIME ();

        IF @ExecutionId IS NOT NULL
        BEGIN
            UPDATE logs.ExecutionLog
               SET EndDateUtc          = @EndTimeUtc
                 , ElapsedMilliseconds = CAST (LEAST (DATEDIFF_BIG (MILLISECOND, @StartTimeUtc, @EndTimeUtc)
                                                    , CAST (2147483647 AS BIGINT)) AS INT)
                 , Successful          = 1
                 , Comments            = @Comments
                 , modifiedBy          = ORIGINAL_LOGIN ()
                 , modifiedDateUtc     = @EndTimeUtc
             WHERE ExecutionLogId = @ExecutionId;
        END;

    END TRY
    BEGIN CATCH

        SELECT @ErrorNumber = ERROR_NUMBER ()
             , @ErrorProc   = ERROR_PROCEDURE ()
             , @ErrorLine   = ERROR_LINE ()
             , @ErrorMsg    = ERROR_MESSAGE ()
                            + N' (error '  + CAST (ERROR_NUMBER () AS NVARCHAR (11))
                            + N', line '   + CAST (ERROR_LINE ()   AS NVARCHAR (11)) + N')';

        IF XACT_STATE () <> 0
        BEGIN
            ROLLBACK TRANSACTION;
        END;

        BEGIN TRY
            IF @ExecutionId IS NULL
               OR NOT EXISTS (SELECT 1
                                FROM logs.ExecutionLog
                               WHERE ExecutionLogId = @ExecutionId)
            BEGIN
                EXEC logs.uspStartExecutionLogging
                      @ProcedureName          = @ProcName
                    , @KeyParameters          = @KeyParameters
                    , @StartDateUtc           = @StartTimeUtc
                    , @ReCreatedAfterRollback = 1
                    , @ExecutionLogId         = @ExecutionId OUTPUT;
            END;
        END TRY
        BEGIN CATCH
            SET @ExecutionId = NULL;
        END CATCH;

        EXEC logs.uspRecordExecutionError
              @ProcedureName   = @ProcName
            , @KeyParameters   = @KeyParameters
            , @ExecutionLogId  = @ExecutionId
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
    , @ObjectName  = N'uspSearch'
    , @Description = N'Global search by name (with aliases), date of birth, address, organization, accession number or case number, within the caller''s access scope. Writes the search text to the purgeable logs.searchLog and, on an exact name and DOB match outside scope, reports only that a record exists elsewhere.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspSearch TO meritWebRole;
END;
GO
