# Issue #117 verification

Verified against merged `origin/main` at `698a21d`. All four workflow findings remain valid.

- Profile switches now request the current working tenant when no new tenant is supplied. Existing session-scope SQL retains it only when permitted by the target profile and otherwise selects an allowed fallback; no authorization rule was loosened. Profile Use forms also carry the current tenant. The header supplies the current local page/query as return context, the profiles page offers a Return to previous page link, and switch actions accept only local return paths. Direct visits stay on My profiles after switching.
- Add another grant appends an indexed row without replacing the form or losing existing edits. It resets the added row to empty place/read access and moves focus to it, retaining the existing master-grant choices and server validation. Client insertion was chosen over an htmx round trip so draft values remain in place. Rows are capped at 100 in the UI.
- Listed users have a live partial-name/user-name/tenant filter with an announced result count. Exact external-account lookup is in a separate disclosure panel that opens when it has an error. Filtering affects only the already-authorized list; account discovery and access checks remain server-controlled.
- Existing counties are presented as labeled checkboxes. Leading-zero FIPS values are retained, submitted selections are normalized to the existing service input, and current county-scope validation remains authoritative. Selections survive validation errors. The manual FIPS fallback remains when no counties are configured, and the existing new-county workflow is retained.

Validation: .NET build and all 173 unit tests passed. The main integration run passed 75 tests; the known intermittent session-renewal test passed its isolated rerun. The new profile-context integration test passed, verifying working-tenant retention and local-only return paths. All three new browser/accessibility checks passed; the existing admin-confirmation regression also passed. Browser authentication setup now waits for scripts and an enabled acknowledgment action, addressing an observed setup race. Git whitespace checks passed.

AI-assisted implementation: OpenAI Codex.
