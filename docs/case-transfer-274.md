# Case transfer between jurisdictions (#274)

Baseline: merged main `1494730` (after PR #275), 2026-11-11.

## Why

Since #268, a county-scoped worker who records a result for a resident of another jurisdiction opens the case in their own county, because row-level security will not let them create work in the residence jurisdiction. The case carries a **"Case opened outside the residence jurisdiction"** task. Until now, nobody could move the case. The same need arises when a family moves.

## What the issue recommended, and what was checked

| Recommendation | Verified against the code | Implemented as |
|---|---|---|
| "A state (or both-county) user transfers a case" | Every case-related table has an `AFTER UPDATE` block predicate (`auth.tvfTenantWritePredicate`), so a move must land in a tenant the caller can write. | `case.edit` with **write access at both the current and the receiving tenant** (`auth.tvfSessionWritePermission`). That is exactly a state user, or someone who works in both jurisdictions. A one-county worker is offered no transfer, and the procedure refuses one (51403). |
| "…with its open tasks" | Moving only open tasks would leave the case's history and completed work invisible to the receiving county, which is the new owner. | All case-linked rows move: case activities, result links, tasks (open and done), notification obligations, exposure sources, patient activities and documents recorded on the case, cluster links and investigation links. |
| "…investigations" | An investigation can serve several sibling cases (`investigationCase`). Moving a shared one would take it away from the other cases. | Investigations that serve **only this case** move. Shared investigations stay with their owner, and only this case's link moves. The history entry counts both. |
| "Audited, keeps the case history" | Audit events now carry the record's owning tenant (#270). | An `exposureCase` update audit event, which the receiving county sees, plus a case-history entry: "Case transferred from A to B. Reason: …", with the counts moved. The original opening and earlier history are kept and move with the case. |
| "Closes the follow-up task" | | The open "Case opened outside the residence jurisdiction" task is completed with the transfer note. |
| Not in the issue | A case assignee who cannot work in the receiving tenant would keep a case they can no longer open. | Case, task and investigation assignees who are not assignable there are cleared. The patient's access rows are refreshed. |

Lab results stay owned by the data root, as every result is. Closed cases cannot be transferred; reopen first, so the jurisdiction that closed a case keeps it.

## Using it

On the case page, **Transfer case → Transfer to another jurisdiction…**:

- **Choices:** active jurisdictions and programs of the same state or tribe, plus the data root, where you can edit cases. The patient's residence jurisdiction is listed first and marked.
- **Required:** a reason and the confirmation "The receiving jurisdiction has agreed to take over this case".
- **After the transfer:** if you do not work in the receiving jurisdiction, you are taken back to the case list.

The routing task's text now points to this action.

## Objects

- `dbo.uspTransferExposureCase`: write, fully instrumented.
- `dbo.uspListCaseTransferTargets`: read, error-only instrumentation.
- `CaseService.TransferAsync` and `TransferTargetsAsync`, and `POST /cases/{id}/transfer`.

## Validation

`CaseOpenedOutsideTheResidenceIsTransferredWithItsWorkByAWorkerInBothJurisdictions`:

1. A Baltimore City-only clerk imports a result for a Baltimore County resident, so the case opens in the city with the routing task.
2. The city-only worker is offered no transfer and is refused.
3. An unconfirmed transfer is refused, with nothing changed.
4. A worker in both counties sees Baltimore County marked as the residence, requests an investigation, and transfers the case.
5. Afterwards:
   - the case, all its tasks and history, and the investigation belong to Baltimore County;
   - the routing task is done, and the history note and audit event exist;
   - a Baltimore County-only worker opens the case and the investigation;
   - the city-only worker no longer can.

Suite results:

- `Merit.Tests`: **338 passed, 0 failed**.
- Full integration suite on a fresh database (`MERIT_AUDIT_FIX274C_CLAUDE`): **216 passed, 0 failed, 2 opt-in replays skipped**.

### Test-isolation fix found on the way

The first full run failed `ClusterDetectionTests.DetectionReportsTheClustersItCreates` (2 clusters reported, 1 expected). The application was not at fault. The test asserts an exact, database-wide count. Other tests (`ManualReceiptCompletenessTests`, the #243 tests) leave open cases for the HL7 sample patients, who share one address and so form a cluster. Whenever those tests ran first, the count included that group. Adding a test method changed xUnit's ordering and exposed it.

- The test now runs one detection first to absorb pre-existing groups, then asserts the exact count for its own group.
- The two county-intake tests also retire their own cases in `finally`.
