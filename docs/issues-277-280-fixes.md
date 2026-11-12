# Fixes for #277, #278, #279 and #280

Baseline: merged main `93afd9a` (after PR #276), 2026-11-11. #279 (filed by Codex) and #280 (filed by Claude) describe the same defect from two runs, and one change fixes both. Each recommendation was checked against the code before implementation. Where a choice was asked for, or a recommendation was not applied as written, the decision and reason are stated below.

## #277: a Security officer saw no county usage

**Verified cause.** `logs.uspGetScreenUsage` and `logs.uspGetScreenSession` matched visits against the session's *record* tenants (`sessionScopeTenant`) and *administered* tenants (`sessionScopeAdminTenant`). `usage.view` is an admin-level permission, so a Security-officer-only session has neither, and the reports came back empty with HTTP 200. They were also limited to the session's own data root.

**Fix.** Both procedures now scope with `auth.tvfReviewTenants('usage.view')`, the function #275 added for this purpose:
- administered tenants plus those where the session holds `usage.view`, clipped by custom profile and view-as;
- matched on the visit's working tenant at the time.

The single-root filter is gone, so a region reviewer reaches the state roots below it. The summary, the session page and both CSV exports use the same table of visits. The existing rule that hides a previous screen from another county still applies.

## #278: county user and sign-in reports missed statewide actors

**Decision asked for in the issue: observed access, on top of local staff.** One function, `auth.tvfReviewAccounts`, now defines the accounts in a county review. The user/access list (page and CSV), sign-in activity (page and CSV) and per-account sign-in history all use it:

- **Local staff:** homed in a reviewed tenant, or holding a current grant there.
- **Outside actors:** the actor of an audit event in the last 365 days on a record a reviewed tenant owned (`logs.auditEvent.owningTenantId`, #270). For people and results, that owner is the patient's residence jurisdiction. A statewide administrator who opens a county case therefore appears in that county's review, including in sign-in activity.
- **Why observed access rather than "any statewide grant":** an effective ancestor grant alone would list every statewide worker in every county, active or not. That contradicts the established rule that a county administrator's Users page shows its own people (`AuthorizationScenarioTests.CountyAdministratorWorksOnlyInsideTheirCounty`). An access-review question about statewide staff belongs to the state reviewer, who sees them as local staff.
- **Not included:** staff of sibling jurisdictions only. Platform accounts appear only if a platform session recorded an event on a county record, which platform roles cannot do.
- **Read-only:**
  - Seeing an outside actor grants nothing. `canManageAccount` remains tied to administering the account's home tenant.
  - The "current access" column shows only grants that reach the reviewed tenants, not an outside actor's grants elsewhere.

## #279 / #280: multi-county intake gave patients to an arbitrary county

**Verified cause.**
- `dbo.uspSavePerson` created every patient owned by the session's working tenant and added a `share` for it. That share is the only kind `uspRefreshPersonAccess` leaves alone, so it was permanent.
- For a worker in two counties, the working tenant was the tie-break in `auth.uspCreateSessionScope` (`ORDER BY depth, tenantId`): a GUID comparison.
- A Harford resident imported while working in Baltimore County was therefore owned by and shared with Baltimore County. Its aliases, contacts, identifiers and places copied that owner. Whether this happened depended on tenant ids, which made the #267 replay flaky.

**Recommendation applied: decide the owner per imported row from its jurisdiction and the session's scope.**

- `uspSavePerson` takes an optional `@residenceCountyFips`. For a **new** person, created by a session working **below the data root**, the owner is the jurisdiction serving that county when the session can write there. Otherwise the working tenant keeps it, as before (no county, a county without a jurisdiction, or one the worker does not serve).
- The creation `share` follows the owner, so a second county is no longer added by default. Child rows (aliases, contacts, identifiers, places) copy the owner when they are created, so nothing has to be moved afterwards.
- Intake passes the reported county (HL7 PID-11, or the CSV `countyFips` column) for the new patient, and for a caregiver it creates from the same report.

**Not changed, as the issue asked:**
- Existing patients are never re-owned by a later report.
- Shares are not removed.
- Statewide sessions (working at the root) still create state records, with county access by residence.
- A worker who serves only one county keeps records of out-of-area residents; case transfer (#274) moves the case.

**Deterministic working tenant.** Equal-depth tenants now fall to `tenantName`, then `tenantCode`, then the id. For example, Baltimore City sorts before Baltimore County.

**Replay wording.** `Scenario267RegionTests` now prints `FAIL 14f` instead of `PASS 14f` when a step 14 access check failed.

**Existing records were not changed.** This read-only query lists patients that are owned by a jurisdiction other than their current residence jurisdiction, through a creation share. Review them before any cleanup:

```sql
SELECT p.personId, o.tenantName AS owner, r.tenantName AS residenceJurisdiction, p.createdBy, p.createdDateUtc
  FROM dbo.person AS p
  JOIN auth.tenant AS o ON o.tenantId = p.owningTenantId
  JOIN auth.tenant AS r ON r.tenantId = dbo.udfPatientWorkOwner (p.personId)
 WHERE p.isDeleted = 0 AND p.owningTenantId <> r.tenantId AND o.tenantType = 'jurisdiction'
   AND EXISTS (SELECT 1 FROM dbo.personAccess a WHERE a.personId = p.personId AND a.tenantId = p.owningTenantId
                                                 AND a.accessReason = 'share' AND a.isDeleted = 0);
```

## Validation

- **`CountyReviewScopeTests` (#277, #278):**
  - An officer holding only Security officer at Baltimore City sees a city worker's usage visit in the session page, its CSV and the summary, but not a Baltimore County session. It still cannot open patients or add users.
  - A statewide administrator who read a city case and failed a sign-in appears in the city officer's audit, users CSV (not manageable there), sign-in activity CSV and that account's history. Baltimore County-only and platform accounts do not.
- **`MultiCountyIntakeGivesNewPatientsToTheirResidenceCountyNotTheWorkingCounty` (#279, #280):**
  - A worker in both counties works in Baltimore City by name, every time.
  - A Baltimore County resident they import is owned by Baltimore County, with no city share or city-owned alias. A city-only colleague cannot open the patient; a county-only colleague can open them and their results.
  - A Prince George's resident stays with the city.
  - A later report for the same patient does not re-own them.
- `AuthorizationScenarioTests.CountyAdministratorWorksOnlyInsideTheirCounty` (statewide staff without county activity are not in a county administrator's list) still holds.
- `SecurityReviewTests` now takes its expectation for city-only staff in a county review from the audit ledger (#278). Other tests can legitimately make such staff county data accessors.

Suite results:

- `Merit.Tests`: **338 passed, 0 failed**.
- Full integration suite on a fresh database (`MERIT_AUDIT_FIX280B_CLAUDE`): **219 passed, 0 failed, 2 opt-in replays skipped**.
- Strict #267 replay on a fresh deployment (`MERIT_SCENARIO_267_FIX280B_CLAUDE`): **passed, no unexpected behaviour.**
  - Harford residents imported by the two-county clerk are owned by Harford.
  - Step 14f passes: the Baltimore County-only profile gets 404 for the Harford patient.
  - The Harford officer's reports contain no Baltimore City-only staff.
- The manifest test (`ManifestIncludesEveryDeployedWebProcedureParameter`) passes after `uspSavePerson`'s new parameter was added to `database/procedureManifest.json`.
