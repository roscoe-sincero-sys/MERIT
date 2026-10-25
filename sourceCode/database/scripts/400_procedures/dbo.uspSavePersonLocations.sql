-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspSavePersonLocations
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Applies a set of person-location changes for one person in one transaction and enforces the address rules in the database: no two primary residences overlap, overlapping residences carry a primary reason, and every date covered by a residence is covered by a primary. Used for adding a place and for the row split when the primary residence changes.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission person.edit (auth.udfHasPermission); refuses with 51403 otherwise.

The row split itself is planned by Merit.Domain (PrimaryResidencePlanner), so the rule is written once and the preview the user sees is the change applied. This procedure is the wall behind it: it takes an update lock on the person's location rows so two sessions cannot both save a primary, applies the changes, then rejects the whole set with 51422 if any rule is broken.

@changesJson is an array of objects with personLocationId, locationId, organizationSiteId, role, isPrimary, primaryReason, scheduleType, hoursPerWeek, scheduleNote, startDate, endDate, source, verifiedDate, isDeleted and rowVersion (hex, 0x...; omitted for new rows).

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspSavePersonLocations ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T042
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspSavePersonLocations
      @personId    UNIQUEIDENTIFIER
      , @changesJson NVARCHAR (MAX)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspSavePersonLocations]')
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
    SET @KeyParameters = CONCAT (N'personId=', @personId);

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        IF auth.udfHasPermission (N'person.edit') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (person.edit).', 1;
        END;

        IF NOT EXISTS (SELECT 1 FROM dbo.person WHERE personId = @personId AND isDeleted = 0)
        BEGIN
            ;THROW 51404, N'The person was not found, or you do not have access to it.', 1;
        END;

        IF ISJSON (@changesJson) = 0
        BEGIN
            ;THROW 50000, N'@changesJson must be a JSON array.', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @changes TABLE
        (
            personLocationId   UNIQUEIDENTIFIER PRIMARY KEY,
            locationId         UNIQUEIDENTIFIER,
            organizationSiteId UNIQUEIDENTIFIER,
            role               VARCHAR (20),
            isPrimary          BIT,
            primaryReason      VARCHAR (30),
            scheduleType       VARCHAR (20),
            hoursPerWeek       DECIMAL (5, 2),
            scheduleNote       NVARCHAR (500),
            startDate          DATE,
            endDate            DATE,
            source             VARCHAR (20),
            verifiedDate       DATE,
            isDeleted          BIT,
            rowVersion         BINARY (8)
        );

        INSERT @changes
        SELECT personLocationId, locationId, organizationSiteId, role, COALESCE (isPrimary, 0), primaryReason, scheduleType,
               hoursPerWeek, scheduleNote, startDate, endDate, source, verifiedDate, COALESCE (isDeleted, 0),
               CONVERT (BINARY (8), rowVersionHex, 1)
          FROM OPENJSON (@changesJson)
          WITH (personLocationId UNIQUEIDENTIFIER, locationId UNIQUEIDENTIFIER, organizationSiteId UNIQUEIDENTIFIER,
                role VARCHAR (20), isPrimary BIT, primaryReason VARCHAR (30), scheduleType VARCHAR (20),
                hoursPerWeek DECIMAL (5, 2), scheduleNote NVARCHAR (500), startDate DATE, endDate DATE, source VARCHAR (20),
                verifiedDate DATE, isDeleted BIT, rowVersionHex VARCHAR (20) '$.rowVersion');

        -- Serialize every writer of this person's places (address rule 1: enforced under row locks, not only in the UI).
        DECLARE @lock INT = (SELECT COUNT (*) FROM dbo.personLocation WITH (UPDLOCK, HOLDLOCK) WHERE personId = @personId);

        DECLARE @expected INT = (SELECT COUNT (*) FROM @changes AS c
                                  WHERE EXISTS (SELECT 1 FROM dbo.personLocation AS pl WHERE pl.personLocationId = c.personLocationId));

        UPDATE pl
           SET locationId = c.locationId, organizationSiteId = c.organizationSiteId, role = c.role, isPrimary = c.isPrimary,
               primaryReason = c.primaryReason, scheduleType = c.scheduleType, hoursPerWeek = c.hoursPerWeek,
               scheduleNote = c.scheduleNote, startDate = c.startDate, endDate = c.endDate, source = c.source,
               verifiedDate = c.verifiedDate, isDeleted = c.isDeleted
          FROM dbo.personLocation AS pl
          JOIN @changes AS c ON c.personLocationId = pl.personLocationId
         WHERE pl.personId = @personId AND pl.isDeleted = 0 AND (c.rowVersion IS NULL OR pl.rowVersion = c.rowVersion);

        IF @@ROWCOUNT <> @expected
        BEGIN
            ;THROW 51409, N'These places were changed by someone else. Reload the person and try again.', 1;
        END;

        INSERT dbo.personLocation (personLocationId, rootTenantId, owningTenantId, personId, locationId, organizationSiteId, role,
                                   isPrimary, primaryReason, scheduleType, hoursPerWeek, scheduleNote, startDate, endDate, source,
                                   verifiedDate)
        SELECT c.personLocationId, p.rootTenantId, p.owningTenantId, @personId, c.locationId, c.organizationSiteId, c.role,
               c.isPrimary, c.primaryReason, c.scheduleType, c.hoursPerWeek, c.scheduleNote, c.startDate, c.endDate, c.source,
               c.verifiedDate
          FROM @changes AS c
          JOIN dbo.person AS p ON p.personId = @personId
         WHERE c.isDeleted = 0
           AND NOT EXISTS (SELECT 1 FROM dbo.personLocation AS pl WHERE pl.personLocationId = c.personLocationId);

        -- ---------------------------------------------------------------------------------------- the rules
        DECLARE @msg NVARCHAR (400);

        -- Rule 1: no two primary residences overlap.
        SELECT TOP (1) @msg = CONCAT (N'Two primary residences overlap from ', CONVERT (NVARCHAR (10), IIF (a.startDate > b.startDate, a.startDate, b.startDate), 23),
                                      N'. A person has one primary residence on any date.')
          FROM dbo.personLocation AS a
          JOIN dbo.personLocation AS b ON b.personId = a.personId AND b.personLocationId > a.personLocationId
         WHERE a.personId = @personId AND a.isDeleted = 0 AND b.isDeleted = 0
           AND a.role = 'residence' AND b.role = 'residence' AND a.isPrimary = 1 AND b.isPrimary = 1
           AND a.startDate <= COALESCE (b.endDate, '9999-12-31') AND b.startDate <= COALESCE (a.endDate, '9999-12-31');

        IF @msg IS NOT NULL THROW 51422, @msg, 1;

        -- Rule 3: where residences overlap, the primary one carries a reason.
        SELECT TOP (1) @msg = N'Two residences overlap, so the primary residence needs a reason (custody order, most overnights, ...).'
          FROM dbo.personLocation AS a
          JOIN dbo.personLocation AS b ON b.personId = a.personId AND b.personLocationId <> a.personLocationId
         WHERE a.personId = @personId AND a.isDeleted = 0 AND b.isDeleted = 0
           AND a.role = 'residence' AND b.role = 'residence' AND a.isPrimary = 1 AND a.primaryReason IS NULL
           AND a.startDate <= COALESCE (b.endDate, '9999-12-31') AND b.startDate <= COALESCE (a.endDate, '9999-12-31');

        IF @msg IS NOT NULL THROW 51422, @msg, 1;

        -- Rule 2: every date covered by a residence is covered by a primary. A gap can only begin at a residence's start
        -- or on the day after a primary ends, so those are the only dates that need testing.
        ;WITH residence AS
        (
            SELECT startDate, COALESCE (endDate, '9999-12-31') AS endDate, isPrimary
              FROM dbo.personLocation
             WHERE personId = @personId AND isDeleted = 0 AND role = 'residence'
        ),
        candidate AS
        (
            SELECT r.startDate AS d FROM residence AS r
            UNION
            SELECT DATEADD (DAY, 1, p.endDate) FROM residence AS p WHERE p.isPrimary = 1 AND p.endDate < '9999-12-31'
        )
        SELECT TOP (1) @msg = CONCAT (N'On ', CONVERT (NVARCHAR (10), c.d, 23),
                                      N' the person has a residence but no primary residence. Pick which residence is primary.')
          FROM candidate AS c
         WHERE EXISTS (SELECT 1 FROM residence AS r WHERE c.d BETWEEN r.startDate AND r.endDate)
           AND NOT EXISTS (SELECT 1 FROM residence AS p WHERE p.isPrimary = 1 AND c.d BETWEEN p.startDate AND p.endDate)
         ORDER BY c.d;

        IF @msg IS NOT NULL THROW 51422, @msg, 1;

        EXEC dbo.uspRefreshPersonAccess @personId = @personId;

        SET @Comments = CONCAT ((SELECT COUNT (*) FROM @changes), N' place change(s) applied.');

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
    , @ObjectName  = N'uspSavePersonLocations'
    , @Description = N'Applies a set of person-location changes for one person in one transaction and enforces the address rules in the database: no two primary residences overlap, overlapping residences carry a primary reason, and every date covered by a residence is covered by a primary. Used for adding a place and for the row split when the primary residence changes.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspSavePersonLocations TO meritWebRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspSavePersonLocations TO meritWorkerRole;
END;
GO
