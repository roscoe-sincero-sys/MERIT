SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO

/*
    logs.auditEvent
    Tamper-evident audit log of every view, change, merge and export of a person, result or case, and of
    sign-in events. An APPEND-ONLY LEDGER table: rows can be inserted and never updated or deleted, and
    SQL Server keeps a cryptographic digest chain over them.

    Ledger history cannot be deleted, so this table holds only IDs, actions, users, counts and times. Names,
    dates of birth, addresses, result values and search text stay out of it; search text goes to the purgeable
    logs.searchLog instead.

    Hand-written rather than generated, because it departs from the table template in two documented ways:

      - NO AFTER UPDATE AUDIT TRIGGER. An append-only ledger rejects UPDATE outright, so there is nothing for
        the trigger to maintain. The audit block columns are kept, with their defaults, so the table has the
        same shape as every other table; deleted* and modified* simply never change from their insert values.
      - isDeleted is always 0 for the same reason. A purge of audit history is not possible by design.

    The only writer is logs.uspRecordAuditEvent.
*/

IF OBJECT_ID (N'logs.auditEvent', N'U') IS NULL
BEGIN
    CREATE TABLE logs.auditEvent
    (
        auditEventId    BIGINT          IDENTITY (1, 1) NOT NULL,
        eventUtc        DATETIME2 (3)   NOT NULL CONSTRAINT DF_logs_auditEvent_eventUtc DEFAULT (SYSUTCDATETIME ()),
        appUserId       UNIQUEIDENTIFIER NULL,
        rootTenantId    UNIQUEIDENTIFIER NULL,
        eventAction     VARCHAR (20)    NOT NULL,
        entityType      VARCHAR (30)    NOT NULL,
        entityId        UNIQUEIDENTIFIER NULL,
        recordCount     INT             NULL,
        clientAddress   VARCHAR (45)    NULL,

        isDeleted       BIT             NOT NULL CONSTRAINT DF_logs_auditEvent_isDeleted       DEFAULT (0),
        deletedBy       NVARCHAR (255)  NOT NULL CONSTRAINT DF_logs_auditEvent_deletedBy       DEFAULT (ORIGINAL_LOGIN ()),
        deletedDateUtc  DATETIME2 (3)   NOT NULL CONSTRAINT DF_logs_auditEvent_deletedDateUtc  DEFAULT (SYSUTCDATETIME ()),
        createdBy       NVARCHAR (255)  NOT NULL CONSTRAINT DF_logs_auditEvent_createdBy       DEFAULT (COALESCE (CAST (SESSION_CONTEXT (N'AppUser') AS NVARCHAR (255)), ORIGINAL_LOGIN ())),
        createdDateUtc  DATETIME2 (3)   NOT NULL CONSTRAINT DF_logs_auditEvent_createdDateUtc  DEFAULT (SYSUTCDATETIME ()),
        modifiedBy      NVARCHAR (255)  NOT NULL CONSTRAINT DF_logs_auditEvent_modifiedBy      DEFAULT (COALESCE (CAST (SESSION_CONTEXT (N'AppUser') AS NVARCHAR (255)), ORIGINAL_LOGIN ())),
        modifiedDateUtc DATETIME2 (3)   NOT NULL CONSTRAINT DF_logs_auditEvent_modifiedDateUtc DEFAULT (SYSUTCDATETIME ()),

        CONSTRAINT PK_logs_auditEvent PRIMARY KEY CLUSTERED (auditEventId),
        CONSTRAINT CK_logs_auditEvent_eventAction
            CHECK (eventAction IN ('view', 'create', 'update', 'delete', 'merge', 'export', 'search', 'signIn',
                                   'signInFailed', 'signOut', 'mfaEnrolled', 'permissionDenied'))
    )
    WITH (LEDGER = ON (APPEND_ONLY = ON));
END;
GO

IF NOT EXISTS (SELECT 1
                 FROM sys.indexes
                WHERE name      = N'IX_logs_auditEvent_entityType_entityId'
                  AND object_id = OBJECT_ID (N'logs.auditEvent'))
BEGIN
    CREATE INDEX IX_logs_auditEvent_entityType_entityId
        ON logs.auditEvent (entityType, entityId);
END;
GO

IF NOT EXISTS (SELECT 1
                 FROM sys.indexes
                WHERE name      = N'IX_logs_auditEvent_appUserId_eventUtc'
                  AND object_id = OBJECT_ID (N'logs.auditEvent'))
BEGIN
    CREATE INDEX IX_logs_auditEvent_appUserId_eventUtc
        ON logs.auditEvent (appUserId, eventUtc);
END;
GO

EXEC util.uspSetObjectDescription @SchemaName = N'logs', @ObjectType = N'TABLE', @ObjectName = N'auditEvent', @Description = N'Append-only ledger of every view, change, merge and export of a person, result or case, and of sign-in events. Holds IDs, actions, users, counts and times only; never names, dates of birth, addresses, result values or search text.';
EXEC util.uspSetObjectDescription @SchemaName = N'logs', @ObjectType = N'TABLE', @ObjectName = N'auditEvent', @ColumnName = N'auditEventId', @Description = N'Surrogate key, ascending in insert order.';
EXEC util.uspSetObjectDescription @SchemaName = N'logs', @ObjectType = N'TABLE', @ObjectName = N'auditEvent', @ColumnName = N'eventUtc', @Description = N'UTC time of the event.';
EXEC util.uspSetObjectDescription @SchemaName = N'logs', @ObjectType = N'TABLE', @ObjectName = N'auditEvent', @ColumnName = N'appUserId', @Description = N'User who acted; NULL for a failed sign-in with an unknown user name, or a named system account.';
EXEC util.uspSetObjectDescription @SchemaName = N'logs', @ObjectType = N'TABLE', @ObjectName = N'auditEvent', @ColumnName = N'rootTenantId', @Description = N'Root tenant the event belongs to.';
EXEC util.uspSetObjectDescription @SchemaName = N'logs', @ObjectType = N'TABLE', @ObjectName = N'auditEvent', @ColumnName = N'eventAction', @Description = N'What happened: view, create, update, delete (soft), merge, export, search, signIn, signInFailed, signOut, mfaEnrolled or permissionDenied.';
EXEC util.uspSetObjectDescription @SchemaName = N'logs', @ObjectType = N'TABLE', @ObjectName = N'auditEvent', @ColumnName = N'entityType', @Description = N'Kind of record acted on, e.g. person, labResult, exposureCase, export.';
EXEC util.uspSetObjectDescription @SchemaName = N'logs', @ObjectType = N'TABLE', @ObjectName = N'auditEvent', @ColumnName = N'entityId', @Description = N'Id of the record acted on.';
EXEC util.uspSetObjectDescription @SchemaName = N'logs', @ObjectType = N'TABLE', @ObjectName = N'auditEvent', @ColumnName = N'recordCount', @Description = N'Rows returned or exported, for list views and exports.';
EXEC util.uspSetObjectDescription @SchemaName = N'logs', @ObjectType = N'TABLE', @ObjectName = N'auditEvent', @ColumnName = N'clientAddress', @Description = N'IP address of the client, for sign-in events.';
EXEC util.uspSetObjectDescription @SchemaName = N'logs', @ObjectType = N'TABLE', @ObjectName = N'auditEvent', @ColumnName = N'isDeleted', @Description = N'Soft-delete flag; always 0. An append-only ledger cannot be updated, so audit history is never deleted.';
EXEC util.uspSetObjectDescription @SchemaName = N'logs', @ObjectType = N'TABLE', @ObjectName = N'auditEvent', @ColumnName = N'deletedBy', @Description = N'Insert-time default only; never changes, since the ledger rejects UPDATE.';
EXEC util.uspSetObjectDescription @SchemaName = N'logs', @ObjectType = N'TABLE', @ObjectName = N'auditEvent', @ColumnName = N'deletedDateUtc', @Description = N'Insert-time default only; never changes, since the ledger rejects UPDATE.';
EXEC util.uspSetObjectDescription @SchemaName = N'logs', @ObjectType = N'TABLE', @ObjectName = N'auditEvent', @ColumnName = N'createdBy', @Description = N'Application user (SESSION_CONTEXT AppUser) or login that wrote the event.';
EXEC util.uspSetObjectDescription @SchemaName = N'logs', @ObjectType = N'TABLE', @ObjectName = N'auditEvent', @ColumnName = N'createdDateUtc', @Description = N'UTC timestamp of row insert.';
EXEC util.uspSetObjectDescription @SchemaName = N'logs', @ObjectType = N'TABLE', @ObjectName = N'auditEvent', @ColumnName = N'modifiedBy', @Description = N'Equal to createdBy; the ledger rejects UPDATE.';
EXEC util.uspSetObjectDescription @SchemaName = N'logs', @ObjectType = N'TABLE', @ObjectName = N'auditEvent', @ColumnName = N'modifiedDateUtc', @Description = N'Equal to createdDateUtc; the ledger rejects UPDATE.';
GO
