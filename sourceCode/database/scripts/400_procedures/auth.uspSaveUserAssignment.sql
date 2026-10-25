-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   auth.uspSaveUserAssignment
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Adds, changes or ends a grant in a user's master profile. Only at tenants the administrator administers; never in their own master profile; a role from the tenant's part of the tree (platform roles only at the platform, for platform accounts); and, except for a platform administrator, never a permission or access level the administrator does not hold at that tenant. The user's sessions end.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission tenant.administer (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec auth.uspSaveUserAssignment ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T160
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE auth.uspSaveUserAssignment
      @userAssignmentId   UNIQUEIDENTIFIER
      , @appUserId          UNIQUEIDENTIFIER
      , @roleId             UNIQUEIDENTIFIER
      , @tenantId           UNIQUEIDENTIFIER
      , @includeDescendants BIT = 1
      , @accessLevel        VARCHAR (10) = 'full'
      , @startDate          DATE
      , @endDate            DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[auth].[uspSaveUserAssignment]')
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
    SET @KeyParameters = CONCAT (N'userAssignmentId=', @userAssignmentId, N', appUserId=', @appUserId, N', tenantId=', @tenantId);

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        IF auth.udfHasPermission (N'tenant.administer') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (tenant.administer).', 1;
        END;

        IF @accessLevel IS NULL OR @accessLevel NOT IN ('read', 'write', 'full')
        BEGIN
            ;THROW 50000, N'@accessLevel must be read, write or full.', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @admin TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @admin SELECT tenantId FROM auth.tvfAdminTenants ();
        DECLARE @platform UNIQUEIDENTIFIER = CAST (HASHBYTES ('MD5', N'tenant:PLATFORM') AS UNIQUEIDENTIFIER);
        DECLARE @me UNIQUEIDENTIFIER = auth.udfSessionUserId ();
        DECLARE @rank INT = CASE @accessLevel WHEN 'full' THEN 3 WHEN 'write' THEN 2 ELSE 1 END;

        IF USER_NAME () <> N'dbo' AND @appUserId = @me
        BEGIN
            ;THROW 51403, N'Nobody can change their own master profile. Ask another administrator.', 1;
        END;

        IF EXISTS (SELECT 1 FROM auth.userAssignment WHERE userAssignmentId = @userAssignmentId)
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM auth.userAssignment AS ua JOIN @admin AS a ON a.tenantId = ua.tenantId
                            WHERE ua.userAssignmentId = @userAssignmentId AND ua.appUserId = @appUserId AND ua.isDeleted = 0)
            BEGIN
                ;THROW 51403, N'That grant is at a tenant you do not administer.', 1;
            END;

            UPDATE auth.userAssignment
               SET endDate = @endDate
             WHERE userAssignmentId = @userAssignmentId AND isDeleted = 0;
        END
        ELSE
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM @admin WHERE tenantId = @tenantId)
            BEGIN
                ;THROW 51403, N'You can grant roles only at tenants you administer.', 1;
            END;

            DECLARE @roleRoot UNIQUEIDENTIFIER = (SELECT rootTenantId FROM auth.role WHERE roleId = @roleId AND isDeleted = 0);
            DECLARE @targetIsPlatform BIT = (SELECT CASE WHEN h.tenantType = 'platform' THEN 1 ELSE 0 END
                                               FROM auth.appUser AS u JOIN auth.tenant AS h ON h.tenantId = u.homeTenantId
                                              WHERE u.appUserId = @appUserId AND u.isDeleted = 0);

            IF @roleRoot IS NULL OR @targetIsPlatform IS NULL
            BEGIN
                ;THROW 51404, N'The user or the role was not found.', 1;
            END;

            DECLARE @roleIsPlatform BIT = IIF (@roleRoot = @platform, 1, 0);
            DECLARE @tenantIsPlatform BIT = IIF (@tenantId = @platform, 1, 0);
            IF @roleIsPlatform <> @tenantIsPlatform OR @roleIsPlatform <> @targetIsPlatform
            BEGIN
                ;THROW 51422, N'Platform accounts hold only platform roles, granted at the platform; everyone else holds record roles at a region, state, tribe or jurisdiction.', 1;
            END;

            IF @roleRoot <> @platform
               AND NOT EXISTS (SELECT 1 FROM auth.tenantClosure WHERE ancestorTenantId = @roleRoot AND descendantTenantId = @tenantId
                                                                  AND isDeleted = 0)
            BEGIN
                ;THROW 51422, N'That role belongs to another part of the tenant tree.', 1;
            END;

            -- No one grants what they do not hold, at that tenant and that access level; the platform administrator excepted.
            IF auth.udfIsPlatformAdmin () = 0
               AND EXISTS (SELECT 1
                             FROM auth.rolePermission AS rp
                             JOIN auth.permission     AS pm ON pm.permissionId = rp.permissionId AND pm.isDeleted = 0
                            WHERE rp.roleId = @roleId AND rp.isDeleted = 0
                              AND (@accessLevel <> 'read' OR pm.accessLevel = 'read')
                              AND NOT EXISTS (SELECT 1 FROM auth.tvfUserGrantPermissions (@me) AS m
                                               WHERE m.tenantId = @tenantId AND m.permissionCode = pm.permissionCode
                                                 AND m.grantRank >= @rank))
            BEGIN
                ;THROW 51403, N'That role, at that access level, includes rights you do not have at that tenant, so you cannot grant it.', 1;
            END;

            INSERT auth.userAssignment (userAssignmentId, appUserId, roleId, tenantId, includeDescendants, accessLevel, startDate,
                                        endDate)
            VALUES (@userAssignmentId, @appUserId, @roleId, @tenantId, @includeDescendants, @accessLevel, @startDate, @endDate);
        END;

        -- A grant change invalidates the user's resolved access: end their open sessions.
        UPDATE auth.sessionScope SET revokedUtc = SYSUTCDATETIME () WHERE appUserId = @appUserId AND revokedUtc IS NULL;

        SET @Comments = N'Grant saved; user sessions revoked.';

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
    , @ObjectName  = N'uspSaveUserAssignment'
    , @Description = N'Adds, changes or ends a grant in a user''s master profile. Only at tenants the administrator administers; never in their own master profile; a role from the tenant''s part of the tree (platform roles only at the platform, for platform accounts); and, except for a platform administrator, never a permission or access level the administrator does not hold at that tenant. The user''s sessions end.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON auth.uspSaveUserAssignment TO meritWebRole;
END;
GO
