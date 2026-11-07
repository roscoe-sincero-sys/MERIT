import { mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { chromium, type Browser } from '@playwright/test';
import { ensureSession } from './auth.ts';
import { Recorder, type Frame } from './capture.ts';
import { BASE_URL, CHANNEL, PATHS, VIDEO } from './config.ts';
import { synthesize, type Clip, type Engine } from './narration.ts';
import { overlayScript, type Rect } from './overlay.ts';
import { assertSafe, assertTrainingTarget } from './privacy.ts';
import { Stage, type Mode } from './stage.ts';
import type { Workflow } from './workflow.ts';

/**
 * Runs a workflow. In 'record' mode it captures the screen and returns the timeline the encoder, captions and
 * transcript are built from; in 'dry' mode it only proves the workflow still works.
 *
 * Timing: narration is synthesized first, and each step is held until both its actions and its speech are done, plus a
 * short beat. Speech starts LEAD ms after the step does. All times in the timeline are milliseconds from the first
 * recorded frame.
 */
export const LEAD = 300;
const TAIL = 450;

/** The viewport, in CSS pixels; the device scale makes it exactly 1920 x 1080 and the text 25% larger. */
export const DEVICE_SCALE = 1.25;
export const VIEWPORT = { width: VIDEO.width / DEVICE_SCALE, height: VIDEO.height / DEVICE_SCALE };

export interface TimedStep {
  scene: number;
  sceneTitle: string;
  index: number;
  say: string;
  start: number;
  end: number;
  speech: { file: string | null; start: number; seconds: number };
  onScreen: string[];
  actions: string[];
  log: { kind: 'action' | 'screen'; text: string }[];
  highlights: { at: number; rect: Rect }[];
  zooms: { at: number; rect: Rect; scale: number }[];
}

export interface Recording {
  steps: TimedStep[];
  frames: Frame[];
  /** Milliseconds from the first frame to the end of the recording. */
  duration: number;
  /** Device pixels per CSS pixel, to place rectangles on the video. */
  scale: number;
}

export interface PerformOptions {
  mode: Mode;
  engine?: Engine;
  /** Where frames go in record mode. */
  workDir?: string;
  headed?: boolean;
  browser?: Browser;
  log?: (line: string) => void;
}

export async function perform(workflow: Workflow, options: PerformOptions): Promise<Recording> {
  const log = options.log ?? (() => undefined);
  const recording = options.mode === 'record';
  assertTrainingTarget();
  await workflow.prepare?.();

  const clips: Clip[][] = workflow.scenes.map(scene => scene.steps.map(step =>
    recording ? synthesize(step.say, options.engine ?? 'none') : { text: step.say, file: null, seconds: 0 }));

  const browser = options.browser ?? await chromium.launch({ channel: CHANNEL, headless: !options.headed });
  const workDir = options.workDir ?? join(PATHS.work, workflow.id);
  const framesDir = join(workDir, 'frames');
  try {
    const storageState = workflow.anonymous ? undefined : await ensureSession(browser, workflow.user);
    const context = await browser.newContext({
      baseURL: BASE_URL, ignoreHTTPSErrors: true, storageState,
      viewport: VIEWPORT, deviceScaleFactor: DEVICE_SCALE,
      locale: 'en-US', timezoneId: 'America/New_York', colorScheme: 'light',
      // The overlay's stylesheet is injected without the application's nonce; only recordings need it.
      bypassCSP: recording,
    });
    if (recording) await context.addInitScript(overlayScript);
    const page = await context.newPage();
    await workflow.setup?.(page);
    // A step whose element never appears fails in seconds, naming the step.
    page.setDefaultTimeout(10_000);
    const stage = new Stage(page, options.mode);
    await stage.goto(workflow.start);
    await assertSafe(page, `${workflow.id} start`);

    let recorder: Recorder | null = null;
    if (recording) {
      rmSync(framesDir, { recursive: true, force: true });
      mkdirSync(framesDir, { recursive: true });
      recorder = new Recorder(framesDir);
      await recorder.start(page);
      // Nudge a repaint so the first frame arrives, then let the opening view settle.
      await stage.placeCursor();
      await page.waitForTimeout(900);
    }
    const t0 = () => recorder?.startedAt ?? 0;

    const steps: TimedStep[] = [];
    for (const [s, scene] of workflow.scenes.entries()) {
      for (const [i, step] of scene.steps.entries()) {
        const where = `${workflow.id} scene ${s + 1} "${scene.title}" step ${i + 1} ("${step.say.slice(0, 50)}…")`;
        log(`  ${s + 1}.${i + 1} ${step.say}`);
        const clip = clips[s][i];
        const started = Date.now();
        try {
          await step.run?.(stage);
          await stage.settle();
          await assertSafe(page, where);
        } catch (error) {
          if (recording) await page.screenshot({ path: join(workDir, 'failure.png') }).catch(() => undefined);
          throw new Error(`${where} failed: ${(error as Error).message.split('\n')[0]}`);
        }
        if (recording) {
          const spoken = LEAD + clip.seconds * 1000 + TAIL;
          const wait = Math.max(spoken, Date.now() - started + (step.hold ?? 500)) - (Date.now() - started);
          if (wait > 0) await page.waitForTimeout(wait);
        }
        const notes = await stage.endStep();
        const relative = <T extends { at: number }>(items: T[]) => items.map(item => ({ ...item, at: item.at - t0() }));
        steps.push({
          scene: s, sceneTitle: scene.title, index: i, say: step.say,
          start: started - t0(), end: Date.now() - t0(),
          speech: { file: clip.file, start: started - t0() + LEAD, seconds: clip.seconds },
          onScreen: notes.onScreen, actions: notes.actions, log: notes.log, highlights: relative(notes.highlights), zooms: relative(notes.zooms),
        });
      }
    }

    let frames: Frame[] = [];
    let duration = steps.at(-1)?.end ?? 0;
    if (recorder) {
      await page.waitForTimeout(700);
      const stopped = await recorder.stop();
      frames = stopped.frames;
      if (!frames.length) throw new Error('The screencast produced no frames.');
      duration = stopped.endAt - t0();
      // A computer that sleeps mid-recording leaves a gap the video would show as a frozen frame for hours.
      const longest = Math.max(...frames.map((f, i) => (frames[i + 1]?.at ?? stopped.endAt) - f.at));
      const longestStep = Math.max(...steps.map(s => s.end - s.start));
      if (longest > 60_000 || longestStep > 120_000) {
        throw new Error(`The recording was interrupted (a ${Math.round(Math.max(longest, longestStep) / 1000)} s gap; did the computer sleep?). Build it again.`);
      }
      writeFileSync(join(workDir, 'steps.json'), JSON.stringify(steps, null, 2));
    }
    await context.close();
    return { steps, frames, duration, scale: DEVICE_SCALE };
  } finally {
    if (!options.browser) await browser.close();
  }
}
