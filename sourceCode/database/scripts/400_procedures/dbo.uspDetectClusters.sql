-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspDetectClusters
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Cluster detection for the caller's root tenant: two or more cases at one primary residence location or building, or at one employer worksite, opened within the window. Each new group becomes a cluster record and an alert task; nothing is merged and no case changes.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission case.open (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspDetectClusters ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T123
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspDetectClusters
      @windowDays INT = 365
      , @minCount   INT = 2
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspDetectClusters]')
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
    SET @KeyParameters = CONCAT (N'windowDays=', @windowDays, N', minCount=', @minCount);

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

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();
        DECLARE @from DATE = DATEADD (DAY, -@windowDays, auth.udfSessionToday ());
        DECLARE @to DATE = auth.udfSessionToday ();
        DECLARE @groups TABLE (clusterType VARCHAR (20), sharedKey UNIQUEIDENTIFIER, title NVARCHAR (200), caseCount INT,
                               owner UNIQUEIDENTIFIER);
        DECLARE @members TABLE (clusterType VARCHAR (20), sharedKey UNIQUEIDENTIFIER, exposureCaseId UNIQUEIDENTIFIER);

        -- Shared building (or the location itself when it has no building record), from primary residences on the open date.
        INSERT @members
        SELECT DISTINCT 'building', COALESCE (l.buildingLocationId, l.locationId), c.exposureCaseId
          FROM dbo.exposureCase AS c
          JOIN dbo.personLocation AS pl ON pl.personId = c.personId AND pl.role IN ('residence', 'childCare') AND pl.isDeleted = 0
                                       AND pl.startDate <= c.openedDate AND (pl.endDate IS NULL OR pl.endDate >= c.openedDate)
          JOIN dbo.location AS l ON l.locationId = pl.locationId
         WHERE c.isDeleted = 0 AND c.openedDate BETWEEN @from AND @to AND c.rootTenantId = @root;

        -- Shared employer worksite.
        INSERT @members
        SELECT DISTINCT 'worksite', COALESCE (e.worksiteLocationId, e.organizationId), c.exposureCaseId
          FROM dbo.exposureCase AS c
          JOIN dbo.employment AS e ON e.personId = c.personId AND e.isDeleted = 0
                                  AND (e.startDate IS NULL OR e.startDate <= c.openedDate)
                                  AND (e.endDate IS NULL OR e.endDate >= DATEADD (DAY, -90, c.openedDate))
         WHERE c.isDeleted = 0 AND c.openedDate BETWEEN @from AND @to AND c.rootTenantId = @root;

        INSERT @groups
        SELECT m.clusterType, m.sharedKey,
               CASE m.clusterType WHEN 'building' THEN CONCAT (N'Cases at ', MAX (l.addressLine1), N', ', MAX (l.city))
                    ELSE CONCAT (N'Cases at worksite ', COALESCE (MAX (wl.addressLine1), MAX (o.organizationName))) END,
               COUNT (DISTINCT m.exposureCaseId), MIN (c.owningTenantId)
          FROM @members AS m
          JOIN dbo.exposureCase AS c ON c.exposureCaseId = m.exposureCaseId
          LEFT JOIN dbo.location AS l ON m.clusterType = 'building' AND l.locationId = m.sharedKey
          LEFT JOIN dbo.location AS wl ON m.clusterType = 'worksite' AND wl.locationId = m.sharedKey
          LEFT JOIN dbo.organization AS o ON m.clusterType = 'worksite' AND o.organizationId = m.sharedKey
         GROUP BY m.clusterType, m.sharedKey
        HAVING COUNT (DISTINCT m.exposureCaseId) >= @minCount;

        -- A cluster that spans tenants goes to the root (the nearest common ancestor in a one-level tree).
        UPDATE g SET owner = @root
          FROM @groups AS g
         WHERE (SELECT COUNT (DISTINCT c.owningTenantId) FROM @members AS m JOIN dbo.exposureCase AS c ON c.exposureCaseId = m.exposureCaseId
                 WHERE m.clusterType = g.clusterType AND m.sharedKey = g.sharedKey) > 1;

        DECLARE @new TABLE (clusterId UNIQUEIDENTIFIER, clusterType VARCHAR (20), sharedKey UNIQUEIDENTIFIER, title NVARCHAR (200),
                            owner UNIQUEIDENTIFIER);

        INSERT dbo.cluster (clusterId, rootTenantId, owningTenantId, clusterType, sharedKey, title, windowStart, windowEnd)
        OUTPUT inserted.clusterId, inserted.clusterType, inserted.sharedKey, inserted.title, inserted.owningTenantId INTO @new
        SELECT CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), @root, g.owner, g.clusterType, g.sharedKey, g.title, @from, @to
          FROM @groups AS g
         WHERE NOT EXISTS (SELECT 1 FROM dbo.cluster AS x WHERE x.clusterType = g.clusterType AND x.sharedKey = g.sharedKey
                                                            AND x.clusterStatus <> 'closed' AND x.isDeleted = 0);

        -- Membership for new and existing open clusters.
        INSERT dbo.caseCluster (caseClusterId, rootTenantId, owningTenantId, clusterId, exposureCaseId, joinedDate)
        SELECT CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), @root, cl.owningTenantId, cl.clusterId, m.exposureCaseId, @to
          FROM @members AS m
          JOIN dbo.cluster AS cl ON cl.clusterType = m.clusterType AND cl.sharedKey = m.sharedKey AND cl.clusterStatus <> 'closed'
                                AND cl.isDeleted = 0
         WHERE EXISTS (SELECT 1 FROM @groups AS g WHERE g.clusterType = m.clusterType AND g.sharedKey = m.sharedKey)
           AND NOT EXISTS (SELECT 1 FROM dbo.caseCluster AS cc WHERE cc.clusterId = cl.clusterId
                              AND cc.exposureCaseId = m.exposureCaseId AND cc.isDeleted = 0);

        INSERT dbo.workTask (workTaskId, rootTenantId, owningTenantId, queueCode, title, detail, clusterId, isUrgent)
        SELECT CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), @root, n.owner, 'cluster', n.title,
               CONCAT (N'Cluster alert for the ', CASE n.clusterType WHEN 'building' THEN N'residential' ELSE N'occupational' END,
                       N' team: review the linked cases.'), n.clusterId, 0
          FROM @new AS n;

        SET @Comments = CONCAT ((SELECT COUNT (*) FROM @new), N' new cluster(s).');

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
    , @ObjectName  = N'uspDetectClusters'
    , @Description = N'Cluster detection for the caller''s root tenant: two or more cases at one primary residence location or building, or at one employer worksite, opened within the window. Each new group becomes a cluster record and an alert task; nothing is merged and no case changes.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspDetectClusters TO meritWorkerRole;
END;
GO
