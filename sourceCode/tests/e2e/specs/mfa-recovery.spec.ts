import { execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { expect, expectAccessible, test } from './helpers';
import { totp } from '../totp';

test('pending MFA offers a different-account escape', async ({ browser }) => {
  const context = await browser.newContext({ ignoreHTTPSErrors: true }); const page = await context.newPage();
  await page.goto('/account/signin');
  await page.getByLabel('User name').fill('supervisor'); await page.getByLabel('Password').fill('MeritDemo!2026');
  await page.getByRole('button', { name: 'Continue', exact: true }).click();
  await expect(page).toHaveURL(/\/account\/mfa$/);
  await page.getByRole('button', { name: 'Sign in as a different user' }).click();
  await expect(page).toHaveURL(/\/account\/signin$/);
  await expect(page.getByLabel('User name')).toBeFocused();
  await context.close();
});

test('recovery codes copy and print explicitly, require acknowledgement, and retain the destination', async ({ browser }) => {
  const id = randomUUID();
  const database = process.env.MERIT_E2E_DATABASE ?? 'MERIT_IT';
  if (database !== 'MERIT_IT') test.skip(true, 'Creates an isolated account only in MERIT_IT.');
  const sql = (query: string) => execFileSync('sqlcmd', ['-S', process.env.MERIT_E2E_SERVER ?? 'localhost\\PORTAL', '-E', '-C', '-I', '-d', database, '-b', '-Q', query]);
  sql(`INSERT auth.appUser (appUserId, rootTenantId, homeTenantId, userName, displayName, passwordHash, securityStamp)
    SELECT '${id}', rootTenantId, homeTenantId, N'e2e-${id}', N'Recovery test', passwordHash, NEWID () FROM auth.appUser WHERE userName = N'admin';`);
  const context = await browser.newContext({ ignoreHTTPSErrors: true }); const page = await context.newPage();
  try {
    await page.goto('/account/signin?ReturnUrl=' + encodeURIComponent('/account/profiles?from=recovery'));
    await page.getByLabel('User name').fill('e2e-' + id); await page.getByLabel('Password').fill('MeritDemo!2026');
    await page.getByRole('button', { name: 'Continue', exact: true }).click();
    await expect(page).toHaveURL(/\/account\/enroll$/);
    const secret = (await page.locator('code').first().innerText()).trim();
    await page.getByRole('textbox', { name: 'Code from the app', exact: true }).fill(totp(secret));
    await page.getByRole('button', { name: 'Turn on and sign in' }).click();
    const proceed = page.getByRole('button', { name: 'Continue to MERIT', exact: true });
    await expect(proceed).toBeDisabled();
    await expect(page.locator('[name=returnUrl]')).toHaveValue('/account/profiles?from=recovery');
    await page.evaluate(() => {
      Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: async (text: string) => { (window as any).copiedCodes = text; } } });
      window.print = () => { document.body.dataset.printed = 'true'; };
    });
    await page.getByRole('button', { name: 'Copy codes' }).click();
    await expect(page.getByRole('status')).toContainText('Recovery codes copied');
    expect(await page.evaluate(() => (window as any).copiedCodes.split('\n').length)).toBe(10);
    await page.getByRole('button', { name: 'Print codes' }).click();
    await expect(page.locator('body')).toHaveAttribute('data-printed', 'true');
    await expectAccessible(page, 'recovery-code saving');
    await page.locator('#saved-codes').focus(); await page.keyboard.press('Space');
    await expect(proceed).toBeEnabled(); await proceed.click();
    await expect(page).toHaveURL(/\/account\/profiles\?from=recovery$/);
  } finally {
    await context.close();
    sql(`DELETE auth.sessionScopeTenant WHERE sessionScopeId IN (SELECT sessionScopeId FROM auth.sessionScope WHERE appUserId = '${id}');
      DELETE auth.sessionScope WHERE appUserId = '${id}'; DELETE auth.mfaRecoveryCode WHERE appUserId = '${id}'; DELETE auth.appUser WHERE appUserId = '${id}';`);
  }
});
