# MERIT training videos

Short, narrated training videos generated from scripts, so they can be rebuilt whenever a screen changes. Each video is
a 1080p MP4 with WebVTT and SRT captions, a written transcript, and a copy without narration. Everything shown is
synthetic demonstration data.

This folder is separate from the application. It has its own `package.json`, nothing in `sourceCode/` refers to it, and
none of its tools ship with MERIT. All of them are free and run locally:

| Tool | Licence | Used for |
|---|---|---|
| [Playwright](https://playwright.dev) 1.64 | Apache-2.0 | Drives the installed Microsoft Edge, captures the screen through the Chromium screencast, renders the title and recap cards |
| [FFmpeg](https://ffmpeg.org) | LGPL/GPL | Assembles the MP4 (H.264, AAC), mixes and normalizes the narration, writes chapters, can burn in captions |
| [Piper](https://github.com/rhasspy/piper) (optional) | MIT; voice trained on public-domain recordings | Natural offline narration |
| Windows speech (fallback) | Part of Windows | Narration without installing anything |

Remotion was considered and not used. Its company licence is paid for organizations of more than three people, and
FFmpeg already covers zooms, cards and captions. See [docs/design.md](docs/design.md) for that decision and the others.

## Install

1. **Node.js 24**, **Microsoft Edge** and **sqlcmd**, which the rest of MERIT's development setup already needs.
2. **FFmpeg:** `winget install Gyan.FFmpeg`. The tooling finds it on PATH or in the winget package folder; set
   `MERIT_TRAINING_FFMPEG` to use another copy.
3. **Packages**, from this folder: `npm install`. This installs Playwright, TypeScript and Node types, and downloads no
   browser.
4. **Training database**, once: `npm run db:deploy`. This creates `MERIT_TRAINING` on `localhost\PORTAL` with the
   synthetic Maryland demo data. It takes about 25 minutes the first time and is safe to run again.
5. **Optional, Piper:** `npm run setup:voice` downloads Piper and the `en_US-kristin-medium` voice into `.tools/` and
   checks both against recorded SHA-256 hashes. Without Piper, narration uses Windows speech.
6. **Check:** `npm run doctor`.

## Generate

In one terminal, start MERIT on the training database:

```powershell
npm run app          # dotnet run of Merit.Web with ConnectionStrings__Merit pointed at MERIT_TRAINING
```

In another terminal:

```powershell
npm run videos                                   # every workflow
npm run videos -- --id enter-paper-result        # one video
npm run videos -- --role intakeClerk             # one role (or --feature peopleAndResults)
npm run videos -- --changed                      # only videos whose script or tooling changed
npm run videos -- --voice none                   # captions only; also --voice sapi or --voice piper
npm run videos -- --burn-in                      # also a copy with the captions drawn into the picture
npm test                                         # the checks below
```

A build takes about twice the video's length. Output goes to `output/<role>/<feature>/` (git-ignored):

| File | What it is |
|---|---|
| `<id>.mp4` | 1920 x 1080, 30 fps, H.264 High, AAC 48 kHz at -16 LUFS, chapters per scene, fast start |
| `<id>.no-narration.mp4` | The same picture with no audio track |
| `<id>.vtt`, `<id>.srt` | Captions: at most two lines of 42 characters, timed to the speech |
| `<id>.transcript.md` | Every narrated line with its time, each action and what is on screen; a full text alternative |
| `<id>.timeline.json` | Step timings, highlights and zooms, the workflow fingerprint and the MERIT commit |
| `<id>.captioned.mp4` | With `--burn-in` only |

`output/README.md` and `output/catalog.json` list every video by role and feature, and flag any that are out of date.

## How a video is made

1. **Guard.** The build refuses to run unless the database name contains `TRAINING`, every account in it has an
   example-domain address, and it has the demo tenant.
2. **Reset.** The workflow's `prepare()` puts the training data back to its starting point. Records the videos create
   are marked, for example results with an accession number starting `TRN-`, and soft-deleted together with any case
   and tasks they caused.
3. **Narration first.** Each line becomes a WAV file, cached in `.work/narration`. The step is then held until both its
   actions and its speech have finished, which keeps the voice, captions and picture in step.
4. **Record.** Edge runs headless at 1536 x 864 with a device scale of 1.25, which captures exactly 1920 x 1080 with text
   25% larger than at 1:1.
   - Sign-in happens off camera with a saved session.
   - A drawn cursor follows real mouse input along eased paths, and presses ripple. Typing has a rhythm, and scrolling
     uses the wheel.
   - Native drop-down lists are drawn by the overlay, because headless browsers do not paint them.
   - Highlights frame the element a step is about, and zooms scale the page itself, so text stays sharp.
5. **Scan.** After every step the visible text and field values are checked for passwords, one-time codes,
   authenticator secrets, keys and tokens, identity numbers, and e-mail addresses outside the example domains. Password
   and code fields are masked as well.
6. **Assemble.** FFmpeg joins the title card, the recording and the recap card, and mixes the narration at its times.
   Captions, the transcript and the timeline come from the same timings.
7. **Validate.** The output is checked for:
   - resolution, frame rate, codec, pixel format, fast start, length and chapters;
   - an audio track and its loudness, when narrated;
   - caption line length, timing and overlap;
   - a transcript that has every line;
   - sensitive strings in any text file.

   A failed check fails the build.

## Tests

`npm test` runs four suites; see [playwright.config.ts](playwright.config.ts).

- **`unit.spec.ts`:** caption splitting and timing, narration helpers, and catalog rules (every workflow has a valid
  role, feature, account and recap, and readable lines).
- **`privacy.spec.ts`:** the scanner finds each kind of sensitive value and passes a synthetic page; the guard refuses a
  non-training database.
- **`workflows.spec.ts`:** every workflow runs fast against the app, without recording. A renamed button, a moved field
  or a changed outcome fails at the step that broke, naming the scene and step. It needs `npm run app`; set
  `MERIT_TRAINING_SKIP_APP=1` to skip it.
- **`outputs.spec.ts`:** every built video passes the output checks and is current with its script.

## More

- [docs/authoring.md](docs/authoring.md): writing and maintaining a workflow script.
- [docs/review-checklist.md](docs/review-checklist.md): what a person checks before a video is published.
- [docs/catalog.md](docs/catalog.md): MERIT's workflows by role and feature, and which suit a short video.
- [docs/design.md](docs/design.md): the tool evaluation and design decisions.

## Configuration

| Variable | Default | |
|---|---|---|
| `MERIT_TRAINING_URL` | `https://localhost:7243` | The running app |
| `MERIT_TRAINING_SERVER` | `localhost\PORTAL` | SQL Server instance |
| `MERIT_TRAINING_DATABASE` | `MERIT_TRAINING` | Must contain `TRAINING` |
| `MERIT_TRAINING_CHANNEL` | `msedge` | Browser to drive (`chrome` also works) |
| `MERIT_TRAINING_FFMPEG` | found automatically | Path to `ffmpeg.exe` |
| `MERIT_TRAINING_VOICE` | `en_US-kristin-medium` | Piper voice |
| `MERIT_TRAINING_SAPI_VOICE` | `Microsoft Zira Desktop` | Windows speech voice |
| `MERIT_TRAINING_CONNECTION` | from `appsettings.Development.json` | Base connection string for `npm run app` (the database is replaced) |
| `MERIT_TRAINING_SKIP_APP` | unset | `1` skips the workflow dry runs |
