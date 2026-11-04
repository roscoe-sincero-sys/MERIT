import { randomUUID } from 'node:crypto';
import type { Page } from '@playwright/test';
import { expect, expectAccessible, test } from './helpers';
import { totp } from '../totp';
import { ISOLATED_DATA_ONLY, sql } from '../database';

/**
 * Password self-service (#190) in a real browser: forgotten password by emailed link (read from Mailpit), the second
 * factor the reset still needs, a signed-in change, and the change an administrator can require. Runs with the
 * "Merit.Web (Mailpit)" profile against MERIT_IT and MERIT_E2E_MAILPIT_URL; the account is synthetic (example.test).
 */
const MAILPIT = process.env.MERIT_E2E_MAILPIT_URL;

async function mailpitText(page: Page, to: string, subject: string, after: number): Promise<string> {
  for (let i = 0; i < 60; i++) {
    const list = await (await page.request.get(`${MAILPIT}/api/v1/messages?limit=50`)).json();
    const message = (list.messages as Array<{ ID: string; Created: string; Subject: string; To: Array<{ Address: string }> }>)
      .find(m => m.Subject === subject && m.To.some(t => t.Address === to) && Date.parse(m.Created) >= after - 1000);
    if (message) return (await (await page.request.get(`${MAILPIT}/api/v1/message/${message.ID}`)).json()).Text as string;
    await page.waitForTimeout(250);
  }
  throw new Error(`No "${subject}" message for ${to}`);
}

test('password: forgotten-password reset needs the link and the second factor; signed-in and required changes', async ({ browser }) => {
  test.skip(ISOLATED_DATA_ONLY !== null, ISOLATED_DATA_ONLY ?? '');
  test.skip(!MAILPIT, 'Set MERIT_E2E_MAILPIT_URL and run the app with the "Merit.Web (Mailpit)" profile.');
  test.setTimeout(180_000);
  const id = randomUUID();
  const user = `pw-e2e-${id.slice(0, 18)}`;
  const email = `${user}@example.test`;
  const reset = 'Granite-Lantern-Orchard-42';
  const changed = 'Copper-Meadow-Harbor-58';
  const chosen = 'Silver-Willow-Canyon-71';
  sql(`INSERT auth.appUser (appUserId, rootTenantId, homeTenantId, userName, displayName, email, passwordHash, securityStamp)
    SELECT '${id}', rootTenantId, homeTenantId, N'${user}', N'Password browser test', N'${email}', passwordHash, NEWID () FROM auth.appUser WHERE userName = N'admin';`);
  const context = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await context.newPage();
  const signIn = async (password: string) => {
    await page.goto('/account/signin');
    await page.locator('#UserName').fill(user);
    await page.locator('#Password').fill(password);
    await page.getByRole('button', { name: 'Continue', exact: true }).click();
  };
  try {
    // Enroll the authenticator first: a reset needs it.
    await signIn('MeritDemo!2026');
    await expect(page).toHaveURL(/\/account\/enroll$/);
    const secret = (await page.locator('code').first().innerText()).replace(/\s/g, '');
    await page.locator('#Code').fill(totp(secret));
    await page.getByRole('button', { name: 'Turn on and sign in' }).click();
    await expect(page.getByText('Save your recovery codes', { exact: true })).toBeVisible();
    await page.locator('#saved-codes').focus();
    await page.keyboard.press('Space');
    await page.getByRole('button', { name: 'Continue to MERIT', exact: true }).click();
    await context.clearCookies();

    // Forgot your password?
    await page.goto('/account/signin');
    await page.getByRole('link', { name: 'Forgot your password?' }).click();
    await expect(page.getByRole('heading', { name: 'Reset your password' })).toBeVisible();
    await expectAccessible(page, 'forgot password');
    const askedAt = Date.now();
    await page.getByLabel('User name').fill(user);
    await page.getByRole('button', { name: 'Email me a reset link' }).click();
    await expect(page.getByRole('heading', { name: 'Check your email' })).toBeVisible();
    await expectAccessible(page, 'forgot password sent');
    const link = /(https:\/\/\S+\/account\/reset\?r=[0-9a-f]{32}&t=[A-Za-z0-9_-]{43})/.exec(await mailpitText(page, email, 'Reset your MERIT password', askedAt))![1];

    // The link lands on a clean address; a wrong code is explained; the right one resets.
    await page.goto(link);
    await expect(page).toHaveURL(/\/account\/reset$/);
    await expect(page.getByText(`For the MERIT account ${user}`)).toBeVisible();
    await expectAccessible(page, 'reset password');
    await page.getByLabel('Code from your authenticator app, or a recovery code').fill('000000');
    await page.getByRole('textbox', { name: 'New password', exact: true }).fill(reset);
    await page.getByRole('textbox', { name: 'New password again', exact: true }).fill(reset);
    await page.getByRole('button', { name: 'Reset password' }).click();
    await expect(page.locator('#reset-errors')).toContainText('That code did not match');
    await expectAccessible(page, 'reset password error');
    await page.getByLabel('Code from your authenticator app, or a recovery code').fill(totp(secret));
    await page.getByRole('textbox', { name: 'New password', exact: true }).fill(reset);
    await page.getByRole('textbox', { name: 'New password again', exact: true }).fill(reset);
    await page.getByRole('button', { name: 'Reset password' }).click();
    await expect(page.getByRole('heading', { name: 'Your password has been reset' })).toBeVisible();
    await expectAccessible(page, 'reset password done');
    await page.goto(link);
    await expect(page.getByText('This link was already used.')).toBeVisible();

    // Sign in with it, then change it on Sign-in methods.
    await signIn(reset);
    await page.locator('#Code').fill(totp(secret));
    await page.getByRole('button', { name: 'Verify and sign in' }).click();
    await page.goto('/account/security');
    await expect(page.getByRole('heading', { name: 'Password' })).toBeVisible();
    await expectAccessible(page, 'sign-in methods with password');
    await page.getByRole('textbox', { name: 'Current password', exact: true }).fill(reset);
    await page.locator('#NewPassword').fill(changed);
    await page.locator('#ConfirmPassword').fill(changed);
    await page.locator('#PasswordAuthenticatorCode').fill(totp(secret));
    await page.getByRole('button', { name: 'Change password' }).click();
    await expect(page.getByText('Your password was changed. Your other MERIT sessions were signed out.')).toBeVisible();
    expect((await page.request.get('/api/v1/me')).status()).toBe(200);

    // An administrator's "choose a new password at next sign-in".
    sql(`UPDATE auth.appUser SET passwordChangeRequired = 1 WHERE appUserId = '${id}';`);
    await context.clearCookies();
    await signIn(changed);
    await page.locator('#Code').fill(totp(secret));
    await page.getByRole('button', { name: 'Verify and sign in' }).click();
    await expect(page).toHaveURL(/\/account\/password\/required/);
    await expectAccessible(page, 'required password');
    await page.goto('/cases');
    await expect(page).toHaveURL(/\/account\/password\/required/);
    await page.getByRole('textbox', { name: 'New password', exact: true }).fill(chosen);
    await page.getByRole('textbox', { name: 'New password again', exact: true }).fill(chosen);
    await page.getByRole('button', { name: 'Save password and continue' }).click();
    await expect(page).not.toHaveURL(/\/account\/password\/required/);
    expect((await page.request.get('/api/v1/me')).status()).toBe(200);
  } finally {
    await context.close();
    sql(`DECLARE @scopes TABLE (id UNIQUEIDENTIFIER); INSERT @scopes SELECT sessionScopeId FROM auth.sessionScope WHERE appUserId = '${id}';
      DELETE auth.sessionScopePermission WHERE sessionScopeId IN (SELECT id FROM @scopes);
      DELETE auth.sessionScopeAdminTenant WHERE sessionScopeId IN (SELECT id FROM @scopes);
      DELETE auth.sessionScopeRoot WHERE sessionScopeId IN (SELECT id FROM @scopes);
      DELETE auth.sessionScopeTenant WHERE sessionScopeId IN (SELECT id FROM @scopes);
      DELETE auth.sessionScope WHERE appUserId = '${id}';
      DELETE auth.passwordReset WHERE appUserId = '${id}';
      DELETE auth.mfaEmailChallenge WHERE appUserId = '${id}';
      DELETE auth.mfaRecoveryCode WHERE appUserId = '${id}';
      DELETE auth.userProfile WHERE appUserId = '${id}';
      DELETE auth.appUser WHERE appUserId = '${id}';`);
  }
});
