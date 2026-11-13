# Settings catalogue, effective settings and version replacement (#289, #290)

Implemented by **Claude (Anthropic coding agent, Claude Code)** on behalf of `roscoe-sincero-sys`, from merged main `e34b6ed` (PR #288), 2026-11-13.

## Verified recommendations and decisions

### #289: invalid values, unknown keys and settings with no effect

| Recommendation | Verified | Decision |
|---|---|---|
| One catalogue of settings, enforced in the service and in `config.uspSaveSetting`; refuse unknown keys | Confirmed: only three keys were validated and the procedure inserted any key | **Implemented.** `SettingCatalogue` (application) and `config.tvfSettingCatalogue` (database) hold the same 26 rows; an integration test compares them. `config.udfSettingProblem` and `SettingCatalogue.Problem` return the same messages. |
| Implement or remove each unread setting | Confirmed by search: nine keys had no reader | **Three implemented, six shown read-only** (below). Nothing was removed from the database. |
| `sessionTimeoutMinutes`: apply per data root within a bounded range, or drop it | The cookie lifetime was fixed at start-up from `Session:IdleTimeoutMinutes` | **Implemented per data root**, 5 to 60 minutes. |
| Typed inputs on the Settings page | Every setting was a free-text box | **Implemented**: selects for true/false and code settings, and a hint line stating the accepted values for every setting. |
| Replay asserts an effect, not only an audit row | The replay only checked the audit export | **Implemented** in steps 12b and 12c. |

**Settings made effective**

- `sessionTimeoutMinutes` is the idle timeout of sessions **started after the change** (sign-in, profile switch, view-as). `auth.uspCreateSessionScope` returns the root's value through `auth.udfSessionIdleMinutes`; the session cookie is issued with that lifetime and sliding renewal keeps it. Sessions already open keep the lifetime they started with. When the stored value is missing or outside 5–60, the deployment default `Session:IdleTimeoutMinutes` applies. The page's expiry warning uses the session's own lifetime.
- `cblsEnabled` and `ablesEnabled`: requesting that extract is refused (422) while the setting is `false`.

**Settings shown read-only as "Not in use"**

`smsCodesEnabled`, `matchingPreset`, `providerLookups`, `localNotification`, `occupationalReferralAgency` and `agencyName`. No feature reads them (there is no SMS code path, one matching rule, no lookup switch, no local-notification step, no referral or letter text). Building those features is outside these issues, so the alternative in the issue was taken: the value stays visible, the page says no feature reads it, and a save is refused in both layers. When a feature is built, set `inUse` in both catalogues.

**Accepted values**

| Kind | Settings |
|---|---|
| Whole number in a range | `adultAgeCutoffYears` 12–21, `pregnancyMaxWeeks` 20–52, `passwordMinLength` 15–128, `sessionTimeoutMinutes` 5–60 |
| Whole number or `NOTSET` | `investigationLookbackDays` 1–3650, `contactAttemptsBeforeLetter` 1–20, `clusterWindowDays` 1–3650, `clusterMinCount` 2–1000 |
| Number (two decimals) or `NOTSET` | `siteInvestigationMinHours` 0.5–168 |
| true / false | `unreportedMethodConfirms`, `cblsEnabled`, `ablesEnabled`, `emailCodesEnabled` |
| One code | `primaryResidencePolicy` (review, move), `arsenicHandling` (task, case), `testingPolicyVersion` (none, MDH-2025-03) |
| Comma-separated codes | `confirmingMethods` (the six test-method codes) |
| Pattern | `statePostalCode` (two capitals), `stateFips` (two digits), `adultCaseOwnerTenantCode` (an active tenant code of the data root; checked in the database) |

Ranges are deliberately wide; they stop nonsense, not policy choices. Values stored before this change are not rewritten. A stored value that is now invalid keeps its existing fallback when read and must be corrected on the next save. A stored key that is not in the catalogue is shown read-only as "Not a MERIT setting".

The #287 redaction helpers stay as a second line for older rows. `config.udfAuditSettingValue` now records `siteInvestigationMinHours` as a decimal, since that setting accepts one.

### #290: later-dated versions removed silently

| Recommendation | Verified | Decision |
|---|---|---|
| Name the removed versions in the audit event; report Changed when something is replaced | Confirmed: a backdated save was `create` with no old value | **Implemented.** The event carries `removedVersionCount` and `removedVersions` (ID, effective dates, value through the redaction helper). The action is `create` only when the setting had no version at all. |
| Confirm before removing later versions, or keep them | Both were considered | **Confirmation implemented.** Keeping later versions would mean a backdated correction on a root seeded today does not take effect today, which is the case #251 fixed. The existing replace rule stays, but `config.uspSaveSetting` refuses the save (422, nothing changed, nothing audited) unless the new `@replaceLaterVersions` flag is set. The message gives the count and the dates. The Settings page has a "Replace later-dated versions" tick for each setting and lists scheduled versions. |
| Decide whether backdating before the first version is allowed; require a reason | — | **Allowed with the same confirmation.** A separate free-text reason was not added: the confirmation plus the named versions in the audit give the reviewer the fact, and free text is kept out of the ledger by the #287 rule. |
| Regression | — | **Added.** |

A same-day correction (same effective date) replaces only that day's version, as before, and needs no confirmation. Effective dates are limited to 1900 through ten years ahead.

## Behaviour changes to know when deploying

- No data migration and no settings need re-entering. Deploy normally.
- A script or integration that calls `config.uspSaveSetting` with an unknown key, an out-of-range value or a backdated date now gets error 51422.
- A data root whose stored `sessionTimeoutMinutes` is between 5 and 60 will start applying it to new sessions. The seeded value is 20, the same as the shipped deployment default.
- `generateProcedures.py` currently rewrites 17 unrelated procedure scripts that have drifted from their definitions. Only the save-setting output was taken from it; `auth.uspCreateSessionScope` and its definition were edited together by hand. That drift is not addressed here.

## Validation

| Check | Result |
|---|---|
| Full integration suite, fresh `MERIT_FIX289_CLAUDE_FULL_20261113` | **235 passed**, zero failed; two opt-in replays skipped |
| #267 replay, fresh `MERIT_SCENARIO_267_CLAUDE_FIX289_20261113` | **Passed**, all steps including new 12b and 12c, `RESULT: no unexpected behaviour` |
| Unit, architecture and manifest tests | **386 passed**, five optional local mail-service tests skipped |
| Upgrade in place of the older `MERIT_SCENARIO_267_CLAUDE_POST288_20261112` | Deployed; three focused regressions passed; its stored `abc`, `banana` and made-up key are reported invalid and left untouched |
| Procedure-generator checks, `git diff --check` | Passed |

Not run: the Angular build and Playwright. The Settings page was compiled and exercised over HTTP, not viewed in a browser.

Tests added:

- `tests/Merit.Tests/SettingCatalogueTests.cs`: accepted and refused values, the six unused settings, and the session-timeout bounds.
- `tests/Merit.IntegrationTests/SettingCatalogueTests.cs`: the two catalogues agree; 25 invalid, unknown or unused saves are refused over HTTP with nothing stored or audited, and six directly against the procedure; valid values save; a new session gets the root's timeout while the changing session and another root keep 20; extract switches; and the #290 sequence (scheduled value, refused change, confirmed change, backdate, same-day correction) with the audit content.
- `ConfigurationAuditTests`: the former "permissive" assertions now assert refusal.
- `Scenario267RegionTests`: step 12b (refusals) and 12c (Maryland session 30 minutes, Virginia session 20).

Only dedicated synthetic SQL databases were used.
