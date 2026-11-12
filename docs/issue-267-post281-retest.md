# Issue #267 retest after PR #281

Reviewer: **Codex (OpenAI coding agent)**, on behalf of `roscoe-sincero-sys`. Tested on 2026-11-12 (America/New_York), against merged main `420e0fa` plus the replay-only fix for [#282](https://github.com/roscoe-sincero-sys/MERIT/issues/282).

**The 23-step replay passes with no unexpected behavior.** The eight focused regressions also pass. No new application defect was found. The initial run found a time-dependent defect in the replay itself (#282), assigned to `roscoe-sincero-sys` with `bug`, `P2`, `severity:medium` and `workflow` labels.

## Confirmed behavior

- Region 3 delegation, all six state/district roots, all 24 Maryland and 133 Virginia jurisdictions, 575 initial scenario accounts, Maryland configuration and 26 extra profiles completed.
- The two-county intake clerk loaded HL7 and CSV and entered results manually. The manual probe saved one result and one receipt task. The statewide workaround was not needed.
- Imported Baltimore County and Harford patients belong to their residence jurisdictions. The Baltimore County-only profile opens its own patient and receives **404** for the Harford patient. Read-only creation attempts are refused.
- Late-report and missing-information tasks, the A–G confirmation/follow-up sequence and discharge-readiness checks completed.
- Baltimore County, Harford County and Baltimore City case assignment, contact/education, investigations, samples and findings completed. Unauthorized writes and sibling reads were refused. Closure was correctly blocked by unresolved required notifications.
- **72 Virginia isolation probes** returned no Maryland data.
- Region 3 and platform audit exports reached descendant state roots. The Harford officer's audit contained **97 rows / seven users**, included Harford and two-county staff, and excluded Baltimore City-only and Virginia staff. Its supported report exports were available. Unknown-name investigation remains platform-only; the platform role has no usage report, as before.
- The focused tests independently confirmed that an officer holding only `securityOfficer` can review county usage without gaining clinical access, and that a statewide actor who accessed county data appears in county user and sign-in reports without becoming manageable there (#277/#278).
- The deterministic multi-county ownership regression (#279/#280), existing security-review tests and case-transfer regression (#274) passed.

## Replay defect #282 and correction

At approximately 04:44, the original replay submitted a historical receipt of **today at 09:00**. The application's future-date validation correctly rejected it. The upload controller redirects on both errors and success; the helper treated the redirect as success and unboxed a nonexistent batch ID. That produced a null-reference exception, falsely logged a recurrence of #268 and stopped the replay at step 14.

A separate real-HTTP probe reproduced the future-date message for 09:00 and accepted 00:00 with the same two-county account/profile. The correction uses **00:00 on the requested receipt date**, follows the redirect, asserts the upload success message and checks the batch ID's type before using it. Business dates and the real clock remain in use. The corrected full replay ran entirely before 09:00.

Recommendation: merge the replay correction so scheduled and early-morning runs can verify the merged application reliably. Retain application future-receipt validation. No application, schema or permission definitions were changed by this retest.

## Validation and evidence

| Check | Result |
|---|---|
| Original replay on `MERIT_SCENARIO_267_CODEX_POST281_20261112` | Failed at step 14 with #282; later steps were not reached |
| Independent receipt probe on that database | Passed: future 09:00 rejected, midnight accepted |
| Corrected replay on fresh `MERIT_SCENARIO_267_CODEX_FIX282_20261112` | **Passed**, all steps reached, no unexpected behavior; 3m46s test body, 5m35s including build/deployment |
| Focused regressions on fresh `MERIT_AUDIT267_CODEX_POST281_20261112` | **Eight passed**, none skipped |
| Unit, architecture and manifest checks on merged main | **338 passed**, five optional local mail-service tests skipped |
| `git diff --check` | Passed |

Local evidence: `sourceCode/.artifacts/issue267-post281/` contains `post281-scenario.trx`, `initial-scenario-evidence.txt`, `post281-receipt-probe.trx`, archived diagnostic source `Scenario267ReceiptProbe.cs.txt`, `fix282-scenario.trx`, `fix282-scenario-evidence.txt`, `post281-support.trx`, `post281-unit.trx` and the #282 issue body. Logs are `sourceCode/.artifacts/issue267-post281-{scenario,receipt-probe,support,unit}.log` and `sourceCode/.artifacts/issue267-fix282-scenario.log`. These local artifacts are ignored by Git; this document preserves the reviewable results.

## Scope and remaining coverage limits

This was real HTTP integration testing against dedicated synthetic databases on `localhost\PORTAL`. The fixture includes standard demo roots, while every scenario tenant begins unconfigured. Most created accounts are not individually signed in. Notifications were created and review-gated; successful delivery and final clinical case closure were not exercised. The original replay's additional policy-coverage limits remain documented in [the original report](issue-267-scenario-2.md). No full integration suite, browser usability audit, Angular build or Playwright run was performed for this retest. Production data and the pre-existing `promptArchive/scenario_2_test.txt` were preserved. Issue #267 remains open for owner acceptance.
