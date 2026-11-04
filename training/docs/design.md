# Design notes

## Tools considered

| Tool | Verdict | Why |
|---|---|---|
| **Playwright** | Used | Apache-2.0 and already used by MERIT's e2e tests. Drives the installed Edge (no browser download), types and clicks like a person, gives element positions for highlights, and exposes the Chromium screencast. |
| Playwright's built-in `recordVideo` | Not used | It encodes VP8 at a fixed, modest quality, which blurs small text at 1080p, and its frames are not tied to the step clock. |
| **FFmpeg** | Used | Free, offline, and does everything after capture: constant-rate H.264, audio placement, loudness normalization (EBU R128), chapters, burned-in captions. |
| Remotion | Not used | Its code is source-available, but the company licence is paid for organizations of more than three people, so it is not free for every agency adopting MERIT. Its strength, React-composed motion graphics, isn't needed for screen recordings. |
| **Piper** | Optional | MIT, offline, natural speech. The default voice (kristin) was trained from scratch on public-domain LibriVox recordings. Popular voices such as lessac, ryan and hfc are trained on non-commercial data, so they are not the default. |
| Windows speech (SAPI) | Fallback | No install. More robotic, but clear. |

## Decisions

- **Capture through the screencast.** `lib/capture.ts` receives a JPEG for every painted frame, with its wall-clock
  time. FFmpeg holds each frame until the next one arrived and resamples to 30 fps. Text stays sharp, and frame times
  share the clock the step timeline uses, so captions and narration line up without guesswork.
- **Device scale 1.25.** A 1536 x 864 viewport at scale 1.25 is captured at exactly 1920 x 1080, with text a quarter
  larger than at 1:1, which is easier to read in a video. MERIT's layout is the same as on a 1536-pixel laptop.
- **Zoom in the page, not in FFmpeg.** Zooming scales the page body with a CSS transform, so the browser re-renders text
  at the higher size rather than enlarging pixels. The cursor layer sits outside the body and is not scaled. The zoom
  ends before the next step's actions.
- **Narration sets the pace.** Speech is made before recording, and each step lasts as long as the longer of its
  actions and its speech. Captions come from the same timings, so nothing drifts.
- **Drawn drop-down lists.** Headless Chromium does not paint native `<select>` pop-ups into the page. The overlay draws
  a list at the control, the cursor chooses the option, and then the real `selectOption` runs.
- **Off-camera sign-in.** Sessions are made before recording and kept, with each account's authenticator secret, in the
  git-ignored `.auth/`. No password or code is typed on camera.
- **Dry runs as tests.** The same workflow code runs without pacing or the overlay. Every action checks its target, so
  the tests fail at the step a change broke, which is what keeps videos regenerable.
- **A training database.** `MERIT_TRAINING` is a separate deployment of the same scripts with the demo data, so
  recording never disturbs the demo database (`MERIT`) or the integration-test database (`MERIT_IT`). The guard refuses
  any other database.
- **Soft-delete resets.** `prepare()` marks out what a video creates and soft-deletes it. The repository's SQL
  conventions forbid hard deletes, and the tooling follows them. Sequence-based numbers, such as case numbers, still
  advance between builds.
- **Content Security Policy.** MERIT sends a nonce-based CSP. Recording contexts set Playwright's `bypassCSP` so the
  overlay's style element applies; dry runs keep the policy.
- **Isolated dependencies.** `training/` has its own `package.json` (Playwright, TypeScript and Node types only), runs
  TypeScript directly on Node 24, and is referenced by nothing in `sourceCode/`.
