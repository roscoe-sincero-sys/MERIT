import { spawnSync } from 'node:child_process';
import { basename, dirname, join, relative } from 'node:path';
import { writeFileSync } from 'node:fs';
import type { Frame } from './capture.ts';
import { tool } from './config.ts';

/**
 * Builds the MP4 with FFmpeg: title card, the recording, recap card; narration clips mixed at their times and
 * loudness-normalized to -16 LUFS; chapters per scene. H.264 High, yuv420p, 30 fps constant, +faststart so it plays
 * while it downloads; AAC 48 kHz. Every file a build reads is passed by path; nothing is downloaded.
 */
export interface AudioPlacement { file: string; at: number }
export interface Chapter { title: string; start: number; end: number }

export interface ComposeInput {
  title: string;
  comment: string;
  titleCard: string;
  recapCard: string;
  introMs: number;
  outroMs: number;
  frames: Frame[];
  recordingMs: number;
  audio: AudioPlacement[];
  chapters: Chapter[];
  workDir: string;
  output: string;
}

const FADE = 0.4;

export function ffmpeg(args: string[], cwd?: string): void {
  const result = spawnSync(tool('ffmpeg'), ['-hide_banner', '-loglevel', 'error', '-y', ...args], { cwd, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  if (result.status !== 0) throw new Error(`ffmpeg failed (${result.status}): ${(result.stderr ?? '').trim().split('\n').slice(-6).join('\n')}`);
}

/** The screencast frames as a concat list: each frame is shown until the next one arrived. */
function frameList(frames: Frame[], recordingMs: number, dir: string): string {
  const t0 = frames[0].at;
  const lines = ['ffconcat version 1.0'];
  frames.forEach((f, i) => {
    const next = i + 1 < frames.length ? frames[i + 1].at - t0 : recordingMs;
    const seconds = Math.max(0.001, (next - (f.at - t0)) / 1000);
    lines.push(`file '${relative(dir, f.file).replace(/\\/g, '/')}'`, `duration ${seconds.toFixed(4)}`);
  });
  // The concat demuxer needs the last file repeated for its duration to count.
  lines.push(`file '${relative(dir, frames.at(-1)!.file).replace(/\\/g, '/')}'`);
  const list = join(dir, 'frames.ffconcat');
  writeFileSync(list, lines.join('\n') + '\n');
  return list;
}

function chaptersFile(chapters: Chapter[], dir: string): string {
  const esc = (s: string) => s.replace(/([=;#\\\n])/g, '\\$1');
  const body = [';FFMETADATA1', ...chapters.flatMap(c =>
    ['[CHAPTER]', 'TIMEBASE=1/1000', `START=${Math.round(c.start)}`, `END=${Math.round(c.end)}`, `title=${esc(c.title)}`])];
  const file = join(dir, 'chapters.txt');
  writeFileSync(file, body.join('\n') + '\n');
  return file;
}

export function compose(input: ComposeInput): void {
  const intro = input.introMs / 1000, outro = input.outroMs / 1000, rec = input.recordingMs / 1000;
  const total = intro + rec + outro;
  const list = frameList(input.frames, input.recordingMs, input.workDir);
  const chapters = chaptersFile(input.chapters, input.workDir);

  const args = [
    '-loop', '1', '-framerate', '30', '-t', intro.toFixed(3), '-i', input.titleCard,
    '-f', 'concat', '-safe', '0', '-i', list,
    '-loop', '1', '-framerate', '30', '-t', outro.toFixed(3), '-i', input.recapCard,
  ];
  for (const a of input.audio) args.push('-i', a.file);
  const metaIndex = 3 + input.audio.length;
  args.push('-i', chapters);

  const video = [
    `[0:v]fps=30,format=yuv420p,setsar=1,fade=t=out:st=${(intro - FADE).toFixed(3)}:d=${FADE}[v0]`,
    `[1:v]fps=30,scale=1920:1080:force_original_aspect_ratio=decrease:flags=lanczos:in_range=full:out_range=limited,pad=1920:1080:(ow-iw)/2:(oh-ih)/2:color=white,` +
      `format=yuv420p,setsar=1,trim=duration=${rec.toFixed(3)},setpts=PTS-STARTPTS,fade=t=in:st=0:d=${FADE},fade=t=out:st=${(rec - FADE).toFixed(3)}:d=${FADE}[v1]`,
    `[2:v]fps=30,format=yuv420p,setsar=1,fade=t=in:st=0:d=${FADE}[v2]`,
    '[v0][v1][v2]concat=n=3:v=1:a=0[v]',
  ];
  const audio: string[] = [];
  if (input.audio.length) {
    input.audio.forEach((a, i) => audio.push(`[${3 + i}:a]aresample=48000,aformat=channel_layouts=mono,adelay=${Math.round(a.at)}:all=1[a${i}]`));
    audio.push(`${input.audio.map((_, i) => `[a${i}]`).join('')}amix=inputs=${input.audio.length}:normalize=0:duration=longest,` +
      `apad=whole_dur=${total.toFixed(3)},loudnorm=I=-16:TP=-1.5:LRA=11,aresample=48000,aformat=channel_layouts=stereo[a]`);
  }
  args.push('-filter_complex', [...video, ...audio].join(';'), '-map', '[v]');
  if (input.audio.length) args.push('-map', '[a]', '-c:a', 'aac', '-b:a', '160k', '-ar', '48000', '-metadata:s:a:0', 'language=eng');
  args.push(
    '-map_chapters', String(metaIndex), '-map_metadata', '-1',
    '-c:v', 'libx264', '-preset', 'slow', '-crf', '18', '-tune', 'stillimage', '-profile:v', 'high', '-level', '4.1',
    '-pix_fmt', 'yuv420p', '-color_range', 'tv', '-colorspace', 'bt709', '-color_primaries', 'bt709', '-color_trc', 'bt709',
    '-r', '30', '-g', '60', '-movflags', '+faststart',
    '-metadata', `title=${input.title}`, '-metadata', `comment=${input.comment}`, '-metadata:s:v:0', 'language=eng',
    '-t', total.toFixed(3), input.output,
  );
  ffmpeg(args);
}

/** The same picture without the narration track, for viewers who prefer captions only. */
export function withoutAudio(source: string, output: string): void {
  ffmpeg(['-i', source, '-map', '0:v', '-map_chapters', '0', '-c', 'copy', '-movflags', '+faststart', output]);
}

/** A copy with the captions drawn into the picture, for players that cannot show a caption file. */
export function burnIn(source: string, srt: string, output: string): void {
  // Run beside the files so the subtitles filter needs no escaped Windows path. libass scales SRT styles from a
  // 288-line canvas, so size 12 is about 45 pixels at 1080p.
  const style = 'FontName=Segoe UI,FontSize=12,PrimaryColour=&H00FFFFFF,BackColour=&H20000000,BorderStyle=4,Outline=0,Shadow=0,MarginV=14';
  ffmpeg(['-i', basename(source), '-vf', `subtitles=${basename(srt)}:force_style='${style}'`, '-map', '0:v', '-map', '0:a?', '-map_chapters', '0',
    '-c:v', 'libx264', '-preset', 'slow', '-crf', '18', '-tune', 'stillimage', '-pix_fmt', 'yuv420p', '-c:a', 'copy', '-movflags', '+faststart', basename(output)],
  dirname(source));
}
