# Issue #107 verification

Verified against merged `origin/main` at `a9427e6`. The issue remains valid: the match-review host accepts bare letters and numbers outside input controls, and decision keys immediately submit mutations after initial heading focus.

Adopted the recommended modifier alternative: all match-review shortcuts now require Alt+Shift, including candidate selection, decisions, history and queue navigation. Visible instructions, decision labels and `aria-keyshortcuts` describe the combination. Number-row selection uses the physical digit code so Shift's punctuation does not break candidate selection. Repeat events and IME composition are ignored, and shortcuts remain inactive in input, textarea, select and contenteditable regions.

Adopted the recommended safeguard confirmation: linking a candidate with warnings requires an explicit confirmation containing the warning text and explaining that the result will be released. Cancelling leaves the record, draft and candidate intact. Both button and shortcut activation use the same decision method.

A timed undo was not selected: release permits a background worker to commit downstream changes, so an undo banner alone cannot promise safe reversal. Preventing accidental character activation and confirming flagged links implements the issue's recommended alternatives without implying a rollback guarantee.

Validation: Angular production build and .NET web build passed. Browser coverage checks bare-key non-mutation, editable-field exclusion, repeat/composition exclusion, warning confirmation/cancellation, and the existing queue, stale-response and decision workflows with Alt+Shift. All 45 selected browser regressions passed; Git whitespace checks passed. Backend code and database behavior are unchanged.

AI-assisted implementation: OpenAI Codex.
