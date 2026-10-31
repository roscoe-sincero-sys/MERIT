-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspCorrectPersonPlace
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Atomically corrects one person's place and its shared year built after checking the original row version.

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

exec dbo.uspCorrectPersonPlace ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T076
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspCorrectPersonPlace
      @personId UNIQUEIDENTIFIER
      , @changesJson NVARCHAR (MAX)
      , @unit NVARCHAR (30) = NULL
      , @yearBuilt INT = NULL
      , @addressKey NVARCHAR (300)
      , @buildingAddressKey NVARCHAR (300) = NULL
      , @correctLocation BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspCorrectPersonPlace]')
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

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        IF ISJSON (@changesJson, ARRAY) <> 1 OR (SELECT COUNT (*) FROM OPENJSON (@changesJson)) <> 1
            THROW 50000, N'Correct one place at a time.', 1;
        DECLARE @place UNIQUEIDENTIFIER = TRY_CONVERT (UNIQUEIDENTIFIER, JSON_VALUE (@changesJson, '$[0].personLocationId')),
                @version BINARY (8) = TRY_CONVERT (BINARY (8), JSON_VALUE (@changesJson, '$[0].rowVersion'), 1),
                @location UNIQUEIDENTIFIER, @site UNIQUEIDENTIFIER;
        SELECT @location = locationId, @site = organizationSiteId FROM dbo.personLocation WITH (UPDLOCK, HOLDLOCK)
         WHERE personLocationId = @place AND personId = @personId AND isDeleted = 0 AND rowVersion = @version;
        IF @location IS NULL THROW 51409, N'This place changed. Reload before editing.', 1;
        IF @correctLocation = 1
        BEGIN
            IF @site IS NOT NULL THROW 51422, N'Edit an organization site address through its registry.', 1;
            DECLARE @country CHAR (2), @street NVARCHAR (120), @city NVARCHAR (60), @state CHAR (2), @zip CHAR (5),
                    @region NVARCHAR (60), @postal NVARCHAR (20), @county CHAR (3), @cityOnly BIT,
                    @housing VARCHAR (20), @tenure VARCHAR (20), @newLocation UNIQUEIDENTIFIER = NEWID ();
            SELECT @country = country, @street = addressLine1, @city = city, @state = state, @zip = zip5,
                   @region = region, @postal = postalCode, @county = countyFips, @cityOnly = isCityOnly,
                   @housing = housingType, @tenure = tenure FROM dbo.location WHERE locationId = @location AND isDeleted = 0;
            DECLARE @saved TABLE (locationId UNIQUEIDENTIFIER, wasExisting BIT);
            INSERT @saved EXEC dbo.uspSaveLocation @locationId = @newLocation, @country = @country, @addressLine1 = @street,
                @unit = @unit, @city = @city, @state = @state, @zip5 = @zip, @region = @region, @postalCode = @postal,
                @countyFips = @county, @isCityOnly = @cityOnly, @addressKey = @addressKey, @buildingAddressKey = @buildingAddressKey,
                @yearBuilt = @yearBuilt, @housingType = @housing, @tenure = @tenure;
            SELECT @location = locationId FROM @saved;
            UPDATE dbo.location SET yearBuilt = @yearBuilt WHERE locationId = @location AND yearBuilt IS DISTINCT FROM @yearBuilt;
            SET @changesJson = JSON_MODIFY (@changesJson, '$[0].locationId', CONVERT (VARCHAR (36), @location));
        END;
        EXEC dbo.uspSavePersonLocations @personId = @personId, @changesJson = @changesJson;
        SET @Comments = N'Place details corrected.';

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
    , @ObjectName  = N'uspCorrectPersonPlace'
    , @Description = N'Atomically corrects one person''s place and its shared year built after checking the original row version.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspCorrectPersonPlace TO meritWebRole;
END;
GO
