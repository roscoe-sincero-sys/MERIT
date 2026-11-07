import { LoginAccount, loginWorkflow, requestReset, newPassword, NEW_PASSWORD, password, verify, wrongCode } from '../../../lib/login.ts';
import { totp } from '../../../lib/totp.ts';

const a = new LoginAccount('loginforgot', 'Finley Forgot');
export default loginWorkflow(a, {
  id: 'login-forgot-password', title: 'Forgotten password: reset and sign in',
  summary: 'Request a reset link, verify your identity, choose a new password, and sign in again.',
  scenes: [
    { title: 'Request your reset link', steps: [
      { say: 'Choose Forgot your password? on the sign-in page. A reset needs access to your account email and your authenticator or an unused recovery code.',
        run: async s => s.click(s.page.getByRole('link', { name: 'Forgot your password?', exact: true })) },
      { say: 'Enter your MERIT user name and choose Email me a reset link. Use your user name rather than guessing an email address.',
        run: async s => requestReset(s, a) },
      { say: 'The same Check your email message appears whether an account exists or not. Check junk or spam. No email? Check your user name and contact your administrator.',
        run: async s => s.highlight(s.page.locator('main')) },
      { say: 'Open the link from the newest email. It works once and for the time shown. The email itself is kept off camera because the link is private.',
        run: async s => { await s.goto(a.link); await s.see(s.page.getByRole('heading', { name: 'Choose a new password', exact: true }));
          await s.expectText(s.page.locator('#reset-password-hint'), /At least 15 characters/);
          if (new URL(s.page.url()).search) throw new Error('A reset token remained in the displayed address.'); } },
    ] },
    { title: 'Verify and choose a password', steps: [
      { say: 'The link alone is not enough. Enter an authenticator code, or an unused recovery code. A recovery code is used up when the reset succeeds.',
        run: async s => s.highlight(s.page.locator('#reset-code-hint')) },
      { say: 'A wrong verification code leaves the password unchanged and uses an attempt on this link. Read the error before retrying.',
        run: async s => { await wrongCode(s, a); await newPassword(s); await s.click(s.page.getByRole('button', { name: 'Reset password', exact: true }));
          await s.expectText(s.page.locator('#reset-errors'), /did not match/); } },
      { say: 'Choose a long passphrase that meets the minimum shown. Enter the same new password twice. MERIT also rejects known breached passwords.',
        run: async s => { await s.type(s.page.locator('#Code'), totp(a.secret)); await s.type(s.page.locator('#NewPassword'), NEW_PASSWORD);
          await s.type(s.page.locator('#ConfirmPassword'), 'A different training passphrase'); await s.click(s.page.getByRole('button', { name: 'Reset password', exact: true }));
          await s.expectText(s.page.locator('#reset-errors'), /passwords don.t match/); } },
      { say: 'Correct the fields, enter a current code, and choose Reset password. MERIT confirms the reset and signs out other sessions on this account.',
        run: async s => { await s.type(s.page.locator('#Code'), totp(a.secret)); await newPassword(s);
          await s.click(s.page.getByRole('button', { name: 'Reset password', exact: true }));
          await s.see(s.page.getByRole('heading', { name: 'Your password has been reset', exact: true })); a.password = NEW_PASSWORD; } },
    ] },
    { title: 'Return with the new password', steps: [
      { say: 'A reset link cannot be used again. If you did not make the reset, contact your MERIT administrator.',
        run: async s => { await s.goto(a.link); await s.expectText(s.page.getByRole('alert'), /already used/); await s.highlight(s.page.getByRole('alert')); } },
      { say: 'Go back to sign in. Use your new password, then the normal authenticator step. Resetting a password does not remove MFA.',
        run: async s => { await s.click(s.page.getByRole('link', { name: 'Back to sign in', exact: true })); await password(s, a); await verify(s, a); } },
    ] },
  ],
  recap: ['Requested and opened a private reset link', 'Verified identity and corrected password errors', 'Signed in with the new password and second factor'],
});
