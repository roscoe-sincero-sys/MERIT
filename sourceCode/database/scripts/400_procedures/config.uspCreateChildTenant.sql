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

Adds a program or a local jurisdiction under a parent the caller administers, maintains the closure table, and maps it to the sub-jurisdictions (county FIPS codes) it serves. It belongs to its parent's data root. States, tribes and regions are data roots: use uspCreateRootTenant. Re-runnable.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission tenant.administer (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec config.uspCreateChildTenant ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T032
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE config.uspCreateChildTenant
      @tenantCode       VARCHAR (30)
      , @tenantName       NVARCHAR (120)
      , @tenantType       VARCHAR (30)
      , @parentTenantCode VARCHAR (30)
      , @countyFipsList   VARCHAR (400) = NULL
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
    SET @KeyParameters = CONCAT (N'tenantCode=', @tenantCode, N', parentTenantCode=', @parentTenantCode);

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        IF auth.udfHasPermission (N'tenant.administer') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (tenant.administer).', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @parent UNIQUEIDENTIFIER, @root UNIQUEIDENTIFIER, @state CHAR (2);
        SELECT @parent = tenantId, @root = rootTenantId, @state = statePostalCode
          FROM auth.tenant WHERE tenantCode = @parentTenantCode AND isDeleted = 0;

        IF @parent IS NULL
        BEGIN
            ;THROW 51404, N'The parent tenant was not found.', 1;
        END;

        IF @tenantType NOT IN ('program', 'jurisdiction')
        BEGIN
            ;THROW 51422, N'A child tenant is a program or a jurisdiction; states, tribes and regions are created as data roots.', 1;
        END;

        IF NOT EXISTS (SELECT 1 FROM auth.tvfAdminTenants () WHERE tenantId = @parent)
        BEGIN
            ;THROW 51403, N'You can add tenants only under tenants you administer.', 1;
        END;

        IF EXISTS (SELECT 1 FROM auth.tenant WHERE tenantId = @parent AND tenantType = 'platform')
        BEGIN
            ;THROW 51422, N'The platform holds data roots only.', 1;
        END;

        DECLARE @id UNIQUEIDENTIFIER = CAST (HASHBYTES ('MD5', CONCAT (N'tenant:', @tenantCode)) AS UNIQUEIDENTIFIER);

        IF NOT EXISTS (SELECT 1 FROM auth.tenant WHERE tenantId = @id)
        BEGIN
            INSERT auth.tenant (tenantId, parentTenantId, rootTenantId, tenantCode, tenantName, tenantType, statePostalCode)
            VALUES (@id, @parent, @root, @tenantCode, @tenantName, @tenantType, @state);
        END;

        -- Closure: self, plus every ancestor of the parent one level further away.
        INSERT auth.tenantClosure (tenantClosureId, ancestorTenantId, descendantTenantId, depth)
        SELECT CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), x.ancestorTenantId, @id, x.depth
          FROM (SELECT @id AS ancestorTenantId, 0 AS depth
                UNION ALL
                SELECT ancestorTenantId, depth + 1 FROM auth.tenantClosure WHERE descendantTenantId = @parent AND isDeleted = 0) AS x
         WHERE NOT EXISTS (SELECT 1 FROM auth.tenantClosure AS c WHERE c.ancestorTenantId = x.ancestorTenantId
                                                                 AND c.descendantTenantId = @id AND c.isDeleted = 0);

        INSERT auth.tenantJurisdiction (tenantJurisdictionId, tenantId, subJurisdictionId)
        SELECT CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), @id, sj.subJurisdictionId
          FROM STRING_SPLIT (COALESCE (@countyFipsList, ''), ',') AS s
          JOIN config.subJurisdiction AS sj ON sj.rootTenantId = @root AND sj.countyFips = LTRIM (RTRIM (s.[value]))
         WHERE NOT EXISTS (SELECT 1 FROM auth.tenantJurisdiction AS tj WHERE tj.tenantId = @id
                                                                      AND tj.subJurisdictionId = sj.subJurisdictionId
                                                                      AND tj.isDeleted = 0);

        SELECT @id AS tenantId;

        SET @Comments = N'Child tenant installed.';

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
    , @Description = N'Adds a program or a local jurisdiction under a parent the caller administers, maintains the closure table, and maps it to the sub-jurisdictions (county FIPS codes) it serves. It belongs to its parent''s data root. States, tribes and regions are data roots: use uspCreateRootTenant. Re-runnable.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON config.uspCreateChildTenant TO meritWebRole;
END;
GO
