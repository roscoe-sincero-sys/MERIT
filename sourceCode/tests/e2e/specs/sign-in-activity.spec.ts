import { readFileSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import { expect, expectAccessible, test } from './helpers';
import { totp } from '../totp';
import { sql } from '../database';

/**
 * Sign-in event log (#191) in a browser: an account's sign-in history, the suspicious-activity report, and the platform-level
 * investigation of unknown user names (hashed, revealed only with a current authenticator code). Uses synthetic names only.
 */
test('sign-in history, activity report and unknown user names are usable and accessible', async ({ browser, asUser }) => {
  test.setTimeout(120_000);
  const suspect = `MeritAdmin-e2e-${randomUUID().slice(0, 8)}`;

  // An attempt at a name that matches no account.
  const visitor = await browser.newContext({ ignoreHTTPSErrors: true });
  const attempt = await visitor.newPage();
  await attempt.goto('/account/signin');
  await attempt.locator('#UserName').fill(suspect);
  await attempt.locator('#Password').fill('Not-a-real-password-1');
  await attempt.getByRole('button', { name: 'Continue', exact: true }).click();
  await expect(attempt.getByText('The user name or password is not right.')).toBeVisible();
  await visitor.close();

  // A tenant administrator: the report, and an account's history.
  const admin = await asUser('admin');
  await admin.goto('/');
  await admin.getByRole('button', { name: 'Menu', exact: true }).click();   // tablet width: the header folds behind Menu
  await admin.locator('summary').filter({ hasText: /^Administration/ }).click();
  await admin.getByRole('link', { name: 'Sign-in activity' }).click();
  await expect(admin.getByRole('heading', { name: 'Sign-in activity', level: 1 })).toBeVisible();
  await expect(admin.getByRole('heading', { name: 'One address trying many accounts' })).toBeVisible();
  await expect(admin.getByRole('link', { name: 'Unknown user names' })).toHaveCount(0);
  await expectAccessible(admin, 'sign-in activity (administrator)');
  const cmbalt = sql("SET NOCOUNT ON; SELECT appUserId FROM auth.appUser WHERE userName = N'cmbalt'").toString().trim().split(/\s+/).pop();
  await admin.goto(`/admin/users/${cmbalt}`);
  await expect(admin.getByRole('heading', { name: 'Sign-in history' })).toBeVisible();
  await expect(admin.getByRole('table', { name: 'Sign-in history, newest first' })).toBeVisible();
  await expectAccessible(admin, 'user page with sign-in history');

  // The platform level: hashed names, a lookup, and a reveal that needs the authenticator.
  const platform = await asUser('platform');
  await platform.goto('/admin/sign-in-activity');
  await platform.getByRole('link', { name: 'Unknown user names' }).click();
  await expect(platform.getByRole('heading', { name: 'Unknown user names', level: 1 })).toBeVisible();
  await expect(platform.getByText(suspect)).toHaveCount(0);
  await expectAccessible(platform, 'unknown user names (hashed)');

  await platform.getByLabel('User name someone may have tried').fill(suspect);
  await platform.getByRole('button', { name: 'Look up' }).click();
  await expect(platform.getByRole('status').filter({ hasText: /attempts? in the last 90 days/ })).toBeVisible();
  await expectAccessible(platform, 'unknown user names (lookup)');

  const secret = readFileSync('.auth/platform.totp', 'utf8').replace(/\s/g, '');
  await platform.getByLabel('Six-digit code from your authenticator app').fill(totp(secret));
  await platform.getByRole('button', { name: 'Show names' }).click();
  await expect(platform.getByRole('heading', { name: 'Names (shown for this page only)' })).toBeVisible();
  await expect(platform.locator('[data-unknown-name]').filter({ hasText: suspect })).toBeVisible();
  await expectAccessible(platform, 'unknown user names (revealed)');
});
