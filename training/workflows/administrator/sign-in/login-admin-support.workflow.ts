import { LoginAccount, loginWorkflow, password, verify, NEW_PASSWORD } from '../../../lib/login.ts';
import { sql } from '../../../lib/sql.ts';

const a = new LoginAccount('loginadmin', 'Avery Administrator', true);
const target = new LoginAccount('loginsupport', 'Taylor');
export default loginWorkflow(a, {
  id: 'login-admin-support', title: 'Administrator: help someone sign in again',
  summary: 'Unlock an account, reset a lost authenticator, and require a private password after confirming the person\'s identity.',
  scenes: [
    { title: 'Verify the person and find their account', steps: [
      { say: 'Use your agency\'s identity-verification procedure before changing sign-in security. Confirm the person through a trusted channel, not a message containing a code.',
        run: async s => { await password(s, a); await verify(s, a); } },
      { say: 'Administrator accounts use an authenticator or recovery code. Email verification is never available to administrators.',
        run: async s => { await s.click(s.page.locator('header').getByRole('link', { name: 'Sign-in methods', exact: true }));
          await s.expectText(s.page.locator('section[aria-labelledby="email-heading"]'), /never available to administrators/);
          await s.highlight(s.page.locator('section[aria-labelledby="email-heading"]')); } },
      { say: 'Open Administration, then Users. Filter by the person\'s name or user name and open the correct account. You can manage only accounts within your authority.',
        run: async s => { await s.click(s.page.locator('#merit-nav summary', { hasText: 'Administration' }));
          await s.click(s.page.locator('#merit-nav').getByRole('link', { name: 'Users', exact: true }));
          await s.type(s.page.locator('#user-filter'), target.user);
          await s.click(s.page.getByRole('link', { name: target.displayName, exact: true })); } },
    ] },
    { title: 'Unlock or reset MFA', steps: [
      { say: 'For a locked account, choose Unlock account after verifying the person. This clears the lock; it does not change their password or authenticator.',
        run: async s => { await s.click(s.page.getByRole('button', { name: 'Unlock account', exact: true }));
          await s.expectText(s.page.locator('main'), /Account unlocked/); } },
      { say: 'For a lost authenticator, choose Reset MFA after verifying the person. The confirmation explains that active sessions and old codes will stop working.',
        run: async s => { s.page.once('dialog', dialog => dialog.accept());
          await s.click(s.page.getByRole('button', { name: 'Reset MFA after verifying the person', exact: true }));
          await s.expectText(s.page.locator('main'), /MFA reset/); } },
      { say: 'Tell the person to sign in again with their password, add the new MERIT authenticator entry, and save the new recovery codes.',
        run: async s => s.highlight(s.page.getByText('Not yet enrolled; the person enrolls at next sign-in.', { exact: true })) },
    ] },
    { title: 'Set a temporary password when needed', steps: [
      { say: 'If a password reset is also needed, enter a new temporary password and select Ask them to choose a new password at their next sign-in.',
        run: async s => { await s.expectText(s.page.locator('main'), /At least 15 characters/);
          await s.type(s.page.locator('#NewPassword'), NEW_PASSWORD);
          if (!(await s.page.locator('#RequirePasswordChange').isChecked())) await s.click(s.page.locator('label[for="RequirePasswordChange"]'));
          await s.highlight(s.page.locator('label[for="RequirePasswordChange"]')); } },
      { say: 'Save the account. Share the temporary password only through your approved secure channel. Never request the person\'s authenticator or recovery codes.',
        run: async s => { await s.click(s.page.getByRole('button', { name: 'Save', exact: true })); await s.expectText(s.page.locator('main'), /saved/i); } },
      { say: 'The person completes MFA and chooses a password only they know. If both password and authenticator were lost, they must complete both reset paths.' },
      { say: 'Another administrator must help an administrator who cannot sign in. A platform administrator should contact another platform administrator.' },
    ] },
  ],
  recap: ['Verified the account and unlocked it', 'Reset MFA and explained re-enrollment', 'Required a private password after a temporary reset'],
}, { prepare: async () => {
  await target.reset();
  sql(`UPDATE auth.appUser SET failedSignInCount = 5, lockoutEndUtc = DATEADD(MINUTE, 15, SYSUTCDATETIME()) WHERE ${target.where};`);
} });
