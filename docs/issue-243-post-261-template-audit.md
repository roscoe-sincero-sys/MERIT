# Issue #243: independent retest after PR #261, with the PR #239 Maryland templates

Baseline: merged main `05879d5` (PR #261), 2026-11-09. This audit changed only tests and documentation. No application code, schema, permission or production data changed.

## Outcome

- **The previously reported results reproduce independently.** Both runs below started from a brand-new database deployed with `-IncludeDemoData`, which also re-checks the #254 fresh-install fix.
- **One new defect was filed: [#262](https://github.com/roscoe-sincero-sys/MERIT/issues/262).** It was found by extending the scenario to the date-driven rules the issue references in PR #239.

## Results

| Check | Result |
|---|---|
| Fresh deployment and expanded continuous replay (`MERIT_SCENARIO_243_CLAUDE_20261109`) | Passed. Covers the account hierarchy with MFA, unconfigured Maryland, 2020 HL7/manual input, 2025 CSV/manual input, program versions, LHD access, the roster/export and the patient overview. |
| Full integration suite on a separate fresh database (`MERIT_AUDIT243_CLAUDE_20261109`) | **205 passed, 0 failed, 1 opt-in replay skipped.** |
| **New:** PR #239 drafts activated from `/config/notification-rules?template=…` in the same Maryland tenant at business date 2025-03-03 (`mdChild`, `mdOwnerChild`, `mdLabElevated`, `mdLabBelow`) | Each draft link pre-filled the form, and each rule was activated with an explicit synthetic calendar and approval. |
| 2025 CSV and manual 4 ug/dL (venous, child under 6) | Caregiver and residence-owner obligations were created due **2025-03-17** (10 business days from receipt). They are in `needsReview` state, as intended for Maryland EBL/owner checklists. A 3 ug/dL result created none. |
| Laboratory-reporting drafts | All three results opened "Verify laboratory reporting timeline". The CSV import had no mapped final-result date. For manual entries, Maryland residence and eligibility are not established automatically, so review is needed. This is conservative behaviour by design. |
| Missing-information check | The CSV import listed only fields the file truly lacked. **Manual entry listed `firstName`, `lastName`, `dob` and every other required field as missing, and opened a pending "Missing Information Received" task:** this is [#262](https://github.com/roscoe-sincero-sys/MERIT/issues/262). The replay records whether this gap is present, so it stays valid before and after the #262 fix. |

## Evidence

- Replay log and step evidence: `sourceCode/.artifacts/audit-243-claude-templates.log`, `sourceCode/.artifacts/scenario-243-replay-evidence.txt`, `sourceCode/tests/Merit.IntegrationTests/TestResults/claude-templates.trx`.
- First fresh-deploy replay: `sourceCode/.artifacts/audit-243-claude-replay.log`.
- Full suite: `sourceCode/.artifacts/audit-243-claude-full.log`, `sourceCode/tests/Merit.IntegrationTests/TestResults/claude-full.trx`.
- Test change: `Scenario243ContinuousTests.cs` adds a `Template` helper and the 2025 PR #239 template block.

## Limits

- The clock-instrumentation limits in [issue-243-post-261-retest.md](issue-243-post-261-retest.md) still apply.
- Calendars, approvals and policy values are synthetic. No pre-July-2022 Maryland draft exists, so the 2020 period still uses hand-configured synthetic rules.
- Pregnancy and affected-property drafts were not activated in this run.
- Historical clinical-policy reconstruction remains unsupported, behind the existing safety guard.
