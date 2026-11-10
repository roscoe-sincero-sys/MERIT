# Issue #243: retest after PR #264

Baseline: merged main `5c0dee1` (PRs #263 and #264), 2026-11-09. This audit changed only tests and documentation. No application code, schema, permission or production data changed.

## Outcome

**No new reproducible defect was found. No new issues were created.**

- The #262 fix holds in the continuous replay: manual entry no longer creates false missing-information findings.
- This retest also covers the remaining parts of the scenario that earlier replays had not exercised.

## Results

| Check | Result |
|---|---|
| Fresh deployment (`-IncludeDemoData`) and continuous replay (`MERIT_SCENARIO_243_POST264_CLAUDE`) | Passed. Covers the account hierarchy with MFA, unconfigured Maryland, 2020 HL7/manual input, 2025 CSV/manual input, program versions, LHD access, the PR #239 child/owner/lab-reporting drafts, the #262 manual-completeness behaviour, the roster/export and the patient overview. |
| **New:** automatic clinical testing review | Each 2025 child result (two CSV imports, two manual entries) opened exactly one Maryland testing review. The `/testing` roster lists it as "Clinical review required", without inventing a target date. |
| **New:** ongoing follow-up retesting schedule | A clinician-confirmed later-phase plan for the CSV-imported patient appears in the seven-day roster and the patient overview. Both show the type "Ongoing follow-up after reviewed decline" and "Due within 7 days". The earlier replay already covered the early follow-up plan, its overdue transition and the CSV contacts. |
| **New:** pregnancy draft (`mdPregnantAdult`) | A pregnancy episode recorded through the patient form made a 2025 adult result population `pregnant`. A 4 ug/dL result created a review-gated **patient** notice due 2025-03-17, and no child notice. |
| **New:** affected-property draft (`mdAffectedChild`) | A qualifying child result created a review-gated **owner** notice due 2025-03-17, alongside the separate residence-owner notice. |
| Full integration suite on a separate fresh database (`MERIT_AUDIT243_POST264_CLAUDE`) | **209 passed, 0 failed, 1 opt-in replay skipped.** |

## Evidence

- Baseline replay: `sourceCode/.artifacts/audit-243-post264-replay.log`, `TestResults/post264-replay.trx`.
- Extended replay: `sourceCode/.artifacts/audit-243-post264-extended.log`, `TestResults/post264-extended.trx`, `sourceCode/.artifacts/scenario-243-replay-evidence.txt`.
- Full suite: `sourceCode/.artifacts/audit-243-post264-full.log`, `TestResults/post264-full.trx`.

## Limits

- The clock-instrumentation limits in [issue-243-post-261-retest.md](issue-243-post-261-retest.md) still apply.
- Thresholds, calendars and approvals are synthetic. There is no Maryland draft before July 2022, so 2020 uses hand-configured synthetic rules.
- Owner and pregnancy notices are verified only as created and review-gated. Completing them with recipient, ownership and form evidence is covered by the existing integration tests, not by this replay.
- Historical clinical-policy reconstruction remains unsupported, behind the existing safety guard.
- This audit is not a usability study or a legal-compliance certification. #243 is left open for the owner to decide.
