import { expect, test } from '@playwright/test';
import { spawnSync } from 'node:child_process';
import { ROOT, LOGIN_DEMO_PASSWORD } from '../lib/config.ts';
import { scanPage } from '../lib/privacy.ts';
import { redactLoginHtml } from '../lib/login.ts';

test('login documents remove setup and recovery secrets before rendering and conceal all verification fields', async ({ page }) => {
  const html = `<main><div class="merit-qr" role="img"><svg><path d="private-qr-payload" /></svg></div>
    <dl><dd class="merit-setup-key"><code>JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP</code></dd></dl>
    <ul data-recovery-codes><li data-recovery-code>ABCDE-FGHIJ</li></ul>
    <label for="Code">Verification code</label><input id="Code" type="text" autocomplete="one-time-code" value="482913">
    <label for="OtherCode">Verification code</label><input id="OtherCode" autocomplete="one-time-code" value="482913"></main>`;
  const redacted = redactLoginHtml(html);
  for (const value of ['private-qr-payload', 'JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP', 'ABCDE-FGHIJ']) expect(redacted).not.toContain(value);
  await page.setContent(redacted);
  expect(await page.locator('svg').count()).toBe(0);
  expect(await page.locator('#Code').getAttribute('type')).toBe('password');
  expect(await page.locator('#OtherCode').getAttribute('type')).toBe('password');
  expect(await scanPage(page)).toEqual([]);
  expect(await page.locator('main').innerText()).toContain('Recovery code hidden for training');
});

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
    ['the login training password', `<input value="${LOGIN_DEMO_PASSWORD}">`, 'training login password'],
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
