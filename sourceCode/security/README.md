# Offline common-password blocklist

`common-passwords.txt` is the unmodified SecLists `Passwords/Common-Credentials/10k-most-common.txt` at commit
[`913b327317496d062bcc7cace524aaad8a693be2`](https://github.com/danielmiessler/SecLists/blob/913b327317496d062bcc7cace524aaad8a693be2/Passwords/Common-Credentials/10k-most-common.txt).
SHA-256 of the original UTF-8/LF file: `68782d6a4a19a4768d5f15dd66bd534e7a33055cc755411e33f16d18c50fdcce`.
Its MIT license is reproduced in `SecLists-LICENSE.txt` and copied beside published application assets.

The web app embeds this corpus and `merit-passwords.txt`, which retains MERIT's original common-password entries and
adds longer common/repeated values relevant to the new minimum. It compares the entire NFC-normalized
password, ignoring case and surrounding whitespace for screening only; hashing preserves case and all spaces.
The application also screens whole-password derivatives of the account name and MERIT. This is a common-password
blocklist, not a claim to contain every password exposed in every breach. Review the corpus periodically, or replace
`IBreachedPasswordList` with an agency-approved larger source. No chosen password is sent to an external service.

The initial platform-administrator bootstrap uses these same two files. Package `security/` with `database/` when distributing
the deployment tooling. Synthetic demo credentials deliberately remain legacy fixtures to test backward compatibility;
they are not production password-policy examples.
