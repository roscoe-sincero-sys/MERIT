# Administrator: help someone sign in again

**Role:** Administrator · **Feature:** Signing in and your account · **Length:** 2:08

Unlock an account, reset a lost authenticator, and require a private password after confirming the person's identity.

*Everything shown is synthetic demonstration data. No real person, result or credential appears in this video.*

**[0:00]** Administrator: help someone sign in again. Unlock an account, reset a lost authenticator, and require a private password after confirming the person's identity.

## 1. Verify the person and find their account

**[0:13]** Use your agency's identity-verification procedure before changing sign-in security. Confirm the person through a trusted channel, not a message containing a code.
- *Action:* Types “loginadmin” in User name
- *Action:* Types into Password (hidden)
- *Action:* Selects “Continue”
- *Action:* Types into Six-digit code from your authenticator app (hidden)
- *Action:* Selects “Verify and sign in”

**[0:25]** Administrator accounts use an authenticator or recovery code. Email verification is never available to administrators.
- *Action:* Selects “Sign-in methods”
- *On screen:* Email codes · OFF · Email codes are not available for your account. Your agency decides whether they can be used, they need an email address on your account, and they are never available to administrators.

**[0:34]** Open Administration, then Users. Filter by the person's name or user name and open the correct account. You can manage only accounts within your authority.
- *Action:* Selects “Administration”
- *Action:* Selects “Users”
- *Action:* Types “loginsupport” in Filter listed users
- *Action:* Selects “Taylor”

## 2. Unlock or reset MFA

**[0:44]** For a locked account, choose Unlock account after verifying the person. This clears the lock; it does not change their password or authenticator.
- *Action:* Selects “Unlock account”

**[0:54]** For a lost authenticator, choose Reset MFA after verifying the person. The confirmation explains that active sessions and old codes will stop working.
- *Action:* Selects “Reset MFA after verifying the person”

**[1:05]** Tell the person to sign in again with their password, add the new MERIT authenticator entry, and save the new recovery codes.
- *On screen:* Not yet enrolled; the person enrolls at next sign-in.

## 3. Set a temporary password when needed

**[1:14]** If a password reset is also needed, enter a new temporary password and select Ask them to choose a new password at their next sign-in.
- *Action:* Types into New password (leave blank to keep) (hidden)
- *Action:* Selects “Ask them to choose a new password at their next sign-in”
- *On screen:* Ask them to choose a new password at their next sign-in

**[1:24]** Save the account. Share the temporary password only through your approved secure channel. Never request the person's authenticator or recovery codes.
- *Action:* Selects “Save”

**[1:35]** The person completes MFA and chooses a password only they know. If both password and authenticator were lost, they must complete both reset paths.

**[1:45]** Another administrator must help an administrator who cannot sign in. A platform administrator should contact another platform administrator.

## Recap

**[1:55]** In this video you verified the account and unlocked it, reset MFA and explained re-enrollment, and required a private password after a temporary reset.

- Verified the account and unlocked it
- Reset MFA and explained re-enrollment
- Required a private password after a temporary reset

---

Generated 2026-11-07 from `training/workflows/administrator/sign-in/login-admin-support.workflow.ts` against MERIT be42f6d 2026-11-06.
