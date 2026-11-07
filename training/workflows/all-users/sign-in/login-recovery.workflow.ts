import { LoginAccount, loginWorkflow, password, signedIn, signOut } from '../../../lib/login.ts';
import { sql } from '../../../lib/sql.ts';
import { totp } from '../../../lib/totp.ts';

const a = new LoginAccount('loginrecovery', 'River Recovery');
export default loginWorkflow(a, {
  id: 'login-recovery', title: 'Lost authenticator: recovery and MFA reset',
  summary: 'Use a saved recovery code, understand one-use codes, and enroll again after an administrator resets MFA.',
  scenes: [
    { title: 'Use an unused recovery code', steps: [
      { say: 'If you lose the MERIT entry in your authenticator, start with your user name and password as usual.', run: async s => password(s, a) },
      { say: 'Choose Can\'t access your authenticator? to see recovery options. An enabled email-code option may also be available on the previous screen.',
        run: async s => s.click(s.page.getByRole('link', { name: "Can't access your authenticator?", exact: true })) },
      { say: 'Enter an unused recovery code saved during setup. It has ten characters; the hyphen is optional. Never share a recovery code.',
        run: async s => s.highlight(s.page.locator('form[action="/account/mfa"]')) },
      { say: 'Choose Use recovery code and sign in. A successful recovery code signs you in once; it does not repair or replace your authenticator.',
        run: async s => { await s.type(s.page.locator('#Code'), a.recovery[0]); await s.click(s.page.getByRole('button', { name: 'Use recovery code and sign in', exact: true })); await signedIn(s); } },
      { say: 'A used code cannot work a second time. If it is rejected, use another unused code or ask for an MFA reset.',
        run: async s => { await signOut(s); await password(s, a);
          await s.click(s.page.getByRole('link', { name: "Can't access your authenticator?", exact: true }));
          await s.type(s.page.locator('#Code'), a.recovery[0]); await s.click(s.page.getByRole('button', { name: 'Use recovery code and sign in', exact: true }));
          await s.expectText(s.page.getByRole('alert'), /That recovery code did not work/); } },
    ] },
    { title: 'No authenticator and no recovery codes', steps: [
      { say: 'Contact your agency\'s MERIT administrator or help desk through your usual support channel. Give your user name and explain what you lost.',
        run: async s => s.highlight(s.page.locator('section[aria-labelledby="mfa-reset-heading"]')) },
      { say: 'Ask them to verify your identity and reset MFA. Administrators need another administrator; platform administrators need another platform administrator.' },
      { say: 'An MFA reset ends active sessions and invalidates old authenticator and recovery codes. Wait for your administrator to confirm it before restarting.',
        run: async s => {
          sql(`DECLARE @user UNIQUEIDENTIFIER = (SELECT appUserId FROM auth.appUser WHERE userName = N'${a.user}' AND isDeleted = 0); EXEC auth.uspResetUserMfa @appUserId = @user;`);
          await s.highlight(s.page.getByRole('button', { name: 'Sign in again after an MFA reset', exact: true }));
        } },
      { say: 'After the confirmed reset, choose Sign in again after an MFA reset and enter your password. MERIT asks you to set up a new authenticator entry.',
        run: async s => { await s.click(s.page.getByRole('button', { name: 'Sign in again after an MFA reset', exact: true }));
          await password(s, a); await s.see(s.page.getByRole('heading', { name: 'Set up your authenticator app', exact: true })); } },
      { say: 'Add the new MERIT entry to your app, verify its current code, and save the new recovery codes. Use the new entry for future sign-ins.',
        run: async s => { await s.type(s.page.locator('#Code'), totp(a.secret)); await s.click(s.page.getByRole('button', { name: 'Turn on and sign in', exact: true }));
          await s.see(s.page.locator('[data-recovery-codes]')); await s.click(s.page.locator('label[for="saved-codes"]'));
          await s.click(s.page.getByRole('button', { name: 'Continue to MERIT', exact: true })); await signedIn(s); } },
    ] },
  ],
  recap: ['Used a one-use recovery code', 'Identified the support path when no codes remain', 'Enrolled again after a confirmed MFA reset'],
});
