// Builds training videos: records each workflow against the training database, then writes
// output/<role>/<feature>/<id>.mp4 (narrated), .no-narration.mp4, .vtt, .srt, .transcript.md and .timeline.json, checks
// them, and refreshes output/catalog.json and output/README.md.
// Usage: npm run videos -- [--id <id>] [--role <role>] [--feature <feature>] [--changed] [--voice piper|sapi|none]
//                          [--burn-in] [--headed] [--keep-frames]
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { parseArgs } from 'node:util';
import { chromium } from '@playwright/test';
import { cues, toSrt, toVtt, type Spoken } from '../lib/captions.ts';
import { recapHtml, renderCard, titleHtml } from '../lib/cards.ts';
import { loadCatalog, outputBase, type Entry } from '../lib/catalog.ts';
import { burnIn, compose, withoutAudio } from '../lib/compose.ts';
import { CHANNEL, PATHS, REPO, VOICE, run } from '../lib/config.ts';
import { LEAD, perform } from '../lib/director.ts';
import { resolveEngine, synthesize, type Engine } from '../lib/narration.ts';
import { transcript } from '../lib/transcript.ts';
import { validate } from '../lib/validate.ts';
import { FEATURES, ROLES } from '../lib/workflow.ts';

const { values: args } = parseArgs({
  options: {
    id: { type: 'string' }, role: { type: 'string' }, feature: { type: 'string' },
    changed: { type: 'boolean' }, voice: { type: 'string' }, 'burn-in': { type: 'boolean' },
    headed: { type: 'boolean' }, 'keep-frames': { type: 'boolean' },
  },
});

const engine: Engine = resolveEngine(args.voice);
const catalog = await loadCatalog();
const chosen = catalog.filter(e =>
  (!args.id || e.workflow.id === args.id) && (!args.role || e.workflow.role === args.role) && (!args.feature || e.workflow.feature === args.feature));
if (!chosen.length) throw new Error('No workflow matches; ids: ' + catalog.map(e => e.workflow.id).join(', '));

function appVersion(): string {
  try {
    const commit = run('git', ['-C', REPO, 'log', '-1', '--format=%h %cs', '--', 'sourceCode/src']).trim();
    const dirty = run('git', ['-C', REPO, 'status', '--porcelain', '--', 'sourceCode/src']).trim() ? ' with local changes' : '';
    return `${commit}${dirty}`;
  } catch {
    return 'unknown';
  }
}

function sentenceList(items: string[]): string {
  const lower = items.map(i => i.charAt(0).toLowerCase() + i.slice(1));
  return lower.length < 2 ? lower.join('') : `${lower.slice(0, -1).join(', ')}, and ${lower.at(-1)}`;
}

function upToDate(entry: Entry): boolean {
  const base = outputBase(entry.workflow);
  if (!existsSync(`${base}.mp4`) || !existsSync(`${base}.timeline.json`)) return false;
  const previous = JSON.parse(readFileSync(`${base}.timeline.json`, 'utf8'));
  return previous.fingerprint === entry.fingerprint && previous.engine === engine;
}

const browser = await chromium.launch({ channel: CHANNEL, headless: !args.headed });
let failures = 0;
try {
  for (const entry of chosen) {
    const w = entry.workflow;
    if (args.changed && upToDate(entry)) { console.log(`= ${w.id}: up to date`); continue; }
    const started = Date.now();
    console.log(`> ${w.id} (${ROLES[w.role]}, ${FEATURES[w.feature]}), narration: ${engine}`);
    const base = outputBase(w);
    const workDir = join(PATHS.work, w.id);
    mkdirSync(dirname(base), { recursive: true });
    mkdirSync(workDir, { recursive: true });
    try {
      const introText = `${w.title}. ${w.summary}`;
      const outroText = `In this video you ${sentenceList(w.recap)}.`;
      const intro = synthesize(introText, engine), outro = synthesize(outroText, engine);
      const introMs = Math.round(Math.max(4000, LEAD + intro.seconds * 1000 + 800));
      const outroMs = Math.round(Math.max(5000, 400 + outro.seconds * 1000 + 1400));

      const rec = await perform(w, { mode: 'record', engine, workDir, browser, log: console.log });
      const total = introMs + rec.duration + outroMs;
      const shift = (ms: number) => Math.round(ms + introMs);

      const titleCard = join(workDir, 'title.png'), recapCard = join(workDir, 'recap.png');
      await renderCard(browser, titleHtml(w), titleCard);
      await renderCard(browser, recapHtml(w), recapCard);

      const audio = [
        ...(intro.file ? [{ file: intro.file, at: LEAD }] : []),
        ...rec.steps.filter(s => s.speech.file).map(s => ({ file: s.speech.file!, at: shift(s.speech.start) })),
        ...(outro.file ? [{ file: outro.file, at: introMs + rec.duration + 400 }] : []),
      ];
      const scenes = w.scenes.map(s => s.title);
      const chapters = scenes.map((title, i) => {
        const inScene = rec.steps.filter(s => s.scene === i);
        return { title, start: i === 0 ? 0 : shift(inScene[0].start), end: i === scenes.length - 1 ? total : shift(rec.steps.find(s => s.scene === i + 1)!.start) };
      });

      compose({
        title: `MERIT training: ${w.title}`,
        comment: `${ROLES[w.role]} · ${FEATURES[w.feature]}. Synthetic demonstration data only.`,
        titleCard, recapCard, introMs, outroMs, frames: rec.frames, recordingMs: rec.duration, audio, chapters, workDir,
        output: `${base}.mp4`,
      });
      const narrated = audio.length > 0;
      if (narrated) withoutAudio(`${base}.mp4`, `${base}.no-narration.mp4`);
      else rmSync(`${base}.no-narration.mp4`, { force: true });

      const spoken: Spoken[] = [
        { text: introText, start: LEAD, seconds: intro.seconds, until: introMs },
        ...rec.steps.map(s => ({ text: s.say, start: shift(s.speech.start), seconds: s.speech.seconds, until: shift(s.end) })),
        { text: outroText, start: introMs + rec.duration + 400, seconds: outro.seconds, until: total },
      ];
      const list = cues(spoken);
      writeFileSync(`${base}.vtt`, toVtt(list));
      writeFileSync(`${base}.srt`, toSrt(list));
      if (args['burn-in']) burnIn(`${base}.mp4`, `${base}.srt`, `${base}.captioned.mp4`);

      const built = new Date().toISOString().slice(0, 10);
      const version = appVersion();
      const source = relative(REPO, entry.file).replace(/\\/g, '/');
      writeFileSync(`${base}.transcript.md`, transcript({ workflow: w, steps: rec.steps, offset: introMs, intro: introText, outro: outroText,
        duration: total, built, appVersion: version, source }));
      writeFileSync(`${base}.timeline.json`, JSON.stringify({
        id: w.id, title: w.title, summary: w.summary, role: w.role, feature: w.feature, user: w.user, source,
        fingerprint: entry.fingerprint, appVersion: version, built, engine, voice: engine === 'piper' ? VOICE : engine === 'sapi' ? 'Windows speech' : null,
        narrated, duration: Math.round(total), introMs, outroMs, scenes,
        steps: rec.steps.map(s => ({ scene: s.scene, say: s.say, start: shift(s.start), end: shift(s.end), actions: s.actions, onScreen: s.onScreen,
          highlights: s.highlights.map(h => ({ ...h, at: shift(h.at) })), zooms: s.zooms.map(z => ({ ...z, at: shift(z.at) })) })),
      }, null, 2) + '\n');

      const problems = validate(base);
      if (problems.length) throw new Error(`the output did not pass its checks:\n  - ${problems.join('\n  - ')}`);
      if (!args['keep-frames']) rmSync(join(workDir, 'frames'), { recursive: true, force: true });
      console.log(`✓ ${relative(REPO, base)}.mp4 (${(total / 1000).toFixed(1)} s) in ${((Date.now() - started) / 1000).toFixed(0)} s`);
    } catch (error) {
      failures++;
      console.error(`✗ ${w.id}: ${(error as Error).message}`);
    }
  }
} finally {
  await browser.close();
}

writeIndex(catalog);
if (failures) process.exitCode = 1;

/** output/catalog.json and output/README.md: every workflow by role and feature, with what has been built. */
function writeIndex(entries: Entry[]): void {
  mkdirSync(PATHS.output, { recursive: true });
  const rows = entries.map(e => {
    const base = outputBase(e.workflow);
    const timeline = existsSync(`${base}.timeline.json`) ? JSON.parse(readFileSync(`${base}.timeline.json`, 'utf8')) : null;
    return { id: e.workflow.id, title: e.workflow.title, role: e.workflow.role, feature: e.workflow.feature,
      built: timeline?.built ?? null, current: timeline?.fingerprint === e.fingerprint, seconds: timeline ? Math.round(timeline.duration / 1000) : null,
      files: timeline ? relative(PATHS.output, base).replace(/\\/g, '/') : null };
  });
  writeFileSync(join(PATHS.output, 'catalog.json'), JSON.stringify(rows, null, 2) + '\n');
  const lines = ['# MERIT training videos', '', 'Generated by `npm run videos` in `training/`. Each video has `.mp4` (narrated), `.no-narration.mp4`, `.vtt` and `.srt` captions, a `.transcript.md` and a `.timeline.json`.', ''];
  for (const role of Object.keys(ROLES) as (keyof typeof ROLES)[]) {
    const mine = rows.filter(r => r.role === role);
    if (!mine.length) continue;
    lines.push(`## ${ROLES[role]}`, '', '| Video | Feature | Length | Built | Files |', '|---|---|---|---|---|');
    for (const r of mine) {
      const length = r.seconds ? `${Math.floor(r.seconds / 60)}:${String(r.seconds % 60).padStart(2, '0')}` : '–';
      lines.push(`| ${r.title} | ${FEATURES[r.feature]} | ${length} | ${r.built ?? 'not built'}${r.built && !r.current ? ' (out of date)' : ''} | ${r.files ? `[mp4](${r.files}.mp4) · [captions](${r.files}.vtt) · [transcript](${r.files}.transcript.md)` : '–'} |`);
    }
    lines.push('');
  }
  writeFileSync(join(PATHS.output, 'README.md'), lines.join('\n'));
}
