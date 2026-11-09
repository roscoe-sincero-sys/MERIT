# Issue #243: continuous replay after PR #248

This is the pre-fix audit record. The subsequent implementation and supported alternatives are documented in [fresh-tenant-249-255.md](fresh-tenant-249-255.md); the replay test now asserts the corrected application paths.

**The unaided scenario remains blocked.** This run verified the merged fixes and executed a conditional continuous replay using one freshly created Maryland tenant. Passing diagnostic tests below do not mean that an administrator or intake clerk can complete the entire requested workflow without the documented workarounds.

Baseline: merged main `f14ce60` (PR #248), 2026-11-08. Parent: [#243](https://github.com/roscoe-sincero-sys/MERIT/issues/243).

## Findings and qualifications

| Issue | Finding relevant to this run |
|---|---|
| [#249](https://github.com/roscoe-sincero-sys/MERIT/issues/249) | Fresh supervisor roles lack `workflow.waive`; delegation needs clear documentation. **Scope-refresh qualification:** actual HTTP tenant creation calls `RefreshScopeAsync`; immediate creation of accounts under the new state succeeded. A stale scope after a raw SQL procedure call does not demonstrate a web onboarding defect. The ordinary guard against granting rights the administrator lacks is intentional. |
| [#250](https://github.com/roscoe-sincero-sys/MERIT/issues/250) | Registering a laboratory leaves the fresh tenant with no intake profiles. There is no web/API editor for creating the sender format. The replay used the existing permission-checked profile procedure as an explicit downstream workaround. |
| [#251](https://github.com/roscoe-sincero-sys/MERIT/issues/251) | Shipped root settings/thresholds start in 2026. On the 2020 business date, the Settings page has no current setting edit forms. The replay submitted known setting keys to their existing HTTP handlers and configured bounded historical thresholds. |
| [#252](https://github.com/roscoe-sincero-sys/MERIT/issues/252) | Existing diagnostics reproduce incorrect state ownership/LHD routing and local record/contact access problems. The continuous run also tests an actual newly enrolled LHD case manager against the state-owned county case. |
| [#253](https://github.com/roscoe-sincero-sys/MERIT/issues/253) | Existing diagnostics reproduce missing manual-result clinical review and incomplete imported caregiver/contact handling. The continuous run creates and verifies a caregiver through actual patient forms to exercise the downstream roster without hiding this limitation. |
| [#254](https://github.com/roscoe-sincero-sys/MERIT/issues/254) | **New:** an entirely fresh `-IncludeDemoData` deployment fails when the generated demo attempts unsupported bulk completion of a confirmation task. The continuous run proceeded only after verifying that the base objects/bootstrap fixture records were installed; it did not turn the failed install into a claimed successful installation. |
| [#255](https://github.com/roscoe-sincero-sys/MERIT/issues/255) | **New:** the shipped intake-clerk role cannot ingest a normal HL7 PID-3 identifier. Actual worker execution held the row as `uncertainMatch` with a `person.view_identifiers` permission error. An explicitly marked supervisor re-send was used only to test downstream calculations. |

No product implementation, permission relaxation or production configuration change was made during this audit.

## Continuous run

1. Created a fresh Region 3 and Maryland state through `/admin/tenants`. Platform, regional administrator, Maryland administrator, supervisor, intake clerk, case manager, investigator and epidemiologist accounts were created through `/admin/users`. Each signed in using its real password and completed authenticator enrollment and recovery-code acknowledgement. Clinical rights were explicitly delegated down the administrator chain; the supervisor separately received user-administration authority.
2. Confirmed zero notification policies, zero custom workflow definitions and no effective 2020 thresholds in the new Maryland tenant. Observed the 2026-only defaults and absent 2020 setting forms.
3. Recorded Maryland settings through their existing authorized HTTP handlers. Registered the laboratory through the reporter form. Provisioned HL7 and CSV formats through the existing scoped profile procedure because the application has no editor.
4. Configured 2020 case/notification test rules through the real configuration forms: threshold 5 ug/dL, ten business days from Department receipt, an explicit synthetic weekday-only calendar. The clerk's valid HL7 import was held by the identifier permission failure; a supervisor re-send loaded it. The result and a separately entered manual 6 ug/dL result created cases and caregiver tasks due **2020-08-17**, from receipt **2020-08-03 10:00 Eastern**. A manual 4 ug/dL result created neither under those selected rules.
5. Advanced the controlled business date to March 2025, configured a separate 3.5 ug/dL case/notification version in the **same Maryland tenant**, then loaded CSV and entered another result manually as the clerk. Both 4 ug/dL results created cases and caregiver tasks due **2025-03-17**, from receipt **2025-03-03 10:00 Eastern**. The original 2020 obligation JSON snapshot and due date remained unchanged.
6. Confirmed that a 2020 draw cannot silently receive the 2025 clinical policy. The merged guard correctly rejects historical clinical reconstruction without a documented current-care decision. Approved historical clinical policy reconstruction is still outside the implemented alternative; this test does not claim it exists.
7. Used the case-manager account to create a caregiver, verify the relationship through an interview source, record phone/email and add a mailing residence through the real forms. Created a reviewed early follow-up plan targeted **2025-03-06**.
8. Verified the plan on the seven-day roster and patient overview, and verified caregiver phone/email/mailing address in filtered CSV. The epidemiologist export omitted those contact details. Advanced the business date to **2025-03-07** and verified overdue status in the roster and patient overview.

These are HTTP-driven form and worker checks, not a new mouse/keyboard browser recording. Existing related diagnostics separately cover LHD routing and imported caregiver gaps.

## Clock, data and legal limits

- The dedicated database is `MERIT_SCENARIO_243_REPLAY`. The test refuses ordinary `MERIT`/`MERIT_IT` names. Production was not used. Synthetic records are retained there for inspection.
- Test instrumentation replaces `IClock` for the replay host/worker and temporarily replaces **only** that database's `auth.udfSessionToday()`. The original SQL function is restored in `finally`, HTTP sessions are signed out, and tracked worker/service scopes are revoked. No operating-system clock is changed.
- Authentication, MFA, expiry and audit wall time remain real. Other business paths that read `SYSUTCDATETIME()` directly are not converted into a universal historical clock by this harness. Assertions above establish the selected receipt/collection-driven targets and testing status, not a complete historical replay of every scheduler, compliance-status calculation or legal workflow.
- The platform seed assignment is backdated only in the dedicated test database so bootstrap authority exists at the initial test date. All scenario accounts/grants thereafter use the application handlers. No clinician or program-owner approval is fabricated.
- The ten-day rule, weekday calendar and policy approval strings are labelled synthetic. There are no intervening holidays in the selected date examples. Pregnancy, property ownership, owner notices, laboratory reporting and the complete 2020 clinical policy are not certified by this run.
- Primary-source checks support the need for period-specific rules: [2019 HB 1233 third-reader text](https://mgaleg.maryland.gov/2019RS/bills/hb/hb1233T.pdf), [current Environment §6-304](https://mgaleg.maryland.gov/mgawebsite/laws/StatuteText?article=gen&section=6-304), and [MDE's 2020 surveillance report](https://mde.maryland.gov/programs/LAND/LeadPoisoningPrevention/SiteAssets/Pages/healthcare/2020%20Annual%20Report%2C%20Childhood%20Blood%20Lead%20Surveillance%20in%20Maryland.pdf). A bill reader or current statute alone is not a complete approved historical program configuration.

## Reproduction and evidence

Opt-in harness: `sourceCode/tests/Merit.IntegrationTests/Scenario243ContinuousTests.cs`.

```powershell
$env:MERIT_IT_DATABASE = 'MERIT_SCENARIO_243_REPLAY'
# First perform the normal isolated deployment; a fresh demo currently exposes #254.
# Set this only after checking that the isolated base/bootstrap fixture is present:
$env:MERIT_IT_SKIP_DEPLOY = '1'
dotnet test sourceCode/tests/Merit.IntegrationTests/Merit.IntegrationTests.csproj `
  --artifacts-path sourceCode/.artifacts/scenario-243-retest `
  -p:SkipAngularBuild=true -p:RestoreIgnoreFailedSources=true `
  --filter 'FullyQualifiedName~Scenario243ContinuousTests'
```

The conditional continuous diagnostic passed. The independently rerun 82-test authorization/intake/care regression suite on `MERIT_IT` passed with zero skips. Local evidence:

- `sourceCode/.artifacts/scenario-243-retest/results/clean-install-failure.trx`: original clean-install failure.
- `sourceCode/.artifacts/scenario-243-retest/results/continuous-final.trx`: continuous diagnostic and step output.
- `sourceCode/.artifacts/scenario-243-replay-evidence.txt`: readable step results.
- `sourceCode/.artifacts/scenario-243-retest/results/retest-regression.trx`: 82 passing regression tests.

Keep #243 open until the remaining onboarding/intake/LHD defects are resolved and the required historical program rules are approved. A successful conditional diagnostic is not a successful unaided workflow.
