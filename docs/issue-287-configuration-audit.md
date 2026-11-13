# Configuration audit history (#287)

Implemented by **Codex (OpenAI coding agent)** on behalf of `roscoe-sincero-sys`, from merged main `b858a05` (PR #286), 2026-11-12.

## Verified recommendations and decisions

- **Settings and thresholds:** the reported gap was confirmed. Their procedures and services wrote no ledger event. Both procedures now append one event inside the existing mutation transaction, identifying the actor, affected root, record ID and before/after configuration. A rolled-back or refused mutation leaves no change event.
- **Notification rules:** creation and retirement already had application-service audit calls, after commit. Approval and activation happen together when an immutable version is created; there is no separate persisted draft/approval step to audit. These calls were moved into the procedures, with an approved/active creation snapshot and a retirement snapshot. This closes the transaction gap and retains one event per operation without duplicate service events.
- **Values:** copying every raw value would be unsafe. The existing settings API also accepts free-text and extension values, including strings in some nominally numeric settings. A shared helper records only known keys and canonical integers or finite codes. Free-text values (including agency names), unknown keys and unrecognized values are redacted, with explicit flags. This does not add new setting validation or change accepted values. Threshold/rule snapshots include identifiers, validated codes, numeric levels, activation/approval flags and effective dates; authority text, rule/calendar names, approval references and other free text stay out of the append-only ledger.
- **County visibility:** root-owned setting, threshold and notification-rule events apply to every county in that data root and are visible read-only to those reviewers. This exception is limited to those three configuration record types. Sibling-owned rules and other roots' events remain excluded, as do unrelated root-owned clinical or administration records. The audit actor filter and outside-actor account review use the same configuration scope; no configuration-edit or account-management rights are granted.
- **Root isolation:** setting history reads/updates now explicitly filter the acting data root. A regional session can see multiple roots; without this filter, one setting save could rewrite descendant roots while recording only one root's event. A regression verifies a region's change leaves Maryland and Virginia values intact.
- **Reports:** Setting and Threshold are available in the record-type filter. Snapshots use the existing **Administration details (IDs)** CSV column; export columns are unchanged. The page explains that free-text configuration values are excluded.
- **Query cost:** the replay exposed repeated grant expansion in the inline account-review query. Its existing public function now delegates to a helper that materializes the reviewed tenant set once. Visibility rules are unchanged. A diagnostic application-user query on the completed Virginia hierarchy took 262 ms cold and 67–69 ms warm, compared with approximately two seconds per history call in the initial replay. The final fresh replay averaged about 40 ms across 304 history calls at its progress checkpoint; these are local diagnostics, not a production performance guarantee.

## Recorded history

Setting events identify the new effective-dated row, previous row ID, known setting key, requested effective date, safe old/new values and redaction flags. The old value is selected at the requested effective date, so same-day replacement preserves its previous value in immutable history even though the earlier mutable row is superseded.

Threshold events distinguish creation, update, deactivation and reactivation and snapshot the stored values, comparator, age band, action identifiers and effective dates. Notification creation records approval/activation together and the approving account ID; retirement records the active-state transition. Snapshots omit free-text fields rather than suggesting that every field is preserved.

The existing policy-adoption guards, threshold validation, date/version behavior and clinical testing policy checks remain in place. Earlier missing configuration events are not reconstructed or backfilled. Older events with unknown ownership are not reclassified as global configuration events.

## Validation

The five focused regressions passed: same-day history and redaction, state/platform/county visibility with cross-root refusal, threshold transitions and stale-write refusal, atomic notification lifecycle with sibling exclusion, rollback of all four mutation procedures, and a regional change isolated from descendant roots.

| Final check | Result |
|---|---|
| Full integration suite on fresh `MERIT_FIX287_CODEX_FINAL_20261112` | **230 passed**, zero failed; two opt-in replays skipped |
| Extended #267 replay on fresh `MERIT_SCENARIO_267_CODEX_287_FINAL_20261112` | **Passed**, all 23 steps, no unexpected behavior; 3m37s test body |
| Unit/architecture/manifest suite | **338 passed**, five optional local mail-service checks skipped |
| Procedure-generator checks | **Three passed** |
| Upgrade of older synthetic `MERIT_FIX284285_CODEX_FINAL_20261112` | Passed; retained append-only ledger (1,229 rows before / 1,234 after deployment's tenant events), preserved its one active testing policy, final helper/wrapper upgraded in place |
| `git diff --check` | Passed |

The #267 replay now changes a Maryland security setting and verifies that Region 3, platform and Harford reviewers see it and the testing-policy adoption in the filtered setting export. Harford's audit contained **140 rows / nine actors**, with Baltimore City-only activity excluded. The original clinical and 72 Virginia isolation checks passed.

Local evidence is under `sourceCode/.artifacts/issue287/`: `fix287-final-full.trx`, `fix287-final-scenario267.trx`, `final-scenario-evidence.txt`, `fix287-final-unit.trx`, focused runs, and `performance.sql` / `performance-results.txt`. Logs are `sourceCode/.artifacts/fix287-*.log`. Dedicated synthetic SQL databases and real HTTP requests were used; production data and the pre-existing untracked file were preserved. No browser usability or Playwright run was performed; views were compiled and exercised over HTTP.
