import { LoginAccount, loginWorkflow, password, verify, NO_EMAIL } from '../../../lib/login.ts';
import { BASE_URL } from '../../../lib/config.ts';
import { sql } from '../../../lib/sql.ts';

const a = new LoginAccount('loginblocked', 'Blair Blocked');
export default loginWorkflow(a, {
  id: 'login-blocked', title: 'Cannot sign in: locks, disabled accounts and timeouts',
  summary: 'Distinguish an account lock from network throttling, and know when to restart or contact your administrator.',
  scenes: [
    { title: 'Account locked after failed attempts', steps: [
      { say: 'Repeated wrong passwords or verification codes can lock an account. Stop guessing and read the message MERIT gives you.',
        run: async s => { sql(`UPDATE auth.appUser SET failedSignInCount = 4 WHERE ${a.where};`);
          await s.type(s.page.locator('#UserName'), a.user); await s.type(s.page.locator('#Password'), 'Deliberately incorrect training passphrase');
          await s.click(s.page.getByRole('button', { name: 'Continue', exact: true })); } },
      { say: 'The account is locked for fifteen minutes. Wait until it unlocks or ask an administrator to unlock it after verifying your identity.',
        run: async s => { await s.expectText(s.page.getByRole('alert'), /locked for 15 minutes/); await s.highlight(s.page.getByRole('alert')); } },
      { say: 'Even a correct password does not bypass the lock. A password-reset link also does not bypass it.',
        run: async s => { await password(s, a); await s.expectText(s.page.getByRole('alert'), /locked for 15 minutes/); } },
      { say: 'After the wait or a confirmed unlock, start again with the correct password and authenticator.',
        run: async s => { sql(`UPDATE auth.appUser SET failedSignInCount = 0, lockoutEndUtc = NULL WHERE ${a.where};`); await password(s, a); await verify(s, a); } },
    ] },
    { title: 'Disabled accounts and expired sign-in steps', steps: [
      { say: 'A disabled or unknown account gets a general sign-in error. If the details are correct but access still fails, contact your administrator.',
        run: async s => { await s.page.context().clearCookies(); sql(`UPDATE auth.appUser SET isActive = 0 WHERE ${a.where};`);
          await s.goto('/account/signin'); await password(s, a); await s.expectText(s.page.getByRole('alert'), /user name or password is not right/);
          await s.highlight(s.page.getByRole('alert')); sql(`UPDATE auth.appUser SET isActive = 1 WHERE ${a.where};`); } },
      { say: 'A sign-in verification page can expire. If MERIT sends you back to sign in, enter your password again and use a fresh code.',
        run: async s => { await password(s, a); await s.page.context().clearCookies(); await s.goto('/account/mfa');
          await s.see(s.page.locator('#UserName')); await s.highlight(s.page.locator('main')); } },
    ] },
    { title: 'Too many attempts from the network', steps: [
      { say: 'Too many attempts from one network is a separate limit. This training instance demonstrates that message without affecting other users.',
        run: async s => { await s.page.context().clearCookies(); await s.goto(`${NO_EMAIL}/account/signin`);
          for (let i = 0; i < 2; i++) {
            await s.type(s.page.locator('#UserName'), 'loginunknown'); await s.type(s.page.locator('#Password'), 'An incorrect training passphrase');
            await s.click(s.page.getByRole('button', { name: 'Continue', exact: true }));
            if (await s.page.getByRole('heading', { name: 'Too many attempts', exact: true }).count()) break;
          }
          await s.see(s.page.getByRole('heading', { name: 'Too many attempts', exact: true })); } },
      { say: 'Wait for the number of seconds shown, then use Back to sign in. Repeated retries do not remove either a network limit or an account lock.',
        run: async s => s.highlight(s.page.locator('main')) },
      { say: 'If the problem continues, tell support which message you see and your user name. Do not send your password, setup key, recovery codes or verification codes.',
        run: async s => { await s.goto(`${BASE_URL}/account/signin`); await password(s, a); await verify(s, a); } },
    ] },
  ],
  recap: ['Recognized account locks and network throttling', 'Restarted an expired verification step', 'Identified when to contact an administrator'],
});
