import { chromium, expect, FullConfig } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { totp } from './totp';

export const USERS = ['supervisor', 'cmbalt', 'admin'] as const;
export const PASSWORD = 'MeritDemo!2026';

/**
 * Resets MFA for the e2e users in the demo database (deployment identity, Windows authentication), then signs each in
 * with the keyboard only and enrolls an authenticator code, saving the session for the specs.
 */
export default async function globalSetup(config: FullConfig): Promise<void> {
  const server = process.env.MERIT_E2E_SERVER ?? 'localhost\\PORTAL';
  const database = process.env.MERIT_E2E_DATABASE ?? 'MERIT';
  execFileSync('sqlcmd', ['-S', server, '-E', '-C', '-I', '-d', database, '-b', '-Q',
    `UPDATE auth.appUser SET mfaEnabled = 0, mfaSecretProtected = NULL, failedSignInCount = 0, lockoutEndUtc = NULL WHERE userName IN (${USERS.map(u => `N'${u}'`).join(', ')});` +
    `DELETE c FROM auth.mfaRecoveryCode AS c JOIN auth.appUser AS u ON u.appUserId = c.appUserId WHERE u.userName IN (${USERS.map(u => `N'${u}'`).join(', ')});`]);

  const use = config.projects[0].use;
  mkdirSync('.auth', { recursive: true });
  const browser = await chromium.launch({ channel: use.channel });
  for (const user of USERS) {
    const context = await browser.newContext({ baseURL: use.baseURL, ignoreHTTPSErrors: true, viewport: { width: 768, height: 1024 } });
    const page = await context.newPage();
    await page.goto('/account/signin');
    await page.getByLabel('User name').focus();
    await page.keyboard.type(user);
    await page.getByLabel('Password').focus();
    await page.keyboard.type(PASSWORD);
    await page.keyboard.press('Enter');
    await page.waitForURL(/\/account\/enroll/);
    const secret = (await page.locator('code').first().innerText()).trim();
    await page.getByRole('textbox', { name: 'Code from the app', exact: true }).focus();
    await page.keyboard.type(totp(secret));
    await page.keyboard.press('Enter');
    // Enrollment answers with the one-time recovery codes; continue past them as a person would.
    await page.getByText('Save your recovery codes').waitFor();
    await page.waitForLoadState('networkidle');
    await page.locator('#saved-codes').focus();
    await page.keyboard.press('Space');
    const proceed = page.getByRole('link', { name: 'Continue to MERIT' }).or(page.getByRole('button', { name: 'Continue to MERIT' }));
    await expect(proceed).toBeEnabled();
    await proceed.focus();
    const continuation = page.waitForResponse(r => r.request().method() === 'POST' && r.url().includes('/account/recovery/continue'));
    await page.keyboard.press('Enter');
    const continued = await continuation;
    if (continued.status() >= 400) throw new Error(`Recovery continuation failed (${continued.status()}) at ${new URL(page.url()).pathname}`);
    await page.waitForURL(url => !url.pathname.startsWith('/account'));
    const me = await page.request.get('/api/v1/me');
    if (!me.ok()) throw new Error(`Enrollment for ${user} did not complete (${me.status()}).`);
    await context.storageState({ path: `.auth/${user}.json` });
    await context.close();
  }
  await browser.close();
}
