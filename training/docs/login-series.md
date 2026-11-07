# Signing in: the complete training series

Watch these in order, or choose the video that matches the problem. Every video has narration, a silent copy,
captions, a copy with captions drawn into the picture, a transcript, and scene chapters. All accounts are synthetic.
No credentials, authenticator setup keys, QR payloads, recovery codes, private reset links, or entered codes are shown.

The ten videos run about 23 minutes in total; individual videos are about two to three minutes each.

| Order | Video | Use cases |
|---|---|---|
| 1 | [First sign-in: set up your authenticator](../output/allUsers/signIn/login-first-time.mp4) | Agency-issued account; password; QR setup; manual setup; wrong setup code; phone time; save, copy or print recovery codes; acknowledgement gate. |
| 2 | [Everyday sign-in and signing out](../output/allUsers/signIn/login-everyday.mp4) | Wrong credentials; switching users; authenticator sign-in; wrong code; returning to a requested page; active profile; sign out; revoked or expired session. |
| 3 | [Lost authenticator: recovery and MFA reset](../output/allUsers/signIn/login-recovery.mp4) | Unused recovery code; reused code rejection; no recovery codes; identity verification; administrator and platform administrator escalation; MFA reset; new enrollment and codes. |
| 4 | [Email codes: enable, choose and turn off](../output/allUsers/signIn/login-email-setup.mp4) | Agency eligibility; email address confirmation; authenticator step-up; email verification; preferred method; email sign-in; disable email; authenticator fallback. |
| 5 | [Email sign-in: missing, old and rejected codes](../output/allUsers/signIn/login-email-help.mp4) | Missing email; spam; wrong code; expiry; resend countdown; newer message supersedes old code; attempt limit; delivery error and support reference; retry; authenticator fallback; withdrawn eligibility. |
| 6 | [Forgotten password: reset and sign in](../output/allUsers/signIn/login-forgot-password.mp4) | Neutral response for unknown accounts; missing email; private one-use link; second factor; wrong code; mismatched passwords; new password; used-link rejection; sign-in after reset. |
| 7 | [Password reset: links and support options](../output/allUsers/signIn/login-reset-help.mp4) | Expired, superseded, invalid or closed links; locked account; no enrolled factor; recovery-code reset; no mail service; both factors lost; administrator support. |
| 8 | [Change your password and temporary passwords](../output/allUsers/signIn/login-password-changes.mp4) | Signed-in change; current password and authenticator; password policy errors; other sessions ended; required change after administrator reset; enforced gate; sign-in with chosen password. |
| 9 | [Cannot sign in: locks, disabled accounts and timeouts](../output/allUsers/signIn/login-blocked.mp4) | Account lockout; correct password during lock; unlock/wait; disabled or unknown account; expired pending sign-in; network rate limit; safe support information. |
| 10 | [Administrator: help someone sign in again](../output/administrator/signIn/login-admin-support.mp4) | Verify identity; scope of authority; no email codes for administrators; find account; unlock; reset MFA; temporary password; require private password; administrator escalation. |

The application currently implements password plus authenticator/recovery codes and optional email codes. The videos
do not invent SSO, text-message codes, security-key or passkey screens that the application does not implement. The
administrator enrollment page's recommendation about future security keys is advisory.

## Build and validate

The existing app on port 7243 can stay running. Build the application before starting the training services if its
Debug build is missing. Install Mailpit using `sourceCode/tools/mail/Install-Mailpit.ps1` if needed. Its installed
executable is reused, but these videos run their own instance on different ports.

From `training/`, in one terminal:

```powershell
npm.cmd run app:login
```

This starts the existing application binary against `MERIT_TRAINING` on port 7343 with mail captured on loopback
ports 11025/18025, and an additional instance on port 7344 with email disabled and a one-request network limit.
Nothing is sent outside this machine. It leaves the normal app's launch settings and mail capture tools alone.
Ctrl+C stops the services this command started. Logs are kept in the ignored `.work/` folder.

In a second terminal:

```powershell
$env:MERIT_TRAINING_URL = 'https://localhost:7343'
npm.cmd test -- workflows.spec.ts --grep '/signIn/login-'
npm.cmd run videos -- --feature signIn --burn-in
npm.cmd test -- outputs.spec.ts --grep 'login-'
```

Use `--id login-first-time` (or another ID from the filenames) for one video. `npm.cmd run videos:login` selects this
series and defaults to port 7343. Regular workflow tests skip the login series unless `MERIT_TRAINING_URL` is explicitly
set, because the series requires the isolated mail and alternate app configuration.

## Repeatability and coverage limits

The scenario creates its own root, **MERIT Sign-in Training (demo)** (`TRN-SIGNIN`), and `login*` accounts through the
application procedures. It resets only those accounts. Setup keys and codes remain in process memory; browser response
redaction removes them before paint. Verification fields are rendered as password fields, so the existing action
logger and privacy scanner keep values out of transcripts and output metadata. Phone-app steps are explained alongside
the actual MERIT enrollment page; no phone emulator or invented app screens are shown.

Issue #198 updates the initial synthetic login passphrase and hash format, asserts the 15-character rule, and exercises
a 14-character rejection. The published exports are included in [`trainingVideos/`](../../trainingVideos/). Set
`MERIT_TRAINING_APP` to an already-built `Merit.Web.dll` to record from an isolated build instead of the default Debug
binary; the app's content root remains the normal web project.

Time-dependent states are prepared explicitly in the synthetic database: an expired challenge or link, a nearly
exhausted code, a nearly locked account, and a revoked session. The viewer sees the application's real response to each
state. The resend countdown runs normally. The dedicated mail server rejects a message for the delivery-error example;
its fault setting is restored in `finally`. The separate no-email and throttled app provides those real configuration
branches. Account and request sending quotas are raised only on these training instances so repeated builds work;
password-reset request cooldown is disabled there. Narration describes the production page's guidance and does not
claim those training quotas are production defaults.

These are user workflow videos, not an exhaustive security test suite. Individual malformed requests, SMTP protocol
faults and all password policy boundary values belong to application tests. Explanatory branches (for example manual
phone entry and contacting another platform administrator) are narrated; supported browser outcomes are asserted in
the workflow scripts. Human playback review is still required before publication; use [review-checklist.md](review-checklist.md).
