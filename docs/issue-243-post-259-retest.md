# Issue #243 retest after PR #259

This is the pre-fix audit record. The implementation of #260 is documented in [caregiver-unit-corrections-260.md](caregiver-unit-corrections-260.md).

Baseline: merged main `64da701`, 2026-11-09. This audit changes only tests/documentation; no application implementation, permission policy or production configuration was changed.

## Outcome

The original fresh-tenant replay passes. The expanded replay also passes after adding actual HTTP publication of separate synthetic program-task versions for 2020 and 2025. County routing and the supported caregiver-correction handoff are implemented; a remaining address-completeness enhancement is logged as [#260](https://github.com/roscoe-sincero-sys/MERIT/issues/260).

| Requested workflow | Retest result |
|---|---|
| Platform → Region 3 → Maryland → supervisor → operational users | Passed through real HTTP tenant/account forms, password login and MFA. Clinical roles and supervisor account-administration authority were explicitly delegated; least-privilege guards remained active. |
| Initially unconfigured Maryland | Passed: no starting notification/program definitions or historical thresholds. The administrator selected a baseline, registered the laboratory and configured HL7/CSV import formats through the application. |
| 2020 thresholds, notifications and program tasks | Published a synthetic case/notification policy and a separate version-1 program definition through the configuration/preview/publication forms. Samples for publication were created and supplied with results through patient/result forms. |
| 2020 HL7/manual input | Both HL7 versions ingested PID-3 as the clerk. Selected qualifying receipt on August 3 generated notification targets of August 17. Program work generated from the imported result was county-owned, accessible to the new LHD manager, and due August 10 under its separate seven-calendar-day policy. Below-threshold manual input did not trigger the selected case/notification rules. |
| Move to 2025/update rules | Published version 2 of the same named program with a non-overlapping effective period and changed threshold/interval. Separate 2025 notification/case rules were configured in the same Maryland tenant. |
| 2025 CSV/manual input | Both qualifying inputs generated notification targets of March 17 and program-task targets of March 8 under the selected five-calendar-day definition. The original 2020 notification and program snapshots and deadlines remained unchanged. |
| Upcoming test/contact/export list | Passed for the reviewed early follow-up plan: seven-day roster and CSV included verified caregiver phone/email/mailing address. Epidemiologist export omitted those contacts. Advancing the business date made the plan overdue. |
| Individual patient target/type/status | Passed in patient overview and the clinical testing workspace. |
| County patient-only work and contact correction | The merged regression suite exercises serving-jurisdiction/state/legacy routing, county completion, mixed-profile denials, correction requests and owner application of phone/email/address with history. The new apartment-specific diagnostic identifies #260. |

## New finding

[#260](https://github.com/roscoe-sincero-sys/MERIT/issues/260), medium/P2 enhancement: caregiver address-correction requests have no structured apartment/unit field, although residence storage and outreach exports include a unit. A same-building unit change can be described only in free-text evidence, and the applied-address guard compares street/city/state/ZIP without checking the requested unit. The owning team can still manually correct the unit through existing residence forms. The diagnostic intentionally probes the server guard; it does not show an automatic incorrect edit or endorse skipping owner source review.

The issue contains HTTP reproduction, source links, impact, recommendations and acceptance cases for unit changes/removal, unit-less and legacy requests, and outreach export. Existing privacy boundaries should remain unchanged.

## Evidence and limits

- Full merged integration suite: **199 passed, zero failed, one intentionally skipped opt-in replay**. Log `sourceCode/.artifacts/audit-243-post259-regression.log`; TRX `sourceCode/tests/Merit.IntegrationTests/TestResults/post259-regression.trx`.
- Original replay: `sourceCode/.artifacts/audit-243-post259-continuous.log` and `TestResults/post259-continuous.trx` — passed on a fresh isolated database.
- Expanded replay: `sourceCode/.artifacts/audit-243-post259-expanded.log` and `TestResults/post259-expanded.trx` — passed; includes program-version publication and frozen targets across both periods.
- Address diagnostic: `sourceCode/.artifacts/audit-243-post259-address.log` and `TestResults/post259-address-diagnostic.trx` — reproduced #260. Its passing assertion records the limitation, not a passing product acceptance criterion.
- The replay uses `MERIT_SCENARIO_243_POST259_20261109`; the independent integration database is `MERIT_AUDIT243_POST259_20261109`, and the address diagnostic uses `MERIT_AUDIT243_CONTACT_POST259`. All are isolated synthetic databases. The diagnostic cleans up its synthetic contact/address rows; replay tenant data remains for inspection.
- The replay restores the original SQL business-date function and signs out/revokes its scopes. RLS remained enabled and the date function was verified restored. Authentication/audit wall time and direct SQL wall-clock paths are not transformed into universal historical simulation.
- The policies, approvals and weekday calendar are synthetic test fixtures. These checks establish the selected engine/workflow behavior, not complete approved Maryland historical clinical/legal policy or all pregnancy, property-owner, reporting and holiday requirements. The existing guard against silently applying 2025 clinical guidance to a historical draw remains correct; historical clinical-policy reconstruction is still unsupported.
- The new tests use real HTTP handlers and workers; this turn did not create a new browser video or unmoderated usability study. Unrelated local files were preserved.

The selected core scenario passes. #260 is a remaining bounded follow-up; #243 remains open and is not being represented as certification of complete historical Maryland policy.
