# Issues #110 and #111 verification

Verified against merged `origin/main` at `c8ea1c2`.

## Findings and recommendation checks

- #110: exports still used the loaded report's filters while allowing edits without warning. The recommended disable-until-applied alternative is implemented: edited filter drafts remove download destinations, expose `aria-disabled`, and show an instruction to apply changes and wait. Exports are also unavailable while loading or after a report error. Filter controls and Apply are disabled during loading; successful responses restore export URLs for the loaded range.
- #110: chart buckets still summed all measures. Only known count columns are now summed. Multiple rows for a non-additive measure produce an omitted chart value and an explanatory message; the table retains every original value. Computing an average of percentages or a median of medians would not be valid without the underlying weights/distribution, so no replacement aggregate is invented.
- #110: chart month categories are now formatted using the same display helper as table cells, after sorting/grouping by the original keys. Text categories use known-code labels.
- #110: CSV and Excel timestamp values now explicitly identify UTC, with seconds retained. SQL datetime2 report instants are treated as UTC; offset-aware instants and local DateTime values are normalized to UTC. DateOnly values remain dates. This adopts the recommended UTC-label alternative rather than introducing jurisdiction-dependent export values.
- #111: the misleading shared value scale was already fixed. Timeline results occupy a date-only event lane at a constant height, and the screen explicitly explains that height does not compare values. Retained this safe alternative to per-analyte axes; threshold lines would not convey a meaningful value on this date-only lane.
- #111: raw place roles, case classifications/statuses and pregnancy outcomes remained in API text. They now use the shared server display names, and selected event kinds use the client Display helper.

## Validation

Angular production build and .NET build passed. All 172 unit tests passed, including a new CSV/Excel timestamp and date regression. The integration run passed 73 tests; the known intermittent session-renewal test failed once and passed its isolated rerun. All 14 selected report/chart/timeline browser and accessibility checks passed, including the new aggregation/export-filter test and the extended unlike-analyte timeline regression. Git whitespace checks passed.

AI-assisted implementation: OpenAI Codex.
