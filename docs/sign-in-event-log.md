# Sign-in event log

Every sign-in step and account-security change is recorded in `logs.signInEvent` with enough to diagnose a problem or spot an attack. The audit ledger (`logs.auditEvent`) keeps its own minimal `signIn` and `signInFailed` rows, unchanged, for tamper evidence. Issue #191.

## What is recorded
| Group | Events |
|---|---|
| Password step | `passwordSucceeded`, `passwordFailed` (reason `wrongPassword`), `unknownUser`, `accountInactive`, `attemptWhileLocked`, `accountLocked` |
| Second factor | `mfaSucceeded` and `mfaFailed`, each with the method (`totp`, `recoveryCode`, `emailCode`) and a reason (`wrongCode`, `expired`, `superseded`, ...); `mfaEnrolled`; `recoveryCodeUsed` |
| Sessions | `sessionStarted` (ties the attempt to the session), `signedOut`, `sessionExpired` (`idle` or `lifetime`), `sessionRevoked` (`passwordChange`, `passwordReset`, `mfaReset`, `deactivated`, `securityStampChanged`), `rateLimited` (the policy that refused) |
| Passwords (#190) | `passwordChanged`, `passwordResetRequested` (with the outcome: `linkSent`, `cooldown`, `limit`, `notAvailable`, or `notSent:<category>`), `passwordResetCompleted`, `passwordSetByAdmin` |
| Administrators | `unlockedByAdmin`, `mfaResetByAdmin`, `accountDeactivated`, `viewAsStarted`, `viewAsEnded`. These rows record the administrator as `actorUserId`. |
| Email codes (#186) | `emailCodeSent`, `emailCodeNotSent`, `emailCodeRefused`, `emailCodesEnabled`, `emailCodesDisabled` |

Each event carries:
- time, step, method and reason;
- the account, the acting administrator, and the data root;
- the failed-attempt count and lockout end the event left behind;
- the client IP address (after trusted forwarded headers) and the raw `X-Forwarded-For` chain when a proxy is configured;
- the user agent (truncated to 400 characters);
- the request's trace id, which also appears in the application log;
- the **session reference**: a non-secret id on `auth.sessionScope`, never the scope key, which is the row-level security key in the cookie;
- the **attempt id**: one per sign-in, carried from the password step to the second factor in the pending-MFA cookie's properties. It is not a claim, because claims bind antiforgery tokens to the identity.

Each event is also written to the application log as a structured `[LoggerMessage]` line with the same fields.

## Names that match no account
A user name typed for an account that doesn't exist may be an attack, or a password typed into the wrong box. It is **never stored or logged in plain text**:

| Copy | How | Who can use it |
|---|---|---|
| `attemptedNameHash` | HMAC-SHA256 of the name, trimmed and lower-cased, under `SignInLog:NameHashKey` (else `EmailCodes:VerifierKey`) | Anyone who can see the report. It groups repeated guesses at the same name and answers the name lookup. The application log shows only its first 16 hex characters. |
| `attemptedNameProtected` | ASP.NET Core data protection, purpose `Merit.SignInLog.AttemptedUserName`, on the key ring that already protects authenticator secrets | Holders of `security.investigate`, through **Show names**, after a current authenticator code |

Attempts at unknown names belong to no jurisdiction, so they are investigated at the platform level.
- The new permission `security.investigate` is granted to the Platform administrator role by default. An agency can move it to dedicated security staff.
- Every procedure that returns these rows checks the permission.
- Reveals, lookups and exports are written to the audit ledger (`view`, `search` and `export` of `unknownUserNames`, with row counts).
- The page warns that names may include a password typed in the wrong box.
- If a data-protection key is removed from the key ring, the names it encrypted can no longer be read. The page then shows "(no longer readable)".

## Screens
- **User page** (`/admin/users/{id}`), for an administrator of the person's home tenant: the latest 50 events.
- **Sign-in activity** (Administration menu, `tenant.administer` or `security.review`) shows, for the last hour, day, week or 30 days:
  - one address trying five or more accounts or names (spraying);
  - one account tried from three or more addresses (stuffing);
  - a sign-in after three or more failures within the hour before it;
  - failures by address and by account;
  - lockouts and rate-limit refusals.

  Tenant administrators and security reviewers see the accounts homed **or holding a current grant** in the tenants they administer or review (#270): a two-county worker appears for both counties. Holders of `security.investigate` see everything, including unknown names (hashed) and refusals that belong to no account. **Export this report (CSV)** downloads every section with its section name (`/admin/sign-in-activity/activity.csv`, audited; #272).
- **Unknown user names** (`security.investigate`): names grouped by hash with attempts, addresses and first and last time. It offers **Look up a name** (hashes what you type; nothing is decrypted), **Show names** (needs a current authenticator code), and a hashed CSV export (`/admin/sign-in-activity/unknown-names.csv`). Names are never exported.
- **Audit log:** the action filter **Sign-in and account security (all)** narrows the ledger to those actions. Scope (#270): a reviewer who covers a data root's top tenant (a state administrator; a platform or region administrator for the roots below it) sees that whole root. Anyone else sees events by the users homed or granted in the tenants they review, plus events by anyone on records those tenants owned when the event was written (`logs.auditEvent.owningTenantId`, stamped from #270 on; earlier events are scoped by user only).
- **Users** (`tenant.administer` or `security.review`) lists accounts and their current access; **Export users and access (CSV)** is the access-review file (`/admin/users.csv`). An account's page offers its sign-in history as CSV (`/admin/users/{id}/sign-in-history.csv`). Both exports are audited (#272).

## Security officer role (#271)
Every data root has a **Security officer (security reports, read only)** role: `audit.view`, `usage.view` and `security.review`. Grant it at the tenants the officer reviews, for example one county. The officer sees the audit log, sign-in activity, users and access, and screen usage for those tenants, with exports. They cannot change accounts or grants and see no patient records unless another role gives them that. A state or county administrator can grant it, because the Administrator role now includes `security.review`.

**Unknown user names stay platform-only, on purpose.** A name that matched no account belongs to no tenant, so it cannot be scoped to a county; `security.investigate` therefore remains on the Platform administrator role.

## Retention
The worker runs `logs.uspPurgeSignInEvents` daily. Events older than `SignInLog:RetentionDays` (default 365, at least 30) **lose their personal data**: the address, forwarded chain, user agent, and both copies of an attempted name. The row is then marked deleted, leaving an anonymous count.

Rows are kept rather than deleted because this database never hard-deletes; the SQL convention gate refuses `DELETE`. The ledger's `signIn` and `signInFailed` rows are never purged.

## Configuration
| Setting | Host | Default | Notes |
|---|---|---|---|
| `SignInLog:NameHashKey` | Web | `EmailCodes:VerifierKey`, else random per process | Base64, 32 bytes or more. Use the same value on every web server; without one, hashes don't match across restarts or servers, and a warning is logged. |
| `SignInLog:RetentionDays` | Worker | `365` | At least 30. |
| `Merit:ForwardedHeaders:KnownProxies` | Web | none | Needed behind a proxy, so addresses are the client's, and so the forwarded chain is recorded. |

## Testing
- **`SignInEventTests`** (integration, 7 tests). They cover:
  - every event type: password, second factor (authenticator, recovery code), lockout, inactive account, rate-limit refusal, sign-out, administrator actions with the actor, view-as;
  - idle, lifetime and security-stamp session ends, using a movable test clock;
  - the attempt id and session reference, and that the scope key never appears;
  - names hashed and encrypted, never in plain text in the table or the log;
  - report scoping, the step-up for reveal, and audited reveal, lookup and export;
  - the audit-log filter;
  - retention run with the worker's own login.
- **`sign-in-activity.spec.ts`** (Playwright with axe) checks the report, the user page, the hashed list, a lookup and a reveal.
