-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   auth.uspCreateSessionScope
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Resolves what a session may see and do: from the master profile, from one of the user's own profiles clipped to the master, or (view as) from another user's master profile clipped to the tenants the caller administers and to the caller's own rights. Stores the tenants with an access level each, the data roots, the administration scope and the permissions; the key goes into the session cookie and into SESSION_CONTEXT on every connection.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Returns three result sets: the scope summary, the permission codes, and the tenants covered with their access level. A platform administrator has one profile: switching and viewing as are refused. The worker calls this for the user a job records, so background work runs under the same security as the request that started it.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec auth.uspCreateSessionScope ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T160
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE auth.uspCreateSessionScope
      @sessionScopeId UNIQUEIDENTIFIER
      , @appUserId      UNIQUEIDENTIFIER
      , @expiresUtc     DATETIME2 (3)
      , @userProfileId  UNIQUEIDENTIFIER = NULL
      , @viewAsUserId   UNIQUEIDENTIFIER = NULL
      , @actingTenantId UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[auth].[uspCreateSessionScope]')
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
    SET @KeyParameters = CONCAT (N'sessionScopeId=', @sessionScopeId, N', appUserId=', @appUserId, N', userProfileId=', @userProfileId, N', viewAsUserId=', @viewAsUserId);

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

        DECLARE @platform UNIQUEIDENTIFIER = CAST (HASHBYTES ('MD5', N'tenant:PLATFORM') AS UNIQUEIDENTIFIER);
        DECLARE @isPlatformUser BIT = (SELECT CASE WHEN h.tenantType = 'platform' THEN 1 ELSE 0 END
                                          FROM auth.appUser AS u
                                          LEFT JOIN auth.tenant AS h ON h.tenantId = u.homeTenantId
                                         WHERE u.appUserId = @appUserId AND u.isActive = 1 AND u.isDeleted = 0);

        IF @isPlatformUser IS NULL
        BEGIN
            ;THROW 51404, N'The account is not active.', 1;
        END;

        -- Grants are dated today in the user's jurisdiction (issue #36). A signed-in caller's connection already carries the
        -- zone; a sign-in does not yet, so it is set here from the user's data root.
        IF SESSION_CONTEXT (N'timeZone') IS NULL
        BEGIN
            DECLARE @zone VARCHAR (64) = (SELECT r.timeZone FROM auth.appUser AS u JOIN auth.tenant AS r ON r.tenantId = u.rootTenantId
                                           WHERE u.appUserId = @appUserId);
            EXEC sys.sp_set_session_context @key = N'timeZone', @value = @zone;
        END;

        IF @userProfileId IS NOT NULL
           AND EXISTS (SELECT 1 FROM auth.userProfile WHERE userProfileId = @userProfileId AND isMaster = 1)
        BEGIN
            SET @userProfileId = NULL;
        END;

        IF @isPlatformUser = 1 AND (@userProfileId IS NOT NULL OR @viewAsUserId IS NOT NULL)
        BEGIN
            ;THROW 51403, N'A platform administrator has one profile and cannot switch profiles or view as another user.', 1;
        END;

        DECLARE @master TABLE (tenantId UNIQUEIDENTIFIER, permissionCode VARCHAR (60), permissionLevel VARCHAR (10), grantRank INT,
                               PRIMARY KEY (tenantId, permissionCode));
        INSERT @master SELECT tenantId, permissionCode, permissionLevel, grantRank FROM auth.tvfUserGrantPermissions (@appUserId);

        DECLARE @x TABLE (tenantId UNIQUEIDENTIFIER, permissionCode VARCHAR (60), permissionLevel VARCHAR (10), grantRank INT,
                          PRIMARY KEY (tenantId, permissionCode));
        DECLARE @profileName NVARCHAR (120) = N'Master profile';

        IF @viewAsUserId IS NOT NULL
        BEGIN
            IF @viewAsUserId = @appUserId
            BEGIN
                ;THROW 51422, N'You are already viewing MERIT as yourself.', 1;
            END;

            IF NOT EXISTS (SELECT 1 FROM auth.appUser AS u JOIN auth.tenant AS h ON h.tenantId = u.homeTenantId
                            WHERE u.appUserId = @viewAsUserId AND u.isActive = 1 AND u.isDeleted = 0 AND h.tenantType <> 'platform')
            BEGIN
                ;THROW 51404, N'That user was not found or is not active.', 1;
            END;

            -- What they can see and do, only where the caller administers, and never more than the caller may do there.
            INSERT @x
            SELECT v.tenantId, v.permissionCode, v.permissionLevel, IIF (v.grantRank < m.grantRank, v.grantRank, m.grantRank)
              FROM auth.tvfUserGrantPermissions (@viewAsUserId) AS v
              JOIN @master AS m ON m.tenantId = v.tenantId AND m.permissionCode = v.permissionCode
             WHERE EXISTS (SELECT 1 FROM @master AS a WHERE a.tenantId = v.tenantId AND a.permissionCode = 'tenant.administer');

            IF NOT EXISTS (SELECT 1 FROM @x)
            BEGIN
                ;THROW 51403, N'You can view MERIT as a user only where you administer their access.', 1;
            END;

            SET @profileName = (SELECT CONCAT (N'Viewing as ', displayName) FROM auth.appUser WHERE appUserId = @viewAsUserId);
        END
        ELSE IF @userProfileId IS NOT NULL
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM auth.userProfile WHERE userProfileId = @userProfileId AND appUserId = @appUserId
                                                            AND isActive = 1 AND isDeleted = 0)
            BEGIN
                ;THROW 51404, N'That profile was not found or is not active.', 1;
            END;

            -- A profile is clipped to the master as it is today, so it never outlives a narrowed master.
            INSERT @x
            SELECT p.tenantId, p.permissionCode, p.permissionLevel, IIF (p.grantRank < m.grantRank, p.grantRank, m.grantRank)
              FROM auth.tvfProfileGrantPermissions (@userProfileId) AS p
              JOIN @master AS m ON m.tenantId = p.tenantId AND m.permissionCode = p.permissionCode;

            SET @profileName = (SELECT profileName FROM auth.userProfile WHERE userProfileId = @userProfileId);
        END
        ELSE
        BEGIN
            INSERT @x SELECT tenantId, permissionCode, permissionLevel, grantRank FROM @master;
        END;

        -- Records: tenants where the scope holds a read or write permission, at the highest level held there.
        DECLARE @tenants TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY, accessRank INT);
        INSERT @tenants SELECT tenantId, MAX (grantRank) FROM @x WHERE permissionLevel IN ('read', 'write') GROUP BY tenantId;

        DECLARE @adminScope TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @adminScope SELECT DISTINCT tenantId FROM @x WHERE permissionCode = 'tenant.administer';

        DECLARE @isPlatformAdmin BIT = CASE WHEN @isPlatformUser = 1
                                             AND EXISTS (SELECT 1 FROM @x WHERE permissionCode = 'platform.administer')
                                            THEN 1 ELSE 0 END;

        -- Acting tenant: the one asked for when the scope covers it, else the highest tenant in the scope.
        DECLARE @acting UNIQUEIDENTIFIER =
            CASE WHEN EXISTS (SELECT 1 FROM @tenants WHERE tenantId = @actingTenantId)
                   OR EXISTS (SELECT 1 FROM @adminScope WHERE tenantId = @actingTenantId) THEN @actingTenantId END;
        IF @acting IS NULL
        BEGIN
            SET @acting = (SELECT TOP (1) s.tenantId
                             FROM (SELECT tenantId FROM @tenants UNION SELECT tenantId FROM @adminScope) AS s
                             JOIN auth.tenantClosure AS c ON c.descendantTenantId = s.tenantId AND c.ancestorTenantId = @platform
                                                         AND c.isDeleted = 0
                            ORDER BY c.depth, s.tenantId);
        END;
        IF @acting IS NULL
        BEGIN
            SET @acting = (SELECT homeTenantId FROM auth.appUser WHERE appUserId = COALESCE (@viewAsUserId, @appUserId));
        END;

        DECLARE @root UNIQUEIDENTIFIER = COALESCE ((SELECT rootTenantId FROM auth.tenant WHERE tenantId = @acting), @platform);

        INSERT auth.sessionScope (sessionScopeId, appUserId, rootTenantId, actingTenantId, userProfileId, viewAsUserId,
                                  isPlatformAdmin, expiresUtc)
        VALUES (@sessionScopeId, @appUserId, @root, @acting, @userProfileId, @viewAsUserId, @isPlatformAdmin, @expiresUtc);

        INSERT auth.sessionScopeTenant (sessionScopeTenantId, sessionScopeId, tenantId, accessLevel)
        SELECT CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), @sessionScopeId, tenantId, CASE accessRank WHEN 3 THEN 'full' WHEN 2 THEN 'write' ELSE 'read' END FROM @tenants;

        INSERT auth.sessionScopeRoot (sessionScopeRootId, sessionScopeId, rootTenantId, accessLevel)
        SELECT CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), @sessionScopeId, r.rootTenantId, CASE r.accessRank WHEN 3 THEN 'full' WHEN 2 THEN 'write' ELSE 'read' END
          FROM (SELECT t.rootTenantId, MAX (x.accessRank) AS accessRank
                  FROM @tenants AS x JOIN auth.tenant AS t ON t.tenantId = x.tenantId
                 GROUP BY t.rootTenantId) AS r;

        INSERT auth.sessionScopeAdminTenant (sessionScopeAdminTenantId, sessionScopeId, tenantId)
        SELECT CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), @sessionScopeId, tenantId FROM @adminScope;

        INSERT auth.sessionScopePermission (sessionScopePermissionId, sessionScopeId, permissionCode)
        SELECT CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), @sessionScopeId, p.permissionCode FROM (SELECT DISTINCT permissionCode FROM @x) AS p;

        IF @viewAsUserId IS NOT NULL
        BEGIN
            INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId)
            VALUES (@appUserId, @root, 'view', 'viewAsUser', @viewAsUserId);
        END;

        SELECT @sessionScopeId                AS sessionScopeId
             , u.appUserId
             , u.userName
             , u.displayName
             , @root                          AS rootTenantId
             , @acting                        AS actingTenantId
             , t.tenantName                   AS actingTenantName
             , r.statePostalCode
             , COALESCE (r.timeZone, 'Eastern Standard Time') AS timeZone
             , u.securityStamp
             , @expiresUtc                    AS expiresUtc
             , @userProfileId                 AS userProfileId
             , @profileName                   AS profileName
             , @viewAsUserId                  AS viewAsUserId
             , v.displayName                  AS viewAsDisplayName
             , @isPlatformAdmin               AS isPlatformAdmin
          FROM auth.appUser AS u
          LEFT JOIN auth.tenant  AS r ON r.tenantId = @root
          LEFT JOIN auth.tenant  AS t ON t.tenantId = @acting
          LEFT JOIN auth.appUser AS v ON v.appUserId = @viewAsUserId
         WHERE u.appUserId = @appUserId;

        SELECT permissionCode FROM auth.sessionScopePermission WHERE sessionScopeId = @sessionScopeId ORDER BY permissionCode;

        SELECT t.tenantId, t.tenantName, t.tenantType, st.accessLevel
          FROM auth.sessionScopeTenant AS st
          JOIN auth.tenant             AS t ON t.tenantId = st.tenantId
         WHERE st.sessionScopeId = @sessionScopeId
         ORDER BY t.tenantName;

        SET @Comments = CONCAT (N'Session scope created: ', @profileName, N'.');

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
      @SchemaName  = N'auth'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspCreateSessionScope'
    , @Description = N'Resolves what a session may see and do: from the master profile, from one of the user''s own profiles clipped to the master, or (view as) from another user''s master profile clipped to the tenants the caller administers and to the caller''s own rights. Stores the tenants with an access level each, the data roots, the administration scope and the permissions; the key goes into the session cookie and into SESSION_CONTEXT on every connection.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON auth.uspCreateSessionScope TO meritWebRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON auth.uspCreateSessionScope TO meritWorkerRole;
END;
GO
