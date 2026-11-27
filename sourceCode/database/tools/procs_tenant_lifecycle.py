"""Per-root-tenant export and purge procedures (#346).

Driven directly from tableDefinitions.py column metadata so every nullable non-key column is set to NULL,
every non-nullable PHI/credential/free-text/binary column is scrubbed on BOTH live (isDeleted = 0) and
previously soft-deleted (isDeleted = 1) rows, and every table (58 rootTenantId tables + 12 tenant/user-linked
auth child tables + append-only logs.auditEvent) is enforced by test_generateProcedures.py.

Purge commits per 5,000-row batch (no_tx=True) so multi-tenant writes on other states are never blocked (I16).
Export excludes all credential columns, includes soft-deleted history, and pages individual tables natively
instead of buffering gigabytes into a single NVARCHAR(MAX) JSON blob (I17).
"""
import re
from procs_common import AUDIT_SESSION_VARS, BOTH
import tableDefinitions as td

# Global reference/lookup tables that do not belong to any root tenant.
GLOBAL_REFERENCE_TABLES: set[str] = {
    "auth.permission",
    "config.lookupValue",
    "config.analyte",
    "config.analyteSpecimen",
    "config.acceptedUnit",
    "config.loincCode",
    "config.actionCode",
    "config.extractLayout",
    "config.extractLayoutField",
    "logs.ledgerVerificationRun",
}

# Tables linked to a root tenant via appUserId, tenantId, roleId, or sessionScopeId (no direct rootTenantId column).
# Tuple: (schema, table, where_predicate_using_@rootTenantId)
CHILD_AUTH_TABLES: list[tuple[str, str, str]] = [
    ("auth", "mfaRecoveryCode", "appUserId IN (SELECT appUserId FROM auth.appUser WHERE rootTenantId = @rootTenantId)"),
    ("auth", "passwordReset", "appUserId IN (SELECT appUserId FROM auth.appUser WHERE rootTenantId = @rootTenantId)"),
    ("auth", "mfaEmailChallenge", "appUserId IN (SELECT appUserId FROM auth.appUser WHERE rootTenantId = @rootTenantId)"),
    ("auth", "userProfileGrant", "userProfileId IN (SELECT up.userProfileId FROM auth.userProfile AS up JOIN auth.appUser AS u ON u.appUserId = up.appUserId WHERE u.rootTenantId = @rootTenantId)"),
    ("auth", "userProfile", "appUserId IN (SELECT appUserId FROM auth.appUser WHERE rootTenantId = @rootTenantId)"),
    ("auth", "userAssignment", "appUserId IN (SELECT appUserId FROM auth.appUser WHERE rootTenantId = @rootTenantId) OR tenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId)"),
    ("auth", "sessionScopeTenant", "sessionScopeId IN (SELECT sessionScopeId FROM auth.sessionScope WHERE rootTenantId = @rootTenantId) OR tenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId)"),
    ("auth", "sessionScopePermission", "sessionScopeId IN (SELECT sessionScopeId FROM auth.sessionScope WHERE rootTenantId = @rootTenantId)"),
    ("auth", "sessionScopeAdminTenant", "sessionScopeId IN (SELECT sessionScopeId FROM auth.sessionScope WHERE rootTenantId = @rootTenantId) OR tenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId)"),
    ("auth", "rolePermission", "roleId IN (SELECT roleId FROM auth.role WHERE rootTenantId = @rootTenantId)"),
    ("auth", "tenantJurisdiction", "tenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId)"),
    ("auth", "tenantClosure", "ancestorTenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId) OR descendantTenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId)"),
]

# Mutable tables carrying rootTenantId directly, in child-before-parent order.
MUTABLE_ROOT_TABLE_NAMES: list[tuple[str, str]] = [
    ("logs", "auditEventChange"),
    ("logs", "auditEventRecord"),
    ("logs", "securityFinding"),
    ("logs", "searchLog"),
    ("logs", "screenVisit"),
    ("logs", "signInEvent"),
    ("dbo", "contactCorrection"),
    ("dbo", "caseResultLink"),
    ("dbo", "notificationObligation"),
    ("dbo", "personDocument"),
    ("dbo", "personActivity"),
    ("dbo", "matchExclusion"),
    ("dbo", "extractRun"),
    ("dbo", "jobQueue"),
    ("dbo", "workTask"),
    ("dbo", "caseCluster"),
    ("dbo", "cluster"),
    ("dbo", "environmentalSample"),
    ("dbo", "investigationCase"),
    ("dbo", "investigation"),
    ("dbo", "exposureSource"),
    ("dbo", "caseActivity"),
    ("dbo", "exposureCase"),
    ("dbo", "stagingHoldReason"),
    ("dbo", "stagingRow"),
    ("dbo", "labResult"),
    ("dbo", "intakeBatch"),
    ("dbo", "intakeProfile"),
    ("dbo", "provider"),
    ("dbo", "product"),
    ("dbo", "employmentAnalyte"),
    ("dbo", "employment"),
    ("dbo", "personLocation"),
    ("dbo", "locationOwner"),
    ("dbo", "organizationSite"),
    ("dbo", "location"),
    ("dbo", "organizationType"),
    ("dbo", "organization"),
    ("dbo", "personRelationship"),
    ("dbo", "pregnancyEpisode"),
    ("dbo", "personIdentifier"),
    ("dbo", "personContactMethod"),
    ("dbo", "personRace"),
    ("dbo", "personAlias"),
    ("dbo", "personMatchKey"),
    ("dbo", "personAccess"),
    ("dbo", "person"),
    ("config", "workflowDefinition"),
    ("config", "notificationRule"),
    ("config", "environmentalStandard"),
    ("config", "threshold"),
    ("config", "jurisdictionSetting"),
    ("config", "subJurisdiction"),
    ("auth", "sessionScopeRoot"),
    ("auth", "sessionScope"),
    ("auth", "appUser"),
    ("auth", "role"),
    ("auth", "tenant"),
]

# Append-only tables that cannot be updated/deleted (export-only, explicitly reported as retained).
APPEND_ONLY_ROOT_TABLES: list[tuple[str, str]] = [
    ("logs", "auditEvent"),
]

# Credential / secret columns NEVER included in root-tenant exports (I17).
EXPORT_EXCLUDED_COLS: set[str] = {
    "passwordHash",
    "mfaSecretProtected",
    "securityStamp",
    "codeHash",
    "codeVerifier",
    "tokenVerifier",
    "attemptedNameProtected",
    "sessionScopeId",
}

# Columns preserved during purge (identifiers, structural routing, or immutable types).
KEEP_COLS_GLOBAL: set[str] = {
    "rootTenantId",
    "owningTenantId",
    "rowVersion",
}
KEEP_COLS_PER_TABLE: dict[str, set[str]] = {
    "auth.tenant": {"tenantCode", "tenantType"},
    "auth.role": {"roleCode"},
    "logs.signInEvent": {"eventUtc", "eventType"},
    "logs.screenVisit": {"screenCode", "navigationType", "eventUtc"},
    "logs.securityFinding": {"ruleCode", "severity", "status", "windowStartUtc", "windowEndUtc", "detectedUtc"},
}

# Explicit scrub expressions for non-nullable columns with CHECK/UNIQUE/format constraints.
NON_NULL_SCRUB_OVERRIDES: dict[tuple[str, str], str] = {
    ("auth.appUser", "userName"): "CONCAT (N'purged-', CAST (appUserId AS NVARCHAR (36)))",
    ("auth.appUser", "displayName"): "N'[purged]'",
    ("auth.appUser", "passwordHash"): "N'[purged]'",
    ("auth.appUser", "securityStamp"): "'00000000-0000-0000-0000-000000000000'",
    ("auth.appUser", "mfaEnabled"): "0",
    ("auth.appUser", "failedSignInCount"): "0",
    ("auth.appUser", "isActive"): "0",
    ("auth.tenant", "isActive"): "0",
    ("auth.userProfile", "isActive"): "0",
    ("auth.sessionScope", "revokedUtc"): "COALESCE (revokedUtc, SYSUTCDATETIME ())",
    ("auth.mfaRecoveryCode", "codeHash"): "N'[purged]'",
    ("auth.mfaEmailChallenge", "codeVerifier"): "CAST (0x00 AS VARBINARY (32))",
    ("auth.mfaEmailChallenge", "supportReference"): "'PURGED'",
    ("auth.passwordReset", "tokenVerifier"): "CAST (0x00 AS VARBINARY (32))",
    ("auth.passwordReset", "supportReference"): "'PURGED'",
    ("dbo.person", "lastName"): "N'[purged]'",
    ("dbo.person", "lastNameKey"): "N'PURGED'",
    ("dbo.person", "sex"): "'unknown'",
    ("dbo.person", "ethnicity"): "'unknown'",
    ("dbo.personAlias", "lastName"): "N'[purged]'",
    ("dbo.personAlias", "lastNameKey"): "N'PURGED'",
    ("dbo.personMatchKey", "lastNameKey"): "N'PURGED'",
    ("dbo.personDocument", "title"): "N'[purged]'",
    ("dbo.personDocument", "fileName"): "N'purged'",
    ("dbo.personDocument", "content"): "0x00",
    ("dbo.personDocument", "contentHash"): "CAST (0x00 AS BINARY (32))",
    ("dbo.location", "country"): "'XX'",
    ("dbo.location", "city"): "N'[purged]'",
    ("dbo.location", "addressKey"): "CONCAT (N'purged-', CAST (locationId AS NVARCHAR (36)))",
    ("dbo.labResult", "collectedDate"): "'1900-01-01'",
    ("dbo.labResult", "population"): "'unknown'",
    ("dbo.labResult", "unitReported"): "N'[purged]'",
    ("dbo.labResult", "unitCanonical"): "N'[purged]'",
    ("dbo.labResult", "valueText"): "N'[purged]'",
    ("dbo.labResult", "reportedSnapshotJson"): "N'{}'",
    ("dbo.exposureCase", "caseStatus"): "'closed'",
    ("dbo.exposureCase", "closureReason"): "'other'",
    ("dbo.exposureCase", "closedDate"): "CAST (SYSUTCDATETIME () AS DATE)",
    ("dbo.stagingRow", "parsedJson"): "N'{}'",
    ("dbo.contactCorrection", "caregiverName"): "N'[purged]'",
    ("dbo.contactCorrection", "proposedValue"): "N'[purged]'",
    ("dbo.contactCorrection", "sourceNote"): "N'[purged]'",
    ("dbo.contactCorrection", "beforeJson"): "N'{}'",
    ("logs.auditEventRecord", "entityId"): "'00000000-0000-0000-0000-000000000000'",
    ("logs.searchLog", "searchText"): "N''",
    ("logs.securityFinding", "title"): "N'[purged]'",
    ("logs.securityFinding", "fingerprint"): "CONCAT ('purged|', CONVERT (VARCHAR (36), securityFindingId))",
}

_TABLES_BY_NAME: dict[str, dict] = {f"{t['schema']}.{t['name']}": t for t in td.TABLES}

# All 70 mutable tenant-owned tables (child auth tables first, then root tables in child-before-parent order).
ALL_MUTABLE_SPECS: list[tuple[str, str, str]] = (
    [(s, n, pred) for s, n, pred in CHILD_AUTH_TABLES]
    + [(s, n, "rootTenantId = @rootTenantId") for s, n in MUTABLE_ROOT_TABLE_NAMES]
)

ALL_TENANT_TABLES: set[str] = (
    {f"{s}.{n}" for s, n, _ in ALL_MUTABLE_SPECS}
    | {f"{s}.{n}" for s, n in APPEND_ONLY_ROOT_TABLES}
)


def _scrub_assignments(full: str) -> list[str]:
    """Derives SET assignments directly from tableDefinitions.py metadata so no PHI/credential column is missed."""
    t = _TABLES_BY_NAME[full]
    pk = t["cols"][0]["name"]
    fks = {fk[0] for fk in t.get("fks", [])}
    keep = KEEP_COLS_GLOBAL | KEEP_COLS_PER_TABLE.get(full, set()) | {pk} | fks
    assigns: list[str] = []
    for c in t["cols"]:
        cname = c["name"]
        if cname in keep:
            continue
        if (full, cname) in NON_NULL_SCRUB_OVERRIDES:
            assigns.append(f"{cname} = {NON_NULL_SCRUB_OVERRIDES[(full, cname)]}")
            continue
        if c.get("null", True):
            assigns.append(f"{cname} = NULL")
            continue
        # Non-nullable column without explicit override:
        ctype = c["type"].upper()
        check = c.get("check") or ""
        if "ISJSON" in check.upper():
            assigns.append(f"{cname} = N'{{}}'")
        elif not check and (ctype.startswith("NVARCHAR") or ctype.startswith("VARCHAR")):
            m = re.search(r"\((\d+)\)", ctype)
            max_len = int(m.group(1)) if m else 100
            val = "N'[purged]'" if max_len >= 8 else ("N'X'" if ctype.startswith("N") else "'X'")
            if not ctype.startswith("N"):
                val = val.lstrip("N")
            assigns.append(f"{cname} = {val}")
        elif not check and (ctype.startswith("VARBINARY") or ctype.startswith("BINARY")):
            assigns.append(f"{cname} = 0x00")
        elif cname == "isActive":
            assigns.append(f"{cname} = 0")
    assigns.append("isDeleted = 1")
    assigns.append("createdBy = N'MERIT tenant purge'")
    assigns.append("modifiedBy = N'MERIT tenant purge'")
    assigns.append("deletedBy = N'MERIT tenant purge'")
    return assigns


def _export_cols(full: str) -> list[str]:
    """Explicit non-credential column list for root-tenant export, including isDeleted and audit metadata (I17)."""
    if full == "logs.auditEvent":
        return [
            "auditEventId", "eventUtc", "appUserId", "rootTenantId", "owningTenantId", "eventAction", "entityType",
            "entityId", "recordCount", "clientAddress", "userAgentHash", "sessionReference", "traceId",
            "actingTenantId", "viewAsUserId", "viewKind", "filterJson", "administrationJson", "createdBy", "createdDateUtc",
        ]
    t = _TABLES_BY_NAME[full]
    cols = [c["name"] for c in t["cols"] if c["name"] not in EXPORT_EXCLUDED_COLS and c["type"].upper() != "ROWVERSION"]
    cols += ["isDeleted", "createdBy", "createdDateUtc", "modifiedBy", "modifiedDateUtc", "deletedBy", "deletedDateUtc"]
    return cols


def _export_body() -> str:
    lines = [
        "DECLARE @limit INT = CASE WHEN @pageSize BETWEEN 1 AND 5000 THEN @pageSize ELSE 1000 END;",
        "DECLARE @offset INT = (CASE WHEN @pageNumber >= 1 THEN @pageNumber ELSE 1 END - 1) * @limit;",
        "",
        "IF @tableName IS NULL",
        "BEGIN",
        "    DECLARE @manifest TABLE (",
        "        tableName   NVARCHAR (128) NOT NULL,",
        "        activeRows  INT NOT NULL,",
        "        deletedRows INT NOT NULL,",
        "        totalRows   INT NOT NULL,",
        "        disposition VARCHAR (30) NOT NULL",
        "    );",
    ]
    for s, n, pred in ALL_MUTABLE_SPECS:
        full = f"{s}.{n}"
        lines.append(
            f"    INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)\n"
            f"    SELECT N'{full}',\n"
            f"           COALESCE (SUM (CASE WHEN isDeleted = 0 THEN 1 ELSE 0 END), 0),\n"
            f"           COALESCE (SUM (CASE WHEN isDeleted = 1 THEN 1 ELSE 0 END), 0),\n"
            f"           COUNT (*), 'exportable'\n"
            f"      FROM {full} WHERE {pred};"
        )
    for s, n in APPEND_ONLY_ROOT_TABLES:
        full = f"{s}.{n}"
        lines.append(
            f"    INSERT @manifest (tableName, activeRows, deletedRows, totalRows, disposition)\n"
            f"    SELECT N'{full}', COUNT (*), 0, COUNT (*), 'ledgerReadOnly'\n"
            f"      FROM {full}\n"
            f"     WHERE rootTenantId = @rootTenantId\n"
            f"       AND (@maxAuditEventId IS NULL OR auditEventId <= @maxAuditEventId);"
        )
    lines.append("    SELECT tableName, COALESCE (activeRows, 0) AS activeRows, COALESCE (deletedRows, 0) AS deletedRows, totalRows, disposition, @maxAuditEventId AS maxAuditEventId FROM @manifest ORDER BY tableName;")
    lines.append("    RETURN;")
    lines.append("END;")
    lines.append("")
    # Paginated single-table streaming without tempdb JSON blob accumulation (I17).
    first = True
    for s, n, pred in ALL_MUTABLE_SPECS:
        full = f"{s}.{n}"
        pk = _TABLES_BY_NAME[full]["cols"][0]["name"]
        col_list = ", ".join(_export_cols(full))
        kw = "IF" if first else "ELSE IF"
        first = False
        lines.append(
            f"{kw} @tableName = N'{full}'\n"
            f"BEGIN\n"
            f"    SELECT {col_list}\n"
            f"      FROM {full}\n"
            f"     WHERE {pred}\n"
            f"     ORDER BY {pk}\n"
            f"    OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;\n"
            f"END"
        )
    for s, n in APPEND_ONLY_ROOT_TABLES:
        full = f"{s}.{n}"
        col_list = ", ".join(_export_cols(full))
        lines.append(
            f"ELSE IF @tableName = N'{full}'\n"
            f"BEGIN\n"
            f"    IF @afterAuditEventId IS NOT NULL\n"
            f"    BEGIN\n"
            f"        SELECT TOP (@limit) {col_list}\n"
            f"          FROM {full}\n"
            f"         WHERE rootTenantId = @rootTenantId\n"
            f"           AND auditEventId <= @maxAuditEventId\n"
            f"           AND auditEventId > @afterAuditEventId\n"
            f"         ORDER BY auditEventId;\n"
            f"    END\n"
            f"    ELSE\n"
            f"    BEGIN\n"
            f"        SELECT {col_list}\n"
            f"          FROM {full}\n"
            f"         WHERE rootTenantId = @rootTenantId\n"
            f"           AND auditEventId <= @maxAuditEventId\n"
            f"         ORDER BY auditEventId\n"
            f"        OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;\n"
            f"    END;\n"
            f"END"
        )
    lines.append("ELSE")
    lines.append("BEGIN")
    lines.append("    ;THROW 51422, N'Unknown or non-tenant table name for root export.', 1;")
    lines.append("END;")
    return "\n".join(lines)


def _export_allowlist_sql() -> str:
    """Returns T-SQL resolving @tableName to a canonical allowlisted table name before any ledger write."""
    all_names = [f"{s}.{n}" for s, n, _ in ALL_MUTABLE_SPECS] + [f"{s}.{n}" for s, n in APPEND_ONLY_ROOT_TABLES]
    values_sql = ", ".join(f"(N'{name}')" for name in all_names)
    return f"""DECLARE @canonicalTableName NVARCHAR (128) = N'[manifest]';
IF @tableName IS NOT NULL
BEGIN
    SELECT @canonicalTableName = v.tableName
      FROM (VALUES {values_sql}) AS v (tableName)
     WHERE v.tableName = @tableName;
    IF @canonicalTableName = N'[manifest]'
    BEGIN
        ;THROW 51422, N'Unknown or non-tenant table name for root export.', 1;
    END;
    IF @canonicalTableName = N'logs.auditEvent' AND @maxAuditEventId IS NULL
    BEGIN
        ;THROW 51422, N'@maxAuditEventId from the export manifest is required when paging logs.auditEvent.', 1;
    END;
END;"""


def _purge_body() -> str:
    unscrubbed = "(isDeleted = 0 OR modifiedBy <> N'MERIT tenant purge' OR createdBy <> N'MERIT tenant purge' OR COALESCE (deletedBy, N'') <> N'MERIT tenant purge')"
    lines = [
        "IF @dryRun IS NULL",
        "BEGIN",
        "    ;THROW 51422, N'@dryRun must be 0 or 1.', 1;",
        "END;",
        "DECLARE @report TABLE (",
        "    tableName           NVARCHAR (128) NOT NULL PRIMARY KEY,",
        "    rowsAffected        INT NOT NULL,",
        "    remainingUnscrubbed INT NOT NULL,",
        "    disposition         VARCHAR (40) NOT NULL",
        ");",
        "DECLARE @n INT = 0, @batch INT = 0, @rem INT = 0;",
        "DECLARE @startedDryRunTx BIT = 0;",
        "IF @dryRun = 1",
        "BEGIN",
        "    BEGIN TRANSACTION;",
        "    SET @startedDryRunTx = 1;",
        "END;",
        "",
        "-- Step 1 (I18, Codex round 1 & 2): Acquire MERIT.tenantHierarchy and MERIT.grants exclusive applocks,",
        "-- recheck descendant roots under the hierarchy lock, revoke EVERY live sessionScope deriving access from",
        "-- @rootTenantId (including cross-root and View As scopes) BEFORE clearing membership rows, deactivate",
        "-- tenants, and fail queued/running jobs using a distinct N'MERIT tenant fence' marker.",
        "BEGIN TRANSACTION;",
        "DECLARE @hierLock INT, @grantLock INT;",
        "EXEC @hierLock = sys.sp_getapplock @Resource = N'MERIT.tenantHierarchy', @LockMode = 'Exclusive',",
        "     @LockOwner = 'Transaction', @LockTimeout = 10000;",
        "IF @hierLock < 0",
        "BEGIN",
        "    ;THROW 51409, N'Tenant hierarchy changes are busy; please retry.', 1;",
        "END;",
        "EXEC @grantLock = sys.sp_getapplock @Resource = N'MERIT.grants', @LockMode = 'Exclusive',",
        "     @LockOwner = 'Transaction', @LockTimeout = 10000;",
        "IF @grantLock < 0",
        "BEGIN",
        "    ;THROW 51409, N'Authorization changes are busy; please retry.', 1;",
        "END;",
        "IF EXISTS (",
        "    SELECT 1",
        "      FROM auth.tenant AS child",
        "     WHERE child.rootTenantId <> @rootTenantId",
        "       AND child.isDeleted = 0",
        "       AND (   child.parentTenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId)",
        "            OR EXISTS (SELECT 1",
        "                         FROM auth.tenantClosure AS tc",
        "                         JOIN auth.tenant        AS anc ON anc.tenantId = tc.ancestorTenantId",
        "                        WHERE tc.descendantTenantId = child.tenantId",
        "                          AND tc.isDeleted = 0",
        "                          AND anc.rootTenantId = @rootTenantId))",
        ")",
        "BEGIN",
        "    ;THROW 51422, N'This root tenant has live descendant data roots. Reparent or purge all child roots before purging this root.', 1;",
        "END;",
        "UPDATE auth.tenant SET isActive = 0, modifiedBy = N'MERIT tenant fence' WHERE rootTenantId = @rootTenantId AND isActive = 1;",
        "UPDATE auth.sessionScope",
        "   SET revokedUtc = COALESCE (revokedUtc, SYSUTCDATETIME ()),",
        "       modifiedBy = N'MERIT tenant fence'",
        " WHERE revokedUtc IS NULL",
        "   AND (   rootTenantId = @rootTenantId",
        "        OR appUserId IN (SELECT appUserId FROM auth.appUser WHERE rootTenantId = @rootTenantId)",
        "        OR viewAsUserId IN (SELECT appUserId FROM auth.appUser WHERE rootTenantId = @rootTenantId)",
        "        OR sessionScopeId IN (SELECT sessionScopeId FROM auth.sessionScopeRoot WHERE rootTenantId = @rootTenantId AND isDeleted = 0)",
        "        OR sessionScopeId IN (SELECT sessionScopeId FROM auth.sessionScopeTenant WHERE tenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId) AND isDeleted = 0)",
        "        OR sessionScopeId IN (SELECT sessionScopeId FROM auth.sessionScopeAdminTenant WHERE tenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId) AND isDeleted = 0));",
        "UPDATE auth.sessionScopeRoot SET isDeleted = 1, modifiedBy = N'MERIT tenant fence' WHERE rootTenantId = @rootTenantId AND isDeleted = 0;",
        "UPDATE auth.sessionScopeTenant SET isDeleted = 1, modifiedBy = N'MERIT tenant fence' WHERE tenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId) AND isDeleted = 0;",
        "UPDATE auth.sessionScopeAdminTenant SET isDeleted = 1, modifiedBy = N'MERIT tenant fence' WHERE tenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId) AND isDeleted = 0;",
        "UPDATE dbo.jobQueue SET jobStatus = 'failed', modifiedBy = N'MERIT tenant fence' WHERE rootTenantId = @rootTenantId AND jobStatus IN ('queued', 'running');",
        "COMMIT TRANSACTION;",
        "",
        "-- #352 / Codex #3: Only set SESSION_CONTEXT (N'AppUser') = N'MERIT tenant purge' when the caller explicitly",
        "-- opts in via @singlePassDedicatedConnection = 1 on a dedicated connection that is closed on timeout/attention.",
        "-- Otherwise keep the safe two-pass path that never mutates session-wide AppUser.",
        "DECLARE @setPurgeAppUser BIT = 0;",
        "IF @singlePassDedicatedConnection = 1 AND SESSION_CONTEXT (N'AppUser') IS NULL",
        "BEGIN",
        "    EXEC sys.sp_set_session_context @key = N'AppUser', @value = N'MERIT tenant purge', @read_only = 0;",
        "    SET @setPurgeAppUser = 1;",
        "END;",
        "",
        "BEGIN TRY",
        "-- Step 2 (B4, I16): Scrub and soft-delete all 70 mutable tenant/auth tables in independently committed 5,000-row batches.",
    ]
    for s, n, pred in ALL_MUTABLE_SPECS:
        full = f"{s}.{n}"
        set_clause = ", ".join(_scrub_assignments(full))
        lines.append(
            f"SET @batch = 1; SET @n = 0;\n"
            f"WHILE @batch > 0\n"
            f"BEGIN\n"
            f"    BEGIN TRANSACTION;\n"
            f"    UPDATE TOP (5000) {full}\n"
            f"       SET {set_clause}\n"
            f"     WHERE ({pred})\n"
            f"       AND {unscrubbed};\n"
            f"    SET @batch = @@ROWCOUNT;\n"
            f"    COMMIT TRANSACTION;\n"
            f"    SET @n += @batch;\n"
            f"    IF @batch = 5000 AND @dryRun = 0\n"
            f"    BEGIN\n"
            f"        WAITFOR DELAY '00:00:00.050';\n"
            f"    END;\n"
            f"END;\n"
            f"INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'{full}', @n, 0, 'scrubbedAndSoftDeleted');"
        )
    lines.append("END TRY")
    lines.append("BEGIN CATCH")
    lines.append("    IF @setPurgeAppUser = 1")
    lines.append("    BEGIN")
    lines.append("        EXEC sys.sp_set_session_context @key = N'AppUser', @value = NULL, @read_only = 0;")
    lines.append("    END;")
    lines.append("    ;THROW;")
    lines.append("END CATCH;")
    lines.append("IF @setPurgeAppUser = 1")
    lines.append("BEGIN")
    lines.append("    EXEC sys.sp_set_session_context @key = N'AppUser', @value = NULL, @read_only = 0;")
    lines.append("END;")
    lines.append("")
    lines.append("-- Step 3 (I18, Codex #2/#4): Final verification pass over all 70 mutable tables after fencing and purging.")
    for s, n, pred in ALL_MUTABLE_SPECS:
        full = f"{s}.{n}"
        lines.append(
            f"UPDATE @report SET remainingUnscrubbed = (SELECT COUNT (*) FROM {full} WHERE ({pred}) AND {unscrubbed}) WHERE tableName = N'{full}';"
        )
    for s, n in APPEND_ONLY_ROOT_TABLES:
        full = f"{s}.{n}"
        lines.append(
            f"DECLARE @ledgerRows INT = (SELECT COUNT (*) FROM {full} WHERE rootTenantId = @rootTenantId);\n"
            f"INSERT @report (tableName, rowsAffected, remainingUnscrubbed, disposition) VALUES (N'{full}', @ledgerRows, 0, 'retainedAppendOnlyLedger');"
        )
    lines.append("")
    lines.append("DECLARE @targetCode VARCHAR (30) = (SELECT tenantCode FROM auth.tenant WHERE tenantId = @rootTenantId);")
    lines.append("DECLARE @totalRemaining INT = (SELECT COALESCE (SUM (remainingUnscrubbed), 0) FROM @report WHERE disposition = 'scrubbedAndSoftDeleted');")
    lines.append("IF @startedDryRunTx = 1 AND @@TRANCOUNT > 0")
    lines.append("BEGIN")
    lines.append("    ROLLBACK TRANSACTION;")
    lines.append("END;")
    lines.append("IF @totalRemaining > 0")
    lines.append("BEGIN")
    lines.append("    ;THROW 51409, N'Root tenant purge incomplete: residual unscrubbed rows remain after fencing and scrubbing. Drain in-flight writers and re-run.', 1;")
    lines.append("END;")
    lines.append("")
    lines.append("SELECT tableName, rowsAffected, remainingUnscrubbed, disposition FROM @report ORDER BY tableName;")
    lines.append(
        "SELECT @rootTenantId AS rootTenantId,\n"
        "       @targetCode AS tenantCode,\n"
        "       @dryRun AS isDryRun,\n"
        "       (SELECT SUM (rowsAffected) FROM @report WHERE disposition = 'scrubbedAndSoftDeleted') AS totalRowsPurged,\n"
        "       @totalRemaining AS totalRemainingUnscrubbed,\n"
        "       @ledgerRows AS ledgerRowsRetained,\n"
        "       DATEADD (DAY, @backupRetentionDays, SYSUTCDATETIME ()) AS backupAgeOutUtc;"
    )
    lines.append("SET @Comments = CONCAT (CASE WHEN @dryRun = 1 THEN N'Dry-run purge ' ELSE N'Purged ' END, N'rootTenantId=', @rootTenantId);")
    return "\n".join(lines)


PROCS = [
    {
        "schema": "config", "name": "uspExportRootTenantInternal", "kind": "read", "grants": [], "execute_as": "OWNER",
        "ticket": "346",
        "desc": "Ungranted owner-context helper for config.uspExportRootTenant: returns the 71-table manifest (including "
                "active/soft-deleted counts and maxAuditEventId cutoff) or streams one table page without credential columns (#346, I17).",
        "params": """@rootTenantId      UNIQUEIDENTIFIER
, @tableName         NVARCHAR (128) = NULL
, @pageNumber        INT = 1
, @pageSize          INT = 1000
, @maxAuditEventId   BIGINT = NULL
, @afterAuditEventId BIGINT = NULL""",
        "key": "CONCAT (N'rootTenantId=', @rootTenantId, N', pageNumber=', @pageNumber, N', pageSize=', @pageSize)",
        "body": _export_body(),
    },
    {
        "schema": "config", "name": "uspExportRootTenant", "kind": "write", "no_tx": True, "require_standalone": True, "grants": BOTH, "ticket": "346",
        "permission_all": ["security.investigate", "tenant.create"],
        "desc": "Platform-only, audited per-root-tenant data export covering all 70 mutable tenant/auth tables plus "
                "read-only logs.auditEvent (#346, I17). Must be called standalone with IMPLICIT_TRANSACTIONS OFF and no "
                "enclosing transaction (returns -100 without rolling back caller work). Validates and canonicalizes "
                "@tableName before committing its durable export audit event, requires @maxAuditEventId from the manifest "
                "when paging logs.auditEvent, and supports keyset paging via @afterAuditEventId.",
        "params": """@rootTenantId      UNIQUEIDENTIFIER
, @tableName         NVARCHAR (128) = NULL
, @pageNumber        INT = 1
, @pageSize          INT = 1000
, @maxAuditEventId   BIGINT = NULL
, @afterAuditEventId BIGINT = NULL""",
        "key": "CONCAT (N'rootTenantId=', @rootTenantId, N', pageNumber=', @pageNumber, N', pageSize=', @pageSize)",
        "validate": """IF NOT EXISTS (SELECT 1 FROM auth.tenant WHERE tenantId = @rootTenantId AND tenantId = rootTenantId)
BEGIN
    ;THROW 51404, N'The root tenant was not found.', 1;
END;
""" + _export_allowlist_sql(),
        "body": f"""
DECLARE @exportFilterJson NVARCHAR (MAX) = (
    SELECT @rootTenantId AS rootTenantId,
           @canonicalTableName AS tableName,
           @pageNumber AS pageNumber,
           @pageSize AS pageSize,
           @maxAuditEventId AS maxAuditEventId,
           @afterAuditEventId AS afterAuditEventId
    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
);
DECLARE @exportAudit TABLE (auditEventId BIGINT NOT NULL);
BEGIN TRANSACTION;
{AUDIT_SESSION_VARS}
INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId, clientAddress, owningTenantId,
                        sessionReference, traceId, actingTenantId, viewAsUserId, userAgentHash, filterJson)
OUTPUT INSERTED.auditEventId INTO @exportAudit (auditEventId)
VALUES (auth.udfSessionUserId (), @rootTenantId, 'export', 'tenant', @rootTenantId, @auditClientAddress, @rootTenantId,
        @auditSessionRef, @auditTraceId, @auditActingTenant, @auditViewAs, @auditUserAgentHash, @exportFilterJson);
COMMIT TRANSACTION;

DECLARE @effectiveMaxAuditEventId BIGINT = COALESCE (@maxAuditEventId, (SELECT TOP (1) auditEventId - 1 FROM @exportAudit));
DECLARE @internalTableName NVARCHAR (128) = CASE WHEN @tableName IS NULL THEN NULL ELSE @canonicalTableName END;
EXEC config.uspExportRootTenantInternal @rootTenantId = @rootTenantId, @tableName = @internalTableName,
     @pageNumber = @pageNumber, @pageSize = @pageSize, @maxAuditEventId = @effectiveMaxAuditEventId,
     @afterAuditEventId = @afterAuditEventId;
SET @Comments = CONCAT (N'Root tenant export completed for ', @rootTenantId);
""",
    },
    {
        "schema": "config", "name": "uspPurgeRootTenantInternal", "kind": "write", "no_tx": True, "require_standalone": True, "grants": [], "execute_as": "OWNER",
        "ticket": "346",
        "desc": "Ungranted owner-context helper for config.uspPurgeRootTenant: fences tenants/sessions/jobs first under "
                "MERIT.tenantHierarchy and MERIT.grants applocks, scrubs every PHI, free-text, binary, geocode, actor, "
                "and credential column across all 70 mutable tenant/auth tables (including previously soft-deleted rows) "
                "in independently committed 5,000-row batches (or rolls back all work when @dryRun = 1), and runs a final "
                "post-purge verification pass (#346, B4, B5, I16, I18).",
        "params": """@rootTenantId                  UNIQUEIDENTIFIER
, @backupRetentionDays           INT = 35
, @dryRun                        BIT = 0
, @singlePassDedicatedConnection BIT = 0""",
        "key": "CONCAT (N'rootTenantId=', @rootTenantId, N', backupRetentionDays=', @backupRetentionDays, N', dryRun=', @dryRun)",
        "body": _purge_body(),
    },
    {
        "schema": "config", "name": "uspPurgeRootTenant", "kind": "write", "no_tx": True, "require_standalone": True, "grants": BOTH, "ticket": "346",
        "permission_all": ["security.investigate", "tenant.create"],
        "desc": "Platform-only, two-step confirmed, audited per-root-tenant purge (#346, B4, B5, I16, I18). Requires "
                "@confirmationCode = N'PURGE:' + tenantCode and refuses the platform root. Fences the root first "
                "(deactivates tenants, revokes sessions, fails queued/running jobs), scrubs and soft-deletes all 70 "
                "mutable tenant/auth tables in 5,000-row batches (or rehearses inside a rolled-back transaction when "
                "@dryRun = 1), verifies zero unscrubbed rows remain, and returns a purge report with backupAgeOutUtc.",
        "params": """@rootTenantId                  UNIQUEIDENTIFIER
, @confirmationCode              NVARCHAR (60)
, @backupRetentionDays           INT = 35
, @dryRun                        BIT = 0
, @singlePassDedicatedConnection BIT = 0""",
        "key": "CONCAT (N'rootTenantId=', @rootTenantId, N', backupRetentionDays=', @backupRetentionDays, N', dryRun=', @dryRun)",
        "validate": """IF @dryRun IS NULL
BEGIN
    ;THROW 51422, N'@dryRun must be 0 or 1.', 1;
END;
DECLARE @targetCode VARCHAR (30), @targetType VARCHAR (30);
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
IF EXISTS (
    SELECT 1
      FROM auth.tenant AS child
     WHERE child.rootTenantId <> @rootTenantId
       AND child.isDeleted = 0
       AND (   child.parentTenantId IN (SELECT tenantId FROM auth.tenant WHERE rootTenantId = @rootTenantId)
            OR EXISTS (SELECT 1
                         FROM auth.tenantClosure AS tc
                         JOIN auth.tenant        AS anc ON anc.tenantId = tc.ancestorTenantId
                        WHERE tc.descendantTenantId = child.tenantId
                          AND tc.isDeleted = 0
                          AND anc.rootTenantId = @rootTenantId))
)
BEGIN
    ;THROW 51422, N'This root tenant has live descendant data roots. Reparent or purge all child roots before purging this root.', 1;
END;
IF COALESCE (@confirmationCode, N'') <> CONCAT (N'PURGE:', @targetCode)
BEGIN
    ;THROW 51422, N'Confirmation code mismatch. Pass PURGE:<tenantCode> to confirm root tenant purge.', 1;
END;
IF @backupRetentionDays IS NULL OR @backupRetentionDays NOT BETWEEN 1 AND 3650
BEGIN
    ;THROW 51422, N'@backupRetentionDays must be between 1 and 3650.', 1;
END;""",
        "body": f"""
IF @dryRun = 0
BEGIN
    BEGIN TRANSACTION;
    {AUDIT_SESSION_VARS}
    INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId, clientAddress, owningTenantId,
                            sessionReference, traceId, actingTenantId, viewAsUserId, userAgentHash)
    VALUES (auth.udfSessionUserId (), @rootTenantId, 'delete', 'tenant', @rootTenantId, @auditClientAddress, @rootTenantId,
            @auditSessionRef, @auditTraceId, @auditActingTenant, @auditViewAs, @auditUserAgentHash);
    COMMIT TRANSACTION;
END;

EXEC config.uspPurgeRootTenantInternal @rootTenantId = @rootTenantId, @backupRetentionDays = @backupRetentionDays,
     @dryRun = @dryRun, @singlePassDedicatedConnection = @singlePassDedicatedConnection;
SET @Comments = CONCAT (CASE WHEN @dryRun = 1 THEN N'Dry-run purge completed for ' ELSE N'Root tenant purge completed for ' END, @rootTenantId);
""",
    },
]
