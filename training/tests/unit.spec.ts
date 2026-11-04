import { expect, test } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { chunk, cues, MAX_LINE, parseVtt, toSrt, toVtt, wrap } from '../lib/captions.ts';
import { loadCatalog } from '../lib/catalog.ts';
import { PATHS } from '../lib/config.ts';
import { readingSeconds, speakable, wavSeconds } from '../lib/narration.ts';
import { FEATURES, ROLES } from '../lib/workflow.ts';

test.describe('captions', () => {
  test('split at sentence ends, not at decimal points', () => {
    expect(chunk('Type the value exactly as reported: 4.2 µg/dL. Then save.')).toEqual(['Type the value exactly as reported: 4.2 µg/dL.', 'Then save.']);
  });

  test('a long sentence becomes cues of at most two lines of 42 characters', () => {
    const text = 'Below it, MERIT lists each level the result was compared with, and whether a case was opened or updated, and which tasks were created for the investigator.';
    for (const part of chunk(text)) {
      const lines = wrap(part);
      expect(lines.length).toBeLessThanOrEqual(2);
      for (const line of lines) expect(line.length).toBeLessThanOrEqual(MAX_LINE);
    }
    expect(chunk(text).join(' ')).toBe(text);
  });

  test('cues follow the speech, never overlap, and last at least a second', () => {
    const list = cues([
      { text: 'First line of speech. And a second sentence here.', start: 300, seconds: 4, until: 6000 },
      { text: 'Next step.', start: 6200, seconds: 0.6, until: 7000 },
    ]);
    expect(list[0].start).toBe(300);
    for (let i = 0; i < list.length; i++) {
      expect(list[i].end - list[i].start).toBeGreaterThanOrEqual(700);
      if (i) expect(list[i].start).toBeGreaterThanOrEqual(list[i - 1].end);
    }
  });

  test('WebVTT and SRT carry the same cues', () => {
    const list = cues([{ text: 'Choose the analyte first. The specimen and unit choices follow it.', start: 1000, seconds: 3, until: 5000 }]);
    const vtt = toVtt(list);
    expect(vtt.startsWith('WEBVTT\n\n')).toBe(true);
    expect(parseVtt(vtt)).toEqual(list);
    expect(toSrt(list)).toContain('00:00:01,000 --> ');
  });
});

test.describe('narration', () => {
  test('units and acronyms are spelled out for speech', () => {
    expect(speakable('4.2 µg/dL in MERIT')).toBe('4.2 micrograms per deciliter in Merit');
  });

  test('reading time is at least two seconds', () => {
    expect(readingSeconds('Save.')).toBe(2);
    expect(readingSeconds(Array(27).fill('word').join(' '))).toBeCloseTo(10, 5);
  });

  test('WAV length comes from the header', () => {
    const rate = 22050, seconds = 1.5, data = Buffer.alloc(Math.round(rate * seconds) * 2);
    const head = Buffer.alloc(44);
    head.write('RIFF', 0); head.writeUInt32LE(36 + data.length, 4); head.write('WAVE', 8);
    head.write('fmt ', 12); head.writeUInt32LE(16, 16); head.writeUInt16LE(1, 20); head.writeUInt16LE(1, 22);
    head.writeUInt32LE(rate, 24); head.writeUInt32LE(rate * 2, 28); head.writeUInt16LE(2, 32); head.writeUInt16LE(16, 34);
    head.write('data', 36); head.writeUInt32LE(data.length, 40);
    mkdirSync(PATHS.work, { recursive: true });
    const file = join(PATHS.work, 'unit-test.wav');
    writeFileSync(file, Buffer.concat([head, data]));
    expect(wavSeconds(file)).toBeCloseTo(seconds, 3);
  });
});

test.describe('catalog', () => {
  test('every workflow is complete and sits under its role and feature', async () => {
    const catalog = await loadCatalog();
    expect(catalog.length).toBeGreaterThan(0);
    for (const { workflow: w, file } of catalog) {
      expect(Object.keys(ROLES)).toContain(w.role);
      expect(Object.keys(FEATURES)).toContain(w.feature);
      expect(w.user, `${w.id} names its synthetic account`).toMatch(/^[a-z]+$/);
      expect(w.recap.length, `${w.id} has a recap`).toBeGreaterThan(0);
      expect(file.replace(/\\/g, '/')).toMatch(new RegExp(`/workflows/[a-z-]+/[a-z-]+/${w.id}\\.workflow\\.ts$`));
      for (const scene of w.scenes) {
        expect(scene.steps.length, `${w.id}: "${scene.title}" has steps`).toBeGreaterThan(0);
        for (const step of scene.steps) {
          expect(step.say.length, `${w.id}: a narration line under 200 characters`).toBeLessThan(200);
          expect(step.say.trim()).toMatch(/[.?!]$/);
        }
      }
    }
  });
});
