import { expect, test } from '@playwright/test';
import { existsSync, readFileSync } from 'node:fs';
import { loadCatalog, outputBase } from '../lib/catalog.ts';
import { validate } from '../lib/validate.ts';

/** Every built video passes its checks and matches the current workflow script and tooling. */
const catalog = await loadCatalog();

for (const { workflow, fingerprint } of catalog) {
  const base = outputBase(workflow);
  test.describe(`${workflow.id} output`, () => {
    test.skip(!existsSync(`${base}.timeline.json`), `not built yet: npm run videos -- --id ${workflow.id}`);

    test('passes the format, caption, transcript and privacy checks', () => {
      expect(validate(base)).toEqual([]);
    });

    test('is current with its workflow script', () => {
      const timeline = JSON.parse(readFileSync(`${base}.timeline.json`, 'utf8'));
      expect(timeline.fingerprint, `out of date: npm run videos -- --id ${workflow.id}`).toBe(fingerprint);
    });
  });
}
