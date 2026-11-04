import { existsSync, readdirSync } from 'node:fs';
import { delimiter, join, resolve } from 'node:path';
import { execFileSync } from 'node:child_process';

/**
 * Settings for the training-video tooling. Everything comes from environment variables with local defaults; nothing
 * here is a secret. The demo password is the published one for the synthetic demo accounts (sourceCode/README.md), and
 * it is typed off camera only.
 *   MERIT_TRAINING_URL        base address of the running web app (default https://localhost:7243)
 *   MERIT_TRAINING_SERVER     SQL Server instance (default localhost\PORTAL)
 *   MERIT_TRAINING_DATABASE   database the app uses; must be a training database (default MERIT_TRAINING)
 *   MERIT_TRAINING_CHANNEL    installed browser to drive (default msedge)
 *   MERIT_TRAINING_FFMPEG     ffmpeg.exe, when it is not on PATH or in the winget package folder
 *   MERIT_TRAINING_VOICE      Piper voice name (default en_US-kristin-medium)
 */
export const ROOT = resolve(import.meta.dirname, '..');
export const REPO = resolve(ROOT, '..');
export const BASE_URL = process.env.MERIT_TRAINING_URL ?? 'https://localhost:7243';
export const SERVER = process.env.MERIT_TRAINING_SERVER ?? 'localhost\\PORTAL';
export const DATABASE = process.env.MERIT_TRAINING_DATABASE ?? 'MERIT_TRAINING';
export const CHANNEL = process.env.MERIT_TRAINING_CHANNEL ?? 'msedge';
export const VOICE = process.env.MERIT_TRAINING_VOICE ?? 'en_US-kristin-medium';
export const DEMO_PASSWORD = 'MeritDemo!2026';

/** The picture: 1080p at 30 frames a second, one CSS pixel to one video pixel. */
export const VIDEO = { width: 1920, height: 1080, fps: 30 } as const;

export const PATHS = {
  auth: join(ROOT, '.auth'),
  tools: join(ROOT, '.tools'),
  work: join(ROOT, '.work'),
  output: join(ROOT, 'output'),
  workflows: join(ROOT, 'workflows'),
  assets: join(ROOT, 'assets'),
};

function onPath(exe: string): string | null {
  for (const dir of (process.env.PATH ?? '').split(delimiter)) {
    const candidate = join(dir, exe);
    if (dir && existsSync(candidate)) return candidate;
  }
  return null;
}

/** ffmpeg or ffprobe: the override, then PATH, then the winget package folder (PATH is only refreshed in new shells). */
export function tool(name: 'ffmpeg' | 'ffprobe'): string {
  const exe = process.platform === 'win32' ? `${name}.exe` : name;
  const override = process.env.MERIT_TRAINING_FFMPEG;
  if (override) {
    const sibling = join(resolve(override, '..'), exe);
    if (existsSync(sibling)) return sibling;
  }
  const found = onPath(exe);
  if (found) return found;
  const packages = join(process.env.LOCALAPPDATA ?? '', 'Microsoft', 'WinGet', 'Packages');
  if (existsSync(packages)) {
    for (const pkg of readdirSync(packages).filter(d => d.startsWith('Gyan.FFmpeg'))) {
      for (const build of readdirSync(join(packages, pkg))) {
        const candidate = join(packages, pkg, build, 'bin', exe);
        if (existsSync(candidate)) return candidate;
      }
    }
  }
  throw new Error(`${name} was not found. Install it (winget install Gyan.FFmpeg) or set MERIT_TRAINING_FFMPEG.`);
}

/** Runs a program and returns its standard output as text. */
export function run(file: string, args: string[], input?: string): string {
  return execFileSync(file, args, { encoding: 'utf8', input, maxBuffer: 64 * 1024 * 1024, stdio: ['pipe', 'pipe', 'pipe'] });
}
