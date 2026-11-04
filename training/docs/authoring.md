# Writing and maintaining a workflow

A video is a script in `workflows/<role>/<feature>/<id>.workflow.ts`. The folder names are the role and feature in
kebab case; `role` and `feature` in the script are the identifiers in [lib/workflow.ts](../lib/workflow.ts).

## A new video

1. Copy [the proof of concept](../workflows/intake-clerk/people-and-results/enter-paper-result.workflow.ts) into the
   right folder and give it a new `id`.
2. **Choose the account.** Use a synthetic demo account whose role matches the video: `intake`, `cmbalt`,
   `supervisor`, `admin`, `cityadmin`, `epi`, `occinv` or `platform`. Its first build enrolls an authenticator off
   camera.
3. **Make it repeatable.** Write `prepare()` so every build starts from the same data. Mark what the video creates (an
   accession number starting `TRN-`, a name ending `(training)`) and soft-delete it in `prepare()`. Add a reset to
   [lib/data.ts](../lib/data.ts) when a new kind of record needs one. Never hard-delete; MERIT doesn't.
4. **Write scenes of steps.** A scene is a chapter. A step is one narrated line (`say`) and the actions that go with it
   (`run`).
5. **Check it:** `npm test -- workflows` runs it fast; `npm run videos -- --id <id>` builds it; then review it with
   [the checklist](review-checklist.md).

## Narration lines

- One or two plain sentences, under 200 characters, ending with a full stop. They are the captions, so write what
  you would say to a colleague beside you.
- Say why as well as what: "search first, so the result joins his existing record".
- Name controls as the screen does: "choose **Save result**".
- Write units as the screen shows them (`µg/dL`); [narration.ts](../lib/narration.ts) spells them out for speech.
- Never put a password, a code or anything personal in a line, even a synthetic one. The output checks reject them.

## Actions

The stage `s` passed to `run` paces everything like a person when recording, and runs it directly in tests. Every
action fails the step if its element is missing, so the dry run catches screen changes.

| Action | Use |
|---|---|
| `s.click(locator)` | Moves the cursor there and clicks |
| `s.type(locator, text)` | Clicks, clears and types. Dates are written `yyyy-mm-dd` and typed as the date field expects. The value is checked afterwards. |
| `s.select(locator, label)` | Chooses a native select option by its visible label, showing the list |
| `s.pick(locator, text, option)` | Types into a lookup, then clicks the suggestion |
| `s.highlight(locator)` | Frames the element until the step ends; its text goes into the transcript as "On screen" |
| `s.zoom(locator, scale?)` | Zooms towards the element (1.6x by default, less for a large one) until the step ends |
| `s.scrollTo(locator)` | Scrolls with the wheel until the element is in view |
| `s.see(locator, { describe })` | Waits until it is visible; `describe: true` adds its text to the transcript |
| `s.expectText(locator, /pattern/)` | Fails unless the text matches: use it to check outcomes |
| `s.goto(path)`, `s.pause(ms)` | Navigation, and a pause that applies only when recording |

Prefer locators a person would recognize: `getByRole('button', { name: 'Save result' })`, `getByLabel('Analyte')`.
Use ids (`#ValueText`) when a label is shared or missing. A step may set `hold` (ms) to keep a busy screen up longer.

## When MERIT changes

- **A screen changed:** `npm test` fails at the step that no longer matches. Update the locator or the line, then
  rebuild with `npm run videos -- --id <id>`.
- **A workflow changed** (new required field, new outcome): update the steps and the recap, and rebuild.
- **The tooling changed:** every fingerprint changes. `npm run videos -- --changed` rebuilds what is out of date, and
  `outputs.spec.ts` lists anything still stale.
- **Retired:** delete the script; delete its files in `output/`.

The `timeline.json` of every video records the MERIT commit it was built from, so you can see how old a video is.
