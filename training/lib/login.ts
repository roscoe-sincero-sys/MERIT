import { chromium, type Page } from '@playwright/test';
import { pbkdf2Sync, randomBytes } from 'node:crypto';
import { BASE_URL, CHANNEL, LOGIN_DEMO_PASSWORD } from './config.ts';
import { sql, lit } from './sql.ts';
import { totp } from './totp.ts';
import type { Stage } from './stage.ts';
import { defineWorkflow, type Workflow } from './workflow.ts';

export const MAIL = process.env.MERIT_TRAINING_MAIL_URL ?? 'http://127.0.0.1:18025';
export const NO_EMAIL = process.env.MERIT_TRAINING_NO_EMAIL_URL ?? 'https://localhost:7344';
export const NEW_PASSWORD = 'Training Orchard Lantern River 2026';
const root = `(SELECT tenantId FROM auth.tenant WHERE tenantCode = 'TRN-SIGNIN' AND isDeleted = 0)`;
const key = (value: string) => `CAST(HASHBYTES('MD5', N'merit-training:${value}') AS UNIQUEIDENTIFIER)`;

/** Same versioned NFC/Identity V3 format as the application; only used for synthetic training setup. */
function initialPasswordHash(): string {
  const salt = randomBytes(16), header = Buffer.alloc(13);
  header[0] = 1; header.writeUInt32BE(2, 1); header.writeUInt32BE(220_000, 5); header.writeUInt32BE(salt.length, 9);
  const subkey = pbkdf2Sync(LOGIN_DEMO_PASSWORD.normalize('NFC'), salt, 220_000, 32, 'sha512');
  return `merit-nfc-v1$${Buffer.concat([header, salt, subkey]).toString('base64')}`;
}

/** These are the only returned HTML changes: secrets are removed before the browser can paint them.
 * Input types are changed so the existing scanner and action logger treat verification fields as secret.
 * The original values stay in the account object in Node memory, never in page attributes or artifacts.
 */
export function redactLoginHtml(html: string): string {
  return html
    .replace(/<div class="merit-qr"[\s\S]*?<\/div>/g,
      '<div class="usa-alert usa-alert--info" role="img" aria-label="QR code hidden for training">QR code hidden for training</div>')
    .replace(/(<dd class="merit-setup-key">)[\s\S]*?(<\/dd>)/g, '$1Setup key hidden for training$2')
    .replace(/(<li data-recovery-code[^>]*>)[\s\S]*?(<\/li>)/g, '$1Recovery code hidden for training$2')
    .replace(/<input\b[^>]*autocomplete="one-time-code"[^>]*>/g, tag =>
      /\btype="[^"]*"/.test(tag) ? tag.replace(/\btype="[^"]*"/, 'type="password"') : tag.replace('<input', '<input type="password"'));
}

export class LoginAccount {
  secret = '';
  recovery: string[] = [];
  password = LOGIN_DEMO_PASSWORD;
  code = '';
  oldCode = '';
  link = '';
  oldLink = '';
  sentAt = 0;
  readonly email: string;
  readonly user: string;
  readonly displayName: string;
  readonly administrator: boolean;
  constructor(user: string, displayName: string, administrator = false) {
    this.user = user; this.displayName = displayName; this.administrator = administrator;
    if (!/^login[a-z]+$/.test(user)) throw new Error('Login scenarios must use their own login-prefixed synthetic account.');
    this.email = `${user}@merit.example`;
  }
  get where(): string { return `appUserId = ${key(`user:${this.user}`)}`; }

  async reset(enrolled = true, emailOn = false): Promise<void> {
    this.password = LOGIN_DEMO_PASSWORD; this.secret = ''; this.recovery = []; this.code = ''; this.link = '';
    sql(`SET XACT_ABORT ON;
EXEC config.uspCreateRootTenant @tenantCode = 'TRN-SIGNIN', @tenantName = N'MERIT Sign-in Training (demo)', @tenantType = 'state',
     @statePostalCode = 'MD', @stateFips = '24', @timeZone = 'Eastern Standard Time';
DECLARE @home UNIQUEIDENTIFIER = ${root};
DECLARE @user UNIQUEIDENTIFIER = ${key(`user:${this.user}`)};
DECLARE @hash NVARCHAR(500) = ${lit(initialPasswordHash())};
EXEC auth.uspSaveUser @appUserId = @user, @userName = ${lit(this.user)}, @displayName = ${lit(this.displayName)},
     @email = ${lit(this.email)}, @passwordHash = @hash, @homeTenantId = @home, @passwordChangeRequired = 0;
DECLARE @role UNIQUEIDENTIFIER = (SELECT roleId FROM auth.role WHERE rootTenantId = @home AND roleCode = 'intakeClerk' AND isDeleted = 0);
DECLARE @grant UNIQUEIDENTIFIER = ${key(`grant:${this.user}:intake`)};
EXEC auth.uspSaveUserAssignment @userAssignmentId = @grant, @appUserId = @user, @roleId = @role, @tenantId = @home,
     @includeDescendants = 1, @accessLevel = 'full', @startDate = '2026-01-01';
${this.administrator ? `SET @role = (SELECT roleId FROM auth.role WHERE rootTenantId = @home AND roleCode = 'administrator' AND isDeleted = 0);
SET @grant = ${key(`grant:${this.user}:admin`)};
EXEC auth.uspSaveUserAssignment @userAssignmentId = @grant, @appUserId = @user, @roleId = @role, @tenantId = @home,
     @includeDescendants = 1, @accessLevel = 'full', @startDate = '2026-01-01';` : ''}
UPDATE auth.appUser SET isActive = 1, mfaEnabled = 0, mfaSecretProtected = NULL, failedSignInCount = 0, lockoutEndUtc = NULL,
     mfaEmailVerifiedUtc = NULL, mfaPreferredMethod = 'totp', securityStamp = NEWID() WHERE appUserId = @user;
UPDATE auth.sessionScope SET revokedUtc = SYSUTCDATETIME() WHERE appUserId = @user AND revokedUtc IS NULL;
UPDATE auth.mfaRecoveryCode SET isDeleted = 1 WHERE appUserId = @user AND isDeleted = 0;
UPDATE auth.mfaEmailChallenge SET isDeleted = 1 WHERE appUserId = @user AND isDeleted = 0;
UPDATE auth.passwordReset SET isDeleted = 1 WHERE appUserId = @user AND isDeleted = 0;
UPDATE config.jurisdictionSetting SET settingValue = N'true' WHERE rootTenantId = @home
     AND settingKey = 'emailCodesEnabled' AND isDeleted = 0 AND effectiveTo IS NULL;`);
    if (enrolled) await this.enrollOffCamera();
    if (emailOn) sql(`UPDATE auth.appUser SET mfaEmailVerifiedUtc = SYSUTCDATETIME() WHERE ${this.where};`);
  }

  private capture(html: string): void {
    const secret = /<dd class="merit-setup-key">\s*<code>([^<]+)<\/code>/.exec(html)?.[1];
    if (secret) this.secret = secret.replace(/\s/g, '');
    const codes = [...html.matchAll(/<li data-recovery-code[^>]*>([^<]+)<\/li>/g)].map(m => m[1].trim().replace(/-/g, ''));
    if (codes.length) this.recovery = codes;
  }

  async protect(page: Page): Promise<void> {
    // Playwright routes only the first request in a redirect chain. Follow that chain off camera,
    // redact the final document, then restore its clean path before the first captured frame.
    await page.addInitScript(() => {
      document.addEventListener('DOMContentLoaded', () => {
        const path = document.querySelector<HTMLMetaElement>('meta[name="merit-training-path"]')?.content;
        if (path) history.replaceState(null, '', path);
      }, { once: true });
    });
    await page.context().route('**/account/**', async route => {
      if (!route.request().isNavigationRequest()) { await route.continue(); return; }
      const response = await route.fetch();
      if (!(response.headers()['content-type'] ?? '').includes('text/html') || response.status() >= 300) {
        await route.fulfill({ response }); return;
      }
      const html = await response.text();
      this.capture(html);
      const final = new URL(response.url());
      const path = (final.pathname + final.search).replace(/&/g, '&amp;').replace(/"/g, '&quot;');
      await route.fulfill({ response, body: redactLoginHtml(html).replace(/<head[^>]*>/i, head => `${head}<meta name="merit-training-path" content="${path}">`) });
    });
  }

  private async enrollOffCamera(): Promise<void> {
    const browser = await chromium.launch({ channel: CHANNEL });
    try {
      const context = await browser.newContext({ baseURL: BASE_URL, ignoreHTTPSErrors: true });
      const page = await context.newPage();
      await page.goto('/account/signin');
      await page.locator('#UserName').fill(this.user);
      await page.locator('#Password').fill(this.password);
      await page.getByRole('button', { name: 'Continue', exact: true }).click();
      await page.locator('code').first().waitFor();
      this.secret = (await page.locator('code').first().innerText()).replace(/\s/g, '');
      await page.locator('#Code').fill(totp(this.secret));
      await page.getByRole('button', { name: 'Turn on and sign in' }).click();
      await page.locator('[data-recovery-code]').first().waitFor();
      this.recovery = (await page.locator('[data-recovery-code]').allTextContents()).map(v => v.trim().replace(/-/g, ''));
      await page.locator('label[for="saved-codes"]').click();
      await page.getByRole('button', { name: 'Continue to MERIT', exact: true }).click();
      if (!(await context.request.get('/api/v1/me')).ok()) throw new Error('Training account enrollment did not finish.');
      await context.close();
    } finally { await browser.close(); }
  }

  async message(page: Page, subject?: string): Promise<string> {
    for (let i = 0; i < 80; i++) {
      const response = await page.request.get(`${MAIL}/api/v1/messages?limit=100`);
      if (!response.ok()) throw new Error('The isolated training mail capture server is unavailable. Run node scripts/login-app.ts.');
      const list = await response.json();
      const message = list.messages.find((m: { To: { Address: string }[]; Created: string; Subject: string }) =>
        m.To.some(t => t.Address === this.email) && Date.parse(m.Created) >= this.sentAt - 1000 && (!subject || m.Subject === subject));
      if (message) return (await (await page.request.get(`${MAIL}/api/v1/message/${message.ID}`)).json()).Text;
      await page.waitForTimeout(250);
    }
    throw new Error('No message arrived for the synthetic login account.');
  }

  async readCode(page: Page): Promise<string> {
    const code = /verification code is (\d{6})/.exec(await this.message(page))?.[1];
    if (!code) throw new Error('The captured training message has no verification code.');
    this.code = code;
    return code;
  }

  async readLink(page: Page): Promise<string> {
    const value = /https:\/\/\S+\/account\/reset\?r=[0-9a-f]{32}&t=[A-Za-z0-9_-]{43}/.exec(await this.message(page, 'Reset your MERIT password'))?.[0];
    if (!value || new URL(value).origin !== new URL(BASE_URL).origin) throw new Error('Expected a local training password-reset link.');
    this.link = value;
    return value;
  }
}

export function loginWorkflow(account: LoginAccount, spec: Omit<Workflow, 'role' | 'feature' | 'user' | 'start' | 'prepare' | 'anonymous' | 'setup'>,
  options: { firstTime?: boolean; emailOn?: boolean; prepare?: () => Promise<void> } = {}): Workflow {
  return defineWorkflow({ ...spec, role: account.administrator ? 'administrator' : 'allUsers', feature: 'signIn', user: account.user,
    start: '/account/signin', anonymous: true, setup: page => account.protect(page),
    prepare: async () => { await account.reset(!options.firstTime, options.emailOn); await options.prepare?.(); },
  });
}

export async function password(s: Stage, a: LoginAccount, destination = '/'): Promise<void> {
  if (!new URL(s.page.url()).pathname.endsWith('/signin')) await s.goto(`/account/signin?ReturnUrl=${encodeURIComponent(destination)}`);
  else if (destination !== '/') await s.goto(`/account/signin?ReturnUrl=${encodeURIComponent(destination)}`);
  await s.type(s.page.locator('#UserName'), a.user);
  await s.type(s.page.locator('#Password'), a.password);
  await s.click(s.page.getByRole('button', { name: 'Continue', exact: true }));
}

export async function verify(s: Stage, a: LoginAccount): Promise<void> {
  await s.type(s.page.locator('#Code'), totp(a.secret));
  await s.click(s.page.getByRole('button', { name: 'Verify and sign in', exact: true }));
  await signedIn(s);
}

export async function signedIn(s: Stage): Promise<void> {
  if (!(await s.page.request.get('/api/v1/me')).ok()) throw new Error('The expected signed-in session was not created.');
  await s.see(s.page.locator('header').getByRole('link', { name: 'Sign-in methods', exact: true }));
}

export async function signOut(s: Stage): Promise<void> {
  await s.click(s.page.locator('form[action="/account/signout"]').getByRole('button', { name: 'Sign out', exact: true }).first());
  await s.see(s.page.locator('#UserName'));
  if ((await s.page.request.get('/api/v1/me')).ok()) throw new Error('Sign out left the session active.');
}

export async function wrongCode(s: Stage, a: LoginAccount): Promise<void> {
  const valid = totp(a.secret);
  await s.type(s.page.locator('#Code'), valid.slice(0, -1) + ((Number(valid.at(-1)) + 1) % 10));
}

export async function requestReset(s: Stage, a: LoginAccount): Promise<void> {
  await s.goto('/account/forgot');
  await s.type(s.page.locator('#UserName'), a.user);
  a.sentAt = Date.now();
  await s.click(s.page.getByRole('button', { name: 'Email me a reset link', exact: true }));
  await s.see(s.page.getByRole('heading', { name: 'Check your email', exact: true }));
  await a.readLink(s.page);
}

export async function newPassword(s: Stage, value = NEW_PASSWORD): Promise<void> {
  await s.type(s.page.locator('#NewPassword'), value);
  await s.type(s.page.locator('#ConfirmPassword'), value);
}

export async function sendEmail(s: Stage, a: LoginAccount, name = 'Email me a code'): Promise<void> {
  a.sentAt = Date.now();
  await s.click(s.page.getByRole('button', { name, exact: true }));
  await s.see(s.page.locator('[data-email-delivery="sent"]'));
  await a.readCode(s.page);
}

/** Fault injection belongs only to the dedicated training mail server, never an arbitrary configured service. */
export async function mailFailure(page: Page, enabled: boolean): Promise<void> {
  const url = new URL(MAIL);
  if (!['127.0.0.1', 'localhost'].includes(url.hostname) || url.port !== '18025') throw new Error('Fault injection requires the isolated loopback training Mailpit on port 18025.');
  const response = await page.request.put(`${MAIL}/api/v1/chaos`, { data: {
    Sender: { ErrorCode: 451, Probability: 0 }, Recipient: { ErrorCode: 550, Probability: enabled ? 100 : 0 },
    Authentication: { ErrorCode: 535, Probability: 0 },
  } });
  if (!response.ok()) throw new Error('Could not configure the isolated training mail fault.');
}
