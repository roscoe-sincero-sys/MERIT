# Issue #112 verification

Verified against merged `origin/main` at `d93c259`. Both findings remain valid: inactive sortable headers emit `aria-sort="none"`, triggering the USWDS attribute selector, and every data row has `tabindex="0"` in addition to its native controls.

The fix removes `aria-sort` from inactive headers and displays an aria-hidden neutral sort indicator. The active header retains ascending/descending semantics and its directional indicator.

Rows now have one roving Tab stop, identified by the last focused record key. If that key is absent from current data, the first row becomes the entry point. Arrow Up/Down and Home/End move from focused rows; Space selects an eligible row. Events originating in cell links, checkboxes or buttons retain native behavior rather than being intercepted by row navigation. Modified keys and loading-state row actions are ignored.

Recommendation check: roving rows alone would still leave potentially hundreds of native checkbox/link Tab stops. A focus-visible “Skip table to pagination” link supplies a direct route while retaining native control access. Its handler focuses pagination within the same grid without changing the URL or discarding filters. The component remains a native table rather than introducing an incomplete ARIA grid interaction model.

Validation: Angular production build and .NET web build passed. The new 50-row browser regression verifies one active sort attribute, one row Tab stop, arrow/Home/End behavior, native checkbox behavior, direct pagination focus and an accessibility audit. Existing keyboard, sorting, selection, paging and recovery regressions were also run; all 45 selected browser regressions passed. Git whitespace checks passed. Backend/database code is unchanged.

AI-assisted implementation: OpenAI Codex.
