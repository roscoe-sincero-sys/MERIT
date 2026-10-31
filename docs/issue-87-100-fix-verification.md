# Issues #87–#100: verification and fixes

Baseline: `4d47185` on `origin/main`, after PRs #121 and #122 were merged. Each report was checked against that revision before making changes. Reports that include an already resolved symptom are addressed by preserving that behavior and fixing the remaining symptoms.

| Issue | Verified state and resulting behavior |
| --- | --- |
| #87 | Findings contained only federal surrogate IDs. They now link to the person through a resolver that applies the extract root, current person access and `person.view`. Ambiguous or inaccessible IDs have no link. Legacy runs work without rewriting reports, and neither findings files nor federal files gain identifying metadata. Persisted `PersonId` metadata is ignored. |
| #88 | Match pagination was already fixed. Create new person is now an explicit button and C shortcut even when candidates remain. Collection date, test, value, unit, sample type, method, guardian and accession context appear beside the comparison. Candidate history uses display labels. |
| #89 | Batch scope was already fixed. Inspect/edit is now independent of bulk checkboxes and moves keyboard focus to the editor. Known failing fields appear first with attention labels and date/value hints. Save and release acts on the inspected row; uncertain matches still require Match review. A group correction atomically applies one field to the exact cause/value/sender/batch, optionally restricted to selected IDs. This is a row correction, not a silent change to a sender's permanent mapping. |
| #90 | Language and preferred contacts now appear in the person summary; open contact details survive refresh. Jobs can be edited, ended and verified without losing their worksite, source, occupation-code version or analytes. Sources can be edited, confirmed and ended, preserving product/location associations and rejecting stale edits. Source validation retains confirmation. Pregnancy source selection is bound. Place schedules, apartments and year built can be corrected under the original version; historical end dates can be entered. Location correction and person-place updates share a transaction, retain address metadata and preserve residence invariants. Year built is explicitly identified as shared address data. |
| #91 | Draft retention was already fixed. Case task assignment/completion now work inline and validate that the task belongs to the displayed case. Activity contact is an explicit patient/related-contact/none selection, validated for the activity date. Closed cases can reopen with a required reason, history entry and review task; an existing open case prevents a duplicate. Case list sorting uses partial navigation. |
| #92 | Investigations now have durable search, investigator/status/type filters, sorting and a 25-row pager with empty-offset recovery. Sample type constrains units and surface entry; compatible custom standard units remain available. Readings can be corrected with recomputed exceedance or voided with a reason, using a timestamp concurrency token preserved as SQL `datetime2`. CSV/XRF import checks the complete file before an atomic write and uses stable submission IDs to prevent retry duplicates. Detail pages link to audit history; investigation requests explain missing targets and cannot submit without one. |
| #93 | Confirmation, reopen and scope-reset safeguards were already present. Dismissal now requires a reason. Completed/dismissed views retain zero-open queues, task detail is visible, cross-page selection has a count and Clear selection action, and overdue filtering is linked from Home and marked per task. |
| #94 | Date tokens are removed before address detection, so date-first name queries work. Multi-part name splits are searched rather than simply dropping the remaining surname tokens. Match reasons and organization types are labelled; organization matches link to the filtered registry. Header search preserves the query on search pages. |
| #95 | File extension filtering already existed. Upload outcomes now use one-time typed messages rather than replayable query text, duplicate warnings retain their batch link, all-held/partially processed batches do not look like fully loaded success, and uploads have a duplicate-submit guard and busy feedback. |
| #96 | The existing accessible menu drawer now applies through 64em, keeping tablet headers compact while retaining keyboard Menu/Escape behavior. |
| #97 | People navigation leads to “Find or add a person.” Add appears after a search, including partial search results, to encourage duplicate checking. The existing add endpoint remains available to authorized staff. |
| #98 | Results and queue URL state were already fixed. Map and cluster filters/page now use URL state with browser history. Cluster detail returns to filtered clusters; map detail links carry a validated return URL. Existing report and list behavior is retained. |
| #99 | Route title suffixes already existed. The host now starts with a single product title; one title strategy avoids duplicate suffixes. Client route changes update current navigation, move focus to the new heading and announce it. Query-only updates preserve focus. Unknown routes render Page not found without loading work queues. |
| #100 | Platform administrators no longer see clinical Configuration navigation. Clinical readers see Reference levels; configuration editors see Configuration. Existing server authorization remains authoritative. |

## Recommendation checks and alternatives

- Linking extract findings does not require adding PHI or identifiers to federal files: resolve the existing surrogate under current access instead.
- A sender mapping change can affect later files and requires configuration authority. The held-record alternative is an explicit, scoped group correction with separate release and revalidation.
- Always using router `replaceUrl` would erase useful Back history. Applied filters push history; only server page corrections replace the current URL.
- Reusing the normal address save could not correct an existing year: that operation only fills missing values. The explicit place-correction operation performs the update under a version check in the same transaction as the person-place change.
- Exposing edit forms alone was insufficient for sample/source concurrency: Dapper's default legacy datetime mapping lost milliseconds. Their original timestamps are sent as `DbType.DateTime2`.

## Sample import

See `sourceCode/samples/environmental_samples.csv`. Required columns are `sampleType,sampleValue,unit,sampleDate`; dates use `yyyy-MM-dd`. Optional columns are `roomOrComponent,surface,notes`. Uploads accept 1–500 readings and at most 1 MB. XRF paint readings use `xrfPaint` and `mg/cm2`; vendor-specific exports must first be converted to this documented format. Any invalid row rejects the entire file. Retrying the same form submission adds no duplicate readings.

## Validation

The change includes unit coverage for date-first and compound-name search and CSV validation; database tests for finding resolution, exact group correction boundaries, source/job/place edits, stale tokens, case reopening and task scoping, sample import/edit/void/retry, overdue filtering and search-first intake; and tablet/browser checks for navigation, keyboard focus, clinical match context and sample correction. Existing authorization and accessibility suites are also run.

Results: Angular production build passed; 171 unit tests, 71 database integration tests and 91 tablet/browser/accessibility tests passed. Final record/view refinements also passed 9 targeted integration checks. SQL conventions passed for 234 scripts, and Git whitespace checks passed.

Repeated integration runs exposed an existing 8 KB limit in the jurisdiction's list of serving tenants. Both summary aggregations now use `nvarchar(max)` so larger datasets render without truncation or failure.

AI contributor: OpenAI Codex. GitHub's contributor graph depends on commit identity; no unverified GitHub account or email was added as a coauthor.

## PR #123 review follow-up

All three review findings were verified and addressed:

- The isolated SQL Server returned NULL for an empty scalar `FOR JSON PATH` subquery. Employment reads now coalesce analyte JSON to `[]`, and the editor defensively accepts null or blank JSON. Integration coverage opens the editor after removing all analytes.
- Five procedure definitions combined parameters on individual lines, causing missing parameters and malformed types in the test manifest. Definitions now use one parameter per line, and the parser recognizes inline separators while preserving decimal precision and quoted defaults. Generated scripts and the manifest were refreshed. Parser regression tests cover both layouts; an integration check compares every tenant-data web procedure's parameter names with the deployed catalog. Cross-tenant tests now accept only known validation, permission, concurrency, constraint and row-security refusals, allowing signature/type errors to fail the suite.
- Clearing an existing source's product name now detaches its product. Integration coverage verifies the link becomes NULL while the product record remains intact, alongside the existing preservation and stale-edit checks.

Review fixes were implemented with assistance from OpenAI Codex.

Follow-up validation: 171 unit tests, 72 SQL integration tests (zero skipped), two Python manifest-parser tests, and SQL conventions for 234 scripts passed. The .NET web project built successfully. Browser tests were not rerun for these server/generator changes.
