import { spawnSync } from 'node:child_process';
import { existsSync, openSync, readFileSync, readSync, closeSync } from 'node:fs';
import { MAX_LINE, parseVtt } from './captions.ts';
import { DEMO_PASSWORD, run, tool } from './config.ts';

/**
 * Checks a built video against what it promises: 1080p H.264 at 30 fps that streams (+faststart), narration audio at a
 * normal loudness when narrated, chapters, captions that are readable and inside the video, a transcript that has
 * every narrated line, and no sensitive strings in any text file.
 */
export interface Timeline {
  id: string;
  title: string;
  duration: number;
  narrated: boolean;
  scenes: string[];
  steps: { say: string; start: number; end: number }[];
}

export interface Probe {
  width: number; height: number; fps: number; codec: string; profile: string; pixFmt: string;
  duration: number; audio: { codec: string; rate: number } | null; chapters: number;
}

export function probe(file: string): Probe {
  const json = JSON.parse(run(tool('ffprobe'), ['-v', 'error', '-show_streams', '-show_format', '-show_chapters', '-of', 'json', file]));
  const v = json.streams.find((s: { codec_type: string }) => s.codec_type === 'video');
  const a = json.streams.find((s: { codec_type: string }) => s.codec_type === 'audio');
  const [n, d] = String(v.r_frame_rate).split('/').map(Number);
  return {
    width: v.width, height: v.height, fps: n / (d || 1), codec: v.codec_name, profile: v.profile, pixFmt: v.pix_fmt,
    duration: Number(json.format.duration) * 1000,
    audio: a ? { codec: a.codec_name, rate: Number(a.sample_rate) } : null,
    chapters: json.chapters?.length ?? 0,
  };
}

/** True when the moov atom comes before the media data, so playback can start before the download ends. */
export function fastStart(file: string): boolean {
  const fd = openSync(file, 'r');
  try {
    const head = Buffer.alloc(8);
    let offset = 0;
    for (let i = 0; i < 16; i++) {
      if (readSync(fd, head, 0, 8, offset) < 8) return false;
      const size = head.readUInt32BE(0), type = head.toString('ascii', 4, 8);
      if (type === 'moov') return true;
      if (type === 'mdat') return false;
      if (size < 8) return false;
      offset += size;
    }
    return false;
  } finally {
    closeSync(fd);
  }
}

/** Integrated loudness in LUFS, measured by FFmpeg's EBU R128 filter. */
export function loudness(file: string): number {
  const result = spawnSync(tool('ffmpeg'), ['-hide_banner', '-nostats', '-i', file, '-map', '0:a', '-af', 'ebur128', '-f', 'null', '-'],
    { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  const summary = (result.stderr ?? '').split('Summary:').at(-1) ?? '';
  const m = /I:\s*(-?[\d.]+)\s*LUFS/.exec(summary);
  return m ? Number(m[1]) : NaN;
}

const SENSITIVE: { rule: string; pattern: RegExp }[] = [
  { rule: 'demo password', pattern: new RegExp(DEMO_PASSWORD.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')) },
  { rule: 'six-digit code beside a code label', pattern: /\b(code|otp|passcode)\b[^\n]{0,40}\b\d{6}\b/i },
  { rule: 'authenticator secret', pattern: /\b(?:[A-Z2-7]{4}\s?){8,}\b/ },
  { rule: 'key or token', pattern: /\b(?:sk|pk|api|key|token|secret)[-_][A-Za-z0-9]{20,}\b/i },
  { rule: 'social security number', pattern: /\b\d{3}-\d{2}-\d{4}\b/ },
];

export function validate(base: string): string[] {
  const problems: string[] = [];
  const need = (file: string) => { if (!existsSync(file)) problems.push(`missing ${file}`); return existsSync(file); };
  if (!need(`${base}.timeline.json`)) return problems;
  const timeline = JSON.parse(readFileSync(`${base}.timeline.json`, 'utf8')) as Timeline;

  if (need(`${base}.mp4`)) {
    const p = probe(`${base}.mp4`);
    if (p.width !== 1920 || p.height !== 1080) problems.push(`picture is ${p.width}x${p.height}, not 1920x1080`);
    if (Math.abs(p.fps - 30) > 0.01) problems.push(`frame rate is ${p.fps}, not 30`);
    if (p.codec !== 'h264' || p.pixFmt !== 'yuv420p') problems.push(`video is ${p.codec} ${p.pixFmt}, not h264 yuv420p`);
    if (Math.abs(p.duration - timeline.duration) > 600) problems.push(`length ${Math.round(p.duration)} ms differs from the timeline's ${timeline.duration} ms`);
    if (timeline.narrated && (!p.audio || p.audio.codec !== 'aac' || p.audio.rate !== 48000)) problems.push('narrated video has no 48 kHz AAC track');
    if (!timeline.narrated && p.audio) problems.push('captions-only video has an audio track');
    if (timeline.narrated && p.audio) {
      const lufs = loudness(`${base}.mp4`);
      if (!(lufs >= -19 && lufs <= -13)) problems.push(`narration loudness is ${lufs} LUFS, outside -19 to -13`);
    }
    if (p.chapters !== timeline.scenes.length) problems.push(`${p.chapters} chapters for ${timeline.scenes.length} scenes`);
    if (!fastStart(`${base}.mp4`)) problems.push('not fast-start: the moov atom follows the media data');
  }

  if (need(`${base}.vtt`) && need(`${base}.srt`)) {
    const vtt = readFileSync(`${base}.vtt`, 'utf8');
    if (!vtt.startsWith('WEBVTT')) problems.push('the VTT file does not start with WEBVTT');
    const cues = parseVtt(vtt);
    if (!cues.length) problems.push('no captions');
    const srtCount = readFileSync(`${base}.srt`, 'utf8').split(/\r?\n\r?\n/).filter(b => b.includes('-->')).length;
    if (srtCount !== cues.length) problems.push(`SRT has ${srtCount} cues, VTT ${cues.length}`);
    cues.forEach((c, i) => {
      if (!c.lines.length || c.lines.some(l => !l.trim())) problems.push(`caption ${i + 1} is empty`);
      if (c.lines.length > 2) problems.push(`caption ${i + 1} has ${c.lines.length} lines`);
      if (c.lines.some(l => l.length > MAX_LINE)) problems.push(`caption ${i + 1} has a line over ${MAX_LINE} characters`);
      if (c.end - c.start < 700) problems.push(`caption ${i + 1} is shown for only ${c.end - c.start} ms`);
      if (i && c.start < cues[i - 1].end) problems.push(`caption ${i + 1} overlaps the one before`);
      if (c.end > timeline.duration + 50) problems.push(`caption ${i + 1} ends after the video`);
    });
    const captionText = cues.map(c => c.lines.join(' ')).join(' ').replace(/\s+/g, ' ');
    for (const step of timeline.steps) {
      if (!captionText.includes(step.say.replace(/\s+/g, ' '))) problems.push(`captions do not contain "${step.say.slice(0, 50)}…"`);
    }
  }

  if (need(`${base}.transcript.md`)) {
    const text = readFileSync(`${base}.transcript.md`, 'utf8');
    for (const step of timeline.steps) if (!text.includes(step.say)) problems.push(`transcript is missing "${step.say.slice(0, 50)}…"`);
  }

  for (const ext of ['vtt', 'srt', 'transcript.md', 'timeline.json']) {
    const file = `${base}.${ext}`;
    if (!existsSync(file)) continue;
    const text = readFileSync(file, 'utf8');
    for (const { rule, pattern } of SENSITIVE) if (pattern.test(text)) problems.push(`${ext} contains a ${rule}`);
  }
  return problems;
}
