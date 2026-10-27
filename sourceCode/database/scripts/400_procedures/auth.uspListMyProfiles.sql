-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   auth.uspListMyProfiles
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

The signed-in user's profiles (the master first) and each profile's grants: the master's are the user's current assignments, the others are the grants the user chose.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec auth.uspListMyProfiles ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T160
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE auth.uspListMyProfiles

AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[auth].[uspListMyProfiles]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = NULL;

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @me UNIQUEIDENTIFIER = auth.udfSessionUserId ();

        SELECT p.userProfileId, p.profileName, p.profileDescription, p.isMaster, p.isActive
          FROM auth.userProfile AS p
         WHERE p.appUserId = @me AND p.isDeleted = 0
         ORDER BY p.isMaster DESC, p.isActive DESC, p.profileName;

        SELECT p.userProfileId, ua.roleId, r.roleName, ua.tenantId, t.tenantName, ua.includeDescendants, ua.accessLevel
          FROM auth.userProfile    AS p
          JOIN auth.userAssignment AS ua ON ua.appUserId = p.appUserId AND ua.isDeleted = 0
                                        AND ua.startDate <= auth.udfSessionToday ()
                                        AND (ua.endDate IS NULL OR ua.endDate >= auth.udfSessionToday ())
          JOIN auth.role   AS r ON r.roleId = ua.roleId
          JOIN auth.tenant AS t ON t.tenantId = ua.tenantId
         WHERE p.appUserId = @me AND p.isMaster = 1 AND p.isDeleted = 0
        UNION ALL
        SELECT g.userProfileId, g.roleId, r.roleName, g.tenantId, t.tenantName, g.includeDescendants, g.accessLevel
          FROM auth.userProfile      AS p
          JOIN auth.userProfileGrant AS g ON g.userProfileId = p.userProfileId AND g.isDeleted = 0
          JOIN auth.role   AS r ON r.roleId = g.roleId
          JOIN auth.tenant AS t ON t.tenantId = g.tenantId
         WHERE p.appUserId = @me AND p.isMaster = 0 AND p.isDeleted = 0;

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
      @SchemaName  = N'auth'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspListMyProfiles'
    , @Description = N'The signed-in user''s profiles (the master first) and each profile''s grants: the master''s are the user''s current assignments, the others are the grants the user chose.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON auth.uspListMyProfiles TO meritWebRole;
END;
GO
