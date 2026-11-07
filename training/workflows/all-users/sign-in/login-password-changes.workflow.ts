import { LoginAccount, loginWorkflow, password, verify, newPassword, NEW_PASSWORD, signedIn, signOut } from '../../../lib/login.ts';
import { sql } from '../../../lib/sql.ts';
import { totp } from '../../../lib/totp.ts';

const a = new LoginAccount('loginpassword', 'Parker Password');
const CHOSEN = 'Training Willow Canyon Morning 2026';
export default loginWorkflow(a, {
  id: 'login-password-changes', title: 'Change your password and temporary passwords',
  summary: 'Change a password while signed in and choose your own password when an administrator requires it.',
  scenes: [
    { title: 'Change a known password', steps: [
      { say: 'If you know your password and want to change it, sign in normally and open Sign-in methods from the header.',
        run: async s => { await password(s, a); await verify(s, a); await s.click(s.page.locator('header').getByRole('link', { name: 'Sign-in methods', exact: true })); } },
      { say: 'Under Password, enter your current password, a new passphrase twice, and a current authenticator code. An email code cannot replace this authenticator check.',
        run: async s => { await s.expectText(s.page.locator('#new-password-hint'), /At least 15 characters/);
          await s.highlight(s.page.locator('section[aria-labelledby="password-heading"]')); } },
      { say: 'Follow the minimum length shown. Passwords must match, must differ from your current one, and must pass the breached-password check.',
        run: async s => { await s.type(s.page.locator('#CurrentPassword'), a.password); await newPassword(s, 'fourteen-chars');
          await s.type(s.page.locator('#PasswordAuthenticatorCode'), totp(a.secret)); await s.click(s.page.getByRole('button', { name: 'Change password', exact: true }));
          await s.see(s.page.locator('#password-errors')); await s.highlight(s.page.locator('#password-errors')); } },
      { say: 'Correct the form and choose Change password. Your other sessions are signed out; this browser continues in a new session.',
        run: async s => { await s.type(s.page.locator('#CurrentPassword'), a.password); await newPassword(s);
          await s.type(s.page.locator('#PasswordAuthenticatorCode'), totp(a.secret)); await s.click(s.page.getByRole('button', { name: 'Change password', exact: true }));
          await s.expectText(s.page.locator('main'), /Your password was changed/); a.password = NEW_PASSWORD; await signedIn(s); } },
    ] },
    { title: 'An administrator requires a new password', steps: [
      { say: 'An administrator may give you a temporary password and require a change at your next sign-in. First complete your password and second-factor steps.',
        run: async s => { sql(`UPDATE auth.appUser SET passwordChangeRequired = 1 WHERE ${a.where};`);
          await signOut(s); await password(s, a); await s.type(s.page.locator('#Code'), totp(a.secret));
          await s.click(s.page.getByRole('button', { name: 'Verify and sign in', exact: true }));
          await s.see(s.page.getByRole('heading', { name: 'Choose a new password', exact: true })); } },
      { say: 'Choose your own new password before continuing. MERIT will keep returning to this page until the required change is complete.',
        run: async s => { await s.goto('/account/security'); await s.see(s.page.getByRole('button', { name: 'Save password and continue', exact: true }));
          await s.highlight(s.page.locator('main')); } },
      { say: 'You already verified this sign-in, so this page asks only for your new password twice. Choose Save password and continue.',
        run: async s => { await newPassword(s, CHOSEN); await s.click(s.page.getByRole('button', { name: 'Save password and continue', exact: true }));
          a.password = CHOSEN; await signedIn(s); } },
      { say: 'Use your chosen password for future sign-ins. Keep it private, and tell your administrator if you receive an unexpected password-change notice.',
        run: async s => { await signOut(s); await password(s, a); await verify(s, a); await s.highlight(s.page.locator('header')); } },
    ] },
  ],
  recap: ['Changed a password using a current authenticator code', 'Completed an administrator-required password change', 'Verified sign-in with the chosen password'],
});
