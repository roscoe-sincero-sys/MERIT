-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   logs.uspExportSignInEvents
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Cursor-paginated canonical export of logs.signInEvent rows over [@fromUtc, @toUtc) within the caller's security review scope. Never returns attempted names (#328).

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission security.review or tenant.administer (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec logs.uspExportSignInEvents ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		328
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE logs.uspExportSignInEvents
      @fromUtc            DATETIME2 (3)
      , @toUtc              DATETIME2 (3)
      , @afterEventUtc      DATETIME2 (3) = NULL
      , @afterSignInEventId UNIQUEIDENTIFIER = NULL
      , @batchSize          INT = 2000
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[logs].[uspExportSignInEvents]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'batchSize=', @batchSize);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'security.review') = 0 AND auth.udfHasPermission (N'tenant.administer') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (security.review or tenant.administer).', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @limit INT = CASE WHEN @batchSize BETWEEN 1 AND 5000 THEN @batchSize ELSE 2000 END;
        DECLARE @all BIT = auth.udfHasPermission (N'security.investigate');
        DECLARE @accounts TABLE (appUserId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @accounts SELECT appUserId FROM auth.tvfReviewAccounts ('security.review');

        SELECT TOP (@limit)
               e.signInEventId, e.eventUtc, e.eventType, e.step, e.method, e.reason,
               e.appUserId, u.userName, u.displayName, e.actorUserId, e.rootTenantId,
               e.clientAddress, e.userAgent, e.sessionReference, e.attemptId, e.traceId,
               e.failedSignInCount, e.lockoutEndUtc
          FROM logs.signInEvent AS e
          LEFT JOIN auth.appUser AS u ON u.appUserId = e.appUserId
         WHERE e.isDeleted = 0 AND e.eventUtc >= @fromUtc AND e.eventUtc < @toUtc
           AND (@afterSignInEventId IS NULL
                OR e.eventUtc > @afterEventUtc
                OR (e.eventUtc = @afterEventUtc AND e.signInEventId > @afterSignInEventId))
           AND (@all = 1 OR e.appUserId IN (SELECT appUserId FROM @accounts))
         ORDER BY e.eventUtc ASC, e.signInEventId ASC;

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
    , @ObjectName  = N'uspExportSignInEvents'
    , @Description = N'Cursor-paginated canonical export of logs.signInEvent rows over [@fromUtc, @toUtc) within the caller''s security review scope. Never returns attempted names (#328).';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspExportSignInEvents TO meritWebRole;
END;
GO
