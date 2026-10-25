-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   config.uspCreateChildTenant
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Adds a program or a local jurisdiction under a tenant the caller administers (any tenant for a platform administrator), with the counties it serves; counties new to the data root are added. Needs tenant.create. Re-runnable for the same parent.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission tenant.create (auth.udfHasPermission); refuses with 51403 otherwise.

Returns the new tenant id. The inserts run in config.uspInstallChildTenant.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec config.uspCreateChildTenant ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T162
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE config.uspCreateChildTenant
      @tenantCode       VARCHAR (30)
      , @tenantName       NVARCHAR (120)
      , @tenantType       VARCHAR (30)
      , @parentTenantCode VARCHAR (30)
      , @countyFipsList   VARCHAR (400) = NULL
      , @countiesJson     NVARCHAR (MAX) = N'[]'
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[config].[uspCreateChildTenant]')
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
    SET @KeyParameters = CONCAT (N'tenantCode=', @tenantCode, N', parentTenantCode=', @parentTenantCode, N', tenantType=', @tenantType);

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        IF auth.udfHasPermission (N'tenant.create') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (tenant.create).', 1;
        END;

        IF @tenantCode IS NULL OR @tenantCode NOT LIKE '[A-Z0-9]%' OR @tenantCode LIKE '%[^A-Z0-9-]%' COLLATE Latin1_General_BIN
           OR LEN (@tenantCode) > 30
        BEGIN
            ;THROW 51422, N'The tenant code uses capital letters, digits and hyphens, up to 30 characters (for example WV-JEFFERSON).', 1;
        END;
        IF LEN (LTRIM (RTRIM (COALESCE (@tenantName, N'')))) = 0
        BEGIN
            ;THROW 51422, N'Give the tenant a name.', 1;
        END;
        IF @countiesJson IS NOT NULL AND ISJSON (@countiesJson) = 0
        BEGIN
            ;THROW 50000, N'@countiesJson must be a JSON array of {fips, name, type}.', 1;
        END;
        DECLARE @parentId UNIQUEIDENTIFIER = (SELECT tenantId FROM auth.tenant WHERE tenantCode = @parentTenantCode AND isDeleted = 0);

        IF @parentId IS NULL
        BEGIN
            ;THROW 51404, N'The parent tenant was not found.', 1;
        END;

        IF NOT EXISTS (SELECT 1 FROM auth.tvfAdminTenants () WHERE tenantId = @parentId)
        BEGIN
            ;THROW 51403, N'You can add tenants only under tenants you administer.', 1;
        END;

        IF @tenantType NOT IN ('program', 'jurisdiction')
        BEGIN
            ;THROW 51422, N'A tenant added under another is a program or a jurisdiction; states, tribes and regions are data roots.', 1;
        END;

        IF EXISTS (SELECT 1 FROM auth.tenant WHERE tenantId = @parentId AND tenantType = 'platform')
        BEGIN
            ;THROW 51422, N'The platform holds data roots only: create a region, state, tribe or stand-alone jurisdiction.', 1;
        END;

        IF EXISTS (SELECT 1 FROM auth.tenant WHERE tenantCode = @tenantCode AND isDeleted = 0 AND parentTenantId <> @parentId)
        BEGIN
            ;THROW 51422, N'That tenant code is already used elsewhere in the tree.', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        EXEC config.uspInstallChildTenant @tenantCode = @tenantCode, @tenantName = @tenantName, @tenantType = @tenantType,
             @parentTenantCode = @parentTenantCode, @countyFipsList = @countyFipsList, @countiesJson = @countiesJson;

        SET @Comments = N'Child tenant created.';

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
      @SchemaName  = N'config'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspCreateChildTenant'
    , @Description = N'Adds a program or a local jurisdiction under a tenant the caller administers (any tenant for a platform administrator), with the counties it serves; counties new to the data root are added. Needs tenant.create. Re-runnable for the same parent.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON config.uspCreateChildTenant TO meritWebRole;
END;
GO
