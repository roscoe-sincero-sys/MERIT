# Clinical testing outreach and verified historical receipts

Addresses #244, #245, #246 and #247. Uses existing testing tasks, immutable plan JSON, intake batches and audit events; no new task, contact or receipt ledger is introduced.

## Clinical policy decision (#244)

The implemented schedule is the [MDH March 2025 clinical guidance](https://mde.maryland.gov/programs/land/LeadPoisoningPrevention/Documents/Reports/MDH%202025%20Maryland%20Guidelines%20for%20Lead%20Exposure.pdf). The document identifies its publication month; it does not establish a complete approved 2020 policy. Inventing a configurable, active historical policy from incomplete sources would not resolve the scenario honestly.

Implemented the issue's alternative: a draw or screening target before March 2025 cannot silently acquire this policy. Saving requires an explicit clinician choice to use current guidance **for current care**, with a separate reason. The SQL writer enforces this independently of the form. The immutable plan snapshot records policy version, reference date, current-care purpose, confirmation, reason and reviewed date. The reason also appears in the plan history.

This is not historical policy reconstruction or a claim of historical compliance. March 2025 is a publication-month guard, not an asserted statutory effective date. Clinical review is still required for every plan, including newer draws. Existing plans are not rewritten. A complete historical policy editor still requires approved program specifications and is outside the supported current-care alternative.

## Original Department receipt on imports (#245)

On **Upload lab results**, expand **Previously received reports / historical import**. Staff with patient editing and intake upload access can attest a verified original Department receipt and its source evidence. All rows must share that receipt; split mixed-receipt files. An unknown original receipt must be verified before using this workflow. Laboratory final, transmission and collection dates are not substitutes for Department receipt.

Three nullable additive batch fields store the original receipt in UTC, the interpreting agency time zone and the evidence. Existing batch `receivedUtc` remains the actual ingestion time. The submitted user and existing procedure/audit instrumentation identify the attesting actor. Result snapshots retain the receipt provenance and batch link; result `receivedUtc` uses the verified receipt when supplied. Receipt-based notification evaluation therefore selects the appropriate effective policy immediately during both HL7 and CSV worker processing.

The form/service reject missing confirmation/evidence, future receipt, and invalid or ambiguous local times. The SQL writer independently checks authority, evidence and future dates. Rows whose collection/report/final-result dates conflict with the verified receipt are held for source review. Ordinary new receipts keep the existing upload-time behavior.

There is no in-place receipt correction operation in this change. Existing batch source/provenance and obligation snapshots are retained; the historical workflow does not rewrite old obligations or reinterpret an existing batch under a different receipt. A genuine re-send still requires the existing explicit re-send workflow and goes through existing duplicate/correction rules.

## Clinical testing roster (#246)

Open **Work queues → Clinical testing outreach**, or follow the link on a patient overview. `/testing` shows one row/card per existing reviewed plan or unresolved clinical review, with patient, stage, source draw, target, urgency, assignee and case link.

Filters cover overdue, today, today through seven calendar days from today, stage, assignee and status. Completed/dismissed work is a separate history view. Reviews without a target appear under all targets/reviews; they are not silently given a clinical deadline. Missing work is not evidence of completed testing.

Caregiver selection uses current, visible relationships. A unique verified primary caregiver is preferred. Otherwise, a sole verified caregiver is shown; multiple or unverified caregivers require review. Adults without a caregiver use their own contact record. Children without one show a missing-caregiver warning. Phone/email values come from recorded contact methods; the mailing-address column explicitly identifies the current primary residence. Staff must verify details before contact.

Contact information requires patient editing authority **at the task's tenant**, clipped to the active profile/view-as grants and current master rights. A read-only/deidentified profile does not inherit contact rights held in another tenant. Patient/task/case RLS continues to apply to the roster and the related contact/location joins.

**Export all matching rows to CSV** requires export permission, traverses all matching pages and records an export audit event with the count. Identical filters and contact restrictions apply to screen and export. CSV values are quoted and formula-leading text is neutralized. The export uses `no-store` caching.

## Patient summary (#247)

The patient overview shows all open plans/reviews, urgent work first, with target, type, responsible staff and a direct link to the exact plan. The overview, testing workspace and roster share one status calculation: overdue, due today, due within seven days, scheduled or review required. Completed and dismissed work never acquires an open-work warning. All dates use the agency-local calendar, and warnings are text rather than color alone.

## Validation

Regression coverage includes actual historical HL7/CSV worker processing with effective-dated notification policies, explicit current-care decisions, inclusive date boundaries, missing/ambiguous/primary caregivers, mixed-tenant privacy, scoped export across 51 rows, formula escaping, export authorization and audit records. The #243 diagnostic cases are included and updated to assert the fixed behavior; the original scenario report remains a dated account of pre-fix observations.

Validated with 316 passing unit/architecture tests (five optional local mail-tool skips), 188 passing integration tests, four passing final contact/export/privacy checks, two generator tests and the Angular production build. Six Edge browser tests passed, including the new historical receipt form, clinical policy decision, testing roster and patient warnings plus existing patient-care flows. Browser checks cover 1280, 390 and 320 pixel widths and automated WCAG A/AA checks.

No production configuration or historical program approval is supplied by these tests. Use approved jurisdiction policies and calendars in deployment.
