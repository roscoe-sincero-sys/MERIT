# Issue #243 retest after PR #261

Baseline: merged main `05879d5` (PR #261), 2026-11-09. **No new reproducible application defect was found in the workflows tested.** No new issues were created. This is a test/documentation audit; application code, schema, permissions and production data were not changed.

## Scenario results

| Requested workflow | Verified result |
|---|---|
| Fresh account hierarchy | Platform → Region 3 → Maryland → supervisor → operational accounts, including an LHD manager, succeeded through HTTP tenant/account forms, actual password login and MFA. Clinical delegation and account-administration authority remained explicit. |
| Initially unconfigured Maryland | Began without notification/program definitions or historical thresholds. Agency baseline, laboratory registration and HL7/CSV formats were configured through the application. |
| 2020 configuration and input | Published synthetic notification/case rules and program version 1. Clerk HL7 imports (both supported versions) and manual entry preserved source/receipt dates. Qualifying notification targets were August 17; the separate program milestone was due August 10. |
| County access | The 2020 imported case and program task were county-owned and accessible to the newly created LHD user. Separate integration cases verified patient-only work, owner review and mixed-profile denial. |
| 2025 configuration and input | Published non-overlapping program version 2 and 2025 notification/case rules in the same tenant. Qualifying CSV/manual input generated March 17 notification targets and March 8 program targets. Original 2020 snapshots/deadlines remained unchanged. |
| Upcoming testing and contacts | A reviewed early follow-up plan appeared in the seven-day roster and CSV with verified caregiver phone/email/address. Epidemiologist export excluded contact details. Advancing the business date made the plan overdue. |
| Individual patient summary | Target/type and upcoming/overdue status appeared in the patient overview and testing workspace. |
| Caregiver address corrections | Merged regressions cover normalized unit changes, unchanged-unit rejection, explicit removal, unit-less residences, legacy/unspecified intent, tampered form keys, owner authorization, retained history and the resulting outreach CSV. |
| Browser forms | All three unit instructions, validation feedback, county submission, owner review/history and program routing controls passed in Edge. Axe and overflow checks passed at 1280, 390 and 320 pixels. |

## Evidence

- Expanded fresh-tenant replay: passed on new `MERIT_SCENARIO_243_POST261_20261109`, with actual HTTP/MFA/worker execution. The replay includes the previously retained local test expansion for program publication and historical snapshot checks; the application baseline is merged main.
- Unit/architecture suite: **333 passed; five optional local-mail tests skipped**.
- Browser suite: **five passed** against merged code and isolated `MERIT_IT`; outgoing mail was disabled and the temporary server was stopped afterward.
- Independent full integration suite: **205 passed, zero failed, one intentionally skipped opt-in historical replay**. See `sourceCode/.artifacts/audit-243-post261-regressions.log` and `sourceCode/tests/Merit.IntegrationTests/TestResults/post261-regressions.trx`.
- Replay log/TRX: `sourceCode/.artifacts/audit-243-post261-replay.log`, `sourceCode/tests/Merit.IntegrationTests/TestResults/post261-expanded-replay.trx`.
- Unit/browser logs: `sourceCode/.artifacts/audit-243-post261-unit.log`, `sourceCode/.artifacts/audit-243-post261-browser.log`.

## Limits

The replay temporarily instruments only application business dates and the isolated database's `auth.udfSessionToday`; authentication/audit wall time and other direct SQL wall-clock reads remain real. The original SQL function is restored and replay scopes are revoked afterward. No OS clock or production database was changed.

Thresholds, ten-business-day notifications, program milestones, approval strings and the weekday calendar are explicitly synthetic fixtures. This establishes the selected engine/workflow behavior, not complete approved historical Maryland clinical/legal policy or every pregnancy, property-owner, laboratory-reporting and holiday condition in one continuous tenant. Additional evidence, closure, recurrence, access and boundary cases are covered separately by the integration suite.

Historical clinical-policy reconstruction remains unsupported. The existing safe guard against silently applying 2025 guidance to a 2020 draw remains in place; this is the previously documented alternative, not a newly discovered defect. The audit does not claim a new browser video, unmoderated usability study or historical compliance certification. Existing unrelated local changes were preserved. #243 has not been automatically closed.
