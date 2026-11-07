import { LoginAccount, loginWorkflow, password, verify, sendEmail, signedIn, signOut, mailFailure } from '../../../lib/login.ts';
import { sql } from '../../../lib/sql.ts';

const a = new LoginAccount('loginmailhelp', 'Morgan Mail');
async function waitForResend(s: import('../../../lib/stage.ts').Stage): Promise<void> {
  await s.page.waitForFunction(() => {
    const button = document.querySelector<HTMLButtonElement>('[data-resend-button]');
    return button && !button.disabled && button.getAttribute('aria-disabled') !== 'true';
  }, undefined, { timeout: 35_000 });
}
export default loginWorkflow(a, {
  id: 'login-email-help', title: 'Email sign-in: missing, old and rejected codes',
  summary: 'Recover from wrong or expired email codes, use the newest message, and switch methods when email cannot be delivered.',
  scenes: [
    { title: 'Wrong or missing email', steps: [
      { say: 'After your password, choose Email me a code. A sent message appears with the masked address and the code lifetime.',
        run: async s => { await password(s, a); await sendEmail(s, a); await s.highlight(s.page.locator('[data-email-delivery="sent"]')); } },
      { say: 'Email can take a minute or two. Check junk or spam, and check the masked address. Never send a verification code to support.',
        run: async s => s.highlight(s.page.locator('[data-resend-status]')) },
      { say: 'If you mistype a code, MERIT explains the error and shows how many attempts remain. Read the message before trying again.',
        run: async s => { const wrong = a.code.slice(0, -1) + ((Number(a.code.at(-1)) + 1) % 10);
          await s.type(s.page.locator('#Code'), wrong); await s.click(s.page.getByRole('button', { name: 'Verify and sign in', exact: true }));
          await s.expectText(s.page.locator('#email-code-errors'), /did not match/); await s.highlight(s.page.locator('#email-code-errors')); } },
    ] },
    { title: 'Expired, replaced and exhausted codes', steps: [
      { say: 'An expired code cannot sign you in. Choose Send a new code after the resend countdown; repeated clicks will not speed up delivery.',
        run: async s => { sql(`UPDATE auth.mfaEmailChallenge SET expiresUtc = DATEADD(SECOND, -1, SYSUTCDATETIME()) WHERE ${a.where} AND consumedUtc IS NULL AND supersededUtc IS NULL AND isDeleted = 0;`);
          await s.type(s.page.locator('#Code'), a.code); await s.click(s.page.getByRole('button', { name: 'Verify and sign in', exact: true }));
          await s.expectText(s.page.locator('#email-code-errors'), /expired/); } },
      { say: 'Wait until Send a new code is available, then ask for another message. Use only the code from the newest email.',
        run: async s => { await waitForResend(s); await sendEmail(s, a, 'Send a new code'); a.oldCode = a.code; } },
      { say: 'Requesting another code replaces the previous one. This example requests a newer message after the next countdown.',
        run: async s => { await waitForResend(s); await sendEmail(s, a, 'Send a new code'); } },
      { say: 'An older email no longer works after a newer code is sent. Use the newest message even if the older email arrives later.',
        run: async s => { await s.type(s.page.locator('#Code'), a.oldCode); await s.click(s.page.getByRole('button', { name: 'Verify and sign in', exact: true }));
          await s.expectText(s.page.locator('#email-code-errors'), /did not match|replaced|no longer/); await s.highlight(s.page.locator('#email-code-errors')); } },
      { say: 'Too many wrong entries can also close a code. Instead of continuing to guess, get a new code or use your authenticator.',
        run: async s => { sql(`UPDATE auth.mfaEmailChallenge SET maxAttempts = failedAttempts + 1 WHERE ${a.where} AND consumedUtc IS NULL AND supersededUtc IS NULL AND isDeleted = 0;`);
          const wrong = a.code.slice(0, -1) + ((Number(a.code.at(-1)) + 1) % 10); await s.type(s.page.locator('#Code'), wrong);
          await s.click(s.page.getByRole('button', { name: 'Verify and sign in', exact: true }));
          await s.expectText(s.page.locator('#email-code-errors'), /too many|no longer/); } },
      { say: 'Use your authenticator app instead returns to the other verification method. Enter its current code to finish signing in.',
        run: async s => { await s.click(s.page.getByRole('link', { name: 'Use your authenticator app instead', exact: true })); await verify(s, a); } },
    ] },
    { title: 'Delivery problems and fallback options', steps: [
      { say: 'If MERIT cannot send the email, it shows a delivery error and support reference. Keep that reference when contacting support, rather than any code.',
        run: async s => { await signOut(s); await password(s, a);
          // The previous sign-in challenge still counts toward the server's normal send cooldown.
          await s.page.waitForTimeout(Math.max(0, 31_000 - (Date.now() - a.sentAt)));
          await mailFailure(s.page, true);
          try { await s.click(s.page.getByRole('button', { name: 'Email me a code', exact: true }));
            await s.see(s.page.locator('[data-email-delivery="failed"]')); await s.highlight(s.page.locator('[data-email-delivery="failed"]')); }
          finally { await mailFailure(s.page, false); } } },
      { say: 'Choose Try sending again after a delivery failure. If email remains unavailable, use your authenticator or an unused recovery code.',
        run: async s => { await sendEmail(s, a, 'Try sending again'); await s.type(s.page.locator('#Code'), a.code);
          await s.click(s.page.getByRole('button', { name: 'Verify and sign in', exact: true })); await signedIn(s); } },
      { say: 'An agency can withdraw email-code access. In that case, the email option disappears; your authenticator remains the way to sign in.',
        run: async s => { sql(`UPDATE auth.appUser SET mfaEmailVerifiedUtc = NULL WHERE ${a.where};`);
          await signOut(s); await password(s, a);
          if (await s.page.getByRole('button', { name: 'Email me a code', exact: true }).count()) throw new Error('Unavailable email was still offered.');
          await s.highlight(s.page.locator('main')); await verify(s, a); } },
    ] },
  ],
  recap: ['Recognized wrong, expired and replaced codes', 'Requested a new code after the countdown', 'Recovered from delivery failures using another method'],
}, { emailOn: true });
