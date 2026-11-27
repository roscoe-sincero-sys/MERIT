-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   logs.uspGetOperationalLogs
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Platform-admin operational log summary: allowlisted logs.ExecutionLog failures (#338), recent logs.DdlChange events, and logs.uspDdlAuditVerify integrity posture (#330).

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission security.investigate (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec logs.uspGetOperationalLogs ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		330
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE logs.uspGetOperationalLogs
      @hours INT = 24
      , @top   INT = 100
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[logs].[uspGetOperationalLogs]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'hours=', @hours, N', top=', @top);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'security.investigate') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (security.investigate).', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @since DATETIME2 (3) = DATEADD (HOUR, -CASE WHEN @hours BETWEEN 1 AND 720 THEN @hours ELSE 24 END, SYSUTCDATETIME ());
        DECLARE @limit INT = CASE WHEN @top BETWEEN 1 AND 500 THEN @top ELSE 100 END;

        -- 1. Allowlisted ExecutionLog failure diagnostics (#338): procedure identity, timing, error code/line/procedure, allowlisted KeyParameters, actor.
        --    Raw ErrorMessage, DynamicSql, and ContextMessage are excluded from operational reports/exports.
        SELECT TOP (@limit)
               ExecutionLogId, ProcedureName, StartDateUtc, EndDateUtc, ElapsedMilliseconds,
               ReCreatedAfterRollback, ErrorNumber, ErrorLine, ErrorProcedure, KeyParameters, createdBy
          FROM logs.ExecutionLog
         WHERE isDeleted = 0 AND Successful = 0 AND StartDateUtc >= @since
         ORDER BY ExecutionLogId DESC;

        -- 2. Recent DDL changes
        SELECT TOP (@limit)
               ChangeId, RecordedUtc AS EventTimeUtc, EventType, SchemaName AS ObjectSchema, ObjectName, ObjectType,
               EventLoginName AS LoginName, createdBy AS AppUser
          FROM logs.DdlChange
         WHERE RecordedUtc >= @since
         ORDER BY ChangeId DESC;

        -- 3. DDL audit integrity posture
        EXEC logs.uspDdlAuditVerify;

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
    , @ObjectName  = N'uspGetOperationalLogs'
    , @Description = N'Platform-admin operational log summary: allowlisted logs.ExecutionLog failures (#338), recent logs.DdlChange events, and logs.uspDdlAuditVerify integrity posture (#330).';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspGetOperationalLogs TO meritWebRole;
END;
GO
