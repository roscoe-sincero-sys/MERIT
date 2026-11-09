# Issue #243: Maryland workflow scenario

Parent: [#243](https://github.com/roscoe-sincero-sys/MERIT/issues/243).

The post-#248 audit below records the original defects. Their implementation and supported alternatives are described in [fresh-tenant-249-255.md](fresh-tenant-249-255.md).

## Retest Status (2026-11-08 post-PR #248)

PR #248 merged into `main` (`f14ce60`), resolving findings [#244](https://github.com/roscoe-sincero-sys/MERIT/issues/244), [#245](https://github.com/roscoe-sincero-sys/MERIT/issues/245), [#246](https://github.com/roscoe-sincero-sys/MERIT/issues/246), and [#247](https://github.com/roscoe-sincero-sys/MERIT/issues/247):
- **#244:** Explicit clinician current-care decisions (`MDH-2025-03` policy purpose) are enforced before saving clinical testing plans for historical draws.
- **#245:** Verified original Department receipt workflow (`historical: true`, `departmentReceivedAt`, `receiptEvidence`, `receiptConfirmed`) allows both HL7 and CSV imports to preserve draw date, record verified Department receipt in agency local time, and immediately trigger effective-dated notification policies without altering actual upload time.
- **#246:** Clinical testing outreach workspace (`/testing`) and scoped CSV export (`/testing.csv`) provide due-in-7-days/overdue filtering, verified caregiver contact details (phone, email, primary residence mailing address), formula neutralization, and export auditing.
- **#247:** Patient overview shows upcoming testing target/type direct links, and the testing workspace displays explicit overdue and "Due within 7 days" status badges.

### Post-#248 Findings on Fresh Tenant Hierarchy Execution

Executing the complete continuous scenario on a freshly created `Region 3 -> Maryland -> Local Health Department` hierarchy uncovered five new concrete problems blocking end-to-end operation on fresh tenants:

| Issue | Priority | Confirmed problem |
|---|---|---|
| [#249](https://github.com/roscoe-sincero-sys/MERIT/issues/249) | P1 / High | Tenant creation does not refresh the creator's active session scope (`auth.sessionScopeAdminTenant`), blocking immediate administration; newly created root tenants omit `workflow.waive` on the `supervisor` role; and operational role delegation by tenant administrators requires an architectural decision. |
| [#250](https://github.com/roscoe-sincero-sys/MERIT/issues/250) | P1 / High | Fresh root tenants have zero laboratory intake profiles, and there is no UI, controller, or application service path to invoke `dbo.uspSaveIntakeProfile` (`intake.configure`), leaving `/intake` disabled. |
| [#251](https://github.com/roscoe-sincero-sys/MERIT/issues/251) | P2 / Medium | `config.uspCreateRootTenant` hardcodes `@effectiveFrom = '2026-01-01'` for all 14 settings and 6 thresholds, leaving 2020 and 2025 empty and blocking open-ended earlier thresholds with overlap error `51422`. |
| [#252](https://github.com/roscoe-sincero-sys/MERIT/issues/252) | P1 / High | `dbo.uspOpenExposureCase` filters on `t.tenantType = 'localHealthDepartment'` (which violates check constraint `'jurisdiction'`), routing all county cases to the state root; LHD users hit RLS error 33504 on patient creation; and caregiver access is not granted to LHDs by `dbo.uspRefreshPersonAccess`. |
| [#253](https://github.com/roscoe-sincero-sys/MERIT/issues/253) | P2 / Medium | Manual result entry does not include `"state": "MD"` in `reportedSnapshotJson`, preventing `dbo.uspReconcileTestingReview` from generating Maryland testingReview tasks; and CSV/HL7 intake drops caregiver phone/address records, creating unverified relationships with no in-place UI verification option. |

---

## Original Pre-Fix Findings (Addressed by PR #248)

| Issue | Priority | Confirmed problem |
|---|---|---|
| [#244](https://github.com/roscoe-sincero-sys/MERIT/issues/244) | P1 / High | A 2020 draw can acquire a March 2025 clinical plan without a historical-policy applicability decision. Clinical intervals and eligibility are not configurable by policy period. (Resolved by PR #248) |
| [#245](https://github.com/roscoe-sincero-sys/MERIT/issues/245) | P1 / High for historical imports | HL7/CSV imports retain collection dates but use upload receipt. There is no verified original-Department-receipt workflow for historical or delayed imports. (Resolved by PR #248) |
| [#246](https://github.com/roscoe-sincero-sys/MERIT/issues/246) | P1 / High | No clinical testing outreach roster combines due-within-seven-days filtering, caregiver phone/email/mailing address and a scoped export. Existing work queues provide a partial foundation. (Resolved by PR #248) |
| [#247](https://github.com/roscoe-sincero-sys/MERIT/issues/247) | P2 / Medium | The patient overview omits the next test target/type. The testing workspace shows overdue text but has no explicit seven-day warning. (Resolved by PR #248) |

---

## Method and Limits

- Tested against the `MERIT_IT` database on `localhost\PORTAL` using real web-login SQL procedures, RLS session scopes, and HTTP form endpoints.
- Re-verified all PR #248 improvements: verified historical receipt batch upload, March 2025 clinical current-care decision requirement, `/testing` and `/testing.csv` outreach rosters, and patient overview next-test warnings.
- Extended the `Scenario243*Tests.cs` suite to exercise the continuous fresh-tenant hierarchy and confirm the five newly filed defect areas.
- All 12 integration tests in the Scenario 243 suite passed (12 passed, 0 failed in 11s), and the 82-test regression suite across `AuthorizationScenarioTests`, `Hl7IntakeTests`, and `PatientCareTests` passed cleanly (82 passed, 0 failed in 77s).

## Scenario Step Summary (Post-#248 Retest)

| Requested Step | Result | Notes / Finding |
|---|---|---|
| Platform creates Region 3 admin | Pass | Created via `config.uspCreateRootTenant`. |
| Region admin creates Maryland admin | Pass with re-scope | `POST /admin/tenants/new` succeeds, but regional admin cannot manage MD until re-scoping: [#249](https://github.com/roscoe-sincero-sys/MERIT/issues/249). |
| Maryland admin creates supervisor | Pass with delegation | Administrator role alone cannot grant clinical supervisor rights. Delegated via platform -> region -> MD admin: [#249](https://github.com/roscoe-sincero-sys/MERIT/issues/249). Supervisor role lacks `workflow.waive`: [#249](https://github.com/roscoe-sincero-sys/MERIT/issues/249). |
| Maryland initially unconfigured | Partial | Fresh tenant has 6 thresholds and 14 settings stamped `effectiveFrom = '2026-01-01'`; zero thresholds/settings for 2020 or 2025: [#251](https://github.com/roscoe-sincero-sys/MERIT/issues/251). |
| Configure laboratory intake profiles | Blocked | Zero profiles exist; no UI/service workflow exists to configure them: [#250](https://github.com/roscoe-sincero-sys/MERIT/issues/250). |
| 2020 HL7 historical import | Pass | Verified receipt `2020-08-03 10:00 EST` generates 2020 obligation due `2020-08-17` (PR #248). |
| 2020 manual entry | Pass | Result preserved with collection `2020-08-01` and receipt `2020-08-03 10:00 EST`. |
| Advance clock/update to 2025 | Pass | 2025 rules configured; 2020 obligations preserved without reinterpretation. |
| 2025 CSV historical import | Pass with routing/caregiver gaps | 4 ug/dL venous result preserved with receipt `2025-03-03`. Case erroneously routed to state root instead of Baltimore County LHD: [#252](https://github.com/roscoe-sincero-sys/MERIT/issues/252). Caregiver phone dropped and relationship left unverified: [#253](https://github.com/roscoe-sincero-sys/MERIT/issues/253). |
| 2025 manual entry | Pass with review gap | Manual result entry omits `"state": "MD"`, so `dbo.uspReconcileTestingReview` does not create `testingReview` tasks: [#253](https://github.com/roscoe-sincero-sys/MERIT/issues/253). |
| Clinical testing outreach roster & CSV export | Pass | `/testing` and `/testing.csv` display filtered overdue/7-day rows with verified caregiver details and audit logging (PR #248). |
| Individual patient next-test overview & warnings | Pass | Target date and "Due within 7 days" / "Overdue" badges display consistently (PR #248). |

Keep #243 open pending resolution of [#249](https://github.com/roscoe-sincero-sys/MERIT/issues/249), [#250](https://github.com/roscoe-sincero-sys/MERIT/issues/250), [#251](https://github.com/roscoe-sincero-sys/MERIT/issues/251), [#252](https://github.com/roscoe-sincero-sys/MERIT/issues/252), and [#253](https://github.com/roscoe-sincero-sys/MERIT/issues/253).
