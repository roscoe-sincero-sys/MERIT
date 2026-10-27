-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspCreateCaseFollowUpTasks
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Creates the cross-link and follow-up tasks a new case triggers -- the links separate child and adult systems cannot make. Each is a task for staff to review, never an automatic case: testing for other children who share the home or are siblings; an occupational inquiry for household adults whose jobs list metal tasks; testing for children and pregnant people in an adult worker's household; prenatal provider notification and newborn testing for a pregnant person.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission case.open (auth.udfHasPermission); refuses with 51403 otherwise.

Idempotent: a task is not created twice for the same case, queue and person.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspCreateCaseFollowUpTasks ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T065
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspCreateCaseFollowUpTasks
      @exposureCaseId  UNIQUEIDENTIFIER
      , @adultAgeCutoffYears INT = 16
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspCreateCaseFollowUpTasks]')
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
    SET @KeyParameters = CONCAT (N'exposureCaseId=', @exposureCaseId);

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        IF auth.udfHasPermission (N'case.open') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (case.open).', 1;
        END;

        IF NOT EXISTS (SELECT 1 FROM dbo.exposureCase WHERE exposureCaseId = @exposureCaseId AND isDeleted = 0)
        BEGIN
            ;THROW 51404, N'The case was not found, or you do not have access to it.', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @personId UNIQUEIDENTIFIER, @root UNIQUEIDENTIFIER, @owner UNIQUEIDENTIFIER, @population VARCHAR (10),
                @category VARCHAR (20), @code VARCHAR (8), @caseNumber VARCHAR (20);
        DECLARE @today DATE = auth.udfSessionToday ();

        SELECT @personId = c.personId, @root = c.rootTenantId, @owner = c.owningTenantId, @population = c.population,
               @category = c.exposureCategory, @code = a.code, @caseNumber = c.caseNumber
          FROM dbo.exposureCase AS c JOIN config.analyte AS a ON a.analyteId = c.analyteId
         WHERE c.exposureCaseId = @exposureCaseId;

        -- Household: everyone sharing the case person's current primary residence, plus relationship links.
        DECLARE @household TABLE (personId UNIQUEIDENTIFIER PRIMARY KEY, viaRelationship BIT, role VARCHAR (30));

        INSERT @household (personId, viaRelationship, role)
        SELECT DISTINCT other.personId, 0, NULL
          FROM dbo.personLocation AS mine
          JOIN dbo.personLocation AS other ON other.locationId = mine.locationId AND other.personId <> mine.personId
                                          AND other.role = 'residence' AND other.isDeleted = 0
                                          AND other.startDate <= @today AND (other.endDate IS NULL OR other.endDate >= @today)
         WHERE mine.personId = @personId AND mine.role = 'residence' AND mine.isPrimary = 1 AND mine.isDeleted = 0
           AND mine.startDate <= @today AND (mine.endDate IS NULL OR mine.endDate >= @today);

        MERGE @household AS h
        USING (SELECT relatedPersonId AS personId, role FROM dbo.personRelationship
                WHERE personId = @personId AND isDeleted = 0 AND (endDate IS NULL OR endDate >= @today)
               UNION
               SELECT personId, role FROM dbo.personRelationship
                WHERE relatedPersonId = @personId AND isDeleted = 0 AND (endDate IS NULL OR endDate >= @today)) AS r
           ON h.personId = r.personId
        WHEN MATCHED THEN UPDATE SET viaRelationship = 1, role = r.role
        WHEN NOT MATCHED THEN INSERT (personId, viaRelationship, role) VALUES (r.personId, 1, r.role);

        DECLARE @tasks TABLE (queueCode VARCHAR (40), title NVARCHAR (200), detail NVARCHAR (2000), personId UNIQUEIDENTIFIER,
                              dueDate DATE);

        -- Child case: other children in the home or siblings -> testing.
        IF @population = 'child'
        BEGIN
            INSERT @tasks
            SELECT 'crossLink', CONCAT (N'Recommend testing ', p.firstName, N' ', p.lastName),
                   CONCAT (N'Shares the home or is a sibling of the child on case ', @caseNumber, N'.'), p.personId, NULL
              FROM @household AS h JOIN dbo.person AS p ON p.personId = h.personId
             WHERE p.dob IS NOT NULL AND DATEDIFF (YEAR, p.dob, @today) < @adultAgeCutoffYears
               AND NOT EXISTS (SELECT 1 FROM dbo.exposureCase AS c WHERE c.personId = p.personId AND c.caseStatus <> 'closed'
                                                                    AND c.isDeleted = 0);

            -- Household adults whose jobs list metal tasks -> occupational inquiry, even when contact-only.
            INSERT @tasks
            SELECT 'crossLink', CONCAT (N'Occupational inquiry for ', p.firstName, N' ', p.lastName),
                   CONCAT (N'Household member of the child on case ', @caseNumber, N'; a current job lists work with metals: ',
                           LEFT (e.metalTasks, 200)), p.personId, NULL
              FROM @household AS h
              JOIN dbo.person AS p ON p.personId = h.personId
              JOIN dbo.employment AS e ON e.personId = p.personId AND e.isDeleted = 0 AND e.metalTasks IS NOT NULL
                                      AND (e.endDate IS NULL OR e.endDate >= @today)
             WHERE p.dob IS NULL OR DATEDIFF (YEAR, p.dob, @today) >= @adultAgeCutoffYears;
        END;

        -- Adult case: children or a pregnant person in the household -> testing.
        IF @population = 'adult'
        BEGIN
            INSERT @tasks
            SELECT 'crossLink', CONCAT (N'Recommend testing ', p.firstName, N' ', p.lastName),
                   CONCAT (N'Child or pregnant household member of the adult on case ', @caseNumber,
                           N'; take-home exposure is possible.'), p.personId, NULL
              FROM @household AS h JOIN dbo.person AS p ON p.personId = h.personId
             WHERE (p.dob IS NOT NULL AND DATEDIFF (YEAR, p.dob, @today) < @adultAgeCutoffYears)
                OR EXISTS (SELECT 1 FROM dbo.pregnancyEpisode AS e WHERE e.personId = p.personId AND e.isDeleted = 0
                              AND e.startDate <= @today AND COALESCE (e.actualEndDate, e.expectedEndDate, DATEADD (WEEK, 42, e.startDate)) >= @today);
        END;

        -- Pregnant case: prenatal provider notification and newborn testing follow-up.
        IF @population = 'pregnant'
        BEGIN
            INSERT @tasks VALUES ('prenatalNotification', N'Notify the prenatal care provider',
                                  CONCAT (N'Case ', @caseNumber, N' is for a pregnant person.'), @personId, DATEADD (DAY, 7, @today));

            INSERT @tasks
            SELECT TOP (1) 'newbornTesting', N'Newborn testing follow-up',
                   CONCAT (N'Arrange testing of the newborn after delivery (case ', @caseNumber, N').'), @personId,
                   COALESCE (e.expectedEndDate, DATEADD (WEEK, 40, e.startDate))
              FROM dbo.pregnancyEpisode AS e
             WHERE e.personId = @personId AND e.isDeleted = 0 AND e.actualEndDate IS NULL
             ORDER BY e.startDate DESC;
        END;

        INSERT dbo.workTask (workTaskId, rootTenantId, owningTenantId, queueCode, title, detail, personId, exposureCaseId, dueDate)
        SELECT CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), @root, @owner, t.queueCode, t.title, t.detail, t.personId, @exposureCaseId, t.dueDate
          FROM @tasks AS t
         WHERE NOT EXISTS (SELECT 1 FROM dbo.workTask AS w
                            WHERE w.exposureCaseId = @exposureCaseId AND w.queueCode = t.queueCode
                              AND w.personId IS NOT DISTINCT FROM t.personId AND w.title = t.title AND w.isDeleted = 0);

        SET @Comments = CONCAT (@@ROWCOUNT, N' follow-up task(s) created.');

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
    , @ObjectName  = N'uspCreateCaseFollowUpTasks'
    , @Description = N'Creates the cross-link and follow-up tasks a new case triggers -- the links separate child and adult systems cannot make. Each is a task for staff to review, never an automatic case: testing for other children who share the home or are siblings; an occupational inquiry for household adults whose jobs list metal tasks; testing for children and pregnant people in an adult worker''s household; prenatal provider notification and newborn testing for a pregnant person.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspCreateCaseFollowUpTasks TO meritWebRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspCreateCaseFollowUpTasks TO meritWorkerRole;
END;
GO
