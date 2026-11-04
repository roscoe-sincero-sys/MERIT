/**
 * Captions from the timeline: each spoken line is split into cues of at most two lines of 42 characters (the common
 * broadcast and WCAG-practice limit), timed across its speech in proportion to length, never shorter than a second and
 * never overlapping. The same cues are written as WebVTT (for the web player) and SRT (for other players and editors).
 */
export interface Cue { start: number; end: number; lines: string[] }

export interface Spoken { text: string; start: number; seconds: number; until: number }

export const MAX_LINE = 42;
const MIN_CUE = 1000;

/** Splits text into caption-sized chunks (at most two lines each), preferring sentence, then clause, breaks. */
export function chunk(text: string): string[] {
  // A sentence ends at . ! or ? followed by a space, so 4.2 and 1.5x stay whole.
  const sentences = text.replace(/\s+/g, ' ').trim().split(/(?<=[.!?]["”’)]*)\s+/).filter(Boolean);
  const out: string[] = [];
  for (const sentence of sentences) {
    let rest = sentence;
    while (!fits(rest)) {
      // The longest start that still wraps into two lines, ending at a clause break when there is one.
      const words = rest.split(' ');
      let cut = 1;
      for (let i = 1; i < words.length && fits(words.slice(0, i).join(' ')); i++) cut = i;
      for (let i = cut; i > Math.ceil(cut * 0.6); i--) {
        if (/[,;:]$/.test(words[i - 1])) { cut = i; break; }
      }
      out.push(words.slice(0, cut).join(' '));
      rest = words.slice(cut).join(' ');
    }
    if (rest) out.push(rest);
  }
  return out;
}

/** True when the text wraps into at most two lines of MAX_LINE characters. */
function fits(text: string): boolean {
  return wrap(text).every(line => line.length <= MAX_LINE);
}

/** Wraps a chunk into one or two balanced lines. */
export function wrap(text: string): string[] {
  if (text.length <= MAX_LINE) return [text];
  const words = text.split(' ');
  let best: string[] = [text], bestScore = Infinity;
  for (let i = 1; i < words.length; i++) {
    const a = words.slice(0, i).join(' '), b = words.slice(i).join(' ');
    if (a.length > MAX_LINE || b.length > MAX_LINE) continue;
    const score = Math.abs(a.length - b.length);
    if (score < bestScore) { best = [a, b]; bestScore = score; }
  }
  return best;
}

export function cues(spoken: Spoken[]): Cue[] {
  const result: Cue[] = [];
  for (const line of spoken) {
    const parts = chunk(line.text);
    const total = parts.reduce((n, p) => n + p.length, 0) || 1;
    const span = Math.max(line.seconds * 1000, parts.length * MIN_CUE);
    let at = line.start;
    parts.forEach((part, i) => {
      const length = Math.max(MIN_CUE, (span * part.length) / total);
      const last = i === parts.length - 1;
      // The last cue stays up a little after the speech, until the step ends.
      const end = last ? Math.min(Math.max(at + length + 400, at + MIN_CUE), Math.max(line.until, at + MIN_CUE)) : at + length;
      result.push({ start: Math.round(at), end: Math.round(end), lines: wrap(part) });
      at = end;
    });
  }
  for (let i = 1; i < result.length; i++) {
    if (result[i].start < result[i - 1].end) result[i - 1].end = result[i].start;
  }
  return result;
}

function stamp(ms: number, separator: '.' | ','): string {
  const h = Math.floor(ms / 3_600_000), m = Math.floor(ms / 60_000) % 60, s = Math.floor(ms / 1000) % 60, f = Math.round(ms % 1000);
  return `${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}${separator}${String(f).padStart(3, '0')}`;
}

export function toVtt(list: Cue[]): string {
  return 'WEBVTT\n\n' + list.map((c, i) => `${i + 1}\n${stamp(c.start, '.')} --> ${stamp(c.end, '.')}\n${c.lines.join('\n')}\n`).join('\n');
}

export function toSrt(list: Cue[]): string {
  return list.map((c, i) => `${i + 1}\n${stamp(c.start, ',')} --> ${stamp(c.end, ',')}\n${c.lines.join('\n')}\n`).join('\n');
}

/** Reads cues back from WebVTT, for validation. */
export function parseVtt(text: string): Cue[] {
  const toMs = (t: string) => { const [h, m, s] = t.split(':'); return (Number(h) * 3600 + Number(m) * 60 + Number(s)) * 1000; };
  return text.replace(/\r/g, '').split(/\n\n+/).flatMap(block => {
    const lines = block.split('\n');
    const timing = lines.findIndex(l => l.includes('-->'));
    if (timing < 0) return [];
    const [a, b] = lines[timing].split('-->').map(s => s.trim());
    return [{ start: Math.round(toMs(a)), end: Math.round(toMs(b)), lines: lines.slice(timing + 1).filter(Boolean) }];
  });
}
