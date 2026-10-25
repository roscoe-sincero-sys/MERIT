-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspRefreshPersonAccess
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Brings dbo.personAccess in step for one person: the tenants serving the county of the current primary residence get residence access, and tenants owning an open case get case access. Called by every procedure that changes a primary residence or a case's ownership.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Converges: rows that should exist are added, rows that no longer apply are soft-deleted, rows that are right are left alone (so their audit columns do not move).

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspRefreshPersonAccess ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T042
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspRefreshPersonAccess
      @personId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspRefreshPersonAccess]')
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

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @today DATE = CAST (SYSUTCDATETIME () AS DATE);
        DECLARE @root UNIQUEIDENTIFIER = (SELECT rootTenantId FROM dbo.person WHERE personId = @personId);
        DECLARE @want TABLE (tenantId UNIQUEIDENTIFIER, accessReason VARCHAR (20), PRIMARY KEY (tenantId, accessReason));

        INSERT @want (tenantId, accessReason)
        SELECT DISTINCT tj.tenantId, 'residence'
          FROM dbo.personLocation AS pl
          JOIN dbo.location AS l ON l.locationId = pl.locationId
          JOIN config.subJurisdiction AS sj ON sj.rootTenantId = l.rootTenantId AND sj.countyFips = l.countyFips AND sj.isDeleted = 0
          JOIN auth.tenantJurisdiction AS tj ON tj.subJurisdictionId = sj.subJurisdictionId AND tj.isDeleted = 0
         WHERE pl.personId = @personId AND pl.isDeleted = 0 AND pl.role = 'residence' AND pl.isPrimary = 1
           AND pl.startDate <= @today AND (pl.endDate IS NULL OR pl.endDate >= @today);

        INSERT @want (tenantId, accessReason)
        SELECT DISTINCT c.owningTenantId, 'case'
          FROM dbo.exposureCase AS c
         WHERE c.personId = @personId AND c.isDeleted = 0 AND c.caseStatus <> 'closed' AND c.owningTenantId <> @root;

        UPDATE pa SET isDeleted = 1
          FROM dbo.personAccess AS pa
         WHERE pa.personId = @personId AND pa.isDeleted = 0 AND pa.accessReason IN ('residence', 'case')
           AND NOT EXISTS (SELECT 1 FROM @want AS w WHERE w.tenantId = pa.tenantId AND w.accessReason = pa.accessReason);

        INSERT dbo.personAccess (personAccessId, rootTenantId, personId, tenantId, accessReason)
        SELECT CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), @root, @personId, w.tenantId, w.accessReason
          FROM @want AS w
         WHERE NOT EXISTS (SELECT 1 FROM dbo.personAccess AS pa WHERE pa.personId = @personId AND pa.tenantId = w.tenantId
                                                                  AND pa.accessReason = w.accessReason AND pa.isDeleted = 0);

        SET @Comments = N'Person access refreshed.';

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
    , @ObjectName  = N'uspRefreshPersonAccess'
    , @Description = N'Brings dbo.personAccess in step for one person: the tenants serving the county of the current primary residence get residence access, and tenants owning an open case get case access. Called by every procedure that changes a primary residence or a case''s ownership.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspRefreshPersonAccess TO meritWebRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspRefreshPersonAccess TO meritWorkerRole;
END;
GO
