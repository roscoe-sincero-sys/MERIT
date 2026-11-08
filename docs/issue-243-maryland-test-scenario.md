# Issue #243: Maryland workflow scenario

This is the original pre-fix audit snapshot. For the subsequent #244–#247 implementation and its supported alternatives, see [Clinical testing outreach and verified historical receipts](testing-outreach-and-historical-receipts.md).

Tested 2026-11-08 against merged main `b9a6844` (PR #242). **The complete requested scenario does not pass.** Supported paths were exercised; four gaps were reproduced and filed. No application, schema, permission-policy or production configuration changes were made. Changes in this branch are diagnostic tests and this report.

Parent: [#243](https://github.com/roscoe-sincero-sys/MERIT/issues/243).

## Findings requiring work

| Issue | Priority | Confirmed problem |
|---|---|---|
| [#244](https://github.com/roscoe-sincero-sys/MERIT/issues/244) | P1 / High | A 2020 draw can acquire a March 2025 clinical plan without a historical-policy applicability decision. Clinical intervals and eligibility are not configurable by policy period. |
| [#245](https://github.com/roscoe-sincero-sys/MERIT/issues/245) | P1 / High for historical imports | HL7/CSV imports retain collection dates but use upload receipt. There is no verified original-Department-receipt workflow for historical or delayed imports. |
| [#246](https://github.com/roscoe-sincero-sys/MERIT/issues/246) | P1 / High | No clinical testing outreach roster combines due-within-seven-days filtering, caregiver phone/email/mailing address and a scoped export. Existing work queues provide a partial foundation. |
| [#247](https://github.com/roscoe-sincero-sys/MERIT/issues/247) | P2 / Medium | The patient overview omits the next test target/type. The testing workspace shows overdue text but has no explicit seven-day warning. |

Each issue contains reproduction, source evidence, user impact, recommendations and acceptance criteria. The import issue distinguishes newly received uploads from historical migrations; it does not recommend substituting report or collection dates for receipt.

## Method and limits

- Used the isolated `MERIT_IT` database, actual web-login SQL procedures/RLS scopes, HTTP form handlers, and real intake/job services. No external notifications were sent.
- Delegated account creation was tested through authorization procedures, **not** a complete first-login/password/MFA browser journey. Existing user-creation HTTP tests ran separately. The new accounts' password hashes are test placeholders, so these accounts are not evidence of successful interactive login.
- Created fresh synthetic Region 3/Maryland roots for delegation. Historical intake/manual-entry checks used the existing configured test root and synthetic patients/profiles/rules. **This is not one continuous fresh-tenant end-to-end run.** Remaining fresh-tenant settings, sender onboarding and rule migration are not certified by these checks.
- The process and SQL clocks remained November 2026. Explicit historical collection/receipt inputs exercised date calculations. **No claim is made that the application clock actually advanced from 2020 to 2025.** Direct `DateTime.UtcNow` calls and SQL `auth.udfSessionToday()` mean replacing the application `TimeProvider` alone would not constitute a full clock replay.
- August 2020 and March 2025 were selected, avoiding ambiguity at the July 2020 case-management change. Clinical and legal/notification policies are distinct.
- Case threshold examples were 5 ug/dL for August 2020 and 3.5 for March 2025. The ten-business-day caregiver notification rule and weekday-only calendars were explicitly labelled **synthetic engine-test policies**, not historical program-owner approval or certification of Maryland legal compliance. Complete historical recipient, holiday, pregnancy, housing and reporting requirements remain subject to program review.
- Synthetic authorization tenants/accounts and intake fixtures remain in `MERIT_IT` following existing scenario-test conventions. New manual-entry rule/patient fixtures are cleaned up. No production database or operating-system clock was changed.

## Scenario results

| Requested step | Result | Evidence / qualification |
|---|---|---|
| Platform creates Region 3 admin | Pass at procedure layer | Created a fresh region and account using platform scope, then created the regional session. |
| Region admin creates Maryland admin with appropriate rights | Pass at procedure layer | Created a Maryland state root beneath that region, refreshed the regional scope and created/granted the MD administrator. |
| Maryland admin creates supervisor | Pass with explicit delegation | An administrator-only account correctly failed to grant clinical supervisor rights it lacked. Platform then granted supervisor rights to the regional administrator; those rights were explicitly delegated to MD admin and the new MD supervisor. |
| Supervisor creates lower-role workflow accounts | Pass with explicit additional role | Supervisor alone correctly received SQL permission error 51403 for account creation. MD admin granted `userAdministrator`; the refreshed supervisor created intake clerk, case manager, investigator and epidemiologist accounts and their scopes. |
| Maryland initially unconfigured | Partial | A fresh Maryland root had **six active shipped thresholds, zero notification rules and zero workflow definitions**. Shipped thresholds start 2026-01-01; a literal zero-threshold tenant is not the installer default. Historical 2020/2025 thresholds were created separately in the configured test root. |
| Administrator configures historical thresholds/notifications | Partial | Actual HTTP configuration forms accepted separate 2020 and 2025 case thresholds and synthetic effective-dated receipt notification rules. Historical clinical rules cannot be selected/configured: #244. This did not test changing every rule on the fresh root through the UI. |
| 2020 HL7 import | Partial | Real upload/worker loaded the result and preserved collection 2020-08-01. Receipt was November 2026 upload time, so a historical receipt-policy task cannot be certified from this import: #245. |
| 2020 manual entry | Pass for selected rules | Actual result-entry HTTP form preserved collection 2020-08-01 and explicit Department receipt 2020-08-03 10:00 Eastern. At 6 ug/dL it opened a case and created an open caregiver notification task due 2020-08-17 under the synthetic weekday calendar. At 4 ug/dL neither that case threshold nor that notification rule qualified. |
| Advance clock/update to 2025 | Partial / not a literal clock replay | Separate 2025 effective-dated rules were configured and exercised. The OS/SQL clock was not moved, and a continuous migration preserving all original 2020 obligations was not performed. |
| 2025 CSV import | Partial | Real upload/worker preserved collection 2025-03-01. Receipt was November 2026 upload time: #245. |
| 2025 manual entry | Pass for selected rules | Explicit receipt 2025-03-03 10:00 Eastern yielded an open caregiver notification task due 2025-03-17. Both 4 and 6 ug/dL opened a case and qualified under the selected 3.5 threshold. |
| Historical clinical testing | Fail | A capillary result of 4 collected 2020-08-01 was accepted for confirmation with target 2020-10-24 and snapshot `MDH-2025-03`: #244. This diagnostic records the defect; it does not endorse the target for 2020. |
| Population upcoming-testing/contact/export screen | Fail as requested | Existing task API exposes targets and overdue status. Queue controls/contracts lack a seven-day testing roster and caregiver contact/export combination: #246. |
| Individual patient next test/type/status | Partial | The separate testing page exposes target/type and overdue status. Patient overview lacks the target, and a plan due today + 3 has no explicit seven-day warning: #247. |

The administrative failures above are intended least-privilege protections, not defects to remove. The correct role combination must be granted by someone already authorized to delegate those rights.

## Validation and reproduction

New local diagnostic files:

- `sourceCode/tests/Merit.IntegrationTests/Scenario243HierarchyTests.cs`
- `sourceCode/tests/Merit.IntegrationTests/Scenario243IntakeTests.cs`
- `sourceCode/tests/Merit.IntegrationTests/Scenario243CareTests.cs`

Existing authorization and HL7 test classes were made `partial` solely to reuse their established test harnesses. No product code changed. Some new diagnostics intentionally assert an observed gap; a passing diagnostic is **not** a passing product requirement.

Run only the nine diagnostic cases:

```powershell
$env:MERIT_IT_SKIP_DEPLOY = '1' # Only when MERIT_IT is already deployed to current main.
dotnet test sourceCode/tests/Merit.IntegrationTests/Merit.IntegrationTests.csproj `
  --artifacts-path sourceCode/.artifacts/scenario-243 `
  -p:SkipAngularBuild=true -p:RestoreIgnoreFailedSources=true `
  --filter 'FullyQualifiedName~Scenario243'
```

Nine diagnostic cases passed. The original focused baseline passed 60 tests covering authorization, user creation, HL7 ingestion, notification/receipt/document care, clinical testing and configurable workflow behavior. The final combined run passed **69 tests, zero failures, zero skips** in 69 seconds. TRX evidence is in `sourceCode/.artifacts/scenario-243/results/final.trx`; the command/log is `sourceCode/.artifacts/scenario-243-final.log`. SQL verification found zero remaining synthetic scenario threshold/notification rules after cleanup. `git diff --check` passed.

## Primary-source review

- [MDE 2020 surveillance report, pages 10–11](https://mde.maryland.gov/programs/LAND/LeadPoisoningPrevention/SiteAssets/Pages/healthcare/2020%20Annual%20Report%2C%20Childhood%20Blood%20Lead%20Surveillance%20in%20Maryland.pdf): records the July 2020 transition to case management at 5 ug/dL and historical reporting requirements. A year alone is insufficient to identify the applicable rule.
- [MDH June 2012 provider letter](https://health.maryland.gov/phpa/OEHFP/EH/Shared%20Documents/Lead/LeadProviderLetterJune2012.pdf): documents an older clinical reference and repeat-testing recommendation. It is evidence of policy evolution, not a complete 2020 policy specification.
- [MDH March 2025 clinical guidelines](https://mde.maryland.gov/programs/land/LeadPoisoningPrevention/Documents/Reports/MDH%202025%20Maryland%20Guidelines%20for%20Lead%20Exposure.pdf): the newer clinical confirmation/follow-up schedules cited by the implemented testing workspace.
- Repository interpretation and known approval limitations: `docs/pr-239-maryland-review.md` and `docs/program-workflows-and-closure-review.md`.

Keep #243 open. After the four findings are addressed and the program owner approves historical configurations/calendars, rerun a continuous scenario on a fresh dedicated database with one controlled clock shared by application, worker and SQL, real account login/MFA, configuration changes, both import formats, manual entry, historical obligation preservation and the new roster/export checks.
