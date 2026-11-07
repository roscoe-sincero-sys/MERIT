import { LoginAccount, loginWorkflow, password, verify, sendEmail, signOut, signedIn } from '../../../lib/login.ts';
import { totp } from '../../../lib/totp.ts';

const a = new LoginAccount('loginemail', 'Emery Email');
export default loginWorkflow(a, {
  id: 'login-email-setup', title: 'Email codes: enable, choose and turn off',
  summary: 'Enable optional email verification, choose the method offered first, and keep your authenticator available.',
  scenes: [
    { title: 'Check whether email codes are available', steps: [
      { say: 'First sign in with your password and authenticator. Open Sign-in methods from the header.',
        run: async s => { await password(s, a); await verify(s, a); await s.click(s.page.locator('header').getByRole('link', { name: 'Sign-in methods', exact: true })); } },
      { say: 'Your agency decides whether email codes are allowed. They need an email address on your account and are never available to administrators.',
        run: async s => s.highlight(s.page.locator('section[aria-labelledby="email-heading"]')) },
      { say: 'Read the warning before enabling email codes. Anyone who can read your email could use them. Your authenticator stays required and available.',
        run: async s => s.highlight(s.page.locator('section[aria-labelledby="email-heading"] .usa-alert')) },
    ] },
    { title: 'Confirm your email address', steps: [
      { say: 'Enter a current authenticator code to start. Confirm that the masked address is yours; ask your administrator to correct it if needed.',
        run: async s => { await s.type(s.page.locator('#AuthenticatorCode'), totp(a.secret));
          a.sentAt = Date.now(); await s.click(s.page.getByRole('button', { name: /^Send a code to / }));
          await s.see(s.page.getByRole('heading', { name: 'Confirm your email address', exact: true })); await a.readCode(s.page); } },
      { say: 'Read the newest email, enter its verification code, and choose Confirm and turn on email codes. This proves access to the address.',
        run: async s => { await s.type(s.page.locator('#Code'), a.code); await s.click(s.page.getByRole('button', { name: 'Confirm and turn on email codes', exact: true }));
          await s.expectText(s.page.locator('main'), /Email codes are on/); } },
      { say: 'Under At sign-in, offer first, choose An email code and save your choice. This changes the first option, not the need for a second factor.',
        run: async s => { await s.click(s.page.locator('label[for="prefer-email"]')); await s.click(s.page.getByRole('button', { name: 'Save choice', exact: true }));
          await s.expectText(s.page.locator('main'), /offer an email code first/); } },
    ] },
    { title: 'Use either method and turn email off', steps: [
      { say: 'Sign out and enter your password again. Get a code by email is now offered first, while your authenticator remains an alternative.',
        run: async s => { await signOut(s); await password(s, a); await s.see(s.page.getByRole('heading', { name: 'Get a code by email', exact: true }));
          await s.highlight(s.page.locator('main')); } },
      { say: 'Choose Email me a code. Enter the code from the newest email, then Verify and sign in. Codes work once and expire after the time shown.',
        run: async s => { await sendEmail(s, a); await s.type(s.page.locator('#Code'), a.code);
          await s.click(s.page.getByRole('button', { name: 'Verify and sign in', exact: true })); await signedIn(s); } },
      { say: 'To prefer your authenticator again, open Sign-in methods, choose My authenticator app, and save your choice.',
        run: async s => { await s.click(s.page.locator('header').getByRole('link', { name: 'Sign-in methods', exact: true }));
          await s.click(s.page.locator('label[for="prefer-totp"]')); await s.click(s.page.getByRole('button', { name: 'Save choice', exact: true })); } },
      { say: 'Turn off email codes removes that option. Your authenticator still works. Sign in once more to check that the email option is gone.',
        run: async s => { await s.click(s.page.getByRole('button', { name: 'Turn off email codes', exact: true }));
          await s.expectText(s.page.locator('main'), /Email codes are off/); await signOut(s); await password(s, a);
          if (await s.page.getByRole('button', { name: 'Email me a code', exact: true }).count()) throw new Error('Email remained available after turning it off.');
          await verify(s, a); } },
    ] },
  ],
  recap: ['Confirmed an email address with both verification steps', 'Changed the sign-in method offered first', 'Used email verification and turned it off again'],
});
