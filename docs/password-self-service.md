# Password self-service

People can change their own password, and reset a forgotten one by email. A reset never skips the second factor. Administrators can still set a password, and can ask for a new one at the person's next sign-in. Issue #190.

| What | Where | Needs |
|---|---|---|
| Change password | **Sign-in methods** (`/account/security`) | current password, new password twice, a current authenticator code |
| Forgotten password | **Forgot your password?** on the sign-in page (`/account/forgot`) | the emailed link, plus an authenticator code or a recovery code |
| Administrator sets a password | the user's page (`/admin/users/{id}`) | `tenant.administer` for the person's home tenant |
| "Choose a new password at next sign-in" | a checkbox on the same page | the person's own password and second factor at sign-in |

## Security
- **Password rules (NIST SP 800-63B-4):** the jurisdiction's `passwordMinLength` (at least 8), a check against breached-password lists, no composition rules, and not the password in use now.
- **The second factor is always needed.** The emailed link only proves the mailbox. The reset page also asks for an authenticator code or a recovery code, and a recovery code is used up. An account that has never set up an authenticator gets no link; its administrator resets it instead.
- **No enumeration.** "Forgot your password?" shows the same page for every user name. The lookup and the email happen off the request, in a background queue, so the response time doesn't vary either.
- **Reset links:**
  - Each carries a random 256-bit token, stored only as an HMAC under `EmailCodes:VerifierKey`.
  - A link works once, for 20 minutes. Five wrong second-factor codes close it, and a newer link supersedes it.
  - Limits: a 60-second cooldown, 3 links per account and 10 per client address per hour, and the `Merit:EmailCodeSendsPerMinute` rate limit per address.
- **No reset-link poisoning.** Links are built from `Merit:PublicBaseUrl` (https), never from the request's Host header. Outside Development, forgotten-password links aren't offered until that setting exists.
- **Keeping the token out of view.** Opening a link moves the token into an encrypted, HttpOnly cookie and redirects to a clean `/account/reset`, so the token isn't left in history. The page sends `Referrer-Policy: no-referrer` and `Cache-Control: no-store`.
- **Lockout:**
  - A wrong current password or code counts toward the account lockout (5 failures, 15 minutes), as at sign-in.
  - A locked account can't use a reset link until the lockout ends.
  - A successful reset clears the lockout.
- **Sessions end.** Every successful change or reset changes the security stamp and revokes all of the account's sessions. A signed-in change starts a fresh session for the same browser. Open reset links close. An MFA reset also closes them, because a link would need the old second factor.
- **A required change can't be skipped.** The flag travels with the session from the database inside the encrypted session cookie. Until the new password is set, every page redirects to `/account/password/required`, and the API answers 409.
- **Notice.** After a change or reset, MERIT emails "Your MERIT password was changed", if mail is configured and the account has an address.
- **Audit** (`logs.auditEvent`):
  - `passwordChanged` (by the person);
  - `passwordResetRequested` (a link was submitted to the mail server);
  - `passwordResetCompleted` (a link was used);
  - `passwordSetByAdmin` (the administrator is the actor, the person is the record).
- **Logging.** Passwords, tokens, links and email addresses never reach the application log. Log lines carry the user id, support reference and correlation id.

## Configuration
| Setting | Default | Notes |
|---|---|---|
| `Merit:PublicBaseUrl` | none | The https address people use, for example `https://merit.health.example.gov`. Required outside Development for forgotten-password links. Development falls back to the request's address. |
| `PasswordReset:LinkLifetimeMinutes` | `20` | |
| `PasswordReset:MaxCodeAttempts` | `5` | Wrong second-factor codes per link. |
| `PasswordReset:CooldownSeconds` | `60` | Between links for one account, while the previous link is still open. |
| `PasswordReset:MaxLinksPerAccount`, `MaxLinksPerAddress`, `LimitWindowMinutes` | `3`, `10`, `60` | |

Links are emailed through the same SMTP settings as email codes (`Email:*`, [email-codes.md](email-codes.md)). Without a mail server, the forgotten-password page tells people to contact their administrator.

## Testing
- `PasswordSelfServiceTests` (integration) covers:
  - the same answer for known and unknown names, and links built from the public base URL;
  - the cookie hand-off and the `no-referrer` header;
  - wrong codes, breached and mismatched passwords, reset with an authenticator code or a recovery code;
  - sessions revoked, links that are used, superseded, expired, tampered with or closed after wrong codes;
  - the per-account limit, accounts without an authenticator, the signed-in change, and the administrator's required change;
  - the audit events, the notices, and nothing secret in the log.
- `PublicBaseUrlTests` (unit) covers Host-header poisoning and http refused outside Development.
- `password-self-service.spec.ts` (Playwright with Mailpit) covers the whole flow in a browser, with axe on seven page states.
