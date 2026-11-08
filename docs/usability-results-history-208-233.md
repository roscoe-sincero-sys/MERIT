# Result entry, history and navigation decisions

Issues: #208, #210, #215, #216, #217, #219, #233.

## Verified recommendations and implementation

| Issue | Repository finding | Change / alternative |
| --- | --- | --- |
| #208 | A manual "corrected" report relied on duplicate detection, which could miss the original when its value changed and it had no accession. There was no authorized withdrawal flow on result detail. | Start correction from an explicit original result. Require a reason, current row version, verified replacement and matching patient. Preserve the source and link replacements. Mark entered in error with reason, verification and confirmation; never silently move a result to another patient. Create affected-case review tasks and warn when a recorded highest result is no longer valid. |
| #210 | Collection date defaulted to today and manual entry identified the patient by name alone. | Blank collection date, server-reloaded name/DOB and permission-filtered identifiers. Review instructions remain beside the form, including after validation. Routine entry has no extra modal. |
| #215 | Case detail returned all patient/substance results without membership. Timeline represented one uninterrupted case span even after reopening. | Explicit `dbo.caseResultLink` records case/result/opening-event associations during classification. Opening, closure and reopening have typed events on the existing `caseActivity` ledger. Current-episode results are the default, with an explicit all-context view. Legacy membership and missing history are labeled rather than inferred from collection dates. Patient cards summarize recorded reopenings; case history retains staff and reasons. Timeline presents discrete recorded events, including legacy status changes, without invented duration bars. |
| #216 | Historical result detail had no original threshold decision; chart thresholds could be mistaken for the rule used at intake. | Preserve the first classification decision, population/age, applicable rule IDs, effective intervals, authority, canonical levels and outcomes. Reruns cannot overwrite this evidence. Legacy results say their original evaluation is unavailable. Explain lab status, record validity and case classification separately. Latest/trend comparisons separate specimen, sample type, speciation, method and unit; detection limits do not get a numeric trend claim. |
| #217 | Home counts did not give new users direct starting actions. | Permission-aware assigned/due work, import, patient lookup/manual entry, configuration and access actions, plus an actionable empty state. Existing operational counts remain visible. |
| #219 | Omitting global search on People moved the account block and shortened the phone header. | Stable desktop grid slots. On People the search slot contains a shortcut that focuses the existing page search, keeping a single search input. Mobile menu behavior remains consistent. |
| #233 | Detail back links lost parent context, Cases did not search while typing, and confirmations mixed browser and in-app patterns. | Shared breadcrumbs with validated local filtered-list returns; result/investigation case context is checked against accessible patient/substance or linked-case data. Debounced Cases search keeps focus and filter URL and exposes loading status. One native HTML dialog serves Razor, htmx and Angular actions with Cancel focus, Escape, restoration and plain-text messages. |

## Deliberate safeguards

- Existing cases are not automatically closed or downgraded when a result is corrected or withdrawn. Outstanding notification and program tasks may remain required. Review tasks and invalid-highest-result warnings make the next step explicit.
- No historical evaluation or episode association is backfilled by guessing. Legacy source, qualifying/highest pointers and existing activity records remain available in the all-results view.
- The first evaluation is written for both manual and worker classification through the shared service. Snapshot JSON is separate from the immutable as-reported JSON.
- New database objects are additive and generated from repository definitions. The association table uses patient row-level security; all reads also constrain the visible case. No separate episode ledger duplicates `caseActivity`.
- A collection of recorded events is not proof that every historical interval or reason is known. Timeline text and case history state this limitation.

## Validation

Regression coverage includes changed-value/no-accession correction, wrong-patient and stale-version rejection, immutable report/evaluation, reason/confirmation requirements, withdrawal retries, three close/reopen cycles, and association stability across a subsequent reopening. Browser coverage checks desktop/tablet/phone header geometry, one People search, search focus, Cases live filtering, phone entry, and confirmation cancellation/acceptance for plain forms, htmx and Angular. SQL conventions, generators, .NET tests and Angular compilation are also checked. Validation uses `MERIT_IT`, never production.

Validation recorded 8 November 2026: full .NET run passed 308 unit tests (five optional mail-tool skips) and 166 integration tests. Final focused integration run passed three scenarios, including an additional actual intake-clerk versus case-manager Home check, receipt preservation and correction audit entries. Across the affected Edge selections, 53 distinct scenarios passed; outdated assertions were corrected for earlier merged labels, the shared confirmation component and intentional filter preservation. Angular build, browser TypeScript checks, JavaScript syntax, generator tests and SQL conventions passed (320 scripts).
