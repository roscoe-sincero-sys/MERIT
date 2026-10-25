-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspGetCaseMap
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Cases for the map: each case with the primary residence on its open date and that location's coordinates. Cases whose residence has no coordinates are returned too, so the screen can list them.

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

exec dbo.uspGetCaseMap ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T109
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspGetCaseMap
      @analyteId  UNIQUEIDENTIFIER = NULL
      , @openedFrom DATE = NULL
      , @openedTo   DATE = NULL
      , @openOnly   BIT = 1
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspGetCaseMap]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'analyteId=', @analyteId, N', openOnly=', @openOnly);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'case.view') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (case.view).', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        SELECT TOP (5000) c.exposureCaseId, c.caseNumber, c.personId, p.lastName, p.firstName, a.code AS analyteCode,
               c.population, c.classification, c.caseStatus, c.openedDate, h.valueCanonical AS highestValue,
               h.unitCanonical AS highestUnit, l.locationId, l.addressLine1, l.city, l.countyFips, l.latitude, l.longitude,
               l.geocodeQuality,
               (SELECT COUNT (*) FROM dbo.caseCluster AS cc
                  JOIN dbo.cluster AS cl ON cl.clusterId = cc.clusterId AND cl.clusterStatus <> 'closed' AND cl.isDeleted = 0
                 WHERE cc.exposureCaseId = c.exposureCaseId AND cc.leftDate IS NULL AND cc.isDeleted = 0) AS openClusterCount
          FROM dbo.exposureCase AS c
          JOIN dbo.person AS p ON p.personId = c.personId
          JOIN config.analyte AS a ON a.analyteId = c.analyteId
          LEFT JOIN dbo.labResult AS h ON h.labResultId = c.highestLabResultId
         OUTER APPLY (SELECT TOP (1) x.locationId, x.addressLine1, x.city, x.countyFips, x.latitude, x.longitude,
                             x.geocodeQuality
                        FROM dbo.personLocation AS pl
                        JOIN dbo.location AS x ON x.locationId = pl.locationId
                       WHERE pl.personId = c.personId AND pl.role = 'residence' AND pl.isPrimary = 1 AND pl.isDeleted = 0
                         AND pl.startDate <= c.openedDate
                       ORDER BY CASE WHEN pl.endDate IS NULL OR pl.endDate >= c.openedDate THEN 0 ELSE 1 END,
                                pl.startDate DESC) AS l
         WHERE c.isDeleted = 0
           AND (@openOnly = 0 OR c.caseStatus <> 'closed')
           AND (@analyteId IS NULL OR c.analyteId = @analyteId)
           AND (@openedFrom IS NULL OR c.openedDate >= @openedFrom)
           AND (@openedTo IS NULL OR c.openedDate <= @openedTo)
         ORDER BY c.openedDate DESC;

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
    , @ObjectName  = N'uspGetCaseMap'
    , @Description = N'Cases for the map: each case with the primary residence on its open date and that location''s coordinates. Cases whose residence has no coordinates are returned too, so the screen can list them.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspGetCaseMap TO meritWebRole;
END;
GO
