import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { PATHS, VOICE, run } from './config.ts';

/**
 * Narration: one WAV per line, made before recording so each step can be held for as long as its speech lasts. That is
 * what keeps voice, captions and action in step. Engines, all offline:
 *   piper   natural voice (npm run setup:voice)                       default when installed
 *   sapi    Windows' built-in speech, no install                     fallback
 *   none    captions only; a step is held for its reading time
 * Clips are cached in .work/narration by engine, voice and text.
 */
export type Engine = 'piper' | 'sapi' | 'none';

export interface Clip { text: string; file: string | null; seconds: number }

const PIPER_EXE = join(PATHS.tools, 'piper', 'piper', 'piper.exe');
const PIPER_VOICE = join(PATHS.tools, 'piper', 'voices', `${VOICE}.onnx`);

export function piperAvailable(): boolean {
  return existsSync(PIPER_EXE) && existsSync(PIPER_VOICE);
}

export function resolveEngine(requested: string | undefined): Engine {
  if (requested === 'none' || requested === 'sapi' || requested === 'piper') {
    if (requested === 'piper' && !piperAvailable()) throw new Error('Piper is not installed: run npm run setup:voice, or use --voice sapi or --voice none.');
    return requested;
  }
  return piperAvailable() ? 'piper' : process.platform === 'win32' ? 'sapi' : 'none';
}

/** Time to read a caption at a comfortable pace (about 160 words a minute), at least two seconds. */
export function readingSeconds(text: string): number {
  return Math.max(2, text.split(/\s+/).filter(Boolean).length / 2.7);
}

export function synthesize(text: string, engine: Engine): Clip {
  if (engine === 'none') return { text, file: null, seconds: readingSeconds(text) };
  const dir = join(PATHS.work, 'narration');
  mkdirSync(dir, { recursive: true });
  const key = createHash('sha256').update(`${engine}|${VOICE}|${text}`).digest('hex').slice(0, 20);
  const file = join(dir, `${key}.wav`);
  if (!existsSync(file)) {
    if (engine === 'piper') {
      // A slightly slower pace than Piper's default reads better over a moving screen.
      run(PIPER_EXE, ['--model', PIPER_VOICE, '--length_scale', '1.08', '--sentence_silence', '0.25', '--output_file', file], speakable(text));
    } else {
      const voice = (process.env.MERIT_TRAINING_SAPI_VOICE ?? 'Microsoft Zira Desktop').replace(/'/g, "''");
      const ps = `Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer;
        try { $s.SelectVoice('${voice}') } catch { }
        $s.Rate = 0; $s.SetOutputToWaveFile('${file.replace(/'/g, "''")}'); $s.Speak([Console]::In.ReadToEnd()); $s.Dispose()`;
      run('powershell', ['-NoProfile', '-NonInteractive', '-Command', ps], speakable(text));
    }
  }
  return { text, file, seconds: wavSeconds(file) };
}

/** Text as it should be spoken: units and symbols the engines read badly, spelled out. */
export function speakable(text: string): string {
  return text
    .replace(/µg\/dL|ug\/dL/g, 'micrograms per deciliter')
    .replace(/\bMERIT\b/g, 'Merit')
    .replace(/\bCLIA\b/g, 'clia')
    .replace(/\bNPI\b/g, 'N P I')
    .replace(/→/g, 'then');
}

/** Length of a PCM WAV file from its header. */
export function wavSeconds(file: string): number {
  const b = readFileSync(file);
  let offset = 12, byteRate = 0;
  while (offset + 8 <= b.length) {
    const id = b.toString('ascii', offset, offset + 4);
    const size = b.readUInt32LE(offset + 4);
    if (id === 'fmt ') byteRate = b.readUInt32LE(offset + 16);
    if (id === 'data') return byteRate ? size / byteRate : 0;
    offset += 8 + size + (size % 2);
  }
  throw new Error(`${file} is not a PCM WAV file.`);
}

export function writeIfChanged(file: string, content: string): void {
  if (!existsSync(file) || readFileSync(file, 'utf8') !== content) writeFileSync(file, content);
}
