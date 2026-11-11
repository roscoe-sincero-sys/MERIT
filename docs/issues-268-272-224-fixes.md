# Fixes for #268, #269, #270, #271, #272 and #224

Baseline: merged main `6f84170` (after PR #273), 2026-11-11. Each recommendation in the issues was checked against the code before it was implemented. Where a recommendation could not be applied as written, the alternative is stated below with the reason.

## #268: county-scoped intake (P1)

**Verified cause.** `dbo.uspSaveLabResult` stores every result as owned by the data root. Work derived from a result inherited that owner or other routed tenants without any check that the session can write there:

- `uspReconcileReceiptQuality` (receipt findings, missing information, lab-reporting review);
- `uspReconcileNotifications` (obligations with no case);
- `uspReconcileTestingReview` and `uspCreateWorkTask` (the patient's residence county);
- `uspOpenExposureCase` (the residence county, or the root).

The `dbo.workTask` block predicate then refused the insert for a county-scoped session.

**Fix.** A new function, `dbo.udfWritableWorkOwner(@root, @preferred)`, keeps the routed owner whenever the session can write it, so state-level sessions behave exactly as before. Otherwise it uses the session's acting tenant. The rule is the one `uspSaveTestingPlan` already used, now applied in all five procedures above. In `uspCreateWorkTask`, an explicitly requested owner or a case's owner is kept as given, so genuine permission errors still surface.

**Result ownership is unchanged.** Moving `labResult.owningTenantId` off the root would change which notification rules apply, because rules are matched with `tenantClosure` against the result owner. The issue's alternative of choosing a writable owner for the generated work was implemented instead.

**Case routing alternative.** If a county clerk records a result for a resident of a jurisdiction they cannot work in, the case can no longer open there (row-level security), and MERIT has no case-transfer feature. The case therefore opens in the clerk's county with a **"Case opened outside the residence jurisdiction"** follow-up task naming the residence jurisdiction. Before this fix, the row failed.

**Manual entry.** If follow-up processing still fails after the result is committed, `ResultService.SaveManualAsync` now returns the saved result with "Result saved, but follow-up processing did not complete: … Ask a supervisor or administrator to review it". It no longer returns a 403 while leaving the result on file. The follow-up steps are reconcilers and can be re-run.

## #269: batch crash on a `permissionDenied` hold

- `CK_dbo_stagingHoldReason_reasonCode` converges to allow `permissionDenied`, using the same drop-and-recheck pattern as `CK_dbo_intakeProfile_fileType`. The held-records label is "Needs an account with permission".
- `IntakeProcessor.ProcessAsync` marks the batch **failed**, with the row count and reason, if a row cannot even be held. It no longer leaves the batch in `parsing`. Rows already loaded or held are kept, and the intake screen shows the failed badge and reason.

## #270: audit scope

- `logs.uspSearchAuditEvents` and `logs.uspListAuditUsers` use `auth.tvfReviewTenants('audit.view')`: the tenants the session administers, plus those where it holds `audit.view`, clipped by profile and view-as as `tvfSessionWritePermission` clips writes.
- **Whole data root:** visible only when the caller reviews the root's top tenant. That covers state administrators, as before, and now platform and region administrators for the roots below them. Audit rows are metadata only.
- **Elsewhere:** events by users homed or granted (now or before) in the reviewed tenants, plus events by **anyone** on records those tenants owned.
- **Alternative used for "records owned in the tenant".** A reviewer with no record access (a security officer, or a pure user administrator) cannot look records up at report time through row-level security. The new nullable `logs.auditEvent.owningTenantId` is therefore stamped by `logs.uspRecordAuditEvent` when the event is written, in the actor's session:
  - the patient work owner for people, patient workspaces and results;
  - the record's owner for cases, investigations, tasks, testing plans, notification obligations and staging rows.

  An append-only ledger accepts a new nullable column, and earlier events stay NULL and are scoped by user only.
- **Sign-in activity and account history:** these include accounts holding a current grant in scope, not only accounts homed there.

## #271: security officer

**Alternative to the suggested "tenant-scoped `security.investigate`."** Unknown sign-in names match no account, so they belong to no tenant and cannot be scoped. They stay platform-only, and this is documented in `docs/sign-in-event-log.md`. Instead:

- **New permission:** `security.review`, for read-only security reports in the tenants granted.
- **New role on every data root:** *Security officer (security reports, read only)*, with `audit.view`, `usage.view` and `security.review`.
  - New roots get it from `config.uspInstallRootTenant`.
  - Existing roots converge through `500_seed/503_securityOfficerRole.sql`.
  - It has no `tenant.administer` and no record permissions.
- **Delegation:** the Administrator role gains `security.review`, so state and county administrators can grant the role under the existing "cannot grant what you do not hold" rule.
- **Pages:** Users and Sign-in activity accept `tenant.administer` **or** `security.review` through the composite policy `securityReports`. For a reviewer, the Users page is read-only: no add, find or edit links.
- **UX bug:** `AdminController.Assign` now shows a business-rule refusal (51422, for example "Platform accounts hold only platform roles…") on the user page instead of a bare 422 error page.

## #272: exports

| Export | Route | Audited as |
|---|---|---|
| Sign-in activity, every section, one row per finding | `/admin/sign-in-activity/activity.csv?hours=` | `export signInActivity` |
| Users and current access (access review) | `/admin/users.csv` | `export userAccess` |
| One account's sign-in history (up to 1,000 events) | `/admin/users/{id}/sign-in-history.csv` | `export signInHistory` |

All three use `TabularExport.Csv` (formula defusing) and the same scope as their pages.

## #224: environment indicator

`Deployment:Environment` (Live, Training, Test, Demo or Development), `Deployment:AgencyName` and `Deployment:SyntheticData` drive `DeploymentIndicator`:

- **Non-live environments:** a banner on every screen, including sign-in, names the environment and agency before anyone acts on a record.
- **The synthetic-data claim:** requires a non-live environment **and** `SyntheticData = true`. A live, unconfigured or misspelled environment never shows it, and never shows a banner built from free text.
- **Nothing comes from host names or connection strings.**
- **Settings:** `appsettings.json` documents the switches with a live-safe default. `appsettings.Development.json` keeps today's demo text.

## Validation

- `Merit.Tests` (unit): 338 passed, 0 failed, including the new `DeploymentIndicatorTests` and the endpoint-policy inventory.
- New integration tests:
  - `CountyScopedClerkImportsAndEntersResultsWithItsWorkKeptInItsCounty`;
  - `PermissionDeniedIsAnAllowedHoldReason`;
  - `SecurityReviewTests`: audit scope including descendant roots, granted-not-homed sign-in activity, the delegable read-only officer, the refusal banner and history export.
- The #267 replay (`Scenario267RegionTests`) now requires these fixes. Two-county intake must work; the Harford officer's reports must exclude Baltimore City-only staff and include the two-county staff; region and platform audits must reach the state roots; and every export must work. Any unexpected behaviour fails it.
- The fresh-deploy #267 replay (`MERIT_SCENARIO_267_FIX_CLAUDE`) **passed**, with no unexpected behaviour:
  - two-county HL7 import loaded;
  - manual entry returned 200 with its receipt task;
  - the Harford officer saw no Baltimore City-only staff and did see the Harford and two-county staff;
  - all exports were available to Region 3, the platform and the officer.
- Full integration suite on a separate fresh database (`MERIT_AUDIT_FIX268_CLAUDE`): **215 passed, 0 failed, 2 opt-in replays skipped**.
- The Angular client and the Playwright e2e suite were not run: no Angular code changed, and the build used `SkipAngularBuild`.

## Follow-up (not in scope)

- MERIT has no way to transfer a case to another jurisdiction. Cases opened under the #268 fallback carry a follow-up task instead.
