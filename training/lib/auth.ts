import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import type { Browser } from '@playwright/test';
import { BASE_URL, DEMO_PASSWORD, PATHS } from './config.ts';
import { lit, sql } from './sql.ts';
import { totp } from './totp.ts';

/**
 * Signed-in sessions for the synthetic accounts a video uses, made off camera so no password or code is ever recorded.
 * Each session and its authenticator secret are kept in training/.auth (git-ignored). A saved session is reused while
 * it is valid; otherwise the account signs in with its password and a code from the saved secret, and only an account
 * with no saved secret has its authenticator reset (an UPDATE in the training database) and enrolled again.
 */
export function sessionFile(user: string): string {
  return join(PATHS.auth, `${user}.json`);
}

export async function ensureSession(browser: Browser, user: string): Promise<string> {
  mkdirSync(PATHS.auth, { recursive: true });
  const state = sessionFile(user);
  const secretFile = join(PATHS.auth, `${user}.totp`);

  if (existsSync(state)) {
    const context = await browser.newContext({ baseURL: BASE_URL, ignoreHTTPSErrors: true, storageState: state });
    const ok = (await context.request.get('/api/v1/me')).ok();
    await context.close();
    if (ok) return state;
  }

  const secret = existsSync(secretFile) ? readFileSync(secretFile, 'utf8').trim() : null;
  if (!secret) {
    sql(`UPDATE auth.appUser SET mfaEnabled = 0, mfaSecretProtected = NULL, failedSignInCount = 0, lockoutEndUtc = NULL
          WHERE userName = ${lit(user)};`);
  }

  const context = await browser.newContext({ baseURL: BASE_URL, ignoreHTTPSErrors: true });
  const page = await context.newPage();
  try {
    await page.goto('/account/signin');
    await page.locator('#UserName').fill(user);
    await page.locator('#Password').fill(DEMO_PASSWORD);
    await page.getByRole('button', { name: 'Continue', exact: true }).click();
    await page.waitForLoadState('networkidle');

    if (page.url().includes('/account/enroll')) {
      const fresh = (await page.locator('code').first().innerText()).replace(/\s/g, '');
      await page.locator('#Code').fill(totp(fresh));
      await page.getByRole('button', { name: 'Turn on and sign in' }).click();
      await page.getByText('Save your recovery codes', { exact: true }).waitFor();
      writeFileSync(secretFile, fresh);
      // The USWDS checkbox input is visually hidden behind its label, so tick it from the keyboard.
      await page.locator('#saved-codes').focus();
      await page.keyboard.press('Space');
      await page.getByRole('button', { name: 'Continue to MERIT', exact: true }).click();
    } else if (secret) {
      await page.locator('#Code').fill(totp(secret));
      await page.locator('#Code').press('Enter');
    } else {
      throw new Error(`${user} was asked for a code but has no saved authenticator secret.`);
    }
    await page.waitForURL(url => !url.pathname.startsWith('/account'), { timeout: 15_000 });
    const me = await context.request.get('/api/v1/me');
    if (!me.ok()) throw new Error(`Sign-in for ${user} did not complete (${me.status()}).`);
    await context.storageState({ path: state });
    return state;
  } catch (error) {
    // A stale secret (the authenticator was reset elsewhere): forget it so the next run enrolls again.
    if (secret && existsSync(secretFile)) writeFileSync(secretFile, '');
    throw new Error(`Could not sign in ${user} for recording: ${(error as Error).message}`);
  } finally {
    await context.close();
  }
}
