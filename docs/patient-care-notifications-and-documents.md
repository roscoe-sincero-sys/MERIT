# Patient follow-up, receipt-based notification and documents (#202–204)

## Program setup

The audit recommendations apply to the current application: patient-only activity and supporting documents were absent, and generic case follow-up did not prove completed notification. Notification deadlines now use **receipt of the blood test**, as clarified by the user, with independent threshold rules. Existing case thresholds and their generic follow-up metrics retain their original purpose.

An administrator opens **Configuration → Notification rules** and records a program-approved policy version:

- Working agency and descendants; analyte/specimen and canonical unit.
- Inclusive minimum / exclusive optional maximum, population at collection and optional inclusive age bounds in months.
- Required patient or caregiver/guardian recipient.
- Calendar or business days, receipt day excluded, policy time zone.
- Explicit holiday dates, calendar name and receipt-date effective period.
- Approval reference and explicit acknowledgment of program approval.

Business days exclude Saturday, Sunday and only the entered holiday dates. Empty holidays explicitly means weekends only. Include observed dates and following-year dates where deadlines cross year-end. Multiple distinct matching rules create independent obligations. There are no automatically activated jurisdiction examples. Maryland reviewable drafts link the verified statutory/regulatory sources and still require an approved agency calendar and source eligibility review. The withdrawn seven-day jurisdiction example has been removed. The program owner must approve actual thresholds, eligibility, recipient, dates and calendar before activation.

New final/corrected valid results evaluate automatically after classification. **Evaluate existing results** queues a job under the administrator's recorded scope; the worker must be running. The patient workspace also provides an explicit evaluation action. Both are idempotent and run in batches of 500. Uncertain censored values or missing age/population that could qualify produce **Needs eligibility review**, rather than silently dropping a possible obligation. Preliminary, cancelled, invalid and definitely nonqualifying results do not generate obligations.

Policy versions are immutable in the application. Retirement stops generating from that version; existing obligations retain their version, holiday snapshot and original deadline. Use separate approved versions with nonoverlapping effective periods for the same rule name. Retiring a mistaken version does not erase historical obligations: review their applicability individually.

## Staff workflow

Open **Patient activities, notification deadlines and documents** from a patient, or the equivalent case link.

- Patient view includes accessible patient-only and case activities. Case view filters to that case.
- Record actual date, audience, channel, recipient, outcome and description. A patient without a case can have activities. Later link an unassociated activity to a case for that patient; the original author, date and event ID stay unchanged.
- Legacy case events remain in place and join the same canonical stream; events are not copied. Legacy audience/channel information that was not collected is shown as unrecorded. New case contact attempts participate in the existing failed-contact letter proposal rule.
- Notification obligations show required recipient, local receipt/deadline, source result, immutable policy, eligibility decision and actual completion evidence. Pending, approaching, overdue, review, excluded, on-time and late statuses are distinguished with text.
- Record an unsuccessful contact attempt as an attempt. It cannot satisfy notification. Completed notification requires a recorded recipient valid on the event date, result, channel, actual date on/after receipt, completed outcome and explicit confirmation. Caregiver evidence requires an outgoing caregiver/guardian/primary-contact/custody relationship valid on that date.
- A delivery may explicitly satisfy all eligible pending obligations for the same result, recipient type and case with **one activity**. Each obligation retains its own deadline. Withdrawing mistaken evidence retains the original event and reason and reopens every obligation using it.
- General task completion/dismissal/reopening cannot bypass evidence or applicability review. A case cannot close while its notification obligations remain unresolved. Exclusions require a recorded reason and are never counted as completed delivery.

**All notification deadlines** opens the scoped compliance report. Full-scope summary counts, paged evidence and an audited CSV export are separate from the existing first-generic-follow-up report. Exclusions and uncertain eligibility are reported separately. No configured/evaluated obligations is not evidence of compliance. Superseded/invalid source results are visibly flagged; staff review applicability without deleting the original history.

## Supporting documents

Patient or case upload supports original PDF, XLSX, JPEG, PNG and WebP files up to 10 MiB. XLSX expansion is bounded to 50 MiB / 2,000 entries; macros, embedded files, external relationships and DTDs are refused. Browser MIME claims do not determine accepted types. This format validation is not antivirus scanning.

Record title, category, document date, source and optional description. Upload a new revision by selecting the earlier document; its bytes remain unchanged. Revisions must have the same patient and case association. Patient-only and case-associated documents are labeled explicitly.

Desktop file selection/native file-picker drop and phone file/photo selection use the same form. JavaScript adds progress, cancellation and retry; ordinary form upload remains available. A retry keeps its upload ID, so a lost response cannot duplicate the original. After cancellation or network failure, check the list before retrying; the server may already have saved the file. Choose a new upload after refreshing to change the file.

Raster images preview inside the authorized application. PDFs and workbooks download as attachments for the organization's chosen viewer. Files are held privately in SQL, never published under the web root. List reads do not fetch blob bytes. Download/preview accesses are audited, use no-store responses and require the original patient's access plus case visibility when associated. Document editing associated with a case also requires case edit permission.

New explicit `document.view` and `document.edit` permissions are granted to standard clinical roles. Pure administrator/user-administrator and de-identified epidemiologist roles receive no default document access. Custom roles/profiles need deliberate permission selection. Patient/case row-level security remains authoritative.

## Deployment and verification

The normal rerunnable database deployment creates four tables, canonical activity view, calendar function, procedures, RLS/FKs/indexes and document permission seeds. Existing case activity rows are retained. Deploy database, web and worker together; no production deployment was performed during development.

Automated coverage includes calendar/business-day differences, explicit holidays, year-end and DATEFIRST independence; receipt/local-time anchoring with late-arriving results; multiple thresholds, uncertain and nonqualifying results; attempt/completion separation and shared evidence withdrawal; patient activity without cases and subsequent linking; document retry/revision/original retrieval, format bounds and authorization. Browser checks cover patient/case entry points, document feedback and responsive/accessibility behavior.
