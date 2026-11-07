-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   logs.uspRecordScreenVisit
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Idempotently records only screen metadata under the current session. Context is resolved server-side; a client cannot supply user, root, profile, role or session reference. Reconciles a server-rendered visit with its browser tab/predecessor hint. No URLs or scope keys are logged.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec logs.uspRecordScreenVisit ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		227
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE logs.uspRecordScreenVisit
      @screenVisitId UNIQUEIDENTIFIER
      , @screenCode VARCHAR (60)
      , @tabId UNIQUEIDENTIFIER = NULL
      , @previousVisitId UNIQUEIDENTIFIER = NULL
      , @navigationType VARCHAR (12) = 'server'
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[logs].[uspRecordScreenVisit]')
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
    SET @KeyParameters = N'screen usage metadata';

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

        DECLARE @scope UNIQUEIDENTIFIER = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER);
        DECLARE @me UNIQUEIDENTIFIER = auth.udfSessionUserId (), @reference UNIQUEIDENTIFIER, @root UNIQUEIDENTIFIER,
                @acting UNIQUEIDENTIFIER, @profile UNIQUEIDENTIFIER, @viewAs UNIQUEIDENTIFIER;
        SELECT @reference = s.sessionReference, @root = s.rootTenantId, @acting = s.actingTenantId,
               @profile = s.userProfileId, @viewAs = s.viewAsUserId
          FROM auth.sessionScope AS s JOIN auth.appUser AS u ON u.appUserId = s.appUserId
         WHERE s.sessionScopeId = @scope AND s.appUserId = @me AND s.revokedUtc IS NULL
           AND s.expiresUtc > SYSUTCDATETIME () AND u.isActive = 1 AND u.isDeleted = 0;
        IF @reference IS NULL
        BEGIN
            ;THROW 51403, N'A current signed-in session is required.', 1;
        END;
        DECLARE @visitLock INT, @visitResource NVARCHAR (100) = CONCAT (N'MERIT.screenVisit.', @screenVisitId);
        EXEC @visitLock = sys.sp_getapplock @Resource = @visitResource, @LockMode = 'Exclusive',
             @LockOwner = 'Transaction', @LockTimeout = 1000;
        IF @visitLock < 0
        BEGIN
            ;THROW 51409, N'The usage visit is busy; navigation is unaffected.', 1;
        END;

        IF @screenVisitId IS NULL OR NULLIF (@screenCode, '') IS NULL
           OR @screenCode COLLATE Latin1_General_100_BIN2 LIKE '%[^a-zA-Z0-9.-]%'
           OR @navigationType NOT IN ('server', 'direct', 'menu', 'link', 'search', 'back', 'reload', 'redirect', 'router')
        BEGIN
            ;THROW 51422, N'Invalid screen metadata.', 1;
        END;
        IF EXISTS (SELECT 1 FROM auth.sessionScope WHERE sessionScopeId IN (@screenVisitId, @tabId, @previousVisitId))
        BEGIN
            ;THROW 51422, N'Usage ids must not be authentication keys.', 1;
        END;
        -- A predecessor is a validated chain within one logical session and tab, not an untrusted URL.
        IF NOT EXISTS (SELECT 1 FROM logs.screenVisit WHERE screenVisitId = @previousVisitId AND appUserId = @me
                        AND sessionReference = @reference AND tabId = @tabId AND isDeleted = 0 AND screenVisitId <> @screenVisitId)
            SET @previousVisitId = NULL;

        IF EXISTS (SELECT 1 FROM logs.screenVisit WHERE screenVisitId = @screenVisitId)
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM logs.screenVisit WHERE screenVisitId = @screenVisitId AND appUserId = @me
                            AND sessionReference = @reference AND screenCode = @screenCode AND isDeleted = 0
                            AND (tabId IS NULL OR @tabId IS NULL OR tabId = @tabId))
            BEGIN
                ;THROW 51403, N'That visit is not available.', 1;
            END;
            UPDATE logs.screenVisit
               SET tabId = COALESCE (tabId, @tabId), previousVisitId = COALESCE (previousVisitId, @previousVisitId),
                   navigationType = CASE WHEN @navigationType = 'server' THEN navigationType ELSE @navigationType END
             WHERE screenVisitId = @screenVisitId;
        END
        ELSE
        BEGIN
            DECLARE @roles VARCHAR (MAX);
            SELECT @roles = STRING_AGG (CAST (r.roleCode AS VARCHAR (MAX)), ',') WITHIN GROUP (ORDER BY r.roleCode)
              FROM (SELECT DISTINCT ro.roleCode FROM auth.userAssignment AS a
                      JOIN auth.role AS ro ON ro.roleId = a.roleId AND ro.isDeleted = 0
                     WHERE a.appUserId = COALESCE (@viewAs, @me) AND a.isDeleted = 0
                       AND a.startDate <= auth.udfSessionToday () AND (a.endDate IS NULL OR a.endDate >= auth.udfSessionToday ())
                       AND (a.tenantId = @acting OR (a.includeDescendants = 1 AND EXISTS
                            (SELECT 1 FROM auth.tenantClosure AS tc WHERE tc.ancestorTenantId = a.tenantId AND tc.descendantTenantId = @acting AND tc.isDeleted = 0)))
                       AND (@profile IS NULL OR EXISTS (SELECT 1 FROM auth.userProfileGrant AS pg WHERE pg.userProfileId = @profile
                            AND pg.roleId = a.roleId AND pg.isDeleted = 0 AND (pg.tenantId = @acting OR (pg.includeDescendants = 1 AND EXISTS
                            (SELECT 1 FROM auth.tenantClosure AS tc WHERE tc.ancestorTenantId = pg.tenantId AND tc.descendantTenantId = @acting AND tc.isDeleted = 0)))))) AS r;
            INSERT logs.screenVisit (screenVisitId, rootTenantId, actingTenantId, appUserId, sessionReference, profileId,
                                    viewAsUserId, roleCodes, screenCode, navigationType, tabId, previousVisitId, createdBy, modifiedBy)
            VALUES (@screenVisitId, @root, @acting, @me, @reference, @profile, @viewAs, @roles, @screenCode,
                    @navigationType, @tabId, @previousVisitId, N'MERIT usage', N'MERIT usage');
        END;

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
      @SchemaName  = N'logs'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspRecordScreenVisit'
    , @Description = N'Idempotently records only screen metadata under the current session. Context is resolved server-side; a client cannot supply user, root, profile, role or session reference. Reconciles a server-rendered visit with its browser tab/predecessor hint. No URLs or scope keys are logged.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspRecordScreenVisit TO meritWebRole;
END;
GO
