import { LoginAccount, loginWorkflow, password, verify, signOut, wrongCode } from '../../../lib/login.ts';
import { sql } from '../../../lib/sql.ts';

const a = new LoginAccount('loginreturn', 'Robin Return');
export default loginWorkflow(a, {
  id: 'login-everyday', title: 'Everyday sign-in and signing out',
  summary: 'Use your password and authenticator, correct common mistakes, switch users, and return after a session ends.',
  scenes: [
    { title: 'Password first', steps: [
      { say: 'Enter your MERIT user name and password. Check the user name if you share a workstation; each person uses their own account.',
        run: async s => { await s.type(s.page.locator('#UserName'), a.user); await s.type(s.page.locator('#Password'), 'A deliberately wrong training password');
          await s.click(s.page.getByRole('button', { name: 'Continue', exact: true })); } },
      { say: 'A wrong user name or password gives the same message. Check typing and Caps Lock. If you cannot remember the password, use Forgot your password.',
        run: async s => s.highlight(s.page.getByRole('alert')) },
      { say: 'Correct the password and choose Continue. A correct password takes you to the verification step, not directly into MERIT.',
        run: async s => { await password(s, a); await s.see(s.page.getByRole('heading', { name: 'Enter your verification code', exact: true })); } },
      { say: 'Wrong person? Choose Sign in as a different user to return to the password screen, then start with the right account.',
        run: async s => { await s.click(s.page.getByRole('button', { name: 'Sign in as a different user', exact: true })); await s.see(s.page.locator('#UserName')); } },
    ] },
    { title: 'Verify with your authenticator', steps: [
      { say: 'Sign in again. Open the MERIT entry in your authenticator app and use its current six-digit code. The app works without receiving a text message.',
        run: async s => { await password(s, a, '/account/security'); await s.highlight(s.page.locator('form[action="/account/mfa"]')); } },
      { say: 'A mistyped or old code is rejected. Wait for the next code, check your phone time, and use the entry for this MERIT account.',
        run: async s => { await wrongCode(s, a); await s.click(s.page.getByRole('button', { name: 'Verify and sign in', exact: true }));
          await s.expectText(s.page.getByRole('alert'), /That code did not work/); await s.highlight(s.page.getByRole('alert')); } },
      { say: 'Enter the current code and choose Verify and sign in. MERIT returns you to the page you requested, when your access permits it.',
        run: async s => { await verify(s, a); await s.see(s.page.getByRole('heading', { name: 'Sign-in methods', exact: true })); } },
    ] },
    { title: 'End your session and return safely', steps: [
      { say: 'Check your name and active profile in the header. The profile determines which records and tools are available after sign-in.',
        run: async s => s.highlight(s.page.locator('header')) },
      { say: 'Choose Sign out when you finish, especially on a shared computer. MERIT returns to the sign-in page and ends this session.',
        run: async s => { await signOut(s); await s.highlight(s.page.locator('#UserName')); } },
      { say: 'If your session expires or an administrator ends it, sign in again. You still need your password and second factor.',
        run: async s => { await password(s, a); await verify(s, a);
          sql(`UPDATE auth.sessionScope SET revokedUtc = SYSUTCDATETIME() WHERE ${a.where} AND revokedUtc IS NULL;`);
          await s.page.context().clearCookies();
          await s.goto('/account/security'); await s.see(s.page.locator('#UserName')); } },
      { say: 'Use the normal two steps to return. If sign-in succeeds but you lack the access you need, check your profile or ask your administrator.',
        run: async s => { await password(s, a); await verify(s, a); await s.highlight(s.page.locator('header a.merit-profile-switch')); } },
    ] },
  ],
  recap: ['Corrected password and verification mistakes', 'Returned to the requested page with two-factor sign-in', 'Signed out and returned after a session ended'],
});
