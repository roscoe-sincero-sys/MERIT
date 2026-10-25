-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   auth.uspSaveMyProfile
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Creates, changes or deactivates one of the signed-in user's own profiles. Each grant must stay inside the master profile: every permission it gives, at every tenant it reaches, must be in the master there at the same or a higher access level. The master profile itself cannot be changed or deactivated here, and a platform administrator has no other profiles.

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

exec auth.uspSaveMyProfile ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T160
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE auth.uspSaveMyProfile
      @userProfileId      UNIQUEIDENTIFIER
      , @profileName        NVARCHAR (80)
      , @profileDescription NVARCHAR (300) = NULL
      , @isActive           BIT = 1
      , @grantsJson         NVARCHAR (MAX)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[auth].[uspSaveMyProfile]')
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
    SET @KeyParameters = CONCAT (N'userProfileId=', @userProfileId, N', isActive=', @isActive);

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        IF @profileName IS NULL OR LEN (LTRIM (RTRIM (@profileName))) = 0
        BEGIN
            ;THROW 51422, N'Give the profile a name.', 1;
        END;
        IF ISJSON (@grantsJson) = 0
        BEGIN
            ;THROW 50000, N'@grantsJson must be a JSON array of grants.', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @me UNIQUEIDENTIFIER = auth.udfSessionUserId ();
        DECLARE @msg NVARCHAR (2048);

        IF @me IS NULL
           OR EXISTS (SELECT 1 FROM auth.appUser AS u JOIN auth.tenant AS h ON h.tenantId = u.homeTenantId
                       WHERE u.appUserId = @me AND h.tenantType = 'platform')
        BEGIN
            ;THROW 51403, N'A platform administrator has only the master profile.', 1;
        END;

        IF EXISTS (SELECT 1 FROM auth.userProfile WHERE userProfileId = @userProfileId)
           AND NOT EXISTS (SELECT 1 FROM auth.userProfile WHERE userProfileId = @userProfileId AND appUserId = @me
                                                            AND isMaster = 0 AND isDeleted = 0)
        BEGIN
            ;THROW 51403, N'You can change only your own extra profiles. The master profile is managed by your administrator.', 1;
        END;

        DECLARE @g TABLE (tenantId UNIQUEIDENTIFIER, roleId UNIQUEIDENTIFIER, includeDescendants BIT, accessLevel VARCHAR (10));
        INSERT @g (tenantId, roleId, includeDescendants, accessLevel)
        SELECT tenantId, roleId, COALESCE (includeDescendants, 1), COALESCE (accessLevel, 'read')
          FROM OPENJSON (@grantsJson)
          WITH (tenantId UNIQUEIDENTIFIER '$.tenantId', roleId UNIQUEIDENTIFIER '$.roleId', includeDescendants BIT '$.includeDescendants',
                accessLevel VARCHAR (10) '$.accessLevel');

        IF @isActive = 1 AND NOT EXISTS (SELECT 1 FROM @g)
        BEGIN
            ;THROW 51422, N'Add at least one grant: a role, a tenant and an access level.', 1;
        END;

        IF EXISTS (SELECT 1 FROM @g WHERE tenantId IS NULL OR roleId IS NULL OR accessLevel NOT IN ('read', 'write', 'full'))
        BEGIN
            ;THROW 51422, N'Each grant needs a tenant, a role and an access level of read, write or full.', 1;
        END;

        -- Never more than the master: every (tenant, permission) the profile would reach must be in the master there.
        SET @msg = (SELECT TOP (1) CONCAT (N'This profile would give you more than your master profile: ', pm.permissionName,
                                           N' at ', t.tenantName, N' (', g.accessLevel, N').')
                      FROM @g AS g
                      JOIN auth.tenantClosure  AS tc ON tc.ancestorTenantId = g.tenantId
                                                    AND (g.includeDescendants = 1 OR tc.depth = 0) AND tc.isDeleted = 0
                      JOIN auth.tenant         AS t  ON t.tenantId = tc.descendantTenantId AND t.isActive = 1 AND t.isDeleted = 0
                      JOIN auth.rolePermission AS rp ON rp.roleId = g.roleId AND rp.isDeleted = 0
                      JOIN auth.permission     AS pm ON pm.permissionId = rp.permissionId AND pm.isDeleted = 0
                     WHERE (g.accessLevel <> 'read' OR pm.accessLevel = 'read')
                       AND NOT EXISTS (SELECT 1 FROM auth.tvfUserGrantPermissions (@me) AS m
                                        WHERE m.tenantId = tc.descendantTenantId AND m.permissionCode = pm.permissionCode
                                          AND m.grantRank >= CASE g.accessLevel WHEN 'full' THEN 3 WHEN 'write' THEN 2 ELSE 1 END)
                     ORDER BY t.tenantName, pm.permissionCode);
        IF @msg IS NOT NULL
        BEGIN
            ;THROW 51422, @msg, 1;
        END;

        IF EXISTS (SELECT 1 FROM auth.userProfile WHERE userProfileId = @userProfileId)
        BEGIN
            UPDATE auth.userProfile
               SET profileName = @profileName, profileDescription = @profileDescription, isActive = @isActive
             WHERE userProfileId = @userProfileId;
        END
        ELSE
        BEGIN
            INSERT auth.userProfile (userProfileId, appUserId, profileName, profileDescription, isMaster, isActive)
            VALUES (@userProfileId, @me, @profileName, @profileDescription, 0, @isActive);
        END;

        UPDATE auth.userProfileGrant SET isDeleted = 1 WHERE userProfileId = @userProfileId AND isDeleted = 0;

        INSERT auth.userProfileGrant (userProfileGrantId, userProfileId, roleId, tenantId, includeDescendants, accessLevel)
        SELECT CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), @userProfileId, roleId, tenantId, includeDescendants, accessLevel FROM @g;

        -- Sessions using the profile end, so a narrowed or deactivated profile takes effect at once.
        UPDATE auth.sessionScope SET revokedUtc = SYSUTCDATETIME ()
         WHERE userProfileId = @userProfileId AND revokedUtc IS NULL
           AND sessionScopeId <> COALESCE (TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER), NEWID ());

        SET @Comments = N'Profile saved.';

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
    , @ObjectName  = N'uspSaveMyProfile'
    , @Description = N'Creates, changes or deactivates one of the signed-in user''s own profiles. Each grant must stay inside the master profile: every permission it gives, at every tenant it reaches, must be in the master there at the same or a higher access level. The master profile itself cannot be changed or deactivated here, and a platform administrator has no other profiles.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON auth.uspSaveMyProfile TO meritWebRole;
END;
GO
