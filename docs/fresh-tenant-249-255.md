# Fresh-tenant workflow fixes (#249–255)

The fixes address the application paths verified in the #243 retest. Configuration examples used by tests are synthetic and are not approved historical program policy.

## Account and policy setup

Tenant creation already refreshes the web session. SQL session scopes remain immutable; no scope mutation or authorization bypass was added. Fresh supervisor roles now include `workflow.waive`, matching the existing deployment backfill in `502_carePermissions.sql`.

The supported operational delegation path is platform → regional administrator → state administrator. Each administrator must explicitly receive the clinical roles it will delegate; an administrator role by itself does not confer clinical access. Give an operational supervisor `userAdministrator` separately when that supervisor must create accounts. The assignment guard continues to reject granting permissions the caller does not possess. Independent clinical-role delegation without clinical access would need a separately approved delegation model.

On **Admin → Tenants**, choose the baseline configuration date. It defaults to the agency's current date. For historical setup, choose the earliest policy period to configure. Shipped reference thresholds are opt-in in the web form; leaving that checkbox clear avoids future seed rows obstructing approved historical thresholds. Existing agencies and their thresholds are not rewritten. Direct installation retains its existing threshold-seeding default for compatibility and accepts `@seedThresholds=0` explicitly.

Classification and reclassification resolve settings and thresholds on each specimen's collection date. The settings cache holds history, resolves each requested period independently, and never substitutes future configuration for missing history. Prehistory uses the existing typed defaults; required unset settings remain unset. Today's operational address-handling policy continues to govern address updates made today.

## Laboratory format configuration

Open **Agency settings → Laboratory import formats** (`/config/intake-formats`). Register the laboratory, choose HL7 or separated text, and supply its date formats and exact column names. Separated text requires patient last name, birth date, test code, value and collection date. Field labels map into the parser's column-to-field representation; administrators do not edit JSON.

The `intake.configure` permission gates every read and write. Updating an active format creates a new version and retires the previous version transactionally. Earlier batches keep their original format. Concurrent revisions, foreign senders and cross-root formats are rejected. Existing code translations/constants are retained on revision; this editor does not introduce a translation editor. New separated-text layouts must supply canonical MERIT values as explained in the form.

Intake clerks can ingest HL7 2.3.1/2.5.1 PID-3 through a worker-only writer that reads identifiers from the stored source row, checks the scoped patient match, deduplicates values and audits the count. It does not grant identifier display/export rights. A permission failure is labeled `permissionDenied`, with an account/setup instruction, rather than a false matching ambiguity.

## LHD work and caregiver contacts

County cases route to active `jurisdiction` tenants. New LHD patients are inserted with the acting tenant's ownership before sharing is recorded. Patient-only clinical testing work uses the serving jurisdiction, with a writable acting-tenant fallback for explicitly shared patients. Existing case ownership is not silently rewritten.

Caregiver visibility uses a contact-only RLS projection on identity, contact methods and residences. It does **not** add caregiver `personAccess` chart grants. It requires an active patient relationship, patient access and a tenant-specific contact capability resolved from the clipped active profile at sign-in. Permission unions from a different tenant cannot grant this projection. Existing sessions acquire the new capability on their next sign-in/profile refresh. Caregiver results, cases, identifiers, documents and clinical work retain their original access checks; writes retain the original block predicate.

HL7 NK1 contacts, PID-13 patient phone and CSV caregiver phone/email/address are preserved. Laboratory relationships remain unverified and do not establish legal custody. Use **Relationships → Verify or update caregiver**, review the source and contacts, choose the relationship, primary-contact status and explicitly verified custody, then save. Ambiguous caregivers go to review. Ended-today relationships stop contributing to outreach immediately.

Manual blood-lead entry now recognizes a Maryland root agency without requiring a source-reported state field, so testing review also runs for manual results.

## Deployment and validation

Deploy during the normal maintenance window with runtime traffic stopped. The security migration temporarily releases policy/function schema bindings, upgrades the contact function and restores the complete policy before runtime resumes. A failed deployment must be completed before reopening access.

The demo generator installs an explicitly historical, completed synthetic confirmation with linked source and completed venous results. Runtime evidence requirements and bulk-completion restrictions remain in place.

Regression coverage includes a clean installation and repeat deployment, unit/architecture tests, HTTP/MFA/worker replay across 2020 and 2025 policies, and negative tests for caregiver chart access. The replay uses an isolated `MERIT_SCENARIO_243_*` database, instruments only its business-date function, restores that function afterward and leaves authentication/audit wall time unchanged. Historical clinical-guideline reconstruction remains unsupported; the current-care attestation guard is retained.

Verified locally on 2026-11-08:

- Full solution build: zero warnings/errors.
- Unit and architecture tests: 323 passed; five optional local-mail tests skipped.
- SQL generator tests: three passed.
- Fresh-database integration suite: 192 passed; the opt-in clock replay skipped by design.
- Dedicated HTTP/MFA/worker replay and caregiver privacy tests: passed, including both supported HL7 versions and rejection of cross-root identifier attachment.
- Edge: format creation/revision and caregiver verification passed, with overflow and axe accessibility checks at 1280, 390 and 320 pixels.
- Clean demo deployment and repeat deployment: all 327 scripts completed; the security policy was restored and enabled.
