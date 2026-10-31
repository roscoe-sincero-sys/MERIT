# Issues #101, #102, #103 and #118 verification

Verified against merged `origin/main` at `63e0ef7` (PR #123).

| Issue | Baseline finding | Resolution |
| --- | --- | --- |
| #101 | Selected labels were already rehydrated from authorized server lookups in result, relationship, employment and place forms. Unselected text still posted as the shared `q` field and disappeared after validation; popups lacked blur handling and empty results lacked an option role. | Each picker posts its own draft query, while lookup requests still send `q`. Selected labels remain server-controlled. Added explicit selection instructions, blur/outside-click/Escape dismissal, stale-response protection, and disabled empty-result options excluded from keyboard and click selection. |
| #102 | Already fixed: `syncToggles` changes the site lookup URL when the place role changes, clears incompatible selections and aborts the previous request. | Retained the existing implementation. The browser regression verifies that changing to School requests `type=school`, and changing an unrelated relationship role does not clear the selection. |
| #103 | Form-scoped conditional controls and relationship/place ID prefixes already existed. Employment, source and pregnancy forms still shared IDs, and the annual extract form still exposed quarter. | Prefixed the remaining person-form IDs and updated labels/error links. Quarter is hidden and disabled for ABLES, and its value is retained when returning to CBLS. |
| #118 | Inline cancellation destroyed the focused control; dialog closure depended on a DOM node that could be replaced by a save. | Remember the opening trigger and section. Cancellation returns to the exact trigger. Dialog closure resolves the refreshed trigger or focuses the section heading if the trigger no longer exists. |

## Recommendation checks

- The existing authorized label hydration is retained. Separately posted, HTML-encoded draft text is redisplayed only for an unselected picker and never used as a record identity.
- Used the recommended explicit selection hint instead of automatic exact-name selection: a matching name alone does not establish which record the user intended.
- Retained the existing dynamic school URL and form-scoped toggles instead of adding a second implementation.
- Verified both native Escape closure and successful dialog-save focus restoration, including a refreshed section with the original action removed.
- Validation testing also exposed a stale `aria-busy` region when a 422 response retargeted its swap. Busy-state cleanup now clears the original request target as well as the final target, preventing the validation screen from remaining dimmed.

## Validation

The .NET web build, 171 unit tests, 72 integration tests and all 99 browser/accessibility tests passed, including existing selected-label hydration and authorization checks. Browser regression coverage includes independently retained unselected queries, selected labels after 422 responses, full-page accessibility with an empty lookup, delayed responses after blur/Escape, concurrent person forms, exact cancel focus, extract quarter behavior, and dialog focus after section replacement. JavaScript syntax and Git whitespace checks also passed.

AI-assisted implementation: OpenAI Codex.
