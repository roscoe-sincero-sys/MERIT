import { execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import jsQR from 'jsqr';
import { PNG } from 'pngjs';
import { expect, test } from '@playwright/test';
import { totp } from '../totp';

test('rendered QR provisions the same account as manual setup and supports enrollment and later sign-in', async ({ browser }) => {
  const database = process.env.MERIT_E2E_DATABASE ?? 'MERIT';
  test.skip(database !== 'MERIT_IT', 'Creates an isolated enrollment account only in MERIT_IT.');
  const id = randomUUID();
  const user = `qr-${id}`;
  const sql = (query: string) => execFileSync('sqlcmd', ['-S', process.env.MERIT_E2E_SERVER ?? 'localhost\\PORTAL', '-E', '-C', '-I', '-d', database, '-b', '-Q', query]);
  sql(`INSERT auth.appUser (appUserId, rootTenantId, homeTenantId, userName, displayName, passwordHash, securityStamp)
    SELECT '${id}', rootTenantId, homeTenantId, N'${user}', N'QR enrollment test', passwordHash, NEWID () FROM auth.appUser WHERE userName = N'admin';`);
  const context = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await context.newPage();
  try {
    const signIn = async () => {
      await page.goto('/account/signin?ReturnUrl=/account/profiles');
      await page.locator('#UserName').fill(user);
      await page.locator('#Password').fill('MeritDemo!2026');
      await page.getByRole('button', { name: 'Continue', exact: true }).click();
    };
    await signIn();
    await expect(page).toHaveURL(/\/account\/enroll$/);
    await expect(page.getByText('Other account (Google, Facebook, etc.)', { exact: true })).toBeVisible();
    await expect(page.getByText('Enter code manually', { exact: true })).toBeVisible();
    const manualKey = (await page.locator('code').first().innerText()).replace(/\s/g, '');
    let scannedKey = '';
    // Decode screenshots, not the SVG markup: the missing viewBox originally clipped the rendered modules.
    for (const [width, zoom] of [[768, 1], [768, 1.25], [768, 2], [375, 1], [375, 2]]) {
      await page.setViewportSize({ width, height: 1024 });
      await page.evaluate(value => { document.documentElement.style.zoom = String(value); }, zoom);
      const png = PNG.sync.read(await page.locator('.merit-qr svg').screenshot());
      const decoded = jsQR(new Uint8ClampedArray(png.data), png.width, png.height);
      expect(decoded, `QR should decode at ${width}px and ${zoom * 100}% zoom`).not.toBeNull();
      const uri = new URL(decoded!.data);
      expect(uri.protocol).toBe('otpauth:');
      expect(uri.hostname).toBe('totp');
      expect(decodeURIComponent(uri.pathname)).toBe(`/MERIT:${user}`);
      expect(uri.searchParams.get('issuer')).toBe('MERIT');
      expect(uri.searchParams.get('algorithm')).toBe('SHA1');
      expect(uri.searchParams.get('digits')).toBe('6');
      expect(uri.searchParams.get('period')).toBe('30');
      scannedKey = uri.searchParams.get('secret')!;
      expect(scannedKey).toBe(manualKey);
    }
    await page.evaluate(() => { document.documentElement.style.zoom = '1'; });
    await page.setViewportSize({ width: 768, height: 1024 });
    // Rejected codes preserve the pending key so the user can retry with the app already configured.
    await page.locator('#Code').fill('123');
    await page.getByRole('button', { name: 'Turn on and sign in' }).click();
    await expect(page.getByText(/That code did not match/).first()).toBeVisible();
    expect((await page.locator('code').first().innerText()).replace(/\s/g, '')).toBe(manualKey);
    await page.locator('#Code').fill(totp(scannedKey));
    await page.getByRole('button', { name: 'Turn on and sign in' }).click();
    await expect(page.getByText('Save your recovery codes', { exact: true })).toBeVisible();
    await page.locator('#saved-codes').focus();
    await page.keyboard.press('Space');
    await page.getByRole('button', { name: 'Continue to MERIT', exact: true }).click();
    await expect(page).toHaveURL(/\/account\/profiles$/);
    expect((await page.request.get('/api/v1/me')).status()).toBe(200);
    await context.clearCookies();
    await signIn();
    await expect(page).toHaveURL(/\/account\/mfa$/);
    await page.locator('#Code').fill(totp(manualKey));
    await page.getByRole('button', { name: 'Verify and sign in' }).click();
    await expect(page).toHaveURL(/\/account\/profiles$/);
    expect((await page.request.get('/api/v1/me')).status()).toBe(200);
  } finally {
    await context.close();
    sql(`DECLARE @scopes TABLE(id uniqueidentifier); INSERT @scopes SELECT sessionScopeId FROM auth.sessionScope WHERE appUserId = '${id}';
      DELETE auth.sessionScopePermission WHERE sessionScopeId IN (SELECT id FROM @scopes);
      DELETE auth.sessionScopeAdminTenant WHERE sessionScopeId IN (SELECT id FROM @scopes);
      DELETE auth.sessionScopeRoot WHERE sessionScopeId IN (SELECT id FROM @scopes);
      DELETE auth.sessionScopeTenant WHERE sessionScopeId IN (SELECT id FROM @scopes);
      DELETE auth.sessionScope WHERE appUserId = '${id}'; DELETE auth.mfaRecoveryCode WHERE appUserId = '${id}'; DELETE auth.appUser WHERE appUserId = '${id}';`);
  }
});
