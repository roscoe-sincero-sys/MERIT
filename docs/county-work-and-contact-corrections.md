# County work routing and caregiver contact corrections (#257, #258)

## Program routing

Program-task configuration now explicitly selects the responsible agency for patient-only work:

- **Serving local jurisdiction**: use the current primary-residence jurisdiction, with deterministic selection for overlapping coverage. For an explicitly shared patient whose resolved owner is outside the caller's write scope, use the authorized working agency. The agency must remain inside the definition's scope, and the profile must carry the appropriate permission at that tenant.
- **This program's agency**: retain state/team ownership even when the patient resides in a county.
- **Patient record owner**: preserve legacy routing.

New web drafts default to serving-jurisdiction routing. Existing definitions receive the legacy value at upgrade; published versions and previously created tasks are not moved or rewritten. Copy and approve a new version when changing a program's policy. Review outstanding state-owned work with its current owner before evaluating a replacement version, to avoid duplicate obligations. Case-associated work continues to use the case agency. Preview shows the responsible agency before publication.

Patient-only ad-hoc work uses the same serving-jurisdiction/authorized-working-agency approach. A county user can create and complete work for an accessible county/shared patient without acquiring state-wide rights. Task writes, completion, exceptions and transfers remain subject to tenant-specific profile permissions; a clinical role in another county cannot augment epidemiology-only access here. Recurrence retains the original task owner and frozen policy/deadline data.

The serving-jurisdiction helper now installs as `365_patientWorkOwner.sql`, before workflow eligibility references it. The SQL object name remains `dbo.udfPatientWorkOwner`.

## Caregiver correction requests

The privacy boundary introduced by #252 remains: contact-only caregiver visibility does not grant a medical-chart write path. The selected alternative is a tracked correction request, rather than a new direct contact-edit permission.

1. From the patient's **Relationships**, choose **Request contact correction**. Enter a proposed phone, email or mailing residence address, the source evidence, and which outdated contact should be removed or ended. Submission requires an active caregiver relationship and contact authority at the patient's tenant in the actual profile.
2. The request appears in **Contact corrections**, with the responsible caregiver-record team and its status. The same history is reachable from the patient's work checklist. Patient staff can track the outcome without accessing the caregiver's unrelated medical records.
3. A user authorized to maintain the caregiver's owning-tenant record opens the caregiver in a separate tab and applies the verified change through the existing contact/residence forms. They review outdated values and remove/end them as appropriate.
4. The reviewer returns to the request and records **Applied** or **Rejected**, with evidence. Applied status is rejected unless the proposed phone/email is currently present or the proposed residence is the current primary residence. A stale or previously resolved review is rejected. Rejection requires a reason but does not modify contact records.

The request retains source evidence, original contact/residence snapshots, the reviewer/time, the outcome and a verified after-snapshot. Existing data permissions are not expanded. Ending a relationship blocks new requests while preserving already authorized patient request history. The request inbox does not send email or assign a clinical deadline automatically.

A dedicated `dbo.contactCorrection` table keeps this approval history separate from program/clinical tasks and their completion/closure rules. RLS permits request access via the patient or caregiver record; procedure checks further restrict patient contact authority and owning-team review. This does not create `personAccess` grants, expose caregiver clinical records or allow arbitrary tenant selection. Caregiver-owning teams can review their requests even if the referring patient's chart is outside their scope; that chart remains unavailable.

## Deployment

Use the normal maintenance deployment with runtime traffic stopped. The existing security migration releases policy bindings, updates dependent functions and restores the full enabled policy. New request predicates are also released before dependent original predicates are upgraded. Complete a failed deployment before reopening traffic. Existing historical work is not automatically re-routed.

Regression coverage includes actual HTTP/RLS request, review, phone/email/address application, stale reviews, ended relationships, mixed-profile denials, patient-only task completion, explicit state/legacy routing and immutable task snapshots. Edge tests cover county submission, owner rejection/history and routing controls, with axe and overflow checks at 1280, 390 and 320 pixels.

Validation: full solution build completed with zero warnings/errors; 323 unit/architecture tests passed (five optional mail tests skipped); the fresh-database integration suite passed 199 tests (one opt-in clock replay skipped); all 62 patient-care/program tests passed again after the final completion/transfer guards. Both Edge tests and all three SQL-generator tests passed. Clean installation and repeat/upgrade deployment completed with row-level security enabled.
