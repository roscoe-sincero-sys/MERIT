# Issues #115 and #116 verification

Verified against merged `origin/main` at `c37f688`.

## Issue #115: already resolved

The current client warns two minutes before the server-provided expiry deadline and offers a session check. Expired Razor and Angular requests retain in-memory drafts, offer sign-in in another tab with the current local return path, and require the original user before resuming. A 401 does not redirect over the draft. Existing browser regressions passed for warning timing, expired Razor/Angular drafts, return links, early expiry, and avoiding repeated expiry notifications.

Retained that implementation. Adding the originally suggested automatic `HX-Redirect` would replace the current document and undermine draft preservation. The existing accessible warning/session-check approach also avoids interrupting users copying notes.

## Issue #116: remaining fixes

- MFA verification and enrollment now offer a CSRF-protected POST to sign in as a different user. It authenticates the pending-MFA scheme and clears that cookie and its pending return state. An expired pending ticket returns to sign-in through authentication.
- Recovery codes have explicit Copy and Print actions, copy status/fallback instructions, and an acknowledgment before continuation. Codes remain on the one-time response; no browser storage was added. Print styling removes navigation and action controls. A noscript submit alternative remains subject to the server acknowledgment check.
- The validated local destination is retrieved from pending authentication properties and carried through enrollment and recovery-code continuation, including query parameters. External destinations fall back to `/`. The MFA verification path uses the same local-destination handling.
- Session sign-in updates the current request principal so the recovery response generates antiforgery tokens for the newly authenticated identity. Integration testing caught and verified this identity transition.
- Sign-in, verification and enrollment focus their primary field when there are no ModelState errors. Existing summaries and field associations were already implemented and are retained.

## Validation

.NET build and all 173 unit tests passed. The final main integration run passed 75 tests, including the new pending-restart/enrollment/destination regression. The known intermittent session-renewal test failed once and passed its isolated rerun. All 29 selected browser and accessibility checks passed, including real pending-MFA restart, recovery-code Copy/Print, acknowledgment, preserved destination, validation focus, and the existing #115 expiry/draft regressions. Git whitespace checks passed.

AI-assisted implementation: OpenAI Codex.
