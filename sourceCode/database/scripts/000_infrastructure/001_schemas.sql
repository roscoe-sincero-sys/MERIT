SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO

/*
    MERIT schemas. dbo holds user data, auth authentication and authorization, config jurisdiction
    configuration and shipped reference data, logs logging and the audit ledger, util utility objects.
    The change-logging installer (020) adds logsData and history.

    CREATE SCHEMA must be alone in its batch, hence the EXEC. Re-runnable: each is created only when missing.
*/
IF SCHEMA_ID (N'auth')   IS NULL EXEC (N'CREATE SCHEMA [auth] AUTHORIZATION [dbo]');
IF SCHEMA_ID (N'config') IS NULL EXEC (N'CREATE SCHEMA [config] AUTHORIZATION [dbo]');
IF SCHEMA_ID (N'logs')   IS NULL EXEC (N'CREATE SCHEMA [logs] AUTHORIZATION [dbo]');
IF SCHEMA_ID (N'util')   IS NULL EXEC (N'CREATE SCHEMA [util] AUTHORIZATION [dbo]');
GO

/*
    Sequences for human-readable numbers (case PB-2026-000123, investigation INV-2026-000045).
    Guarded, so a second run leaves the current value alone.
*/
IF NOT EXISTS (SELECT 1 FROM sys.sequences WHERE name = N'caseNumberSequence' AND SCHEMA_NAME (schema_id) = N'dbo')
BEGIN
    EXEC (N'CREATE SEQUENCE dbo.caseNumberSequence AS INT START WITH 1 INCREMENT BY 1 NO CYCLE CACHE 20;');
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.sequences WHERE name = N'investigationNumberSequence' AND SCHEMA_NAME (schema_id) = N'dbo')
BEGIN
    EXEC (N'CREATE SEQUENCE dbo.investigationNumberSequence AS INT START WITH 1 INCREMENT BY 1 NO CYCLE CACHE 20;');
END;
GO
