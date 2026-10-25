-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspSaveEmployment
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Adds, changes or ends a job, and replaces the analytes the worker is exposed to (JSON array of analyte ids).

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission person.edit (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspSaveEmployment ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T043
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspSaveEmployment
      @employmentId          UNIQUEIDENTIFIER
      , @personId              UNIQUEIDENTIFIER
      , @organizationId        UNIQUEIDENTIFIER
      , @worksiteLocationId    UNIQUEIDENTIFIER = NULL
      , @jobTitle              NVARCHAR (120) = NULL
      , @industryText          NVARCHAR (200) = NULL
      , @occupationCode        VARCHAR (4) = NULL
      , @occupationCodeVersion VARCHAR (10) = NULL
      , @naicsOverride         VARCHAR (6) = NULL
      , @metalTasks            NVARCHAR (1000) = NULL
      , @ppeNotes              NVARCHAR (1000) = NULL
      , @hoursPerWeek          DECIMAL (5, 2) = NULL
      , @startDate             DATE = NULL
      , @endDate               DATE = NULL
      , @source                VARCHAR (20)
      , @verifiedDate          DATE = NULL
      , @analyteIdsJson        NVARCHAR (MAX) = N'[]'
      , @isDeleted             BIT = 0
      , @rowVersion            BINARY (8) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspSaveEmployment]')
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
    SET @KeyParameters = CONCAT (N'employmentId=', @employmentId, N', personId=', @personId);

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

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        IF EXISTS (SELECT 1 FROM dbo.employment WHERE employmentId = @employmentId)
        BEGIN
            UPDATE dbo.employment
               SET organizationId = @organizationId, worksiteLocationId = @worksiteLocationId, jobTitle = @jobTitle,
                   industryText = @industryText, occupationCode = @occupationCode, occupationCodeVersion = @occupationCodeVersion,
                   naicsOverride = @naicsOverride, metalTasks = @metalTasks, ppeNotes = @ppeNotes, hoursPerWeek = @hoursPerWeek,
                   startDate = @startDate, endDate = @endDate, source = @source, verifiedDate = @verifiedDate,
                   isDeleted = @isDeleted
             WHERE employmentId = @employmentId AND personId = @personId AND isDeleted = 0
               AND (@rowVersion IS NULL OR rowVersion = @rowVersion);
            IF @@ROWCOUNT = 0
            BEGIN
                ;THROW 51409, N'This record was changed by someone else, or is no longer available. Reload it and try again.', 1;
            END;
        END
        ELSE
        BEGIN
            INSERT dbo.employment (employmentId, rootTenantId, owningTenantId, personId, organizationId, worksiteLocationId,
                                   jobTitle, industryText, occupationCode, occupationCodeVersion, naicsOverride, metalTasks,
                                   ppeNotes, hoursPerWeek, startDate, endDate, source, verifiedDate)
            SELECT @employmentId, rootTenantId, owningTenantId, @personId, @organizationId, @worksiteLocationId, @jobTitle,
                   @industryText, @occupationCode, @occupationCodeVersion, @naicsOverride, @metalTasks, @ppeNotes,
                   @hoursPerWeek, @startDate, @endDate, @source, @verifiedDate
              FROM dbo.person WHERE personId = @personId;
        END;

        DECLARE @analytes TABLE (analyteId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @analytes SELECT DISTINCT TRY_CAST ([value] AS UNIQUEIDENTIFIER) FROM OPENJSON (COALESCE (@analyteIdsJson, N'[]'))
                          WHERE TRY_CAST ([value] AS UNIQUEIDENTIFIER) IS NOT NULL;

        UPDATE ea SET isDeleted = 1
          FROM dbo.employmentAnalyte AS ea
         WHERE ea.employmentId = @employmentId AND ea.isDeleted = 0
           AND (@isDeleted = 1 OR NOT EXISTS (SELECT 1 FROM @analytes AS a WHERE a.analyteId = ea.analyteId));

        INSERT dbo.employmentAnalyte (employmentAnalyteId, rootTenantId, owningTenantId, personId, employmentId, analyteId)
        SELECT CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), p.rootTenantId, p.owningTenantId, @personId, @employmentId, a.analyteId
          FROM @analytes AS a
          JOIN dbo.person AS p ON p.personId = @personId
         WHERE @isDeleted = 0
           AND NOT EXISTS (SELECT 1 FROM dbo.employmentAnalyte AS ea WHERE ea.employmentId = @employmentId
                                                                     AND ea.analyteId = a.analyteId AND ea.isDeleted = 0);

        SET @Comments = N'Employment saved.';

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
    , @ObjectName  = N'uspSaveEmployment'
    , @Description = N'Adds, changes or ends a job, and replaces the analytes the worker is exposed to (JSON array of analyte ids).';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspSaveEmployment TO meritWebRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspSaveEmployment TO meritWorkerRole;
END;
GO
