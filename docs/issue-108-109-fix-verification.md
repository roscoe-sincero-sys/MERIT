# Issues #108 and #109 verification

Both issues remain valid on merged `origin/main` at `5935ff5`.

## Recommendation verification

- Held rejection now confirms the selected record count and reason before starting the request. Cancellation preserves selection and reason. Failure retains the reason for correction/retry; success clears it.
- Cluster removal confirms the case number and cluster title. Rejoining remains an ordinary action.
- Plain user-administration POST forms confirm MFA reset and ending a role grant. Prompts identify the account/grant and explain session termination. Reset MFA is hidden for unenrolled accounts. Plain forms use a delegated submit handler because `hx-confirm` alone would not apply to their ordinary browser POSTs.
- Successful user save, grant addition/removal, MFA reset and unlock set one-time TempData feedback rendered with `role="status"`. Assignment failures retain their separate error feedback.
- Locked accounts display their lockout expiry and an Unlock account POST action when the current administrator can manage the account. The application and generated SQL enforce `tenant.administer` plus administration of the account's home tenant. Unlock clears failed-sign-in count and lockout only; password, MFA, recovery codes, security stamp and account activation are untouched. Reusing MFA reset would clear security credentials, so a dedicated scoped procedure is used instead.

## Validation

Angular production build passed. SQL conventions passed for 235 scripts. All 171 unit tests passed. The main integration run passed 72 tests; the known intermittent session-renewal test failed once and passed its targeted rerun. The new unlock integration test also passed: another tenant's administrator cannot unlock the account, credentials/MFA/security stamp survive, feedback is consumed once, and unenrolled accounts hide MFA reset.

All 22 selected browser checks passed across the main run and corrected admin redirect fixture rerun. They cover cancellation before mutation, reason preservation/reset, named cluster removal, both plain admin confirmations, and existing pending-write/recovery behavior. Git whitespace checks passed.

AI-assisted implementation: OpenAI Codex.
