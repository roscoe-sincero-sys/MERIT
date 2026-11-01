import { execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { expect, expectAccessible, test } from './helpers';
import { totp } from '../totp';

test('lost authenticator offers recovery codes and verified administrator reset followed by re-enrollment', async ({ browser, asUser }) => {
  const database = process.env.MERIT_E2E_DATABASE ?? 'MERIT';
  test.skip(database !== 'MERIT_IT', 'Creates an isolated account only in MERIT_IT.');
  const id = randomUUID();
  const user = `lost-${id}`;
  const sql = (query: string) => execFileSync('sqlcmd', ['-S', process.env.MERIT_E2E_SERVER ?? 'localhost\\PORTAL', '-E', '-C', '-I', '-d', database, '-b', '-Q', query]);
  sql(`INSERT auth.appUser(appUserId,rootTenantId,homeTenantId,userName,displayName,passwordHash,securityStamp)
    SELECT '${id}',rootTenantId,homeTenantId,N'${user}',N'Lost authenticator browser test',passwordHash,NEWID()
    FROM auth.appUser WHERE userName='admin';`);
  const context = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await context.newPage();
  try {
    const password = async () => {
      await page.goto('/account/signin?ReturnUrl=/account/profiles');
      await page.locator('#UserName').fill(user);
      await page.locator('#Password').fill('MeritDemo!2026');
      await page.getByRole('button', { name: 'Continue', exact: true }).click();
    };
    const enroll = async () => {
      await expect(page).toHaveURL(/\/account\/enroll$/);
      const secret = (await page.locator('code').first().innerText()).replace(/\s/g, '');
      await page.locator('#Code').fill(totp(secret));
      await page.getByRole('button', { name: 'Turn on and sign in' }).click();
      await expect(page.getByText('Save your recovery codes', { exact: true })).toBeVisible();
      const recoveryCode = (await page.locator('[data-recovery-code]').first().innerText()).trim();
      await page.locator('#saved-codes').focus();
      await page.keyboard.press('Space');
      await page.getByRole('button', { name: 'Continue to MERIT', exact: true }).click();
      await expect(page).toHaveURL(/\/account\/profiles$/);
      return { secret, recoveryCode };
    };
    await password();
    const original = await enroll();
    await context.clearCookies();
    await password();
    await expect(page).toHaveURL(/\/account\/mfa$/);
    await page.getByRole('link', { name: "Can't access your authenticator?" }).click();
    await expect(page.getByRole('heading', { name: 'Sign in with a recovery code', exact: true })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'No recovery codes available?' })).toBeVisible();
    await expectAccessible(page, 'lost authenticator recovery');
    const code = page.getByRole('textbox', { name: 'Recovery code', exact: true });
    const recover = page.getByRole('button', { name: 'Use recovery code and sign in' });
    await code.fill('INVALID');
    await recover.click();
    await expect(page.getByRole('alert').filter({ hasText: 'That recovery code did not work' })).toBeVisible();
    await expect(code).toBeEmpty();
    await code.fill(original.recoveryCode);
    await recover.click();
    await expect(page).toHaveURL(/\/account\/profiles$/);
    expect((await page.request.get('/api/v1/me')).status()).toBe(200);

    await context.clearCookies();
    await password();
    await page.getByRole('link', { name: "Can't access your authenticator?" }).click();
    await code.fill(original.recoveryCode);
    await recover.click();
    await expect(page.getByRole('alert').filter({ hasText: 'That recovery code did not work' })).toBeVisible();
    expect((await page.request.get('/api/v1/me')).status()).toBe(401);

    const admin = await asUser('admin');
    await admin.goto(`/admin/users/${id}`);
    admin.once('dialog', dialog => dialog.accept());
    await admin.getByRole('button', { name: 'Reset MFA after verifying the person' }).click();
    await expect(admin.getByRole('status')).toContainText('MFA reset');
    await page.getByRole('button', { name: 'Sign in again after an MFA reset' }).click();
    await expect(page).toHaveURL(/\/account\/signin$/);
    await password();
    const replacement = await enroll();
    expect(replacement.secret).not.toBe(original.secret);
    await context.clearCookies();
    await password();
    await page.getByRole('link', { name: "Can't access your authenticator?" }).click();
    await code.fill(original.recoveryCode);
    await recover.click();
    await expect(page.getByRole('alert').filter({ hasText: 'That recovery code did not work' })).toBeVisible();
    await page.getByRole('link', { name: 'Use an authenticator code instead' }).click();
    await page.locator('#Code').fill(totp(replacement.secret));
    await page.getByRole('button', { name: 'Verify and sign in' }).click();
    await expect(page).toHaveURL(/\/account\/profiles$/);
  } finally {
    await context.close();
    sql(`DECLARE @scopes TABLE(id uniqueidentifier); INSERT @scopes SELECT sessionScopeId FROM auth.sessionScope WHERE appUserId='${id}';
      DELETE auth.sessionScopePermission WHERE sessionScopeId IN(SELECT id FROM @scopes);
      DELETE auth.sessionScopeAdminTenant WHERE sessionScopeId IN(SELECT id FROM @scopes);
      DELETE auth.sessionScopeRoot WHERE sessionScopeId IN(SELECT id FROM @scopes);
      DELETE auth.sessionScopeTenant WHERE sessionScopeId IN(SELECT id FROM @scopes);
      DELETE auth.sessionScope WHERE appUserId='${id}'; DELETE auth.mfaRecoveryCode WHERE appUserId='${id}'; DELETE auth.appUser WHERE appUserId='${id}';`);
  }
});
