# MERIT Log Retention, Source-Address Privacy, and Release Governance (#336, #340, #342)

This guide defines the records-retention schedule, field-scrubbing rules, source-address privacy controls, and production release verification checks across all MERIT logging stores, exports, and application log collectors.

## 1. Retention Schedule Across Log Stores (#336)

All purgeable SQL tables follow MERIT's **no-hard-delete invariant**: worker purgers scrub personal and correlation fields and set `isDeleted = 1` in **independently committed 5,000-row batches** (with a 50 ms yield between full batches) so concurrent web and intake writes are never blocked (#340). All active-row queries and filtered indexes (`WHERE isDeleted = 0`) exclude purged rows.

| Store | Config Key | Default (Range) | Purger Procedure | Fields Scrubbed on Expiry | Fields Retained (Aggregate / Compliance) |
| --- | --- | --- | --- | --- | --- |
| `logs.signInEvent` | `SignInLog:RetentionDays` | 365 days (30–3650) | `logs.uspPurgeSignInEvents` | `clientAddress`, `forwardedFor`, `userAgent`, `attemptedNameHash`, `attemptedNameProtected`, `sessionReference`, `traceId` (`isDeleted = 1`) | `signInEventId`, `eventUtc`, `eventType`, `step`, `method`, `reason`, `rootTenantId`, `appUserId` (for anonymous/aggregate counts) |
| `logs.auditEvent` | *Immutable ledger* | Permanent (`APPEND_ONLY = ON`) | *None (append-only)* | *Never updated or deleted* | IDs, `eventAction`, `entityType`, `entityId`, `recordCount`, `userAgentHash` (64-bit SHA-256 prefix, never raw UA), `sessionReference`, `traceId`, `viewKind`, non-PHI `filterJson`. Sign-in ledger rows (`signIn`, `signInFailed`) omit `clientAddress` (`NULL`) so sign-in IP addresses expire solely under `logs.signInEvent` (#336). |
| `logs.auditEventRecord` | `AuditManifest:RetentionDays` | 365 days (30–3650) | `logs.uspPurgeAuditEventRecords` | `entityId` set to `00000000-0000-0000-0000-000000000000`, `isDeleted = 1` | `auditEventId`, `rootTenantId`, `entityType`, `createdDateUtc` (capacity-planning note below) |
| `logs.auditEventChange` | `AuditChangeHistory:RetentionDays` | 365 days (30–3650) | `logs.uspPurgeAuditEventChanges` | `beforeJson = NULL`, `afterJson = NULL`, `isDeleted = 1` | `auditEventId`, `rootTenantId`, `owningTenantId`, `entityType`, `entityId`, `changedUtc` |
| `logs.searchLog` | `SearchLog:RetentionDays` | 90 days (30–3650) | `logs.uspPurgeSearchLog` | `searchText = N''`, `clientAddress = NULL`, `sessionReference = NULL`, `actingTenantId = NULL`, `isDeleted = 1` | `searchLogId`, `searchedUtc`, `rootTenantId`, `appUserId`, `resultCount`, `existsElsewhere`, `searchSource` |
| `logs.screenVisit` | `UsageLog:RetentionDays` | 90 days (30–365) | `logs.uspPurgeScreenVisits` | `appUserId`, `sessionReference`, `profileId`, `viewAsUserId`, `actingTenantId`, `roleCodes`, `tabId`, `previousVisitId`, `durationMs`, timestamp truncated to UTC date, `createdBy`/`modifiedBy`/`deletedBy` anonymized (`isDeleted = 1`) | `rootTenantId`, `screenCode`, UTC date (anonymous screen totals) |
| `logs.securityFinding` | Governed by incident triage policy | Retained for auditability | *No automatic scrub* | Holds counts, rule codes, and correlation keys only — never patient data | Full finding lifecycle (`open`, `acknowledged`, `resolved`, `falsePositive`) |
| Structured application logs (`Logging:Format=Json`) | External SIEM / log collector policy | Align with `SignInLog:RetentionDays` (max 365 days) | External collector lifecycle | Collector expires raw JSON log streams containing `ClientAddress` / `UserAgent` on the same schedule as `logs.signInEvent` | Aggregated metrics / dashboards without IP or user identifiers |
| Structured log exports (`/admin/log-export`) | Downstream consumer policy | Transient / aligned with recipient retention | Consumer lifecycle | Prefer `pseudonymize=true` (default); identified exports (`pseudonymize=false`) inherit the strictest retention of the exported streams | Audited in `logs.auditEvent` (`entityType = 'structuredLogExport'`) |

## 2. Source-Address & User-Agent Privacy Requirements (#336)

1. **Trusted Proxy Boundary:** `clientAddress` is populated only from `HttpContext.Connection.RemoteIpAddress` after ASP.NET Core forwarded-headers middleware validates trusted proxies (`ForwardedHeaders`). Raw `X-Forwarded-For` chains are stored only in `logs.signInEvent.forwardedFor` and scrubbed on `SignInLog:RetentionDays` expiry.
2. **Access Control:**
   - `logs.signInEvent` (`clientAddress`, `userAgent`): restricted to `security.review` / `tenant.administer` / `security.investigate`, scoped by `auth.tvfReviewAccounts`.
   - `logs.auditEvent` (`clientAddress`, `userAgentHash`): restricted to `audit.view`, scoped by `#270` (`auth.tvfReviewTenants`). Raw `User-Agent` strings are **never** stored in `logs.auditEvent`; only a 64-bit SHA-256 hex prefix (`userAgentHash`) is recorded for mid-session drift detection (#327).
   - `logs.searchLog` (`clientAddress`): restricted to `security.review` / `audit.view`; raw `searchText` is never exported and is visible in-app only to `security.investigate`.
3. **Export Pseudonymization:** Structured log exports default to `pseudonymize=true`, replacing `clientAddress` with `<subnet>|ip_<12 hex>` (per-export keyed HMAC-SHA256) and `appUserId` with `usr_<16 hex>`, so AI-agent and external SIEM consumers can perform behavioral analysis without receiving raw IP addresses or account GUIDs.

## 3. Capacity Planning Decision for `logs.auditEventRecord` (#340)

- **Decision:** Retain **soft-delete** (`isDeleted = 1`, `entityId` scrubbed to the zero GUID) rather than introducing a hard-delete exception, preserving MERIT's database-wide no-hard-delete invariant.
- **Growth & Indexing:** Expect ~1–2 rows per listed or exported record (~80 bytes/row in the clustered index). Filtered indexes (`WHERE isDeleted = 0`) keep active query paths (`logs.uspSearchAuditEvents`, `logs.uspGetAuditEventRecords`) independent of historical scrubbed rows.

## 4. Per-Root-Tenant Export and Offboarding Purge (#346)

Platform administrators (holding both `security.investigate` and `tenant.create`) can export or purge a single root tenant's complete dataset without affecting any other data root:

1. **Export:** `EXEC config.uspExportRootTenant @rootTenantId = '<guid>', @includePayloads = 1;`
   - Audited in `logs.auditEvent` (`eventAction = 'export'`, `entityType = 'tenant'`).
   - Returns a manifest covering every table carrying `rootTenantId` (enforced by `test_root_tenant_lifecycle_inventory_covers_every_root_tenant_table` in `test_generateProcedures.py`), including read-only `logs.auditEvent` rows.
2. **Two-Step Confirmed Purge:** `EXEC config.uspPurgeRootTenant @rootTenantId = '<guid>', @confirmationCode = N'PURGE:<tenantCode>', @backupRetentionDays = 35;`
   - Refuses the platform root and rejects any mismatched confirmation code (`51422`).
   - Audited in `logs.auditEvent` (`eventAction = 'delete'`, `entityType = 'tenant'`).
   - Scrubs PHI/PII fields and soft-deletes (`isDeleted = 1`) all mutable rows for `@rootTenantId` across clinical, configuration, auth, and purgeable log tables while leaving other roots untouched.
   - **Append-only ledger retention:** `logs.auditEvent` is `APPEND_ONLY = ON` and cannot be deleted; its rows (IDs, actions, and timestamps only — never patient PHI or sign-in IPs) are retained and reported as `ledgerRowsRetained`.
   - **Backup ageing:** The purge report returns `backupAgeOutUtc = DATEADD(DAY, @backupRetentionDays, SYSUTCDATETIME())` indicating when purged records age out of full database backups.

## 5. Production Release Verification Checklist (#342, #347)

Before promoting logging schema changes to production, operations should verify and record evidence for:

1. **Deploy order (PR #348 / #347):** Deploy SQL scripts **before** the web binary (`WorkIntakeExtractData` reads the second `hiddenCount` result set from `logs.uspGetAuditEventRecords`, and `PersonAndPlaceData` sends `@isVerification` to `dbo.uspSavePersonRelationship`).
2. **Append-only ledger upgrade & verification (#342, #347):** Rehearse `161_logs.auditEvent.sql` on a production-sized copy, record migration duration/locking (including `CK_logs_auditEvent_eventAction` replacement), ensure `ALLOW_SNAPSHOT_ISOLATION` is enabled (`ALTER DATABASE [Db] SET ALLOW_SNAPSHOT_ISOLATION ON;`), and run `EXEC sys.sp_verify_database_ledger;` immediately afterward.
3. **Per-environment worker & web configuration (#342, #347):**
   - `SignInLog:RetentionDays` (default `365`)
   - `AuditManifest:RetentionDays` (default `365`, clamped `30..3650`)
   - `AuditChangeHistory:RetentionDays` (default `365`, clamped `30..3650`)
   - `SearchLog:RetentionDays` (default `90`, clamped `30..3650`)
   - `UsageLog:RetentionDays` (default `90`, clamped `30..365`)
   - `Merit:AuthenticatedRequestsPerMinute` (default `600` per user/min)
   - `SecurityAlerts:ScanIntervalMinutes` (default `15`), `WindowHours` (default `24`), `WebhookUrl` (HTTPS; retries transient 5xx/429/network failures up to 3 attempts with 1s/3s backoff)
   - `Logging:Format=Json` on both `Merit.Web` and `Merit.Worker` where logs ship to a collector.
4. **Production-scale plans & tuning (#342, #347):** Capture query plans and latency on production-sized data for `logs.uspRunSecurityFindingScan`, 31-day 4-stream structured exports, manifest owner stamping on results/case lists, and offline index builds (`IX_logs_searchLog_searchedUtc_searchLogId`, `IX_logs_signInEvent_legacyCorrelationCleanup`). Run the anomaly scanner for one week to measure false-positive rates (including `midSessionIpDrift`, `midSessionUserAgentDrift`, and `apiOnlyNoScreenVisits`) before enabling outbound webhooks.
5. **Row history (#332):** Decided and implemented as purgeable `logs.auditEventChange` snapshots (closed in PR #343).
