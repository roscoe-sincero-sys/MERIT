import { randomUUID } from 'node:crypto';
import type { Page } from '@playwright/test';
import { expect, expectAccessible, test } from './helpers';
import { totp } from '../totp';
import { ISOLATED_DATA_ONLY, sql } from '../database';

/**
 * Email one-time codes in a real browser with real mail: the app runs with the "Merit.Web (Mailpit)" launch profile
 * against MERIT_IT, and Mailpit (tools/mail/Start-MailTool.ps1 -Tool Mailpit) captures every message locally.
 *   MERIT_E2E_MAILPIT_URL  Mailpit's web address (http://127.0.0.1:8025); the spec skips without it.
 * The account is synthetic (example.test), and the jurisdiction setting is put back to false afterwards.
 */
const MAILPIT = process.env.MERIT_E2E_MAILPIT_URL;
const SMTP4DEV = process.env.MERIT_E2E_SMTP4DEV_URL;

/** The code in the newest message to the address since the send, from whichever capture tool the app is pointed at. */
async function mailpitCode(page: Page, to: string, after: number): Promise<string> {
  for (let i = 0; i < 40; i++) {
    let text: string | undefined;
    if (MAILPIT) {
        const list = await (await page.request.get(`${MAILPIT}/api/v1/messages?limit=50`)).json();
        const message = (list.messages as Array<{ ID: string; Created: string; To: Array<{ Address: string }> }>)
          .find(m => m.To.some(t => t.Address === to) && Date.parse(m.Created) >= after - 1000);
        if (message) text = (await (await page.request.get(`${MAILPIT}/api/v1/message/${message.ID}`)).json()).Text as string;
      } else {
        const list = await (await page.request.get(`${SMTP4DEV}/api/messages?pageSize=50`)).json();
        const message = (list.results as Array<{ id: string; receivedDate: string; to: string[] }>)
          .find(m => m.to.includes(to) && Date.parse(m.receivedDate) >= after - 1000);
        if (message) text = await (await page.request.get(`${SMTP4DEV}/api/messages/${message.id}/plaintext`)).text();
      }

      const code = text ? /verification code is (\d{6})/.exec(text)?.[1] : undefined;
      if (code) return code;
      await page.waitForTimeout(250);
    }
    throw new Error(`No captured code for ${to}`);
  }

  async function chaos(page: Page, recipient: [number, number]): Promise<void> {
    const response = await page.request.put(`${MAILPIT}/api/v1/chaos`, {
      data: { Sender: { ErrorCode: 451, Probability: 0 }, Recipient: { ErrorCode: recipient[0], Probability: recipient[1] }, Authentication: { ErrorCode: 535, Probability: 0 } },
    });
    expect(response.ok()).toBe(true);
  }

  test('email codes: turned on with the authenticator, used at sign-in, failures explained, slow sends recovered', async ({ browser }) => {
    test.skip(ISOLATED_DATA_ONLY !== null, ISOLATED_DATA_ONLY ?? '');
    test.skip(!MAILPIT && !SMTP4DEV, 'Set MERIT_E2E_MAILPIT_URL (or MERIT_E2E_SMTP4DEV_URL) and run the app with the matching launch profile.');
    test.setTimeout(180_000);
    const id = randomUUID();
    const user = `otp-e2e-${id.slice(0, 18)}`;
    const email = `${user}@example.test`;
    const policy = `UPDATE config.jurisdictionSetting SET settingValue = N'%V' WHERE settingKey = 'emailCodesEnabled' AND isDeleted = 0 AND effectiveTo IS NULL
      AND rootTenantId = (SELECT rootTenantId FROM auth.appUser WHERE userName = N'admin');`;
    sql(`INSERT auth.appUser (appUserId, rootTenantId, homeTenantId, userName, displayName, email, passwordHash, securityStamp)
      SELECT '${id}', rootTenantId, homeTenantId, N'${user}', N'Email code browser test', N'${email}', passwordHash, NEWID () FROM auth.appUser WHERE userName = N'admin';`
      + policy.replace('%V', 'true'));
    const context = await browser.newContext({ ignoreHTTPSErrors: true });
    const page = await context.newPage();
    try {
      const password = async () => {
        await page.goto('/account/signin?ReturnUrl=/account/security');
        await page.locator('#UserName').fill(user);
        await page.locator('#Password').fill('MeritDemo!2026');
        await page.getByRole('button', { name: 'Continue', exact: true }).click();
      };

      // First sign-in: the authenticator is required before anything else.
      await password();
      await expect(page).toHaveURL(/\/account\/enroll$/);
      const secret = (await page.locator('code').first().innerText()).replace(/\s/g, '');
      await page.locator('#Code').fill(totp(secret));
      await page.getByRole('button', { name: 'Turn on and sign in' }).click();
      await expect(page.getByText('Save your recovery codes', { exact: true })).toBeVisible();
      await page.locator('#saved-codes').focus();
      await page.keyboard.press('Space');
      await page.getByRole('button', { name: 'Continue to MERIT', exact: true }).click();
      await expect(page).toHaveURL(/\/account\/security$/);

      // Turning email codes on: NIST's position first, then the authenticator, then a code to the address.
      await expect(page.getByText('NIST SP 800-63B-4 does not accept email')).toBeVisible();
      await expectAccessible(page, 'sign-in methods');
      await page.getByLabel('Six-digit code from your authenticator app').fill(totp(secret));
      let sentAt = Date.now();
      await page.getByRole('button', { name: /^Send a code to o\*\*\*@example\.test$/ }).click();
      await expect(page.getByRole('heading', { name: 'Confirm your email address' })).toBeVisible();
      await expect(page.getByRole('status').filter({ hasText: 'We sent a six-digit code to o***@example.test' })).toBeVisible();
      await expectAccessible(page, 'confirm email address');
      await page.getByLabel('Six-digit code from the email').fill(await mailpitCode(page, email, sentAt));
      await page.getByRole('button', { name: 'Confirm and turn on email codes' }).click();
      await expect(page.getByText('Email codes are on.')).toBeVisible();

      // Sign-in with an email code; a wrong one is explained and focus moves to the summary.
      await context.clearCookies();
      await password();
      await expect(page).toHaveURL(/\/account\/mfa$/);
      await expect(page.getByRole('heading', { name: 'Or get a code by email' })).toBeVisible();
      await expectAccessible(page, 'mfa with email option');
      sentAt = Date.now();
      await page.getByRole('button', { name: 'Email me a code' }).click();
      await expect(page.getByRole('heading', { name: 'Enter the code from your email' })).toBeVisible();
      const resend = page.getByRole('button', { name: 'Send a new code' });
      await expect(resend).toHaveAttribute('aria-disabled', 'true');
      await expect(page.locator('[data-resend-status]')).toContainText('You can ask for a new code in');
      await expectAccessible(page, 'email code entry');
      const code = await mailpitCode(page, email, sentAt);
      await page.getByLabel('Six-digit code from the email').fill(code === '000000' ? '111111' : '000000');
      await page.getByRole('button', { name: 'Verify and sign in' }).click();
      await expect(page.locator('#email-code-errors')).toBeFocused();
      await expect(page.locator('#email-code-errors')).toContainText('That code did not match');
      await expectAccessible(page, 'email code error');
      await page.getByLabel('Six-digit code from the email').fill(code);
      await page.getByRole('button', { name: 'Verify and sign in' }).click();
      await expect(page).toHaveURL(/\/account\/security$/);
      expect((await page.request.get('/api/v1/me')).status()).toBe(200);

      // The mail server refuses the message (Mailpit Chaos): the person gets the plain message and a support reference.
      // smtp4dev has no fault injection, so with it this part is covered by the SMTP fault tests instead.
      if (MAILPIT) {
      await chaos(page, [550, 100]);
      try {
        await context.clearCookies();
        await password();
        await page.getByRole('button', { name: 'Email me a code' }).click();
        const alert = page.getByRole('alert').filter({ hasText: "We couldn't send your verification code. Please try again. If the problem continues, contact support." });
        await expect(alert).toBeVisible();
        await expect(alert.locator('[data-support-reference]')).toHaveText(/^EM-[A-Z2-9]{8}$/);
        await expect(page.getByRole('button', { name: 'Try sending again' })).toBeEnabled();
        await expectAccessible(page, 'email code not sent');
      } finally {
        await chaos(page, [451, 0]);
      }

      // It recovers at once: no cooldown after a failed send.
      sentAt = Date.now();
      await page.getByRole('button', { name: 'Try sending again' }).click();
      await expect(page.getByRole('status').filter({ hasText: 'We sent a six-digit code' })).toBeVisible();
      await mailpitCode(page, email, sentAt);
    }

    // A send that takes too long: the page says so and gives the controls back instead of hanging.
    // The answer never arriving is simulated by cancelling the navigation after merit.js has handled the submit (a
    // window listener runs after the document's); the server's own bound is SmtpFaultInjectionTests' timeout case.
    await context.clearCookies();
    await password();
    await expect(page).toHaveURL(/\/account\/mfa$/);
    await page.locator('form[action="/account/mfa/email/send"]').evaluate(form => {
      (form as HTMLFormElement).dataset.meritTimeout = '1500';
      window.addEventListener('submit', evt => evt.preventDefault());
    });
    const send = page.getByRole('button', { name: 'Email me a code' });
    await send.click();
    await expect(page.getByRole('status').filter({ hasText: 'Sending your code' })).toBeVisible();
    await expect(send).toBeDisabled();
    // A second press while it works is ignored rather than sending another code.
    await expect(page.locator('form[action="/account/mfa/email/send"]')).toHaveAttribute('aria-busy', 'true');
    await expect(page.getByRole('status').filter({ hasText: 'This is taking longer than usual' })).toBeVisible({ timeout: 10_000 });
    await expect(send).toBeEnabled();
    await expectAccessible(page, 'slow email code send');
  } finally {
    await context.close();
    sql(policy.replace('%V', 'false') + `
      DECLARE @scopes TABLE (id UNIQUEIDENTIFIER); INSERT @scopes SELECT sessionScopeId FROM auth.sessionScope WHERE appUserId = '${id}';
      DELETE auth.sessionScopePermission WHERE sessionScopeId IN (SELECT id FROM @scopes);
      DELETE auth.sessionScopeAdminTenant WHERE sessionScopeId IN (SELECT id FROM @scopes);
      DELETE auth.sessionScopeRoot WHERE sessionScopeId IN (SELECT id FROM @scopes);
      DELETE auth.sessionScopeTenant WHERE sessionScopeId IN (SELECT id FROM @scopes);
      DELETE auth.sessionScope WHERE appUserId = '${id}';
      DELETE auth.mfaEmailChallenge WHERE appUserId = '${id}';
      DELETE auth.mfaRecoveryCode WHERE appUserId = '${id}';
      DELETE auth.userProfile WHERE appUserId = '${id}';
      DELETE auth.appUser WHERE appUserId = '${id}';`);
  }
});
