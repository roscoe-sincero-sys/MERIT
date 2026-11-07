# Workflow and accessibility fixes: #209, #211, #212, #222, #223, #226, #228

The audit recommendations were checked against the implementation on merged main, then verified with SQL-backed and browser tests. No training data or production records are required for these changes.

| Issue | Implemented behavior and decision |
| --- | --- |
| #209 | Unsaved manual results and case-form edits prompt **Stay** / **Leave without saving** on ordinary links, sign-out and other full-page form navigation. Browser exit/reload uses the browser's own warning. Stay/Escape restores focus and keeps values; successful htmx saves clear only the submitted form, preserving edited sibling forms. Reverted values stop prompting. Patient text is kept only in document memory; no browser-storage drafts were added. Links opening another tab and phone/email links do not discard the page and are exempt. |
| #211 | Assignment pickers and SQL writes use a shared eligibility function: active accounts, current grants, write/full access, work-specific read/edit capability and the actual owning tenant. Task writes also validate access to the linked patient and case. Invalid bulk assignment is rejected atomically with an explanation; no selected tasks are assigned. Existing assignees remain named if their eligibility later changes, and an unchanged historical assignment can be retained while editing other case/investigation fields. Eligibility uses master grants, so staff can switch out of a reduced profile to do their work. |
| #212 | Intake without configured formats shows the setup dependency and administrator/support next step before any file chooser; no unusable upload form is rendered. Inactive profiles are rejected server-side. Configured profiles display sender, version, separator, header row and date requirements. An existing header can be downloaded as plain text. HL7 and profiles without a header direct staff to the laboratory-specific layout rather than generating a misleading generic template or clinical sample. |
| #222 | Configuration links stack at narrow widths, retain readable labels and identify the current page with `aria-current`. Settings/analyte tables have named, keyboard-focusable scrolling regions. |
| #223 | Match review has a valid named region during loading, review and empty states. Removing the last record announces the empty state and puts focus on the page heading. Unused shortcut controls are hidden when there is no review; directly opened records retain their shortcuts. |
| #226 | People Search has visible **Search** text and accessible name **Search people** at all widths; the control keeps space for the label on phones. |
| #228 | The six person-section headings contain only their titles. Existing action controls are siblings in a wrapping header, so heading and region names stay concise and button behavior/focus restoration is preserved. |

## Deployment

Deploy the database scripts before the application. New `auth.tvfAssignableUserTenants` is used by assignment list/update procedures. Procedure generation and `procedureManifest.json` are updated; the SQL convention gate covers the added function and generated scripts. No new tables or permission grants are introduced.

Web includes new `unsaved-work.js`, revised Razor markup and CSS; deploy rebuilt Angular assets for Match review. The Worker continues using the existing intake/job data contracts. Its build remains part of validation.

## Limits

- Navigation warnings require JavaScript. Browser-controlled exit prompts use browser-provided text and choices. The warning does not auto-save, recover drafts after closure or retain clinical text across sessions.
- This guard covers manual result entry and the case forms that already preserve sibling drafts. It does not claim draft protection for every form in MERIT or unsaved Angular state.
- Assignment eligibility is checked when assigning; later access changes do not silently remove historical responsibility. The UI labels an existing ineligible case/investigation assignee so a supervisor can choose a replacement. Invalid bulk selections are refused as a group rather than partially reassigned.
- Intake setup remains an administrative deployment/support operation. No nonexistent configuration screen is linked, and the header download is not presented as a complete laboratory file specification.
- Automated accessibility/reflow checks include desktop, 390px and 320px Chromium viewports; physical iOS/Android and a full screen-reader assessment are outside these checks.
