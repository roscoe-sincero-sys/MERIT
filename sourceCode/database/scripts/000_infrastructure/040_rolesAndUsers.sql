SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO

/*
    MERIT database roles and the users for the two application logins.

    The specification requires that the application's database login hold EXECUTE on procedures only,
    with no direct table access, and that the worker use its own login with EXECUTE only on the procedures
    its jobs call. So:

        meritWebRole     EXECUTE, granted per procedure in each procedure's own script. Nothing else.
        meritWorkerRole  EXECUTE, granted per procedure, only on what background jobs call.
        logsAuditReader  SELECT on the DDL change log (granted by 020_logDbChanges.sql), for auditors.

    The skill's applicationRole and readOnlyRole are deliberately NOT created: 050_permissions.sql grants
    them SELECT, INSERT and UPDATE on SCHEMA::dbo, which would hand table access to the application logins.
    With the roles absent that script skips those grants and still applies its DENYs on the data schemas.

    Logins are server objects and are created by Deploy-MeritDatabase.ps1 (-CreateLogins). This script
    maps a database user to each login that exists and adds it to its role. sqlcmd variables:
        WebLogin     login name of the web application, default meritWeb
        WorkerLogin  login name of the worker, default meritWorker
*/

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NULL
BEGIN
    CREATE ROLE meritWebRole AUTHORIZATION dbo;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NULL
BEGIN
    CREATE ROLE meritWorkerRole AUTHORIZATION dbo;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'logsAuditReader') IS NULL
BEGIN
    CREATE ROLE logsAuditReader AUTHORIZATION dbo;
END;
GO

-- Web application user.
IF SUSER_ID (N'$(WebLogin)') IS NOT NULL AND DATABASE_PRINCIPAL_ID (N'$(WebLogin)') IS NULL
BEGIN
    EXEC (N'CREATE USER [$(WebLogin)] FOR LOGIN [$(WebLogin)] WITH DEFAULT_SCHEMA = [dbo];');
END;
GO

IF DATABASE_PRINCIPAL_ID (N'$(WebLogin)') IS NOT NULL
   AND IS_ROLEMEMBER (N'meritWebRole', N'$(WebLogin)') = 0
BEGIN
    ALTER ROLE meritWebRole ADD MEMBER [$(WebLogin)];
END;
GO

-- Worker user.
IF SUSER_ID (N'$(WorkerLogin)') IS NOT NULL AND DATABASE_PRINCIPAL_ID (N'$(WorkerLogin)') IS NULL
BEGIN
    EXEC (N'CREATE USER [$(WorkerLogin)] FOR LOGIN [$(WorkerLogin)] WITH DEFAULT_SCHEMA = [dbo];');
END;
GO

IF DATABASE_PRINCIPAL_ID (N'$(WorkerLogin)') IS NOT NULL
   AND IS_ROLEMEMBER (N'meritWorkerRole', N'$(WorkerLogin)') = 0
BEGIN
    ALTER ROLE meritWorkerRole ADD MEMBER [$(WorkerLogin)];
END;
GO

-- The sequences are read by procedures through ownership chaining; nothing is granted on them.
