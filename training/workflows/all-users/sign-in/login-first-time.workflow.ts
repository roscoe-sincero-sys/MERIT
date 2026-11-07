import { LoginAccount, loginWorkflow, password, signedIn } from '../../../lib/login.ts';
import { totp } from '../../../lib/totp.ts';

const a = new LoginAccount('loginfirst', 'Alex First');
export default loginWorkflow(a, {
  id: 'login-first-time', title: 'First sign-in: set up your authenticator',
  summary: 'Sign in for the first time, add MERIT to an authenticator app, and save your recovery codes.',
  scenes: [
    { title: 'Start with your account', steps: [
      { say: 'Use the MERIT address provided by your agency. Your administrator supplies your user name and initial password; there is no public account registration.',
        run: async s => s.highlight(s.page.locator('form[action="/account/signin"]')) },
      { say: 'Enter your user name and password, then choose Continue. Passwords and all security codes are hidden in this training video.',
        run: async s => { await password(s, a); await s.see(s.page.getByRole('heading', { name: 'Set up your authenticator app', exact: true })); } },
    ] },
    { title: 'Add the authenticator app', steps: [
      { say: 'Every account needs a second factor. In Microsoft Authenticator, add an Other account and scan the QR code. Another authenticator app can also scan it.',
        run: async s => s.highlight(s.page.getByRole('img', { name: 'QR code hidden for training', exact: true })) },
      { say: 'Cannot scan? Choose Other account and enter the account name and setup key manually. Choose Time based if asked. Keep the setup key private.',
        run: async s => s.highlight(s.page.locator('dl')) },
      { say: 'Use the current verification code from the MERIT entry in your app. The setup key is not the code to enter here.',
        run: async s => s.highlight(s.page.locator('form[action="/account/enroll"]')) },
      { say: 'If a code fails, wait for a new one and check automatic date and time on your phone. Keep this page open; starting sign-in again creates a new setup key.',
        run: async s => {
          const valid = totp(a.secret);
          await s.type(s.page.locator('#Code'), valid.slice(0, -1) + ((Number(valid.at(-1)) + 1) % 10));
          await s.click(s.page.getByRole('button', { name: 'Turn on and sign in', exact: true }));
          await s.see(s.page.getByRole('alert'));
        } },
      { say: 'Enter the current code and choose Turn on and sign in.',
        run: async s => { await s.type(s.page.locator('#Code'), totp(a.secret)); await s.click(s.page.getByRole('button', { name: 'Turn on and sign in', exact: true }));
          await s.see(s.page.getByRole('heading', { name: 'Save your recovery codes', exact: true })); } },
    ] },
    { title: 'Save recovery codes before continuing', steps: [
      { say: 'Each recovery code works once if you lose your authenticator. Save them securely now; MERIT will not show them again after you continue.',
        run: async s => s.highlight(s.page.locator('[data-recovery-codes]')) },
      { say: 'Copy codes and Print codes are available. Store your own codes using your agency-approved method; do not share them or put them in a support message.',
        run: async s => s.highlight(s.page.locator('[data-recovery-tools]')) },
      { say: 'Continue to MERIT stays disabled until you confirm that the codes are saved. Leaving this page does not skip the requirement.',
        run: async s => { if (!(await s.page.getByRole('button', { name: 'Continue to MERIT', exact: true }).isDisabled())) throw new Error('Recovery acknowledgement was not required.');
          await s.goto('/'); await s.see(s.page.getByRole('heading', { name: 'Save your recovery codes', exact: true })); } },
      { say: 'After saving your codes, select I have saved these codes, then Continue to MERIT. Your account is ready for everyday sign-in.',
        run: async s => { await s.click(s.page.locator('label[for="saved-codes"]')); await s.click(s.page.getByRole('button', { name: 'Continue to MERIT', exact: true }));
          await signedIn(s); await s.highlight(s.page.locator('header')); } },
    ] },
  ],
  recap: ['Entered an agency-provided account', 'Added MERIT to an authenticator app', 'Saved and acknowledged one-use recovery codes'],
}, { firstTime: true });
