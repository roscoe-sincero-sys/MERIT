# Security reports training video

The complete walkthrough uses the real local MERIT screens and synthetic data. It begins as a Baltimore City security
officer, switches to a Maryland administrator for account history, and finishes as a platform investigator. Sign-in
and authenticator enrollment happen off camera; the investigation's verification code is masked and omitted from the
transcript action log.

Rebuild from `training/` with the training app running:

```powershell
npm.cmd run videos -- --id security-reports --voice piper --burn-in
```

The source is [security-reports.workflow.ts](../workflows/administrator/security/security-reports.workflow.ts).
The generated files are under `training/output/administrator/security/`; the deliverable copies are under
[`trainingVideos/administrator/security/`](../../trainingVideos/administrator/security/).

## Coverage

| Chapter | Use cases shown or explained |
|---|---|
| Choose the right scope and review access | Officer permissions; active profile and working place; county, region and platform scope; home agency versus granted access; user filtering; MFA, last sign-in and status; current-access CSV; referral to an administrator. |
| Investigate all sign-in patterns | Window selection; password spraying; credential stuffing; success after repeated failures; failures by address; failures by account; lockouts and rate-limit refusals; empty-result interpretation; all-section CSV. |
| Trace views, changes and exports in the audit log | Security-action group; user, action, record type, date and record-ID filters; all-events links; clear filters; counts, ordering and page size; record-link permissions; filtered CSV and its 10,000-event limit; historical ownership scope. |
| Understand screen usage and reconstruct a session | Period selection; screen totals and optional visible time; role breakdown and non-additive counts; result-entry paths, loops and strict search-to-saved flow; summary CSV; recent-session limits; ordered session visits; UTC, tab and predecessor interpretation; page CSV and paging; reference lookup; missing telemetry and retention. |
| Administrator: investigate one account | User search; master-profile review; latest-50-event history; event, reason, address, browser, session and attempt correlation; history CSV and its 1,000-event limit. |
| Platform investigator: unknown user names | Investigation permission; hashed grouping; counts, addresses and times; matching and nonmatching name lookups; 90-day lookup window; hashed-only CSV; warning about mis-entered passwords; current-code verification; audited temporary reveal; unavailable encryption keys; return to hashed view. |
| Follow up and preserve the review trail | Audit trail for exports, lookups and reveals; selecting a report for a question; recording scope, period, identifiers and findings; authorized follow-up. |

The report sections and controls are exercised against the application in the workflow dry run, including real CSV
downloads. Rare or volume-dependent outcomes (10,000-event truncation, extra session pages, expired encryption keys,
and retention loss) are explained on the relevant screen rather than artificially filling the database to trigger them.

## Demonstration fixtures

`prepare()` creates a county officer and six synthetic staff accounts with fixed identifiers. Five staff belong to
Baltimore City; one belongs to Baltimore County and must be absent from the officer's user list. Synthetic sign-in
events use documentation-only IP addresses in `192.0.2.0/24` and a dedicated trace marker. Navigation rows are explicitly
illustrative, not actual patient work. Rebuilds soft-delete only this workflow's marked sign-in and usage fixtures.

An unknown-name attempt is generated through the real sign-in page so hashing and encryption use the running app's
keys. Account administration and evidence export use application screens. The privacy guard refuses non-training
databases or non-example account email addresses, and every completed step is scanned.

## Validation and review

```powershell
npm.cmd run typecheck
npm.cmd test -- tests/unit.spec.ts tests/privacy.spec.ts
npm.cmd test -- tests/workflows.spec.ts --grep security-reports
npm.cmd test -- tests/outputs.spec.ts --grep security-reports
```

The output checks verify resolution, frame rate, codecs, narration loudness, chapters, caption timings and line lengths,
transcript completeness, and sensitive text. Follow the [human review checklist](review-checklist.md) before publishing
the video as official training.
