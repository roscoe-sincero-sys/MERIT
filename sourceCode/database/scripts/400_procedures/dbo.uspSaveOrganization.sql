-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspSaveOrganization
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Creates or updates an organization and replaces its set of types (JSON array of type codes).

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

exec dbo.uspSaveOrganization ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T043
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspSaveOrganization
      @organizationId       UNIQUEIDENTIFIER
      , @organizationName     NVARCHAR (120)
      , @organizationNameKey  NVARCHAR (120)
      , @naicsCode            VARCHAR (6) = NULL
      , @naicsVersion         VARCHAR (10) = NULL
      , @npi                  VARCHAR (10) = NULL
      , @cliaNumber           VARCHAR (10) = NULL
      , @parentOrganizationId UNIQUEIDENTIFIER = NULL
      , @contactName          NVARCHAR (120) = NULL
      , @phone                NVARCHAR (30) = NULL
      , @email                NVARCHAR (200) = NULL
      , @isActive             BIT = 1
      , @typesJson            NVARCHAR (MAX) = N'[]'
      , @rowVersion           BINARY (8) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspSaveOrganization]')
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
    SET @KeyParameters = CONCAT (N'organizationId=', @organizationId);

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

        IF EXISTS (SELECT 1 FROM dbo.organization WHERE organizationId = @organizationId)
        BEGIN
            UPDATE dbo.organization
               SET organizationName = @organizationName, organizationNameKey = @organizationNameKey, naicsCode = @naicsCode,
                   naicsVersion = @naicsVersion, npi = @npi, cliaNumber = @cliaNumber,
                   parentOrganizationId = @parentOrganizationId, contactName = @contactName, phone = @phone, email = @email,
                   isActive = @isActive
             WHERE organizationId = @organizationId AND isDeleted = 0 AND (@rowVersion IS NULL OR rowVersion = @rowVersion);
            IF @@ROWCOUNT = 0
            BEGIN
                ;THROW 51409, N'This record was changed by someone else, or is no longer available. Reload it and try again.', 1;
            END;
        END
        ELSE
        BEGIN
            INSERT dbo.organization (organizationId, rootTenantId, owningTenantId, organizationName, organizationNameKey,
                                     naicsCode, naicsVersion, npi, cliaNumber, parentOrganizationId, contactName, phone, email,
                                     isActive)
            VALUES (@organizationId, @root, @root, @organizationName, @organizationNameKey, @naicsCode, @naicsVersion, @npi,
                    @cliaNumber, @parentOrganizationId, @contactName, @phone, @email, @isActive);
        END;

        DECLARE @types TABLE (code VARCHAR (30) PRIMARY KEY);
        INSERT @types SELECT DISTINCT CAST ([value] AS VARCHAR (30)) FROM OPENJSON (COALESCE (@typesJson, N'[]'));

        UPDATE t SET isDeleted = 1
          FROM dbo.organizationType AS t
         WHERE t.organizationId = @organizationId AND t.isDeleted = 0
           AND NOT EXISTS (SELECT 1 FROM @types AS x WHERE x.code = t.organizationTypeCode);

        INSERT dbo.organizationType (organizationTypeId, rootTenantId, owningTenantId, organizationId, organizationTypeCode)
        SELECT CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), @root, @root, @organizationId, x.code
          FROM @types AS x
         WHERE NOT EXISTS (SELECT 1 FROM dbo.organizationType AS t WHERE t.organizationId = @organizationId
                                                                    AND t.organizationTypeCode = x.code AND t.isDeleted = 0);

        SET @Comments = N'Organization saved.';

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
    , @ObjectName  = N'uspSaveOrganization'
    , @Description = N'Creates or updates an organization and replaces its set of types (JSON array of type codes).';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspSaveOrganization TO meritWebRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspSaveOrganization TO meritWorkerRole;
END;
GO
