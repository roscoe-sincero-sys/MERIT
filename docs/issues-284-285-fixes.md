# Administration auditing and adopted testing policies (#284 / #285)

Implemented by **Codex (OpenAI coding agent)** on behalf of `roscoe-sincero-sys`, from merged main `e86aa47` (PR #283), 2026-11-12. Both issue recommendations were checked against the procedure bodies, application services, report scope and existing clinical tests.

## #284: administration audit history

The reported gap was present. Account, assignment, tenant and profile mutations did not write the audit ledger, apart from a few password/email events. Application-service audit calls would leave a failure window after commit, so the new events are inserted inside each mutation's existing database transaction.

- Accounts: create, update, deactivate, reactivate, unlock and administrator MFA reset. Creating an account also records its initial master profile. Administrator-triggered password/email-security events identify the administrator as actor and the subject account's home tenant as owner.
- Assignments: grant, update and end, with immutable account/role/tenant IDs, access level, descendant flag and effective dates. The report calls an end event **Ended grant**; the existing inclusive assignment dates are retained.
- Tenants: create and successful existing-tenant calls through the authorized root/child entry procedures. Internal installers remain ungranted.
- Custom profiles: create, update and deactivate, with an event for each affected old/new tenant. Deactivation is the existing supported alternative to physical deletion; history is retained.
- County events use the affected tenant's root/owner, rather than the administrator's home root. This lets Harford see the platform's Harford grant and Maryland administration there. Explicitly owned events are scoped to their owner; actor-based fallback is retained for older/unowned events. The audit user filter uses the same scope.
- The additive `logs.auditEvent.administrationJson` column holds identifiers, access codes/flags and grant dates. No user-entered names, descriptions, email addresses, passwords or MFA secrets are copied into it. The page displays these snapshots and the CSV adds an **Administration details (IDs)** column. The action/type filters include the new events.

The existing outside-actor review also recognizes these administration events: a platform or statewide administrator who acted on a county may appear in that county's security review. This grants no ability to manage their account. Earlier missing administration events cannot be reconstructed reliably from last-modifier columns and are not backfilled.

## #285: explicit policy adoption and reference history

The reported cross-state behavior was present. A checkbox did not limit the save procedure to Maryland, and an existing testing plan could trigger Maryland closure checks in another state.

The fix implements explicit per-root adoption using the existing effective-dated jurisdiction settings, rather than only adding a Maryland page check:

1. **`testingPolicyVersion` defaults to `none`**, for new roots and upgrades. The settings page offers no policy or `MDH-2025-03`. Only a session with configuration authority at the data root, clipped to its active profile/view-as rights, can adopt or disable it; a county configuration grant is insufficient.
2. The supported policy is a versioned built-in definition. `MDH-2025-03` is accepted only for an actual Maryland root, independent of editable/report-supplied state hints. Supporting another state's policy requires its own reviewed definition; changing a label cannot activate Maryland's rules there.
3. The workspace reads policy adoption from the **patient's** root, including for regional staff. An inactive/unconfigured root shows a neutral message and no Maryland forms or links. A direct save is refused in SQL. Automated testing-review creation and the Maryland below-level closure guard use the same adoption check.
4. Confirmation, the earlier-unconfirmed-capillary check, below-reference monitoring, discharge readiness and closure read child whole-blood lead case/reference thresholds as of **each source draw date and patient age**. The latest applicable effective period wins; missing/conflicting reference values fail with a configuration message. The saved plan snapshots its adopted version and anchor reference level. The policy's existing interval bands and urgent clinician-review requirements remain in its definition.
5. Existing plans are retained. A legacy Maryland plan or Maryland-looking report in Virginia cannot activate the policy or impose Maryland closure requirements.

The optional synthetic Maryland demo explicitly adopts its policy on its initial data load and records reference history (5 for July 2020–2023; 3.5 from 2024). Normal installations do not activate it. The historical-care test now uses an elevated 2020 value of 6, rather than treating 4 as elevated against the historical reference of 5; its current-care decision and source-date assertions remain.

## Deployment and validation

Deploy the additive schema/procedure changes normally. Existing program roots stay off until their root configuration administrator reviews and activates a supported policy in **Configuration → Settings**, with applicable reference history in **Thresholds**. Existing ledger rows and clinical plan records remain intact. CSV consumers should allow the additional details column.

The procedure definition bodies were synchronized with the changed deployed SQL, preserving previously merged security/ownership guards when these definitions are generated. No new public procedure parameters are required.

| Final check | Result |
|---|---|
| Full integration suite, fresh `MERIT_FIX284285_CODEX_FINAL_20261112` | **225 passed**, zero failed; two opt-in replays skipped |
| Extended #267 replay, fresh `MERIT_SCENARIO_267_CODEX_284285_FINAL_20261112` | **Passed**, all 23 steps, no unexpected behavior; 4m21s test body |
| Unit, architecture and manifest tests | **338 passed**, five optional local mail-service tests skipped |
| Procedure generator tests | **Three passed** |
| Upgrade of older synthetic `MERIT_SCENARIO_267_CODEX_FIX282_20261112` | Passed: append-only ledger retained (421 rows before, 426 after deployment's new events), new column/action constraint present; all eight existing roots have `none`, zero active testing policies |
| `git diff --check` | Passed |

The extended replay retained all 72 Virginia isolation probes and added a direct Virginia plan refusal. The Harford audit contained **120 rows / nine actors**, including the platform and Maryland administrators, with Baltimore City-only activity excluded. Maryland confirmation/follow-up/discharge checks passed after explicit adoption.

Local, ignored evidence is under `sourceCode/.artifacts/issue284285/`: `fix284285-final-full.trx`, `fix284285-final-scenario267.trx`, `scenario267-final-evidence.txt`, `fix284285-final-unit.trx`, and diagnostic runs. Logs are `sourceCode/.artifacts/fix284285-final-{full,scenario267,unit}.log` and `sourceCode/.artifacts/fix284285-upgrade.log`.

Dedicated synthetic SQL databases and real HTTP requests were used; production data was untouched. The regression coverage includes transaction rollback, audit actor/subject separation and county exclusion, state policy opt-in/off and root-only authorization, configured reference levels through closure, a legacy Virginia plan with a Maryland-looking report, and the full #267 replay extended with Virginia refusal and Harford administration-grant assertions. No browser usability or Playwright run was performed; views compiled and were exercised over HTTP.
