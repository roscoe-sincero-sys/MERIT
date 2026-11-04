// Downloads the Piper text-to-speech engine and one voice into training/.tools/piper (git-ignored) and checks each file
// against the SHA-256 recorded here. Piper is MIT-licensed and runs offline. The default voice, en_US-kristin-medium,
// was trained from scratch on public-domain LibriVox recordings; ljspeech and norman are other public-domain voices.
// Usage: npm run setup:voice [-- --voice en_US-ljspeech-medium]
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { PATHS, run } from '../lib/config.ts';

const PIPER = {
  url: 'https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip',
  sha256: 'f3c58906402b24f3a96d92145f58acba6d86c9b5db896d207f78dc80811efcea',
};
const VOICES: Record<string, { path: string; onnx: string; json: string }> = {
  'en_US-kristin-medium': { path: 'en/en_US/kristin/medium',
    onnx: '5849957f929cbf720c258f8458692d6103fff2f0e3d3b19c8259474bb06a18d4',
    json: '5681426d4aead22195de70531eeeeddb46493cfaffc5764b2ea3db73428b651c' },
};

const arg = process.argv.indexOf('--voice');
const voice = arg > 0 ? process.argv[arg + 1] : 'en_US-kristin-medium';
const dir = join(PATHS.tools, 'piper');
mkdirSync(join(dir, 'voices'), { recursive: true });

async function download(url: string, target: string, sha256: string | null): Promise<void> {
  if (existsSync(target) && (!sha256 || hash(target) === sha256)) { console.log(`ok   ${target}`); return; }
  console.log(`get  ${url}`);
  const res = await fetch(url);
  if (!res.ok) throw new Error(`${url}: HTTP ${res.status}`);
  writeFileSync(target, Buffer.from(await res.arrayBuffer()));
  const actual = hash(target);
  if (sha256 && actual !== sha256) {
    rmSync(target);
    throw new Error(`${target}: SHA-256 ${actual} does not match the recorded ${sha256}; the file was removed.`);
  }
  if (!sha256) console.log(`     not pinned; SHA-256 ${actual}`);
}

function hash(file: string): string {
  return createHash('sha256').update(readFileSync(file)).digest('hex');
}

const zip = join(dir, 'piper_windows_amd64.zip');
await download(PIPER.url, zip, PIPER.sha256);
if (!existsSync(join(dir, 'piper', 'piper.exe'))) {
  // Expand-Archive, not tar: from Git Bash, GNU tar reads "C:" in a path as a remote host.
  run('powershell', ['-NoProfile', '-NonInteractive', '-Command',
    `Expand-Archive -LiteralPath '${zip.replace(/'/g, "''")}' -DestinationPath '${dir.replace(/'/g, "''")}' -Force`]);
}

const known = VOICES[voice];
const base = `https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/${known?.path ?? voiceCardPath(voice)}/${voice}`;
await download(`${base}.onnx`, join(dir, 'voices', `${voice}.onnx`), known?.onnx ?? null);
await download(`${base}.onnx.json`, join(dir, 'voices', `${voice}.onnx.json`), known?.json ?? null);
console.log(`Piper and ${voice} are ready in ${dir}. Check the voice's MODEL_CARD licence before using a voice that is not pinned here.`);

function voiceCardPath(name: string): string {
  const [lang, speaker, quality] = name.split('-');
  return `${lang.split('_')[0]}/${lang}/${speaker}/${quality}`;
}
