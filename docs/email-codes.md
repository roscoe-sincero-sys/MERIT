# Email one-time codes

MERIT always asks for a second factor after the password. The authenticator app (TOTP) is required for every account. Email one-time codes are an **additional** way to finish signing in: a person can turn them on only where their jurisdiction allows them, and only if they hold no administrator role. The authenticator app and recovery codes keep working either way, so no account can lose its authenticator by using email codes.

> **NIST SP 800-63B-4 does not accept email for out-of-band authentication.** The specification keeps email codes off by default (`emailCodesEnabled = false`), and the Settings screen states this and asks for a confirmation before a jurisdiction turns them on. Administrators need phishing-resistant MFA, so they never get email codes.

Contents: [How it works](#how-it-works) · [Configuration](#configuration) · [Local mail tools](#local-mail-tools) · [Walkthrough](#walkthrough) · [Failure handling](#failure-handling) · [Logging and audit](#logging-and-audit) · [Testing](#testing) · [Production relay](#production-relay) · [Troubleshooting](#troubleshooting) · [What was verified](#what-was-verified)

## How it works

| Step | What happens |
|---|---|
| Policy | `config.jurisdictionSetting` `emailCodesEnabled` (per data root, effective-dated). Read at sign-in by `auth.uspGetEmailCodePolicy`, which runs as its owner (the setting is root-scoped reference data that row-level security hides before a session exists) and is granted to no role. Any administrator assignment keeps email codes off. |
| Turning on | **Sign-in methods** (`/account/security`, linked in the header). The person enters a current authenticator code (step-up; a stolen session alone is not enough), MERIT sends a code to the account's address, and the person types it. Only then is `auth.appUser.mfaEmailVerifiedUtc` set. |
| Sign-in | On the code step, **Email me a code** (or first, if the person prefers email). The code page shows the masked address, the expiry, a resend control with its cooldown, and links back to the authenticator and recovery codes. |
| Turning off | One button on Sign-in methods. An email-address change or an administrator's MFA reset also turns email codes off and cancels any open code. |

**Code security**

- Six digits from the operating system's CSPRNG (`RandomNumberGenerator.GetInt32`).
- Stored only as a keyed verifier: HMAC-SHA256 over the user, the purpose and the code, under `EmailCodes:VerifierKey`, which only the web servers hold. The code itself is never stored or logged.
- Five-minute lifetime (`EmailCodes:LifetimeSeconds`), one use, five wrong entries per code (`EmailCodes:MaxAttempts`). Wrong codes also count toward the account lockout (5 failures, 15 minutes), the same as wrong authenticator codes.
- A new code supersedes the open one. A filtered unique index allows at most one open code per account.
- Issuing, the cooldown and limits, superseding and consuming all happen in stored procedures under a lock on the account row. Concurrent clicks, tabs or web servers cannot get two working codes, and one code cannot be used twice (`auth.uspVerifyEmailChallenge` decides in a single `UPDATE`).
- A code is bound to its purpose: an enroll code never signs anyone in.
- Resend cooldown: 30 seconds (`EmailCodes:ResendCooldownSeconds`). A code that is already used, or whose send failed, does not hold back a new one. Limits are 5 codes per account and 20 per client address in 15 minutes (`MaxCodesPerAccount`, `MaxCodesPerAddress`, `LimitWindowMinutes`), plus an ASP.NET Core rate limit of 10 send requests per client address per minute (`Merit:EmailCodeSendsPerMinute`).
- Error messages say what to do: wrong (with the tries left), expired, replaced by a newer code, no longer usable. They never reveal anything about another account, and the password step already came first.

## Configuration

Mail is off unless configured (`Email:Provider` = `Disabled`). Without a mail server, email codes are not offered at all.

| Setting | Default | Notes |
|---|---|---|
| `Email:Provider` | `Disabled` | `Smtp` to send. |
| `Email:Smtp:Host`, `Email:Smtp:Port` | –, `587` | The relay. |
| `Email:Smtp:Security` | `StartTls` | `StartTls` (required; a server that does not offer STARTTLS is refused before credentials or the message are sent), `SslOnConnect` (implicit TLS, port 465), or `None`. `None` is accepted only for a server on this machine (`localhost`, `127.0.0.1`, `::1`). Startup fails otherwise. |
| `Email:Smtp:UserName`, `Email:Smtp:Password` | empty (anonymous) | Set both or neither. **Never in appsettings**: use user secrets in development and environment variables or a secret store in deployment (`Email__Smtp__Password`). |
| `Email:Smtp:FromAddress`, `Email:Smtp:FromName` | –, `MERIT` | Sender. Must be allowed by the relay's SPF and DKIM. |
| `Email:Smtp:ConnectTimeoutSeconds` | `10` | Per connection. |
| `Email:Smtp:OperationTimeoutSeconds` | `15` | Per SMTP command. |
| `Email:Smtp:TotalTimeoutSeconds` | `25` | The whole send, with retries and waits. The browser shows a "taking longer than usual" message after 30 seconds. |
| `Email:Smtp:MaxAttempts`, `Email:Smtp:RetryDelayMilliseconds` | `3`, `750` | Retries with doubling waits and jitter, only for failures that cannot have delivered the message. |
| `Email:Smtp:TrustedRootCertificatePath` | empty | PEM or DER root of a private CA the relay's certificate chains to. The host name and expiry are still checked. There is no setting that turns certificate validation off. |
| `Email:Smtp:CheckCertificateRevocation` | `true` | |
| `EmailCodes:VerifierKey` | random per process in Development | Base64, at least 32 bytes, the same on every web server. **Required outside Development when `Email:Provider` is `Smtp`.** Startup fails without it. |
| `EmailCodes:*` limits | see [Code security](#how-it-works) | |
| `Merit:EmailCodeSendsPerMinute` | `10` | Per client address. |

Create a verifier key and keep it out of source control:

```powershell
$key = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))   # PowerShell 7
# Windows PowerShell 5.1:
$bytes = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes); $key = [Convert]::ToBase64String($bytes)
dotnet user-secrets set "EmailCodes:VerifierKey" $key --project sourceCode/src/Merit.Web
```

To turn email codes on for a jurisdiction, a person with `config.edit` goes to **Settings**, sets *Email codes enabled* to `true`, ticks the NIST confirmation, and chooses **Change**.

## Local mail tools

Two capture tools are supported. Both run on 127.0.0.1 only, keep mail in memory, and have relaying off, so a message to a real address is captured, not delivered. Tests always use addresses at `example.test` (RFC 2606).

| | Mailpit v1.33.0 | smtp4dev 3.14.0 |
|---|---|---|
| SMTP | `127.0.0.1:1025` | `127.0.0.1:2525` |
| Web UI and API | `http://127.0.0.1:8025` | `http://127.0.0.1:8080` |
| Fault injection | Chaos (`-Chaos`): Sender, Recipient and Authentication triggers, error code 400–599, probability 0–100, from the UI or `PUT /api/v1/chaos` | none |
| Comes from | `Install-Mailpit.ps1`: pinned release, SHA-256 checked against GitHub's asset digest | repository-local dotnet tool (`sourceCode/.config/dotnet-tools.json`). 3.15.0 does not install as a tool with SDK 9.0.200, so 3.14.0 is pinned. |
| Launch profile | `Merit.Web (Mailpit)` | `Merit.Web (smtp4dev)` |

```powershell
cd sourceCode
.\tools\mail\Start-MailTool.ps1 -Tool Mailpit -Chaos      # or -Tool Smtp4dev; both can run at once
$env:ConnectionStrings__Merit = "<your development connection string>"   # as usual
dotnet run --project src/Merit.Web --launch-profile "Merit.Web (Mailpit)"
# open http://127.0.0.1:8025 to read the mail
.\tools\mail\Stop-MailTool.ps1                             # stops both
```

The launch profiles set only environment variables (`Email__Provider=Smtp`, host, port, `Security=None`, a sender). `appsettings.Development.json` is not changed. With Docker, `docker compose -f tools/mail/docker-compose.mail.yml up -d` starts the same tools on the same loopback ports.

Making Mailpit fail on purpose (Chaos must be enabled at start-up; a restart resets the triggers):

```powershell
$chaos = @{ Sender = @{ ErrorCode = 451; Probability = 0 }; Recipient = @{ ErrorCode = 550; Probability = 100 }; Authentication = @{ ErrorCode = 535; Probability = 0 } } | ConvertTo-Json
Invoke-RestMethod -Method Put -Uri http://127.0.0.1:8025/api/v1/chaos -Body $chaos -ContentType 'application/json'
# ... try "Email me a code" ... then set every Probability back to 0
```

## Walkthrough

1. Start Mailpit and run MERIT with the Mailpit profile (above).
2. As an administrator, turn on *Email codes enabled* on **Settings** (tick the NIST confirmation).
3. Sign in as a non-administrator account that has an email address, then open **Sign-in methods** in the header.
4. Enter the current authenticator code and choose **Send a code to …**. The code arrives in Mailpit within a second. Type it and choose **Confirm and turn on email codes**.
5. Sign out and sign in again. After the password, choose **Email me a code**, read the code in Mailpit, and choose **Verify and sign in**.
6. Optional: choose *An email code* under **At sign-in, offer first** to put the email option first.
7. Set Mailpit Chaos `Recipient` to `550`/`100` and ask for a code. The page shows *We couldn't send your verification code. Please try again. If the problem continues, contact support.* with a support reference such as `EM-7KQ2M9XA`. Set the probability back to 0 and choose **Try sending again**. No cooldown applies after a failed send.

## Failure handling

The sender (`Merit.Web/Infrastructure/Email/SmtpEmailSender.cs`, MailKit 4.18.1) classifies every failure. It retries only failures that cannot have delivered the message: a 4xx reply, or a connection, TLS or sign-in failure before the `DATA` command went out. A failure after `DATA` is never retried, because the server may already have the message.

| # | Scenario | Result category | Retried | Person sees | Tested by |
|---|---|---|---|---|---|
| 1 | Server unavailable (421 greeting) | `serviceUnavailable` | yes (3) | failure message + reference | fake server |
| 2 | Connection refused | `connectionRefused` | yes | same | fake server (closed port) |
| 3 | DNS failure | `dns` | no (yes only for a temporary DNS error) | same | `.invalid` host |
| 4 | Timeout (silent server) | `timeout` | yes before `DATA`, within the 25 s budget | same | fake server; bounded time asserted |
| 5 | Authentication rejected (535) | `authenticationFailed` | no | same | fake server |
| 6 | Expired certificate | `certificateExpired` | no | same | fake server + test CA |
| 7 | Untrusted CA | `certificateUntrusted` | no | same | fake server; also "private CA not configured" |
| 8 | Host name mismatch | `certificateNameMismatch` | no | same | fake server |
| 9 | STARTTLS unavailable or stripped | `startTlsUnavailable` | no; no credentials or message sent | same | fake server; commands asserted |
| 10 | Temporary rejection (451) | `temporaryRejection` | yes | same | fake server; Mailpit Chaos |
| 11 | Permanent rejection (550) | `messageRejected` / `senderRejected` | no | same | fake server; Mailpit Chaos (Sender 550) |
| 12 | Recipient rejected (550 5.1.1) | `recipientRejected` | no | same | fake server; Mailpit Chaos (Recipient 550) |
| 13 | Intermittent failure | submitted on the 2nd attempt | – | "We sent a code" | fake server (451 then OK; drop before message then OK) |
| 14 | Accepted but not delivered | submitted | – | "We sent a code" (MERIT never claims delivery) | fake server (accept and discard) |
| 15 | Duplicate requests | one code, one email | – | the rest see the cooldown notice | integration test (8 concurrent sends; 4 concurrent checks → 1 verified) |
| 16 | Send fails after the code was generated | the code is closed (`supersededUtc`), status `failed` | – | failure + reference; retry with no wait; the lost code never works | integration test |
| 17 | Browser waits too long | – | – | busy state, then "This is taking longer than usual…", controls usable again | Playwright |
| – | Connection dropped after `DATA` | `protocol`, not retried | no | failure + reference | fake server |
| – | Caller cancelled (browser gone) | `canceled`, recorded | no | – | fake server |

What the person sees never includes the server's reply or the failure category. Support finds them by the reference.

## Logging and audit

Every log line is structured (`[LoggerMessage]`). Lines carry the user id, purpose, support reference, correlation (trace) id, attempt count, elapsed time, failure category and numeric SMTP reply. They never carry the code, the email address, credentials or the server's text. The integration tests capture every log line and assert this. Example lines:

```text
info:  Email code request created for user 0192f8a1-… (signIn), reference EM-7KQ2M9XA, correlation 4bf92f3577b34da6a3ce929d0e0e4736
info:  Email code submitted to the mail server for user 0192f8a1-… (signIn), reference EM-7KQ2M9XA, after 1 attempt(s) in 41 ms, correlation 4bf9…
warn:  SMTP attempt 1 for reference EM-Q4T8ZP2D failed at Transfer: RecipientRejected, reply 550, will retry False, correlation 0af7…
warn:  Email code not sent for user 0192f8a1-… (signIn), reference EM-Q4T8ZP2D: RecipientRejected, SMTP reply 550, 1 attempt(s), retryable False, 12 ms, correlation 0af7…
info:  Email code check Wrong for user 0192f8a1-… (signIn), 4 attempt(s) left
```

`auth.mfaEmailChallenge` keeps the support reference, correlation id, delivery status, failure category, numeric reply and attempt count for each code. Audit events (`logs.auditEvent`, shown on the Audit log screen): `mfaEmailEnabled`, `mfaEmailDisabled`, `mfaMethodChanged`, `mfaCodeSent`, `mfaCodeNotSent`, `mfaCodeRefused` (cooldown or limit), `mfaCodeLocked`. Sign-in success and failure stay `signIn` and `signInFailed`.

To find a reported reference: `SELECT * FROM auth.mfaEmailChallenge WHERE supportReference = 'EM-…'`, then search the web log for the reference or the correlation id.

## Testing

| Suite | Command | Needs |
|---|---|---|
| Unit and SMTP fault injection (28 + code tests) | `dotnet test tests/Merit.Tests` | nothing |
| Integration (email-code flows) | `dotnet test tests/Merit.IntegrationTests --filter EmailCodeTests` | SQL Server (`MERIT_IT`) |
| Mailpit and smtp4dev (capture, Chaos 451/550, recovery) | `$env:MERIT_MAILPIT_URL='http://127.0.0.1:8025'; $env:MERIT_SMTP4DEV_URL='http://127.0.0.1:8080'; dotnet test tests/Merit.Tests --filter LocalMailToolTests` | the tools running |
| Browser and accessibility (axe on 7 page states) | `cd tests/e2e; $env:MERIT_E2E_DATABASE='MERIT_IT'; $env:MERIT_E2E_MAILPIT_URL='http://127.0.0.1:8025'; npx playwright test specs/email-codes.spec.ts` | app on the Mailpit (or smtp4dev, with `MERIT_E2E_SMTP4DEV_URL`) profile against `MERIT_IT` |

## Production relay

- Use the agency's authenticated relay (or a government email service) on 587 with `StartTls`, or 465 with `SslOnConnect`. Never `None`.
- Give MERIT its own relay account, restricted to the MERIT sender address. Put the password in the deployment's secret store and rotate it. Set `EmailCodes:VerifierKey` the same way.
- Publish SPF and DKIM for the sender domain, and DMARC with enforcement, so the codes are not marked as spam. Use a no-reply sender. MERIT sets `Auto-Submitted: auto-generated` so auto-replies leave it alone.
- If the relay uses a private CA, set `TrustedRootCertificatePath` to that root. Do not install it machine-wide just for MERIT.
- Keep `TotalTimeoutSeconds` under 30 so the page never waits longer than the browser message. Watch the `Email code not sent` warnings and the `mfaCodeNotSent` audit events. A burst means the relay is failing.
- SMTP cannot confirm that a message reached the mailbox. "Submitted" is the best MERIT can know. People can always use the authenticator app or a recovery code instead.

## Troubleshooting

| Symptom | Likely cause | What to do |
|---|---|---|
| No "Email me a code" button | Policy off, an administrator role, no email address, not turned on, or `Email:Provider` is `Disabled` | Check Settings, the account, and the app's configuration. |
| Startup fails: `EmailCodes:VerifierKey is required…` | `Smtp` outside Development without a key | Set the key (see [Configuration](#configuration)). |
| Startup fails: `Security None is allowed only…` | Plain text to a remote host | Use `StartTls` or `SslOnConnect`. |
| Every send fails `certificateUntrusted` | Private CA | Set `TrustedRootCertificatePath`. |
| `startTlsUnavailable` | Wrong port (465 needs `SslOnConnect`), or something strips STARTTLS | Fix the port or security mode. Investigate the network if the port is right. |
| `timeout` | Firewall, or wrong host or port | Test with `Test-NetConnection <host> -Port <port>`. |
| Codes stop working after a restart (Development) | No key configured, so a random one is made per process | Set a user-secret key. |
| "A code was sent a moment ago" | Cooldown | Wait the seconds shown, or use the authenticator. |
| Mailpit `Port 1025 is already in use` | Another tool on the port | `Stop-MailTool.ps1`, or `-SmtpPort`/`-WebPort`, and set `Email__Smtp__Port` to match. |
| smtp4dev 3.15.0 tool install error `DotnetToolSettings.xml was not found` | That package is not installable as a tool with SDK 9.0.200 | Keep the pinned 3.14.0. |

## What was verified

These were run on the build machine (Windows 11, SQL Server 2022, .NET SDK 9.0.200, Edge):

- Mailpit v1.33.0 (native, checksum-verified) and smtp4dev 3.14.0 (dotnet tool), each started by `Start-MailTool.ps1` and bound to 127.0.0.1 only.
- MERIT ran on each launch profile, and the browser spec signed in with a code read from each tool's API.
- Mailpit Chaos produced 451 and 550 replies, and MERIT recovered when Chaos was turned off.
- All suites listed above.

**Not verified here:** Docker is not installed on the build machine, so `docker-compose.mail.yml` (pinned images `axllent/mailpit:v1.33.0` and `rnwood/smtp4dev:3.14.0`, whose tags were confirmed on Docker Hub) has not been run. Authenticated SMTP and TLS were tested against the in-process fake server with generated certificates, not against Mailpit, which ran plain-text on loopback.
