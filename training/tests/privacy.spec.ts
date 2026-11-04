import { expect, test } from '@playwright/test';
import { spawnSync } from 'node:child_process';
import { ROOT } from '../lib/config.ts';
import { scanPage } from '../lib/privacy.ts';

test.describe('on-screen privacy scan', () => {
  test('a synthetic MERIT page is clean', async ({ page }) => {
    await page.setContent(`<main><h1>Leo Rivera</h1><p>Born Feb 2, 2021 · intake@merit.example</p>
      <p>Accession TRN-26-0481 · Case PB-2026-000004 · CLIA 21D0712345</p><input type="password" value="hidden-by-type"></main>`);
    expect(await scanPage(page)).toEqual([]);
  });

  for (const [name, html, rule] of [
    ['a one-time code', '<p>Your verification code is 482913</p>', 'one-time code beside a code label'],
    ['an authenticator secret', '<code>JBSW Y3DP EHPK 3PXP JBSW Y3DP EHPK 3PXP</code>', 'authenticator secret (base32)'],
    ['the demo password', '<input value="MeritDemo!2026">', 'demo password'],
    ['a real e-mail address', '<p>jane.doe@gmail.com</p>', 'e-mail address outside the example domains'],
    ['a key', '<p>api_ABCDEFGHIJKLMNOPQRSTUVWX</p>', 'API key or token'],
    ['a social security number', '<p>SSN 123-45-6789</p>', 'US social security number'],
  ] as const) {
    test(`finds ${name}`, async ({ page }) => {
      await page.setContent(`<main>${html}</main>`);
      const findings = await scanPage(page);
      expect(findings.map(f => f.rule)).toContain(rule);
      // The report never repeats the sensitive value.
      for (const f of findings) expect(f.sample).not.toContain('482913');
    });
  }
});

test.describe('training-database guard', () => {
  test('refuses any database that is not a training database', () => {
    const result = spawnSync(process.execPath, ['-e', `import('./lib/privacy.ts').then(m => m.assertTrainingTarget())`], {
      cwd: ROOT, encoding: 'utf8', env: { ...process.env, MERIT_TRAINING_DATABASE: 'MERIT' },
    });
    expect(result.status).not.toBe(0);
    expect(result.stderr).toContain('Refusing to record against MERIT');
  });
});
