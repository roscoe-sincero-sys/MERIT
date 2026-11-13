# Ending a role grant removes it at once (#295)

Implemented by **Claude (Anthropic coding agent, Claude Code)** on behalf of `roscoe-sincero-sys`, from merged main `fd09f98` (PR #294), 2026-11-13.

## The problem, verified

Pressing **End** on a grant saved today as its end date and signed the person out. `endDate` is the *last day* a grant applies, so every resolver still accepted the grant until midnight. A fresh password and MFA sign-in got the access back. I confirmed the cause in the code as the issue describes it, and the new regression test fails at the fresh-sign-in step when the resolver ignores the new column.

## Recommendations and decisions

| Recommendation | Decision |
|---|---|
| Give End an explicit revocation state checked by every effective-grant resolver; keep inclusive dates for scheduled expiry | **Implemented** as `auth.userAssignment.revokedUtc`. The row is not soft-deleted, so the grant stays in the account's history. |
| Keep revoking open sessions; a fresh sign-in or queued job must not reacquire the grant; do not disable the account | **Implemented.** Sign-in, profile switching and worker jobs all build their scope from `auth.tvfUserGrantPermissions`, which now excludes revoked grants. Other grants are untouched. |
| Do not fix by setting yesterday as the end date | **Followed.** The end date becomes today (or the start date for a grant that has not started, or stays as it is for one already expired), so `CK_auth_userAssignment_DateOrder` holds. |
| Real-HTTP regression: same-day grant, older grant, one of several; old session and fresh MFA sign-in; scheduled end dates and audit correct | **Implemented** in `GrantRevocationTests.cs`. Queued jobs are covered through the shared resolver, not by a separate job test. |

## What changed

- **Table:** `auth.userAssignment.revokedUtc DATETIME2 (3) NULL`, added additively. No data migration.
- **`auth.uspSaveUserAssignment`:** new `@revokeNow BIT = 0`. With 1 it stamps `revokedUtc` and sets the end date as above; the audit event is `revoke` and its details now include `revokedUtc`. A grant that is already ended is refused (422): add a new grant to give access back.
- **Resolvers that now require `revokedUtc IS NULL`:** `auth.tvfUserGrantPermissions`, `auth.tvfAssignableUserTenants`, `auth.tvfReviewAccounts` (current-grant branch), `auth.uspListMyProfiles`, `auth.uspListUsers` (the grants column) and `logs.uspRecordScreenVisit` (role snapshot).
- **Left as they are:** `auth.uspGetUserForSignIn` and `auth.uspGetEmailCodePolicy` treat anyone who has ever held an administrator grant as an administrator for MFA purposes. That is the stricter side, so I did not narrow it. The account and audit visibility checks (`uspGetUser`, `uspListViewAsUsers`, `uspListAuditUsers`, `uspSearchAuditEvents`) also still count ended grants, so administrators and reviewers keep seeing the account and its history.
- **Application:** `AdminService.EndAssignmentAsync` passes `revokeNow`. The account page shows "(ended by an administrator)" and offers **End** for any grant not yet ended, including one with a scheduled end date; previously a grant with an end date could not be ended early.

## Behaviour to know when deploying

- A grant ended *before* this change has only an end date, so it still works until midnight of the day it was ended. Nothing needs to be done after that.
- Setting a scheduled end date still means "through that day". Only End is immediate.
- The audit report shows the revocation time in the event details.

## Validation

See the pull request for the figures of the run that produced it. Tests added: `tests/Merit.IntegrationTests/GrantRevocationTests.cs` (ending the only grant, granted today and years ago: old session refused, fresh MFA sign-in refused, grant row and audit kept, second End refused; ending one of two grants keeps the other; a scheduled end date of today still works and can then be ended early).

`auth.uspListUsers` is one of the procedure scripts that has drifted from its generator definition, so its script and `procs_access.py` were edited together by hand; the other scripts match the generator.

Only dedicated synthetic SQL databases were used.
