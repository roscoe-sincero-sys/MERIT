# Issues #105 and #106 verification

Verified against merged `origin/main` at `87c0cf1`.

- #105 is already resolved. The shared dialog contains its own alert region, modal htmx requests send `X-Merit-Dialog`, and the exception filter retargets errors into the dialog. Existing regression coverage verifies permission and validation failures preserve inputs. Extended the modal browser regression to cover concurrency (409) and unexpected-server (500) responses as well.
- #106 is partially resolved. Cluster controls already use the server-provided `canEdit` capability, and read-only regression coverage exists. However, the add form still reset immediately, and case lookup was absent.

The add form now resets only after a successful response. Its value remains visible while a request is pending and after failure. The case input offers suggestions by last name or case number, using the existing tenant-scoped case list through a `case.view`-protected endpoint, capped at ten results. Suggestions are ordinary keyboard-accessible buttons; choosing one populates the exact case number and requires explicit Add case confirmation. Exact-number entry remains supported. Debounced searches cancel older requests, clear obsolete results, and clean up when leaving the page.

Recommendation checks: retained the existing modal routing and server-derived editing capability. Adopted success-only reset and case suggestions without duplicating search SQL or trusting a client permission list in place of per-record capabilities. Integration coverage verifies name/number lookup and exclusion of another tenant's case.

Validation: Angular production build and 171 unit tests passed. The integration run passed 72 tests including the new picker isolation test; one unrelated session-renewal test failed transiently and passed on its isolated rerun. Browser results are recorded in the PR.

AI-assisted implementation: OpenAI Codex.

Browser validation: 25 recovery/modal/read-only regression checks passed, followed by the new picker regression passing on the final label correction (26 checks total). The picker check also runs the accessibility audit. Git whitespace checks passed.
