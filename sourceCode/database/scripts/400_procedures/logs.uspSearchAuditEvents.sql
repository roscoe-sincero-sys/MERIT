-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   logs.uspSearchAuditEvents
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Searches the audit ledger within the caller's root tenant by user, record, action, record type and time, newest first, one page at a time with the total. Record type 'report' covers every report.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission audit.view (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec logs.uspSearchAuditEvents ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T051
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE logs.uspSearchAuditEvents
      @appUserId   UNIQUEIDENTIFIER = NULL
      , @entityId    UNIQUEIDENTIFIER = NULL
      , @eventAction VARCHAR (20) = NULL
      , @entityType  VARCHAR (30) = NULL
      , @fromUtc     DATETIME2 (3) = NULL
      , @toUtc       DATETIME2 (3) = NULL
      , @pageNumber  INT = 1
      , @pageSize    INT = 100
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[logs].[uspSearchAuditEvents]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'appUserId=', @appUserId, N', entityId=', @entityId, N', eventAction=', @eventAction, N', entityType=', @entityType, N', pageNumber=', @pageNumber, N', pageSize=', @pageSize);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'audit.view') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (audit.view).', 1;
        END;

        IF @pageNumber < 1 OR @pageSize NOT BETWEEN 1 AND 10000
        BEGIN
            ;THROW 50000, N'@pageNumber must be 1 or more and @pageSize between 1 and 10000.', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        SELECT e.auditEventId, e.eventUtc, e.appUserId, u.displayName, e.eventAction, e.entityType, e.entityId,
               e.recordCount, e.clientAddress, COUNT (*) OVER () AS totalCount
          FROM logs.auditEvent AS e
          LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
         WHERE e.rootTenantId = auth.udfSessionRootTenantId ()
           AND (@appUserId IS NULL OR e.appUserId = @appUserId)
           AND (@entityId IS NULL OR e.entityId = @entityId)
           AND (@eventAction IS NULL OR e.eventAction = @eventAction)
           AND (@entityType IS NULL OR e.entityType = @entityType OR (@entityType = 'report' AND e.entityType LIKE 'report:%'))
           AND (@fromUtc IS NULL OR e.eventUtc >= @fromUtc)
           AND (@toUtc IS NULL OR e.eventUtc < @toUtc)
         ORDER BY e.auditEventId DESC
        OFFSET (@pageNumber - 1) * @pageSize ROWS
         FETCH NEXT @pageSize ROWS ONLY
        OPTION (RECOMPILE);

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
      @SchemaName  = N'logs'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspSearchAuditEvents'
    , @Description = N'Searches the audit ledger within the caller''s root tenant by user, record, action, record type and time, newest first, one page at a time with the total. Record type ''report'' covers every report.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspSearchAuditEvents TO meritWebRole;
END;
GO
