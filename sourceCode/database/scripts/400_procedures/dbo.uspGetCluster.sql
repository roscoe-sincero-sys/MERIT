-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspGetCluster
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

One cluster: its header, every case that joined (with the primary residence on the case's open date and its coordinates, for the map) and its open tasks.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission case.view (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspGetCluster ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T109
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspGetCluster
      @clusterId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspGetCluster]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'clusterId=', @clusterId);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'case.view') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (case.view).', 1;
        END;

        IF NOT EXISTS (SELECT 1 FROM dbo.cluster WHERE clusterId = @clusterId AND isDeleted = 0)
        BEGIN
            ;THROW 51404, N'The cluster was not found, or you do not have access to it.', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @isDbo BIT = CASE WHEN USER_NAME () = N'dbo' THEN 1 ELSE 0 END;
        DECLARE @canViewTasks BIT = CASE WHEN @isDbo = 1 THEN 1 ELSE auth.udfHasPermission (N'task.view') END;
        DECLARE @idTenants TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
        IF @isDbo = 0
        BEGIN
            INSERT @idTenants (tenantId)
            SELECT tenantId
              FROM auth.tvfIdentifiedTenants ('case.view');
        END;

        DECLARE @clusterIdentified BIT = CASE WHEN @isDbo = 1
                                                OR EXISTS (SELECT 1 FROM dbo.cluster AS cl
                                                             JOIN @idTenants AS idt ON idt.tenantId = cl.owningTenantId
                                                            WHERE cl.clusterId = @clusterId)
                                              THEN 1 ELSE 0 END;

        SELECT cl.clusterId, cl.clusterType,
               CASE WHEN @clusterIdentified = 1 THEN cl.sharedKey ELSE NULL END AS sharedKey,
               CASE WHEN @clusterIdentified = 1 THEN cl.title ELSE N'De-identified cluster' END AS title,
               cl.clusterStatus, cl.windowStart, cl.windowEnd,
               CASE WHEN @clusterIdentified = 1 THEN cl.notes ELSE NULL END AS notes,
               cl.createdDateUtc, t.tenantName AS owningTenantName, cl.rowVersion,
               CAST (CASE WHEN EXISTS (SELECT 1 FROM auth.tvfTenantWritePredicate (cl.rootTenantId, cl.owningTenantId, cl.isDeleted)) THEN 1 ELSE 0 END AS BIT) AS canEdit
          FROM dbo.cluster AS cl
          LEFT JOIN auth.tenant AS t ON t.tenantId = cl.owningTenantId
         WHERE cl.clusterId = @clusterId;

        SELECT cc.caseClusterId, cc.exposureCaseId, c.caseNumber,
               CASE WHEN @isDbo = 1 OR idt.tenantId IS NOT NULL THEN c.personId ELSE CAST (0x0 AS UNIQUEIDENTIFIER) END AS personId,
               CASE WHEN @isDbo = 1 OR idt.tenantId IS NOT NULL THEN p.lastName ELSE N'De-identified' END AS lastName,
               CASE WHEN @isDbo = 1 OR idt.tenantId IS NOT NULL THEN p.firstName ELSE NULL END AS firstName,
               a.code AS analyteCode,
               c.population, c.classification, c.caseStatus, c.openedDate, h.valueCanonical AS highestValue,
               h.unitCanonical AS highestUnit, cc.joinedDate, cc.leftDate,
               CASE WHEN @isDbo = 1 OR idt.tenantId IS NOT NULL THEN l.locationId ELSE NULL END AS locationId,
               CASE WHEN @isDbo = 1 OR idt.tenantId IS NOT NULL THEN l.addressLine1 ELSE NULL END AS addressLine1,
               CASE WHEN @isDbo = 1 OR idt.tenantId IS NOT NULL THEN l.unit ELSE NULL END AS unit,
               CASE WHEN @isDbo = 1 OR idt.tenantId IS NOT NULL THEN l.city ELSE NULL END AS city,
               CASE WHEN @isDbo = 1 OR idt.tenantId IS NOT NULL THEN l.latitude ELSE NULL END AS latitude,
               CASE WHEN @isDbo = 1 OR idt.tenantId IS NOT NULL THEN l.longitude ELSE NULL END AS longitude
          FROM dbo.caseCluster AS cc
          JOIN dbo.exposureCase AS c ON c.exposureCaseId = cc.exposureCaseId
          JOIN dbo.person AS p ON p.personId = c.personId
          JOIN config.analyte AS a ON a.analyteId = c.analyteId
          LEFT JOIN @idTenants AS idt ON idt.tenantId = c.owningTenantId
          LEFT JOIN dbo.labResult AS h ON h.labResultId = c.highestLabResultId
         OUTER APPLY (SELECT TOP (1) x.locationId, x.addressLine1, x.unit, x.city, x.latitude, x.longitude
                        FROM dbo.personLocation AS pl
                        JOIN dbo.location AS x ON x.locationId = pl.locationId
                       WHERE pl.personId = c.personId AND pl.role = 'residence' AND pl.isPrimary = 1 AND pl.isDeleted = 0
                         AND pl.startDate <= c.openedDate
                       ORDER BY CASE WHEN pl.endDate IS NULL OR pl.endDate >= c.openedDate THEN 0 ELSE 1 END,
                                pl.startDate DESC) AS l
         WHERE cc.clusterId = @clusterId AND cc.isDeleted = 0
         ORDER BY CASE WHEN cc.leftDate IS NULL THEN 0 ELSE 1 END, c.openedDate;

        SELECT w.workTaskId, w.queueCode,
               CASE WHEN @isDbo = 1 OR (@clusterIdentified = 1 AND idt.tenantId IS NOT NULL) THEN w.title ELSE N'Cluster alert' END AS title,
               CASE WHEN @isDbo = 1 OR (@clusterIdentified = 1 AND idt.tenantId IS NOT NULL) THEN w.detail ELSE NULL END AS detail,
               w.dueDate, w.isUrgent,
               CASE WHEN @isDbo = 1 OR (@clusterIdentified = 1 AND idt.tenantId IS NOT NULL) THEN u.displayName ELSE NULL END AS assignedToName
          FROM dbo.workTask AS w
          LEFT JOIN @idTenants AS idt ON idt.tenantId = w.owningTenantId
          LEFT JOIN auth.appUser AS u ON u.appUserId = w.assignedToUserId
         WHERE w.clusterId = @clusterId
           AND w.taskStatus = 'open'
           AND w.isDeleted = 0
           AND @canViewTasks = 1;

        -- =========================================================================================
        -- ===== End of the procedure's own work. ===================================================
        -- =========================================================================================

    END TRY
    BEGIN CATCH

        SELECT @ErrorNumber = ERROR_NUMBER ()
             , @ErrorProc   = ERROR_PROCEDURE ()
             , @ErrorLine   = ERROR_LINE ()
             , @ErrorMsg    = ERROR_MESSAGE ()
                            + N' (error '  + CAST (ERROR_NUMBER () AS NVARCHAR (11))
                            + N', line '   + CAST (ERROR_LINE ()   AS NVARCHAR (11)) + N')';

        EXEC logs.uspRecordExecutionError
              @ProcedureName   = @ProcName
            , @KeyParameters   = @KeyParameters
            , @ExecutionLogId  = NULL
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
    , @ObjectName  = N'uspGetCluster'
    , @Description = N'One cluster: its header, every case that joined (with the primary residence on the case''s open date and its coordinates, for the map) and its open tasks.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspGetCluster TO meritWebRole;
END;
GO
