# Screen usage and workflow reports (#227)

MERIT records first-party screen visits so authorized supervisors and administrators can review navigation and improve workflows. This is best-effort navigation metadata, not proof of attention, productivity, clinical work, caregiver notification or task completion.

## Storage decision

Use the new **`logs.screenVisit`** table. Keep the existing **`logs.auditEvent`** ledger unchanged.

| Option | Assessment |
| --- | --- |
| Add audit-event subtypes | The existing table is an SQL Server append-only ledger. Subtypes alone add neither session/context columns nor an ability to update durations or scrub retained workforce navigation. Putting this metadata in it would make it permanent. |
| Ledger event plus mutable sidecar | Still requires a new table, introduces a permanent linkage and provides no advantage for optional usage measurements. |
| Separate retainable screen visits | Supports idempotent browser/server reconciliation, cumulative duration updates and removal of workforce/session context after retention. Keeps the security/data-access ledger's meaning and protections intact. |

Usage report reads and CSV exports use existing `view`/`export` audit actions with entity type `screenUsage`. The audit ledger receives no usage session reference or visit ID. The usage table is not tamper-evident and must not replace the security audit.

## Collection and privacy

- Successful authenticated full Razor pages, including empty pages, are recorded by a result filter. Angular records successful router screen changes. Static allowlisted user guides record a server-side visit, without browser duration. Redirect responses, APIs, polling, assets, htmx fragments and validation errors are excluded. The displayed successful manual-result-save outcome is an explicit exception to the fragment rule.
- An allowlisted screen code maps to a static name and normalized route in `ScreenCatalog`. No raw URL, query string, referrer address, patient ID, patient name, search text, form value or client address is collected by this feature. Referrer information is a validated predecessor screen visit, never a browser referrer URL. Anonymous sign-in screens are not usage visits; existing sign-in events cover authentication.
- Records **do contain workforce personal data**: actor ID, root/working tenant, active profile, optional view-as user, active role codes, timestamps and a non-secret logical session reference. The issue's literal “no personal data” acceptance wording cannot coexist with per-user session reports. The implementation excludes patient data and credentials, minimizes workforce metadata and applies retention. Access should follow agency personnel-data policy.
- Actor, context, roles and session reference come from the current server-side session, never the browser payload. Authentication scope keys are not stored in visits or procedure instrumentation. A random tab ID and visit ID support correlation. A predecessor must belong to the same actor, logical sign-in session and tab.
- Profile, working-tenant and view-as changes replace the authorization scope while retaining the logical sign-in reference. Fresh sign-in gets a new reference. Correlation failure is best effort and must not block switching profiles. Multiple tabs can interleave; use predecessor links rather than assuming adjacent timestamps belong to one workflow.
- Browser duration is cumulative visible **and focused** time, checkpointed on blur, visibility change, navigation and page hide. It is capped at 30 minutes per visit and updated monotonically, not summed. Recording and duration updates serialize per visit to prevent reconciliation/checkpoint deadlocks. Closing a browser, lost connectivity, blocked JavaScript, rate limits or unavailable storage can leave gaps. Unknown time is not zero. There is no periodic heartbeat and no historic backfill.

## Reports and permissions

Open **Administration → Usage** (`/admin/usage`). New permission `usage.view` is seeded for standard Supervisor and Administrator roles, including newly installed roots. It is not automatically granted to ordinary intake/case staff, user administrators or epidemiologists. Sign in again to refresh permissions after deployment. Custom roles require an explicit grant.

Reads require that permission in both the web application and SQL, and restrict visits to the current root and accessible working tenants. Out-of-scope predecessors and lifecycle events are not exposed. Report queries cover 1–90 days, defaulting to 30.

- Screen and active-profile-role counts; multi-role counts are not additive.
- Five most common paths into **Enter a result**, using up to three predecessor screens in a tab chain. Missing, expired or inaccessible predecessors start a new path.
- The ten most common recorded A → B → A paths. These are not necessarily mistakes.
- Observed elapsed time for the strict search → person → result entry → saved sequence, excluding negative/over-24-hour sequences. Extra screens and missing events mean a flow is not counted; elapsed time includes pauses.
- The latest 100 sessions plus total session count, session-reference lookup and ordered visits paged in groups of 100. Existing retained session-start/end events are shown where accessible; browser closure does not prove sign-out.
- Summary CSV and explicitly labeled **current page** session CSV. Counts cover the selected period, not only the latest 100 sessions.

The sign-in page and daily operations guide disclose measurement to staff. No external analytics service or third-party script is used.

## Deployment and operations

Deploy the database scripts, Web and Worker together using the normal deployment process. This change adds table script `169_logs.screenVisit.sql`, six procedures, a reference permission and `501_usagePermissions.sql`, and updates root installation. The generated procedure manifest includes the additions. Existing ledger schema and security policies are unchanged. Runtime database logins remain EXECUTE-only: web may record/read authorized usage, while only the worker may run retention.

| Setting | Process | Default | Meaning |
| --- | --- | --- | --- |
| `ScreenVisit:Enabled` | Web | `true` | Disable new usage collection and its browser script without affecting normal workflows. |
| `ScreenVisit:RequestsPerMinute` | Web | `120` | Per-actor capture limit; constrained to 1–600. Rejections return 429 without sign-in lockout or a flood of sign-in alerts. |
| `ScreenVisit:RetentionDays` | Worker | `90` | Workforce/session context retention, constrained to 30–365 days. Reports query at most 90 days. |

Environment overrides use double underscores, for example `ScreenVisit__Enabled=false`. The Worker must run for retention to occur: its purger starts after one minute and runs daily, processing expired rows in batches. Monitor the existing worker logs for success/failure and verify the configured period matches agency policy.

Retention strips actor/session/profile/view-as/working-tenant/role/tab/predecessor/duration fields, reduces event and created timestamps to a calendar date, replaces attribution with generic retention text, resets the navigation hint and soft-deletes the row. Only coarse root/screen/day metadata remains; normal reports exclude these rows. It does not purge the independent security ledger or sign-in log, whose policies still apply.

## Verification

Focused unit and SQL-backed integration tests cover the catalog and filter, empty pages, fragment/API exclusion, idempotency, actor ownership, normalized metadata, session continuity, report paths/loops/timing/exports, scope isolation, anti-forgery and worker-only retention. Browser checks exercise actual navigation, duration checkpoints and accessible reporting. Navigation measurements remain best effort even when these checks pass.
