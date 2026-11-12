# Issue #267: test scenario 2 (EPA Region 3, Maryland and Virginia counties, security reports)

Latest result: [post-PR #281 retest, 2026-11-12](issue-267-post281-retest.md). The replay passes after correcting its early-morning receipt defect (#282). The report below records the original baseline and findings.

Baseline: merged main `fac72d7` (after PR #266), 2026-11-11. This work added a test and this document. No application code, schema, permission or production data changed. All data is synthetic and lives in dedicated `MERIT_SCENARIO_267_*` / `MERIT_AUDIT267_*` databases on `localhost\PORTAL`.

## Outcome

- **Most of the scenario works as written.** That covers the delegation chain, all 24 Maryland and 133 Virginia jurisdictions with their staff, Maryland configuration, profiles, the three county case/investigation workflows, Virginia isolation and the Region 3 profiles.
- **Five issues filed:**

| Issue | Severity | Finding |
|---|---|---|
| [#268](https://github.com/roscoe-sincero-sys/MERIT/issues/268) | P1, high | County-scoped intake clerks cannot import (HL7/CSV) or enter results. Lab results are owned by the data root, so receipt-quality tasks are blocked by row-level security. Manual entry returns 403 **after** the result is saved. |
| [#269](https://github.com/roscoe-sincero-sys/MERIT/issues/269) | P2, medium | The `permissionDenied` hold reason violates `CK_dbo_stagingHoldReason_reasonCode`, so the batch crashes and stays in `parsing` instead of holding the row. |
| [#270](https://github.com/roscoe-sincero-sys/MERIT/issues/270) | P2, medium, security | The audit report is scoped to the whole data root. A Harford-only administrator sees statewide events, including Baltimore City-only staff. Platform and region administrators see no events below them. Sign-in activity omits staff homed elsewhere who work Harford data. |
| [#271](https://github.com/roscoe-sincero-sys/MERIT/issues/271) | P2, medium | There is no delegable security-officer role. `security.investigate` exists only on the platform role, which cannot be granted to a county account. The 51422 refusal shows a bare 422 page. |
| [#272](https://github.com/roscoe-sincero-sys/MERIT/issues/272) | P3, low | No export for sign-in activity, the user/access list or per-account sign-in history. |

## How it was tested

`Scenario267RegionTests` (opt-in: `MERIT_IT_DATABASE=MERIT_SCENARIO_267_*`) runs the 23 steps with real HTTP pages for every account, tenant, grant, profile, configuration and record. SQL is used only to read identifiers and check outcomes. Business dates are the real clock; no clock instrumentation is used. One run creates 575 accounts.

| Step | Result |
|---|---|
| 1–2 | The platform administrator signs in with password and MFA. The fixture also holds the demo data roots; every tenant in the scenario is new. |
| 3 | The platform creates Region 3 and its administrator (administrator + supervisor at the region). |
| 4 | Region 3 creates DE, DC, MD, PA, VA and WV, each with an administrator. Maryland has 24 jurisdictions and Virginia 133. Maryland starts with no rules, active thresholds or import formats. |
| 5 | The Maryland administrator creates a health-department tenant and an administrator for each of the 24 Maryland jurisdictions. |
| 6–8 | The Baltimore City, Baltimore County and Harford County administrators each create a supervisor, case manager and intake clerk. |
| 9 | The Maryland administrator creates a supervisor, case manager and intake clerk who work in **both** Baltimore County and Harford County. |
| 10–11 | The Virginia administrator creates all 133 county/city tenants, an administrator for each, and a supervisor, case manager and intake clerk for each (532 accounts). |
| 12 | Maryland configuration; see the next section. |
| 13 | 26 extra profiles created through *My profiles*: read only and write for each Maryland account, plus Baltimore County-only and Harford-only profiles for the two-county staff. |
| 14 | **The two-county intake clerk cannot import or enter results (#268, #269).** A statewide Maryland intake clerk is used as a workaround: HL7 (Baltimore County) and CSV (Harford) imports route each case to its county, and the #202 reviewer notes and testing scenario behave as specified (below). The single-county profile opens only its own county's patient (Harford patient: 404). The read-only profile cannot open the new-patient form or create a patient. |
| 15–17 | In each of Baltimore County, Harford County and Baltimore City: the case routes to the county; the supervisor assigns it to the county case manager; the case manager logs a contact attempt and education, requests and schedules a residential investigation, records a dust sample and issues findings. The read-only profile cannot log activity. The intake clerk can view but not update the case. Another county's supervisor gets 404 on both the case and the investigation. The supervisor's closure attempt is correctly refused until the required notifications are completed or justified. |
| 18 | The Virginia state administrator, a county administrator, a case manager and a supervisor made 72 attempts on Maryland patients, cases, results, testing plans and investigations, by direct link and through search, case/investigation lists, the testing roster and its export. **None returned Maryland data.** Searches say "No matches in your access scope". |
| 19 | Region 3 profiles: a read-only profile cannot create an account or a state; a full "accounts only" profile created a Delaware user administrator. |
| 20–21 | Region 3 and the platform ran the audit, audit export, sign-in activity, unknown names (platform only) and its export, usage report and export (Region 3 only), and the user list. See #270 and #272. |
| 22 | The platform role cannot be granted to the Harford account (422), and no Maryland role holds `security.investigate` (#271). The platform granted **User administrator** at Harford County only. |
| 23 | The Harford officer's user list is correctly limited to Harford and the two-county staff. **The audit export lists 17 users across Maryland, including Baltimore City-only staff (#270).** Sign-in activity shows only Harford-homed accounts. The unknown-names and usage reports are not available. |

## Maryland configuration (step 12) and the #202 decisions

Configured through the administrator pages:

- settings (`MD`, FIPS 24, adult cutoff 18, investigation look-back, contact attempts);
- a reporting laboratory;
- reference-level history as case thresholds: 5 ug/dL from 2020-07-01 to 2023-12-31, and 3.5 from 2024-01-01, for children under 6;
- all 11 shipped Maryland drafts:
  - § 6-304 child caregiver;
  - pregnant minor and adult;
  - residence owner, child and pregnancy;
  - § 6-846 affected-property owners, child and pregnancy;
  - COMAR 26.02.01.04 lab reporting, elevated and below-reference;
  - the July–October 2022 and October 2022–December 2023 historical child windows;
- HL7 and CSV import formats.

A 7.5 ug/dL Harford child result created review-gated caregiver, residence-owner and affected-property-owner notices, each due 10 business days after receipt.

Verified against the reviewer notes:

- **Late laboratory report** (COMAR 26.02.01.04): an elevated CSV result received 12 days after its final-result date was recorded as a *completed* "Late laboratory report detected" task.
- **Missing information** (COMAR 26.02.01.03): an incomplete report got a *completed* "Missing information detected" task plus a *pending* "Missing Information Received" task.
- **Confirmatory testing, §2 scenario.** Results A, B and C are below the reference level, D is 3.6 capillary, E is below, and F is 5.0 capillary. A confirmation plan from F is refused ("earlier unresolved elevated capillary draw"). The plan from the initial draw D is due 12 weeks after D's draw.
- **Follow-up, §3.** Venous G, one month after D (4.2 ug/dL), anchors the early follow-up three months after G's draw.
- **Discharge, §4.** Discharge readiness is recorded after two consecutive venous results below 3.5, four months apart, with remediation attested.

Notes for the owner on step 12 ("update issue if anything is missing"):

- The clinical testing schedule is built in (MDH March 2025 tables, policy `MDH-2025-03`). The administrator does not configure it; it applies when the tenant or report state is MD. MERIT's windows sit inside the issue's ranges, at the stricter end:
  - 3.5–9.9: 12 weeks (the issue says 1–3 months);
  - 10–19: 4 weeks (1–2 weeks, or up to a month);
  - 20–44: 2 weeks;
  - 45 and above: same day, marked urgent.
- After the confirming venous G arrives, the confirmation plan stays **open** until staff resolve it on the testing page. It is not closed automatically.
- Not exercised here:
  - the two-capillary-within-84-days EBL route;
  - the COMAR 26.16.08.05 step to contact the family to schedule the environmental investigation;
  - the "CDC revision applies one year later" rule (thresholds are entered by date).
- Certification (Form 4620) is not tracked, as the reviewer decided.

## Other observations (not filed)

- **The Region 3 account can open Maryland patient records.** The delegation guard requires an administrator to hold every right they grant (#243 design). To delegate supervisor, case-manager and intake roles downward, the region account was given supervisor at the region, which covers every state below it. This is the existing design, but owners should know that delegating clinical roles from the region gives the region account clinical access everywhere. The platform is exempt from the guard and could grant directly instead.
- Patients imported by the statewide clerk are owned by the Maryland root; cases and investigations route to the county.
- The platform administrator has no usage report (`usage.view` belongs to the administrator and supervisor roles).

## Evidence

- Fresh-deploy replay: `sourceCode/.artifacts/audit-267-replay.log`, `TestResults/s267-final.trx`, `sourceCode/.artifacts/scenario-267-replay-evidence.txt`.
- Full suite on a separate fresh database (`MERIT_AUDIT267_CLAUDE`): `sourceCode/.artifacts/audit-267-full.log`, `TestResults/s267-full.trx`.
- Diagnostic runs: `sourceCode/.artifacts/s267-run*.log`. The #268 root cause was confirmed by temporarily instrumenting `dbo.uspReconcileReceiptQuality` in the throwaway test database only; the procedure was restored afterwards.

## Limits

- Step 14's two-county intake runs only after #268 is fixed; until then the replay records the defect and uses a statewide clerk. Once #268 is fixed, the replay switches back to the two-county clerk automatically.
- Accounts are created through the real pages, but most Virginia accounts and most of the 24 Maryland county administrators are not signed in. Signing in exercises MFA enrollment, already covered by the other accounts.
- Notifications were checked as created and review-gated, not completed, so case closure was verified only as correctly refused.
- This is not a usability study or a legal-compliance certification. #267 is left open for the owner.
