# Program tasks, evidence and closure review

Implements #205, #206, #207, #213 and #218 on the merged #239 workflows. Earlier audit claims about unrestricted notification/testing completion are now outdated: their dedicated evidence gates remain in place.

## Program configuration

Open **Configuration → Program tasks**. Create a draft with its name/version, instructions, source event, patient population, optional substance/sample and threshold, age bounds, completion type and up to ten additional typed evidence fields. Conditions with missing/censored facts produce an applicability review, rather than a false eligible decision. Patient-review population uses current birth/pregnancy information and jurisdiction settings; received-result population/age uses the recorded collection context; case-triggered population uses the case context.

Preview two accessible sample patients, with active case IDs for a case-triggered definition. Preview explains agency scope, effective period, eligibility or missing facts. Publish only after entering program-owner approval and acknowledging the previewed conditions, timing, role, evidence and closure rules. The three-attempt outreach template is an **inactive example**, requires review of the moved/unlocatable condition, and is not asserted to be New Jersey law. Its completion is an internal review of the evidenced attempts, not a fictional successful contact.

Published versions are immutable. Copy to a new version or retire an old version. No overlapping published version of the same name is allowed within an agency. Already created tasks keep their original instructions, due policy, custom evidence schema, closure criteria and approval. Retirement stops future generation/recurrence without deleting existing work.

New final/corrected results evaluate definitions through classification. Existing-patient evaluation is explicit and runs in the worker under the initiating profile/working-agency scope. Patient checklists also expose an explicit evaluation action. No production backfill occurs merely by deploying the feature. Writes outside the caller's agency scope are skipped; required work owned by another agency needs its authorized staff, not an implicit privilege escalation.

## Timing, assignment and recurrence

Source events are patient review, received result or active-case evaluation. Calendar/business days reuse the approved explicit holiday/date calculation. Blank due interval means no invented deadline; zero means the source date. Suggested responsible role does not grant access or assign an arbitrary person. Use existing queue assignment, which checks the selected worker's access to the actual patient/case.

Optional recurrence creates one next occurrence from actual completion under the frozen day/calendar policy. It retains the previous task and activity. It does not advance an overdue date merely because a newer result arrived. Required recurring work needs an explicit policy-permitted stop/waiver or transfer before closure. This is a bounded task-definition editor, not executable user code or an unrestricted clinical rules language. Unrepresented conditions belong in the instructions with explicit applicability review. Existing notification and clinical testing policies retain their dedicated workflows.

## Completing and correcting work

**Patient work checklist** lists pending and historical tasks and their dispositions. Ordinary workers can add scoped ad-hoc work without changing program definitions.
Merged aliases retain their history but do not receive new program/ad-hoc obligations; the checklist links to the canonical patient.
Routine ad-hoc work entered in error can be dismissed in the queue with a reason. That does not create completion evidence or allow a worker to bypass a published program requirement.

- Internal completion requires a completed outcome and source note.
- Contact completion requires an identified external recipient, channel and reached outcome.
- Delivery completion requires an identified external recipient, channel and completed outcome.
- An attempt records its actual outcome and leaves required successful work open.
- Configured evidence controls enforce required text/date/number/yes-or-no values.
- Saving creates one canonical patient/case activity and updates the task in the same database transaction. A failed validation leaves both unchanged.
- Bulk completion accepts shared notes only for ordinary internal work. Contact/delivery, program, notification, receipt and clinical testing tasks use their evidence forms. Repeated internal completion is idempotent; a done task with linked evidence must be reopened through an explicit evidence correction.

Corrections retain the original event, author, date and reason. Linked work reopens. An unfulfilled recurrence created from incorrect completion is cancelled and retained; already resolved dependencies require further review. Notification evidence still uses its original multi-obligation withdrawal flow. Reopen a closed case before changing evidence supporting closure. Transferred work requires review with the receiving case, preventing duplicate open obligations from a blind correction.

## Closure review

At the case's **Closure review**, inspect outstanding task names/counts, program criteria and recorded failed-contact count. Required work must have evidence, a justified applicability decision, an authorized policy-permitted waiver, or an open receiving task after transfer. The separately granted `workflow.waive` permission is seeded only to supervisors; ordinary case workers cannot authorize that exception. A policy scoped to Lost to follow-up does not indiscriminately block deceased/duplicate closures. Existing statutory notification and Maryland clinical gates remain separate.

The confirmation includes a hash of the current open task IDs/row versions. If work changes while the page is open, closure is rejected for a fresh review. Routine tasks dismissed by closure record **autoDismissed**, closure reason and note. Completed, waived, transferred, not-applicable and automatically dismissed tasks remain distinct. Legacy tasks whose evidence was not recorded are not relabelled as compliant retrospectively.

## Workspace navigation and content

Patient and case pages have keyboard/touch section links, native fragment history and a compact sticky identity strip. Patient jumps reach results, case history, timeline, activity and documents; case jumps reach overview, tasks, results, activity and closure. Empty secondary sections are disclosed without removing their add/edit controls or any existing data. Shared header destinations retain their routes and permission filtering. See [terminology](terminology.md).

Automated checks cover source eligibility, publication/versioning, real worker execution, atomic evidence, failed attempts, policy-specific closure, waiver permission, transfer preservation, corrections and recurrence. Browser checks should be used alongside clerk/case-manager validation; automated label checks do not establish that representative users completed an unmoderated usability study.
