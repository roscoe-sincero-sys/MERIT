// Checks everything a build needs and says how to fix what is missing. Usage: npm run doctor
import { chromium, request } from '@playwright/test';
import { BASE_URL, CHANNEL, DATABASE, SERVER, VOICE, run, tool } from '../lib/config.ts';
import { piperAvailable } from '../lib/narration.ts';
import { assertTrainingTarget } from '../lib/privacy.ts';

let failed = 0;
async function check(name: string, fn: () => unknown | Promise<unknown>, fix: string, optional = false): Promise<void> {
  try {
    const detail = await fn();
    console.log(`ok    ${name}${detail ? `: ${detail}` : ''}`);
  } catch (error) {
    if (!optional) failed++;
    console.log(`${optional ? 'note ' : 'FAIL '} ${name}: ${(error as Error).message.split('\n')[0]}\n      ${fix}`);
  }
}

await check('Node.js 22.18 or later', () => {
  const [major, minor] = process.versions.node.split('.').map(Number);
  if (major < 22 || (major === 22 && minor < 18)) throw new Error(process.version);
  return process.version;
}, 'Install Node.js 24 LTS (it runs the TypeScript tooling directly).');
await check('FFmpeg', () => run(tool('ffmpeg'), ['-hide_banner', '-version']).split('\n')[0], 'winget install Gyan.FFmpeg (or set MERIT_TRAINING_FFMPEG).');
await check('FFmpeg filters and encoders', () => {
  const filters = run(tool('ffmpeg'), ['-hide_banner', '-filters']), encoders = run(tool('ffmpeg'), ['-hide_banner', '-encoders']);
  const missing = ['loudnorm', 'amix', 'adelay', 'subtitles', 'ebur128'].filter(f => !new RegExp(`\\s${f}\\s`).test(filters))
    .concat(['libx264', 'aac'].filter(e => !new RegExp(`\\s${e}\\s`).test(encoders)));
  if (missing.length) throw new Error(`missing ${missing.join(', ')}`);
}, 'Use a full FFmpeg build (Gyan.FFmpeg includes libx264, libass and loudnorm).');
await check('ffprobe', () => tool('ffprobe'), 'It ships with FFmpeg.');
await check(`Browser (${CHANNEL})`, async () => { const b = await chromium.launch({ channel: CHANNEL }); const v = b.version(); await b.close(); return v; },
  'Install Microsoft Edge, or set MERIT_TRAINING_CHANNEL=chrome.');
await check('sqlcmd', () => run('sqlcmd', ['-?']).split('\n')[0] || 'found', 'Install the SQL Server command-line tools (sqlcmd).');
await check(`Training database ${DATABASE} on ${SERVER}`, () => { assertTrainingTarget(); return 'synthetic demo data only'; },
  'npm run db:deploy creates it with the demo data (about 25 minutes the first time).');
await check(`MERIT at ${BASE_URL}`, async () => {
  // The ASP.NET Core development certificate is not trusted by Node, so ask through Playwright, as recording does.
  const api = await request.newContext({ ignoreHTTPSErrors: true });
  try {
    const res = await api.get(`${BASE_URL}/account/signin`);
    if (!res.ok()) throw new Error(`HTTP ${res.status()}`);
    return `HTTP ${res.status()}`;
  } finally {
    await api.dispose();
  }
}, 'npm run app starts it on the training database (in its own terminal).');
await check(`Piper voice ${VOICE}`, () => { if (!piperAvailable()) throw new Error('not installed'); return 'installed'; },
  'npm run setup:voice installs it; without it narration uses Windows speech, or use --voice none.', true);

if (failed) { console.log(`\n${failed} check(s) failed.`); process.exitCode = 1; } else console.log('\nReady: npm run videos');
