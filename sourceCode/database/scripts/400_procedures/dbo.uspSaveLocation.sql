-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspSaveLocation
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Finds or creates a location. An existing location with the same normalized address and unit is reused, so everyone at one address shares one record; a unit's building record (same address, no unit) is found or created and linked.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission person.edit (auth.udfHasPermission); refuses with 51403 otherwise.

Returns one row: locationId (the existing one when reused) and wasExisting.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspSaveLocation ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T042
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspSaveLocation
      @locationId         UNIQUEIDENTIFIER
      , @country            CHAR (2) = 'US'
      , @addressLine1       NVARCHAR (120) = NULL
      , @unit               NVARCHAR (30) = NULL
      , @city               NVARCHAR (60)
      , @state              CHAR (2) = NULL
      , @zip5               CHAR (5) = NULL
      , @region             NVARCHAR (60) = NULL
      , @postalCode         NVARCHAR (20) = NULL
      , @countyFips         CHAR (3) = NULL
      , @isCityOnly         BIT = 0
      , @addressKey         NVARCHAR (300)
      , @buildingAddressKey NVARCHAR (300) = NULL
      , @yearBuilt          INT = NULL
      , @housingType        VARCHAR (20) = NULL
      , @tenure             VARCHAR (20) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspSaveLocation]')
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
    SET @KeyParameters = CONCAT (N'locationId=', @locationId);

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

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();
        DECLARE @existing UNIQUEIDENTIFIER, @building UNIQUEIDENTIFIER;

        IF @country = 'US' AND @isCityOnly = 0 AND (@addressLine1 IS NULL OR @state IS NULL OR @zip5 IS NULL)
        BEGIN
            ;THROW 51422, N'A US address needs a street address, state and ZIP code, unless it is a city-only location.', 1;
        END;

        SELECT TOP (1) @existing = locationId
          FROM dbo.location WITH (UPDLOCK, HOLDLOCK)
         WHERE rootTenantId = @root AND addressKey = @addressKey AND isDeleted = 0;

        IF @existing IS NOT NULL
        BEGIN
            -- Fill gaps on the shared record without overwriting what someone else recorded.
            UPDATE dbo.location
               SET countyFips  = COALESCE (countyFips, @countyFips)
                 , yearBuilt   = COALESCE (yearBuilt, @yearBuilt)
                 , housingType = COALESCE (housingType, @housingType)
                 , tenure      = COALESCE (tenure, @tenure)
             WHERE locationId = @existing
               AND ((countyFips IS NULL AND @countyFips IS NOT NULL) OR (yearBuilt IS NULL AND @yearBuilt IS NOT NULL)
                 OR (housingType IS NULL AND @housingType IS NOT NULL) OR (tenure IS NULL AND @tenure IS NOT NULL));

            SELECT @existing AS locationId, CAST (1 AS BIT) AS wasExisting;
        END
        ELSE
        BEGIN
            IF @unit IS NOT NULL AND @buildingAddressKey IS NOT NULL
            BEGIN
                SELECT TOP (1) @building = locationId
                  FROM dbo.location WITH (UPDLOCK, HOLDLOCK)
                 WHERE rootTenantId = @root AND addressKey = @buildingAddressKey AND isDeleted = 0;

                IF @building IS NULL
                BEGIN
                    SET @building = CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER);
                    INSERT dbo.location (locationId, rootTenantId, owningTenantId, country, addressLine1, city, state, zip5,
                                         region, postalCode, countyFips, addressKey, yearBuilt, housingType)
                    VALUES (@building, @root, @root, @country, @addressLine1, @city, @state, @zip5, @region, @postalCode,
                            @countyFips, @buildingAddressKey, @yearBuilt, @housingType);
                END;
            END;

            INSERT dbo.location (locationId, rootTenantId, owningTenantId, country, addressLine1, unit, city, state, zip5, region,
                                 postalCode, countyFips, isCityOnly, addressKey, buildingLocationId, yearBuilt, housingType, tenure)
            VALUES (@locationId, @root, @root, @country, @addressLine1, @unit, @city, @state, @zip5, @region, @postalCode,
                    @countyFips, @isCityOnly, @addressKey, @building, @yearBuilt, @housingType, @tenure);

            SELECT @locationId AS locationId, CAST (0 AS BIT) AS wasExisting;
        END;

        SET @Comments = IIF (@existing IS NULL, N'Location created.', N'Existing location reused.');

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
    , @ObjectName  = N'uspSaveLocation'
    , @Description = N'Finds or creates a location. An existing location with the same normalized address and unit is reused, so everyone at one address shares one record; a unit''s building record (same address, no unit) is found or created and linked.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspSaveLocation TO meritWebRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspSaveLocation TO meritWorkerRole;
END;
GO
