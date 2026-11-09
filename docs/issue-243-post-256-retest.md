# Issue #243 retest after PR #256

This is the pre-fix audit record. The implementation of its two findings is described in [county-work-and-contact-corrections.md](county-work-and-contact-corrections.md).

Tested merged main `a021230` on 2026-11-09 UTC (2026-11-08 Eastern). The existing fresh-tenant replay passes, but two additional county workflows remain incomplete. Keep #243 open.

## New findings

| Issue | Impact | Verified result |
|---|---|---|
| [#257](https://github.com/roscoe-sincero-sys/MERIT/issues/257) | High / P1 | For a state-owned patient with a current county primary residence, state-published patient-review work stays state-owned. The county manager opens the patient (200), cannot see the task on the checklist, and receives 404 on its task page. Creating patient-only ad-hoc work as that manager returns 403. Clinical testing-plan routing fixed by #252 does not cover these general work paths. |
| [#258](https://github.com/roscoe-sincero-sys/MERIT/issues/258) | Medium / P2 enhancement | A county manager can read an imported caregiver's contacts but cannot amend them, and no tracked correction-request/handoff is offered. Direct contact writes are correctly denied by the privacy boundary. Add contact maintenance or a correction request without opening the caregiver's clinical chart. |

Both issues contain reproduction, source links, user impact, recommendations and acceptance criteria. #258 distinguishes an intentional security denial from the missing operational handoff.

## Scenario verification

| Requested workflow | Result |
|---|---|
| Fresh platform → Region 3 → Maryland → supervisor → operational users | Passed through actual HTTP account/tenant forms, password login and MFA. Clinical delegation and separate supervisor account-administration authority remain explicit. |
| Initially unconfigured Maryland | Passed: no notification rules, custom program definitions or historical thresholds; administrator selects the baseline and configures formats through the application. |
| 2020 HL7/manual results and notification dates | Passed for selected synthetic policies. Both supported HL7 versions ingest identifiers as the clerk; caregiver contact information survives import. Qualifying receipt on August 3 creates the August 17 target. Below-threshold manual result creates neither selected case nor notification. |
| Change to 2025 rules and CSV/manual entry | Passed in the same fresh Maryland tenant. Qualifying receipt on March 3 creates the March 17 target; earlier 2020 obligation snapshot/date remains unchanged. |
| County case access and clinical work | Case routing/access and patient-only clinical-plan/privacy regression passed. General patient-only program/ad-hoc work has the separate #257 failure. |
| Upcoming test list, contact details and export | Selected clinical plan appears in seven-day roster and CSV with verified caregiver phone/email/address. Epidemiologist export excludes those contacts. Advancing the business date makes the plan overdue. Contact correction is limited by #258. |
| Patient next-test type/target and status | Passed for the selected plan in patient overview and testing workspace. |
| Historical clinical-policy reconstruction | Still unsupported; the existing guard rejects silently using the 2025 policy for a 2020 draw. This is the documented safe alternative from #244, not a newly discovered bypass. |

## Validation and limits

- Entire merged integration suite on fresh `MERIT_AUDIT_243_20261109`: **192 passed, zero failed, one intentionally skipped opt-in replay**.
- Continuous replay on fresh `MERIT_SCENARIO_243_AUDIT_20261109`: **one passed**, including fresh installation and real worker execution.
- Added program-routing diagnostic plus existing caregiver privacy diagnostic: **two passed**. After adding the ad-hoc creation check, the program-routing diagnostic passed again. The new diagnostic asserts observed failures; its passing status does not certify the feature.
- No application, schema, permission-policy or production data changes were made. The only new source file is the audit diagnostic `Scenario243PostMergeAuditTests.cs`; this report records the outcome. Unrelated local changes were preserved.
- Local evidence: `.artifacts/issue-243-audit-continuous.log`, `.artifacts/issue-243-audit-regression.log`, `.artifacts/issue-243-audit-diagnostics.log`, `.artifacts/issue-243-audit-program-routing.log`, and integration `TestResults/{continuous,regression,post-merge-diagnostics,program-routing}.trx` under `sourceCode`.
- The replay replaces application business dates and only the isolated database's `auth.udfSessionToday`, restoring the SQL function afterward. Authentication/audit wall time and other direct SQL wall-clock reads remain real. It is not universal historical scheduler simulation.
- The replay's thresholds, ten-business-day caregiver policy and weekday calendar are explicitly synthetic. It does not certify complete approved Maryland historical clinical/legal policy, holiday calendars, every pregnancy/owner/laboratory-reporting condition, or all program milestones in one fresh-tenant run. Separate full-suite tests exercise additional workflow, evidence, closure and receipt paths. No program-owner approval is fabricated.

The previous comment claiming that no blockers remain was broader than the available coverage. The selected core replay passes; #257 and #258 remain actionable follow-up work.
