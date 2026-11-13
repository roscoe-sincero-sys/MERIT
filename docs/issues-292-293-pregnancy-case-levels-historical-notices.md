# Pregnancy case levels, unconfigured populations and historical owner notices (#292, #293)

Implemented by **Claude (Anthropic coding agent, Claude Code)** on behalf of `roscoe-sincero-sys`, from merged main `def3d41` (PR #291), 2026-11-13.

## Verified recommendations and decisions

### #292: pregnant patients got notice reviews but no case

| Recommendation | Verified | Decision |
|---|---|---|
| Replay step 12 configures the pregnancy reference level and asserts a pregnant case | Confirmed: the replay created child levels only | **Implemented.** Step 12 now saves all four reference-level drafts; new step 17b asserts a pregnant adult and a pregnant minor each get a case, an investigation request and the three notice reviews. |
| Ship the child and pregnancy reference levels as drafts | The package had notification drafts and no thresholds | **Implemented.** Four case-level drafts (child under 6 and pregnancy; 5 µg/dL July 2020–December 2023, 3.5 from January 2024) are listed on the Thresholds page. A draft only prefills the form; nothing is saved or activated until the administrator saves it. |
| Warn when an active notification rule's population has no case level | Nothing compared the two | **Implemented.** The Thresholds and Notification rules pages show a warning naming each such rule. |
| Create a review item for an elevated result in a population with no case level | Classification stored "no case level configured" and created nothing | **Implemented** as one `populationReview` task, "Elevated result with no case level for this population". No case is opened automatically. |
| Record "no case level by design" for ages 6–17 and non-pregnant adults | This is a program decision | **Not implemented.** Until Maryland decides, such a result creates the review task above, which staff can close. |

**The coverage warning** compares each active Department notification rule (laboratory-reporting rules are ignored) with the active case levels for the same analyte and specimen. A rule for the child, adult or pregnant population needs a case level for that population or for all populations whose effective period overlaps the rule's. A rule for all populations needs any overlapping case level. Age bands are not compared, because the rule list does not carry them; this is a known limit.

**The review task** is created when a final numeric result (not a "<" result) matches no case level for its population and age, no case is open, and its value is at or above the lowest case level configured for any population. It is not urgent and is due 14 days after the draw. A root with no case levels at all creates none.

### #293: historical periods lacked owner notices

| Recommendation | Verified | Decision |
|---|---|---|
| Add residence-owner drafts for both historical periods | Confirmed: only caregiver drafts existed | **Implemented**: two drafts with the same thresholds, dates, age limit and 10-business-day deadline as the caregiver drafts. |
| Add affected-property-owner drafts for those periods | The issue text gives § 6-846 no dates or historical thresholds | **Not implemented.** I did not assume how § 6-846 applied before 2024. |
| Decide the period before July 1, 2022 | The issue text gives no threshold for it | **Documented, not assumed.** The Notification rules page now states that the shipped drafts start with results received on July 1, 2022 and that an approved rule must be added if earlier notices are to be tracked. A result received before that date still produces no notice review. |
| Do not raise years-overdue historical reviews as urgent | Every notice review was urgent | **Implemented.** A review whose deadline is more than a year past is created as not urgent. Its due date and content are unchanged. |
| Extend the replay | — | **Implemented** as step 17c. |

## Behaviour changes to know when deploying

- No data migration. Drafts are not activated by deploying: an existing Maryland root gets the new owner notices and the pregnancy case levels only after its administrator reviews and saves them. Until then the new warning says what is missing.
- After adding a rule, use "Evaluate existing results" on it to create reviews for results already stored.
- Results already classified are not re-classified; the review task appears for results received after deployment or re-run after a date-of-birth or pregnancy change.
- The shipped draft count is now 13.

## Validation

| Check | Result |
|---|---|
| Full integration suite, fresh `MERIT_FIX292_CLAUDE_FULL_20261113` | **236 passed**, zero failed; two opt-in replays skipped |
| #267 replay, fresh `MERIT_SCENARIO_267_CLAUDE_FIX292_20261113` | **Passed**, all steps including new 17b and 17c, `RESULT: no unexpected behaviour` |
| Unit, architecture and manifest tests | **394 passed**, five optional local mail-service tests skipped |
| The #292/#293 probe re-run on the replay database | Pregnant adult: case and investigation request. 7-year-old and non-pregnant adult at 10.0: one review task each. 2022 and 2023 results: caregiver and owner reviews. 2021 result: still none (documented). |

Not run: the Angular build and Playwright. The pages were compiled and exercised over HTTP, not viewed in a browser.

Tests added: `tests/Merit.Tests/PolicyCoverageTests.cs` (drafts, coverage rules, classifier review task), `tests/Merit.IntegrationTests/PolicyCoverageTests.cs` (the warning appears and clears on both pages; drafts prefill the form), and replay steps 17b and 17c.

`dbo.uspReconcileNotifications` is one of the procedure scripts that has drifted from its generator definition, so the script and `procs_care.py` were edited together by hand.

Only dedicated synthetic SQL databases were used.
