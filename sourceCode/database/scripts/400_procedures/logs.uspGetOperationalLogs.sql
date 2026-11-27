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

Platform-admin operational log summary: per-token allowlisted logs.ExecutionLog failures (#338), recent logs.DdlChange events, and logs.uspDdlAuditVerify integrity posture via ungranted owner helper (#330, B3).

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

        -- 1. Allowlisted ExecutionLog failure diagnostics (#338, Claude I7): filter per token even on legacy rows.
        SELECT TOP (@limit)
               e.ExecutionLogId, e.ProcedureName, e.StartDateUtc, e.EndDateUtc, e.ElapsedMilliseconds,
               e.ReCreatedAfterRollback, e.ErrorNumber, e.ErrorLine, e.ErrorProcedure,
               CASE
                   WHEN e.KeyParameters IS NULL OR CHARINDEX (N'=', e.KeyParameters) = 0 THEN LEFT (e.KeyParameters, 200)
                   ELSE (SELECT LEFT (STRING_AGG (
                            CASE
                                WHEN kv.k IS NOT NULL AND (
                                       kv.k LIKE N'%Id' OR kv.k LIKE N'%Count' OR kv.k LIKE N'%Number'
                                    OR kv.k LIKE N'%Days' OR kv.k LIKE N'%Hours' OR kv.k LIKE N'%Minutes'
                                    OR kv.k LIKE N'%Size' OR kv.k LIKE N'%Rows' OR kv.k LIKE N'%Code'
                                    OR kv.k LIKE N'%Type' OR kv.k LIKE N'%Action' OR kv.k LIKE N'%Kind'
                                    OR kv.k LIKE N'%Source' OR kv.k LIKE N'%Role' OR kv.k LIKE N'%By'
                                    OR kv.k LIKE N'%Only' OR kv.k LIKE N'%Fips' OR kv.k LIKE N'%Status'
                                    OR kv.k LIKE N'%Date' OR kv.k LIKE N'from%' OR kv.k LIKE N'to%'
                                    OR kv.k LIKE N'effective%' OR kv.k LIKE N'period%' OR kv.k LIKE N'is%'
                                    OR kv.k LIKE N'include%' OR kv.k LIKE N'has%'
                                    OR kv.k IN (N'top', N'page', N'assignedToMe', N'classification', N'closureReason', N'viewKind',
                                                N'workerName', N'settingKey', N'decision', N'population', N'sort', N'status', N'step', N'method'))
                                THEN LTRIM (RTRIM (tok.value))
                                ELSE N'[redacted]'
                            END, N', '), 500)
                           FROM STRING_SPLIT (LEFT (e.KeyParameters, 500), N',') AS tok
                          CROSS APPLY (SELECT LTRIM (RTRIM (LEFT (tok.value, NULLIF (CHARINDEX (N'=', tok.value), 0) - 1))) AS k) AS kv)
               END AS KeyParameters,
               e.createdBy
          FROM logs.ExecutionLog AS e
         WHERE e.isDeleted = 0 AND e.Successful = 0 AND e.StartDateUtc >= @since
         ORDER BY e.ExecutionLogId DESC;

        -- 2. Recent DDL changes
        SELECT TOP (@limit)
               ChangeId, RecordedUtc AS EventTimeUtc, EventType, SchemaName AS ObjectSchema, ObjectName, ObjectType,
               EventLoginName AS LoginName, createdBy AS AppUser
          FROM logs.DdlChange
         WHERE RecordedUtc >= @since
         ORDER BY ChangeId DESC;

        -- 3. DDL audit integrity posture (called in owner context ONLY after caller-context security.investigate check above)
        EXEC logs.uspDdlAuditVerifyInternal @ProblemsOnly = 0;

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
    , @Description = N'Platform-admin operational log summary: per-token allowlisted logs.ExecutionLog failures (#338), recent logs.DdlChange events, and logs.uspDdlAuditVerify integrity posture via ungranted owner helper (#330, B3).';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspGetOperationalLogs TO meritWebRole;
END;
GO
