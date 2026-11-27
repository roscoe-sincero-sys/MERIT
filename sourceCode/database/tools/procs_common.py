"""Shared fragments for procedure definitions."""

# A new application-style key generated inside a procedure: random bytes 0-9, milliseconds since 1970 big-endian in
# bytes 10-15, which is where SQL Server sorts a UNIQUEIDENTIFIER first. Same layout as Merit.Domain.Ids.TimeOrderedId.
# NEWID () is evaluated per row, so this is safe in a set-based INSERT ... SELECT.
NEWID = ("CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) "
         "AS BINARY (6)) AS UNIQUEIDENTIFIER)")

WEB = ["meritWebRole"]
WORKER = ["meritWorkerRole"]

# The queuing session's profile, view-as user and acting tenant, stored on every job so the worker runs it under the
# same security as the request that started it (issue #7).
JOB_SCOPE = """DECLARE @jobProfile UNIQUEIDENTIFIER, @jobViewAs UNIQUEIDENTIFIER, @jobActing UNIQUEIDENTIFIER;
SELECT @jobProfile = userProfileId, @jobViewAs = viewAsUserId, @jobActing = actingTenantId
  FROM auth.sessionScope
 WHERE sessionScopeId = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER) AND isDeleted = 0;"""
JOB_SCOPE_COLUMNS = "requestedProfileId, requestedViewAsUserId, requestedActingTenantId"
JOB_SCOPE_VALUES = "@jobProfile, @jobViewAs, @jobActing"

# Session and request correlation context for logs.auditEvent and logs.searchLog (#326, #327).
AUDIT_SESSION_VARS = """DECLARE @auditScope UNIQUEIDENTIFIER = TRY_CAST (SESSION_CONTEXT (N'scopeKey') AS UNIQUEIDENTIFIER);
DECLARE @auditSessionRef UNIQUEIDENTIFIER, @auditActingTenant UNIQUEIDENTIFIER, @auditViewAs UNIQUEIDENTIFIER;
SELECT @auditSessionRef = sessionReference, @auditActingTenant = actingTenantId, @auditViewAs = viewAsUserId
  FROM auth.sessionScope
 WHERE sessionScopeId = @auditScope AND revokedUtc IS NULL AND expiresUtc > SYSUTCDATETIME () AND isDeleted = 0;
DECLARE @auditClientAddress VARCHAR (45) = TRY_CAST (SESSION_CONTEXT (N'clientAddress') AS VARCHAR (45));
DECLARE @auditTraceId VARCHAR (64) = TRY_CAST (SESSION_CONTEXT (N'traceId') AS VARCHAR (64));
DECLARE @auditUserAgentHash CHAR (16) = TRY_CAST (SESSION_CONTEXT (N'userAgentHash') AS CHAR (16));"""
BOTH = ["meritWebRole", "meritWorkerRole"]

# Rejects a missing or stale row version on an UPDATE. Use after the UPDATE: @@ROWCOUNT = 0 means the row changed or is
# not visible.
CONCURRENCY = """IF @@ROWCOUNT = 0
BEGIN
    ;THROW 51409, N'This record was changed by someone else, or is no longer available. Reload it and try again.', 1;
END;"""


def not_found(var: str, table: str, key: str, what: str) -> str:
    return f"""IF NOT EXISTS (SELECT 1 FROM {table} WHERE {key} = {var} AND isDeleted = 0)
BEGIN
    ;THROW 51404, N'{what} was not found, or you do not have access to it.', 1;
END;"""
