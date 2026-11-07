import { LoginAccount, loginWorkflow, requestReset, newPassword, NO_EMAIL, NEW_PASSWORD, password, signedIn } from '../../../lib/login.ts';
import { BASE_URL } from '../../../lib/config.ts';
import { sql } from '../../../lib/sql.ts';
import { totp } from '../../../lib/totp.ts';

const a = new LoginAccount('loginresethelp', 'Reese Reset');
const resetRows = () => `${a.where} AND isDeleted = 0 AND consumedUtc IS NULL`;
export default loginWorkflow(a, {
  id: 'login-reset-help', title: 'Password reset: links and support options',
  summary: 'Handle expired, replaced or closed links, locked accounts, unavailable email, and a missing authenticator.',
  scenes: [
    { title: 'Use a valid, current link', steps: [
      { say: 'Reset links are private and short lived. This example shows the messages you may see when a link cannot be used.',
        run: async s => { await requestReset(s, a); await s.goto(a.link); await s.see(s.page.locator('#NewPassword')); } },
      { say: 'If a link has expired, choose Ask for a new link. Do not keep retrying the expired email.',
        run: async s => { sql(`UPDATE auth.passwordReset SET expiresUtc = DATEADD(SECOND, -1, SYSUTCDATETIME()) WHERE ${resetRows()};`);
          await s.goto('/account/reset'); await s.expectText(s.page.getByRole('alert'), /expired/); await s.highlight(s.page.getByRole('alert')); } },
      { say: 'A newer reset request replaces the previous link. Use the newest email, even if older messages arrive later.',
        run: async s => { a.oldLink = a.link; await requestReset(s, a); await s.goto(a.oldLink);
          await s.expectText(s.page.getByRole('alert'), /newer link/); } },
      { say: 'An invalid or incomplete link also cannot be used. Open the complete link from the newest email, or request another.',
        run: async s => { await s.goto('/account/reset?r=invalid&t=invalid'); await s.expectText(s.page.getByRole('alert'), /isn.t valid/); } },
    ] },
    { title: 'Account and verification problems', steps: [
      { say: 'A locked account must wait or be unlocked by an administrator. A password-reset link does not bypass an account lock.',
        run: async s => { sql(`UPDATE auth.appUser SET lockoutEndUtc = DATEADD(MINUTE, 15, SYSUTCDATETIME()) WHERE ${a.where};`);
          await s.goto(a.link); await s.expectText(s.page.getByRole('alert'), /account is locked/i); } },
      { say: 'An account that has never enrolled an authenticator cannot reset its password by email. Ask the administrator to verify your identity and help.',
        run: async s => { sql(`UPDATE auth.appUser SET lockoutEndUtc = NULL, mfaEnabled = 0 WHERE ${a.where};`);
          await s.goto(a.link); await s.expectText(s.page.getByRole('alert'), /hasn.t set up an authenticator/); } },
      { say: 'Too many wrong verification codes close the link. Ask for a new link or contact your administrator instead of continuing to guess.',
        run: async s => { sql(`UPDATE auth.appUser SET mfaEnabled = 1 WHERE ${a.where};
          UPDATE auth.passwordReset SET failedAttempts = maxAttempts - 1 WHERE ${resetRows()} AND supersededUtc IS NULL;`);
          await s.goto(a.link); const valid = totp(a.secret);
          await s.type(s.page.locator('#Code'), valid.slice(0, -1) + ((Number(valid.at(-1)) + 1) % 10)); await newPassword(s);
          await s.click(s.page.getByRole('button', { name: 'Reset password', exact: true }));
          await s.see(s.page.getByRole('heading', { name: 'This link no longer works', exact: true })); } },
      { say: 'If the app is missing but you have an unused recovery code, a fresh reset link can use it as the second factor. The code is then used up.',
        run: async s => { await requestReset(s, a); await s.goto(a.link); await s.type(s.page.locator('#Code'), a.recovery[0]); await newPassword(s);
          await s.click(s.page.getByRole('button', { name: 'Reset password', exact: true }));
          await s.see(s.page.getByRole('heading', { name: 'Your password has been reset', exact: true })); a.password = NEW_PASSWORD; } },
    ] },
    { title: 'When email or both factors are unavailable', steps: [
      { say: 'Some MERIT installations cannot email reset links. Their reset page tells you to contact an administrator after confirming who you are.',
        run: async s => { await s.page.context().clearCookies(); await s.goto(`${NO_EMAIL}/account/forgot`);
          await s.expectText(s.page.locator('main'), /can.t email reset links/); await s.highlight(s.page.locator('main')); } },
      { say: 'If you have lost both the password and the authenticator, and have no recovery codes, contact your administrator to verify your identity and reset both.' },
      { say: 'Email never arrives? Check the user name and junk folder, then use your agency support channel. The Check your email page does not prove that an account exists.' },
      { say: 'After a successful reset, return to the normal sign-in process. Your authenticator still works unless an administrator has reset MFA.',
        run: async s => { await s.goto(`${BASE_URL}/account/signin`); await password(s, a);
          await s.type(s.page.locator('#Code'), totp(a.secret)); await s.click(s.page.getByRole('button', { name: 'Verify and sign in', exact: true })); await signedIn(s); } },
    ] },
  ],
  recap: ['Recognized unusable links and account restrictions', 'Reset a password using an unused recovery code', 'Identified when administrator assistance is required'],
});
