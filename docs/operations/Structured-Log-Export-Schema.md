# MERIT Structured Log Export Schema (#328)

MERIT provides a unified, cursor-paginated structured log export at `/admin/log-export` (download endpoint `/admin/log-export/download`) designed for external SIEM ingestion and independent AI-agent behavioral analysis.

## Endpoint Parameters

| Parameter | Values | Default | Notes |
| --- | --- | --- | --- |
| `format` | `jsonl`, `csv` | `jsonl` | `jsonl` emits UTF-8 NDJSON (`application/x-ndjson`); `csv` emits canonical lossless CSV with formula-injection protection. |
| `streams` | Comma-separated subset of `audit`, `signin`, `usage`, `search` | `audit` | Each requested stream is authorized independently against `audit.view`, `security.review`/`tenant.administer`, and `usage.view`. |
| `from`, `to` | `YYYY-MM-DD` (UTC inclusive, max 31 days per request) | Last 7 days | Evaluated as the half-open UTC interval `[from 00:00:00.000Z, (to + 1 day) 00:00:00.000Z)`, streamed directly to the HTTP response in 2,000-row cursor-paginated batches (`DbType.DateTime2` cursors) without buffering the full export in memory. |
| `pseudonymize` | `true`, `false` | `true` | Enables deterministic keyed pseudonymization (AI-agent-safe mode). |

Every export is recorded in the tamper-evident `logs.auditEvent` ledger (`eventAction = 'export'`, `entityType = 'structuredLogExport'`, `recordCount = <exported rows>`).

## Canonical Conventions

- **Timestamps:** ISO-8601 UTC with millisecond precision (`yyyy-MM-ddTHH:mm:ss.fffZ`).
- **Codes:** Raw canonical database codes (`eventAction`, `entityType`, `eventType`, `screenCode`, `navigationType`), never humanized UI labels.
- **Correlation Keys:** `sessionReference` (non-secret session GUID), `traceId` (HTTP request correlation ID), `attemptId` (sign-in attempt GUID), `rootTenantId`, `owningTenantId`, `actingTenantId`, `viewAsUserId`.
- **Search Text Protection:** `logs.searchLog` exports NEVER include raw `searchText` (which may contain patient PHI).

## Pseudonymized / AI-Agent-Safe Mode (`pseudonymize=true`)

- `appUserId`, `actorUserId`, and `viewAsUserId` are replaced with deterministic per-export keyed HMAC-SHA256 tokens (`usr_<16 hex>`), preserving cross-stream joins without revealing account GUIDs.
- `userName`, `displayName`, and `filterJson` are omitted (`null`).
- `clientAddress` is replaced with `<subnet prefix>|ip_<12 hex>` (`/24` for IPv4, `/48` for IPv6), preserving subnet and exact-IP grouping without exposing raw client IPs.
- **Identifiers preserved for behavioral joins:** `entityId`, `sessionReference`, `traceId`, `attemptId`, and `userAgent` remain raw so external agents can correlate activity across streams and detect automated user-agents or bulk record scraping. Because each export uses a fresh random HMAC key, user/IP pseudonyms are consistent within a single export and do not link across separate exports.

## JSONL Stream Schemas

### `stream = "auditEvent"`
`auditEventId`, `eventUtc`, `eventAction`, `entityType`, `entityId`, `recordCount`, `viewKind`, `appUserId`, `userName`, `displayName`, `rootTenantId`, `owningTenantId`, `actingTenantId`, `viewAsUserId`, `sessionReference`, `traceId`, `clientAddress`, `filterJson`.

### `stream = "signInEvent"`
`signInEventId`, `eventUtc`, `eventType`, `step`, `method`, `reason`, `appUserId`, `userName`, `displayName`, `actorUserId`, `rootTenantId`, `clientAddress`, `userAgent`, `sessionReference`, `attemptId`, `traceId`, `failedSignInCount`, `lockoutEndUtc`.

### `stream = "screenVisit"`
`screenVisitId`, `eventUtc`, `rootTenantId`, `actingTenantId`, `appUserId`, `userName`, `displayName`, `sessionReference`, `profileId`, `viewAsUserId`, `roleCodes`, `screenCode`, `navigationType`, `tabId`, `previousVisitId`, `durationMs`.

### `stream = "searchLog"`
`searchLogId`, `searchedUtc`, `appUserId`, `userName`, `displayName`, `rootTenantId`, `actingTenantId`, `sessionReference`, `clientAddress`, `resultCount`, `existsElsewhere`, `searchSource`.

## Allowlisted SQL Execution Diagnostics (#338)

Operational exports (`/admin/operations-log.jsonl` and `.csv`) expose only the allowlisted `logs.ExecutionLog` diagnostic fields:
`ExecutionLogId`, `ProcedureName`, `StartDateUtc`, `EndDateUtc`, `ElapsedMilliseconds`, `ReCreatedAfterRollback`, `ErrorNumber`, `ErrorLine`, `ErrorProcedure`, `KeyParameters` (identifiers/counts only), and `createdBy`. Raw `ErrorMessage`, `DynamicSql`, and `ContextMessage` are excluded from operational reports and exports.
