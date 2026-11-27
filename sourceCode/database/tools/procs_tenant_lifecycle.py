"""Per-root-tenant export and purge procedures (#346).

Covers every table carrying rootTenantId (enforced by test_generateProcedures.py inventory test).
Public entrypoints run EXECUTE AS CALLER with platform-only permissions (security.investigate AND tenant.create)
and delegate cross-RLS data work to ungranted EXECUTE AS OWNER internal helpers (same pattern as
logs.uspDdlAuditVerifyInternal).
"""
from procs_common import AUDIT_SESSION_VARS, BOTH

# Every mutable table carrying rootTenantId, in dependency-safe purge order (children before parents).
# Tuple: (schema, table, extra_scrub_set_clause)
# Kept in exact sync with tableDefinitions.py by test_root_tenant_lifecycle_inventory in test_generateProcedures.py.
MUTABLE_ROOT_TABLES: list[tuple[str, str, str]] = [
    # Logs (purgeable / scrubbable)
    ("logs", "auditEventChange", "beforeJson = NULL, afterJson = NULL"),
    ("logs", "auditEventRecord", "entityId = '00000000-0000-0000-0000-000000000000', owningTenantId = NULL"),
    ("logs", "securityFinding", "clientAddress = NULL, sessionReference = NULL, detailJson = NULL"),
    ("logs", "searchLog", "searchText = N'', clientAddress = NULL, sessionReference = NULL, actingTenantId = NULL"),
    ("logs", "screenVisit", "appUserId = NULL, sessionReference = NULL, profileId = NULL, viewAsUserId = NULL, actingTenantId = NULL, roleCodes = NULL, tabId = NULL"),
    ("logs", "signInEvent", "clientAddress = NULL, forwardedFor = NULL, userAgent = NULL, attemptedNameHash = NULL, attemptedNameProtected = NULL, sessionReference = NULL, traceId = NULL"),
    # Clinical / workflow children
    ("dbo", "contactCorrection", ""),
    ("dbo", "caseResultLink", ""),
    ("dbo", "notificationObligation", ""),
    ("dbo", "personDocument", "title = N'[purged]', fileName = N'purged', content = 0x00, contentHash = CAST (0x00 AS BINARY (32)), description = NULL"),
    ("dbo", "personActivity", "note = N'[purged]', recipientLabel = NULL"),
    ("dbo", "matchExclusion", "incomingKey = N'[purged]', decisionNote = NULL"),
    ("dbo", "extractRun", ""),
    ("dbo", "jobQueue", "payloadJson = N'{}'"),
    ("dbo", "workTask", "title = N'[purged]', detail = NULL"),
    ("dbo", "caseCluster", ""),
    ("dbo", "cluster", "notes = NULL"),
    ("dbo", "environmentalSample", "notes = NULL"),
    ("dbo", "investigationCase", ""),
    ("dbo", "investigation", "findings = NULL"),
    ("dbo", "exposureSource", "sourceDescription = NULL"),
    ("dbo", "caseActivity", "note = NULL"),
    ("dbo", "exposureCase", ""),
    ("dbo", "stagingHoldReason", ""),
    ("dbo", "stagingRow", "parsedJson = N'{}', correctedJson = NULL"),
    ("dbo", "labResult", ""),
    ("dbo", "intakeBatch", ""),
    ("dbo", "intakeProfile", ""),
    ("dbo", "provider", ""),
    ("dbo", "product", ""),
    ("dbo", "employmentAnalyte", ""),
    ("dbo", "employment", "jobTitle = NULL, metalTasks = NULL, ppeNotes = NULL"),
    ("dbo", "personLocation", ""),
    ("dbo", "locationOwner", ""),
    ("dbo", "organizationSite", ""),
    ("dbo", "location", "addressLine1 = N'[purged]', unit = NULL"),
    ("dbo", "organizationType", ""),
    ("dbo", "organization", ""),
    ("dbo", "personRelationship", ""),
    ("dbo", "pregnancyEpisode", ""),
    ("dbo", "personIdentifier", "identifierValue = N'[purged]'"),
    ("dbo", "personContactMethod", "contactValue = N'[purged]'"),
    ("dbo", "personRace", ""),
    ("dbo", "personAlias", "lastName = N'[purged]', firstName = NULL, middleName = NULL, lastNameKey = N'PURGED', firstNameKey = NULL"),
    ("dbo", "personMatchKey", "lastNameKey = N'PURGED', firstNameKey = NULL"),
    ("dbo", "personAccess", ""),
    ("dbo", "person", "lastName = N'[purged]', firstName = NULL, middleName = NULL, lastNameKey = N'PURGED', firstNameKey = NULL, dob = NULL"),
    # Configuration
    ("config", "workflowDefinition", ""),
    ("config", "notificationRule", ""),
    ("config", "environmentalStandard", ""),
    ("config", "threshold", ""),
    ("config", "jurisdictionSetting", ""),
    ("config", "subJurisdiction", ""),
    # Auth / tenant hierarchy (sessions -> users -> roles -> tenants)
    ("auth", "sessionScopeRoot", ""),
    ("auth", "sessionScope", "revokedUtc = COALESCE (revokedUtc, SYSUTCDATETIME ())"),
    ("auth", "appUser", "isActive = 0, email = NULL"),
    ("auth", "role", ""),
    ("auth", "tenant", "isActive = 0"),
]

# Append-only tables that cannot be deleted/updated (export-only, explicitly reported as retained).
APPEND_ONLY_ROOT_TABLES: list[tuple[str, str]] = [
    ("logs", "auditEvent"),
]

ALL_ROOT_TABLES: set[str] = {f"{s}.{n}" for s, n, _ in MUTABLE_ROOT_TABLES} | {f"{s}.{n}" for s, n in APPEND_ONLY_ROOT_TABLES}


def _export_body() -> str:
    lines = [
        "DECLARE @manifest TABLE (",
        "    tableName    NVARCHAR (128) NOT NULL,",
        "    exportedRows INT NOT NULL,",
        "    disposition  VARCHAR (30) NOT NULL,",
        "    payloadJson  NVARCHAR (MAX) NULL",
        ");",
        "",
    ]
    for s, n, _ in MUTABLE_ROOT_TABLES:
        full = f"{s}.{n}"
        lines.append(
            f"INSERT @manifest (tableName, exportedRows, disposition, payloadJson)\n"
            f"SELECT N'{full}', COUNT (*), 'exported',\n"
            f"       CASE WHEN @includePayloads = 1 AND COUNT (*) > 0\n"
            f"            THEN (SELECT * FROM {full} WHERE rootTenantId = @rootTenantId AND isDeleted = 0 FOR JSON PATH)\n"
            f"            ELSE NULL END\n"
            f"  FROM {full} WHERE rootTenantId = @rootTenantId AND isDeleted = 0;"
        )
    for s, n in APPEND_ONLY_ROOT_TABLES:
        full = f"{s}.{n}"
        lines.append(
            f"INSERT @manifest (tableName, exportedRows, disposition, payloadJson)\n"
            f"SELECT N'{full}', COUNT (*), 'ledgerReadOnly',\n"
            f"       CASE WHEN @includePayloads = 1 AND COUNT (*) > 0\n"
            f"            THEN (SELECT * FROM {full} WHERE rootTenantId = @rootTenantId FOR JSON PATH)\n"
            f"            ELSE NULL END\n"
            f"  FROM {full} WHERE rootTenantId = @rootTenantId;"
        )
    lines.append("")
    lines.append("SELECT tableName, exportedRows, disposition, payloadJson FROM @manifest ORDER BY tableName;")
    return "\n".join(lines)


def _purge_body() -> str:
    lines = [
        "DECLARE @report TABLE (",
        "    tableName    NVARCHAR (128) NOT NULL,",
        "    rowsAffected INT NOT NULL,",
        "    disposition  VARCHAR (40) NOT NULL",
        ");",
        "DECLARE @n INT = 0;",
        "",
    ]
    for s, n, scrub in MUTABLE_ROOT_TABLES:
        full = f"{s}.{n}"
        set_clause = f"{scrub}, isDeleted = 1, modifiedBy = N'MERIT tenant purge'" if scrub else "isDeleted = 1, modifiedBy = N'MERIT tenant purge'"
        lines.append(
            f"UPDATE {full}\n"
            f"   SET {set_clause}\n"
            f" WHERE rootTenantId = @rootTenantId AND isDeleted = 0;\n"
            f"SET @n = @@ROWCOUNT;\n"
            f"INSERT @report (tableName, rowsAffected, disposition) VALUES (N'{full}', @n, 'scrubbedAndSoftDeleted');"
        )
    for s, n in APPEND_ONLY_ROOT_TABLES:
        full = f"{s}.{n}"
        lines.append(
            f"DECLARE @ledgerRows INT = (SELECT COUNT (*) FROM {full} WHERE rootTenantId = @rootTenantId);\n"
            f"INSERT @report (tableName, rowsAffected, disposition) VALUES (N'{full}', @ledgerRows, 'retainedAppendOnlyLedger');"
        )
    lines.append("")
    lines.append("SELECT tableName, rowsAffected, disposition FROM @report ORDER BY tableName;")
    lines.append(
        "SELECT @rootTenantId AS rootTenantId,\n"
        "       (SELECT tenantCode FROM auth.tenant WHERE tenantId = @rootTenantId) AS tenantCode,\n"
        "       (SELECT SUM (rowsAffected) FROM @report WHERE disposition = 'scrubbedAndSoftDeleted') AS totalRowsPurged,\n"
        "       @ledgerRows AS ledgerRowsRetained,\n"
        "       DATEADD (DAY, @backupRetentionDays, SYSUTCDATETIME ()) AS backupAgeOutUtc;"
    )
    lines.append("SET @Comments = CONCAT (N'Purged rootTenantId=', @rootTenantId);")
    return "\n".join(lines)


PROCS = [
    {
        "schema": "config", "name": "uspExportRootTenantInternal", "kind": "read", "grants": [], "execute_as": "OWNER",
        "ticket": "346",
        "desc": "Ungranted owner-context helper for config.uspExportRootTenant: reads all rootTenantId tables across RLS (#346).",
        "params": """@rootTenantId    UNIQUEIDENTIFIER
, @includePayloads BIT = 0""",
        "key": "CONCAT (N'rootTenantId=', @rootTenantId, N', includePayloads=', @includePayloads)",
        "body": _export_body(),
    },
    {
        "schema": "config", "name": "uspExportRootTenant", "kind": "write", "grants": BOTH, "ticket": "346",
        "permission_all": ["security.investigate", "tenant.create"],
        "desc": "Platform-only, audited per-root-tenant data export covering every table with rootTenantId plus read-only "
                "logs.auditEvent rows (#346). Returns a manifest of row counts per table and optional JSON payloads.",
        "params": """@rootTenantId    UNIQUEIDENTIFIER
, @includePayloads BIT = 0""",
        "key": "CONCAT (N'rootTenantId=', @rootTenantId, N', includePayloads=', @includePayloads)",
        "validate": """IF NOT EXISTS (SELECT 1 FROM auth.tenant WHERE tenantId = @rootTenantId AND tenantId = rootTenantId)
BEGIN
    ;THROW 51404, N'The root tenant was not found.', 1;
END;""",
        "body": f"""
{AUDIT_SESSION_VARS}
INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId, clientAddress, owningTenantId,
                        sessionReference, traceId, actingTenantId, viewAsUserId, userAgentHash)
VALUES (auth.udfSessionUserId (), @rootTenantId, 'export', 'tenant', @rootTenantId, @auditClientAddress, @rootTenantId,
        @auditSessionRef, @auditTraceId, @auditActingTenant, @auditViewAs, @auditUserAgentHash);

EXEC config.uspExportRootTenantInternal @rootTenantId = @rootTenantId, @includePayloads = @includePayloads;
SET @Comments = CONCAT (N'Root tenant export completed for ', @rootTenantId);
""",
    },
    {
        "schema": "config", "name": "uspPurgeRootTenantInternal", "kind": "write", "grants": [], "execute_as": "OWNER",
        "ticket": "346",
        "desc": "Ungranted owner-context helper for config.uspPurgeRootTenant: scrubs and soft-deletes all mutable "
                "rootTenantId tables across RLS while leaving append-only logs.auditEvent intact (#346).",
        "params": """@rootTenantId        UNIQUEIDENTIFIER
, @backupRetentionDays INT = 35""",
        "key": "CONCAT (N'rootTenantId=', @rootTenantId, N', backupRetentionDays=', @backupRetentionDays)",
        "body": _purge_body(),
    },
    {
        "schema": "config", "name": "uspPurgeRootTenant", "kind": "write", "grants": BOTH, "ticket": "346",
        "permission_all": ["security.investigate", "tenant.create"],
        "desc": "Platform-only, two-step confirmed, audited per-root-tenant purge (#346). Requires @confirmationCode = "
                "N'PURGE:' + tenantCode and refuses the platform root. Scrubs and soft-deletes every mutable table "
                "carrying rootTenantId, leaves append-only logs.auditEvent intact (IDs only), and returns a purge report "
                "including when data ages out of backups.",
        "params": """@rootTenantId        UNIQUEIDENTIFIER
, @confirmationCode    NVARCHAR (60)
, @backupRetentionDays INT = 35""",
        "key": "CONCAT (N'rootTenantId=', @rootTenantId, N', backupRetentionDays=', @backupRetentionDays)",
        "validate": """DECLARE @targetCode VARCHAR (30), @targetType VARCHAR (30);
SELECT @targetCode = tenantCode, @targetType = tenantType
  FROM auth.tenant
 WHERE tenantId = @rootTenantId AND tenantId = rootTenantId;

IF @targetCode IS NULL
BEGIN
    ;THROW 51404, N'The root tenant was not found.', 1;
END;
IF @targetType = 'platform'
BEGIN
    ;THROW 51422, N'The platform root tenant cannot be purged.', 1;
END;
IF COALESCE (@confirmationCode, N'') <> CONCAT (N'PURGE:', @targetCode)
BEGIN
    ;THROW 51422, N'Confirmation code mismatch. Pass PURGE:<tenantCode> to confirm root tenant purge.', 1;
END;
IF @backupRetentionDays NOT BETWEEN 1 AND 3650
BEGIN
    ;THROW 51422, N'@backupRetentionDays must be between 1 and 3650.', 1;
END;""",
        "body": f"""
{AUDIT_SESSION_VARS}
INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId, clientAddress, owningTenantId,
                        sessionReference, traceId, actingTenantId, viewAsUserId, userAgentHash)
VALUES (auth.udfSessionUserId (), @rootTenantId, 'delete', 'tenant', @rootTenantId, @auditClientAddress, @rootTenantId,
        @auditSessionRef, @auditTraceId, @auditActingTenant, @auditViewAs, @auditUserAgentHash);

EXEC config.uspPurgeRootTenantInternal @rootTenantId = @rootTenantId, @backupRetentionDays = @backupRetentionDays;
SET @Comments = CONCAT (N'Root tenant purge completed for ', @rootTenantId);
""",
    },
]
