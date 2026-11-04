-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   logs.uspListUnknownUserNames
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Attempts at user names that match no account over the last @hours, grouped by the name's keyed hash: attempts, addresses, first and last time. With @includeProtected = 1 each group also carries one encrypted copy of the name, which the web application decrypts after a step-up and audits.

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

exec logs.uspListUnknownUserNames ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T213
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE logs.uspListUnknownUserNames
      @hours            INT = 168
      , @includeProtected BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[logs].[uspListUnknownUserNames]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'hours=', @hours, N', includeProtected=', @includeProtected);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'security.investigate') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (security.investigate).', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @since DATETIME2 (3) = DATEADD (HOUR, -CASE WHEN @hours BETWEEN 1 AND 8760 THEN @hours ELSE 168 END, SYSUTCDATETIME ());

        SELECT TOP (500) CONVERT (VARCHAR (64), e.attemptedNameHash, 2)                  AS attemptedNameHash
             , COUNT (*)                                                                   AS attempts
             , COUNT (DISTINCT e.clientAddress)                                            AS addresses
             , MIN (e.eventUtc)                                                            AS firstUtc
             , MAX (e.eventUtc)                                                            AS lastUtc
             , CASE WHEN @includeProtected = 1 THEN MAX (e.attemptedNameProtected) END     AS attemptedNameProtected
             , (SELECT STRING_AGG (a.clientAddress, ', ')
                  FROM (SELECT DISTINCT TOP (5) x.clientAddress
                          FROM logs.signInEvent AS x
                         WHERE x.attemptedNameHash = e.attemptedNameHash AND x.eventUtc >= @since AND x.clientAddress IS NOT NULL
                           AND x.isDeleted = 0) AS a)
                                                                                           AS topAddresses
          FROM logs.signInEvent AS e
         WHERE e.eventType = 'unknownUser' AND e.attemptedNameHash IS NOT NULL AND e.eventUtc >= @since AND e.isDeleted = 0
         GROUP BY e.attemptedNameHash
         ORDER BY attempts DESC, lastUtc DESC;

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
    , @ObjectName  = N'uspListUnknownUserNames'
    , @Description = N'Attempts at user names that match no account over the last @hours, grouped by the name''s keyed hash: attempts, addresses, first and last time. With @includeProtected = 1 each group also carries one encrypted copy of the name, which the web application decrypts after a step-up and audits.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspListUnknownUserNames TO meritWebRole;
END;
GO
