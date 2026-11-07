# Password-policy review and hardening — issue #198

The previous default was **8 characters**, not a rule requiring *exactly* eight. A tenant could configure a longer
minimum, and the service capped new passwords at 128 UTF-16 code units. NIST SP 800-63B-4 permits an eight-character
minimum for passwords used only with MFA; it requires 15 for password-only authentication. Consequently, eight alone
was not proof of noncompliance. MERIT now adopts a **15-character floor for all new and changed passwords**, retaining
stricter tenant minima up to 128.

Reviewed against [NIST's password-verifier guidance](https://pages.nist.gov/800-63-4/sp800-63b.html#passwordver),
[OWASP's authentication guidance](https://cheatsheetseries.owasp.org/cheatsheets/Authentication_Cheat_Sheet.html#implement-proper-password-strength-controls)
and [OWASP's password-storage recommendations](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html#pbkdf2).
OWASP currently recommends 220,000 PBKDF2-HMAC-SHA512 iterations; that is a work-factor recommendation, not a
NIST-prescribed iteration count.

| Area | Before the fix | Current behavior |
|---|---|---|
| Minimum | Seed/fallback 8; admin validation trusted an unclamped value | 15 minimum everywhere; larger valid tenant minima retained |
| Maximum | 128 UTF-16 units | 128 Unicode code points after NFC; no silent truncation |
| Unicode | Supplementary characters counted twice; no normalization | Scalar-value length after NFC; malformed UTF-16 rejected; normalized confirmation comparison |
| Tenant policy | Admin used their active settings; reset SQL used the account root | Admin, new-account, reset and change paths use the selected account home root; authority checked before policy lookup |
| Settings | Arbitrary text or out-of-range values accepted | New settings require an integer 15–128; existing malformed/below-floor values resolve to 15 and over-limit values to 128 |
| UI | Admin hint fixed at eight; other hints depended on SQL | Shared rule text with effective minimum and maximum in all password forms |
| Blocklist | About 60 mostly short examples | Pinned offline common-password corpus, shared MERIT supplement and whole-password account/service-name derivative checks |
| Hashing | Framework defaults, 100,000 PBKDF2-SHA512 iterations | Explicit Identity V3 PBKDF2-HMAC-SHA512 at 220,000, independent random 128-bit salts and a normalization-version marker |
| Hash upgrades | `SuccessRehashNeeded` ignored | Successful legacy verification upgrades through a binary compare-and-swap of hash and stamp |
| Unknown-name check | A malformed fixed dummy hash could fail before doing password work | Cached, valid dummy hash generated with the configured hasher |
| Bootstrap | Initial platform password bypassed app validation and used old hashing defaults | Same length, NFC, offline blocklists, expected-name checks, hash marker and work factor |

## Protections retained

Spaces, case and the entire password remain significant. Long passphrases and international text are accepted without
uppercase/digit/symbol quotas. Password managers, autofill, paste and visibility controls remain available. There is no
periodic password expiration, password hint, or security-question feature. The app still rejects choosing the current
password, requires the second factor, limits account guessing to five failures before a 15-minute lock, and applies
per-address request throttling. Reset links remain one use, short lived and MFA protected. Real password changes still
change the security stamp, revoke sessions and produce the existing audit events/notification.

## Compatibility and deployment

Existing credentials are **not bulk-reset**. An unmarked Identity hash is verified against its original input bytes.
After successful verification, it is replaced with `merit-nfc-v1$` plus the standard Identity V3 encoding of the NFC
password. New hashes verify canonically equivalent Unicode input; case and leading/trailing spaces are preserved.
Hash-only upgrades leave `passwordChangedUtc`, the required-change flag, security stamp and active sessions unchanged.
An atomic binary comparison prevents a stale upgrade from restoring a concurrently reset credential. Another
successful concurrent hash upgrade is accepted only after re-reading the account and re-verifying the current hash.

Deploy the generated procedures together with the application. `auth.uspGetPasswordPolicy` enforces the new floor for
already-installed tenants even if their stored historical setting still says eight; new tenant seeds use 15. Existing
stricter valid settings continue to apply. Administrators should update historical configuration values during policy
review. Sites that need existing users to choose longer passwords can use the existing required-change control;
automatic periodic rotation has not been introduced.

The initial platform-admin deployment path uses the shared `sourceCode/security/` files. Its input is a SecureString;
plaintext is not printed or passed on a process command line. The SQL procedures receive only the hash. The bundled
corpus is an offline screening list, not a promise to detect every compromised password; see its
[source and license](../sourceCode/security/README.md). Public demo data remains a legacy compatibility fixture, while
new login-training accounts receive a longer synthetic passphrase in the updated format.

The training scripts assert the new hint and exercise a 14-character rejection. Updated published recordings live in
[`trainingVideos/`](../trainingVideos/), with their reproducible source under [`training/`](../training/).

## Validation and scope

Unit regressions cover policy bounds, code-point counting, combining characters and Hangul, whole-value screening,
case/space preservation, salt/work-factor encoding, canonical equivalence and unmarked-hash compatibility. SQL/browser
integration regressions cover the account's effective policy (including an administrator in another root), malformed
settings, old sessions surviving a hash-only upgrade, binary hash comparison, stale credential updates and real
password-change/reset flows. Training walkthroughs and output validation cover the recorded UI and exported media.

This review does **not** certify an authentication assurance level for MERIT. The existing optional email-code exception
is explicitly outside NIST-approved out-of-band authentication, and longer passwords do not resolve that exception or
add phishing-resistant authenticators. No changes to those authentication options are included in this password fix.
