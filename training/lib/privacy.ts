import type { Page } from '@playwright/test';
import { DATABASE, DEMO_PASSWORD, LOGIN_DEMO_PASSWORD } from './config.ts';
import { sqlValues } from './sql.ts';

/**
 * Guards that keep real or sensitive information out of a video. A build stops at the first failure.
 *   1. The target: only a training database (its name contains TRAINING), whose every account is a synthetic demo
 *      account (an address at a reserved example domain) and whose data belongs to the demo tenant.
 *   2. What is on screen: before and after every step the visible text and field values are scanned for anything that
 *      looks like a password, a one-time code, a secret or key, an identity number, or a real e-mail address.
 * Passwords and codes are also masked by the overlay, and sign-in happens off camera (auth.ts).
 */
const SYNTHETIC_DOMAINS = /@([a-z0-9-]+\.)*(example|example\.com|example\.org|example\.net|test|localhost|invalid)$/i;

export function assertTrainingTarget(): void {
  if (!/TRAINING/i.test(DATABASE)) {
    throw new Error(`Refusing to record against ${DATABASE}: videos use a training database (MERIT_TRAINING_DATABASE must contain TRAINING).`);
  }
  const real = sqlValues(`SELECT TOP 5 userName FROM auth.appUser
    WHERE isDeleted = 0 AND email IS NOT NULL AND email NOT LIKE N'%@%.example' AND email NOT LIKE N'%@%.test'
      AND email NOT LIKE N'%@example.com' AND email NOT LIKE N'%@localhost';`);
  if (real.length) throw new Error(`Refusing to record: ${DATABASE} has accounts with non-example addresses (${real.join(', ')}).`);
  const tenants = sqlValues(`SELECT COUNT(*) FROM auth.tenant WHERE isDeleted = 0 AND tenantName LIKE N'%(demo)%';`);
  if (Number(tenants[0] ?? 0) === 0) throw new Error(`Refusing to record: ${DATABASE} has no demo tenant; deploy it with -IncludeDemoData.`);
}

export interface Finding { rule: string; sample: string }

const RULES: { rule: string; pattern: RegExp }[] = [
  { rule: 'demo password', pattern: new RegExp(DEMO_PASSWORD.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')) },
  { rule: 'training login password', pattern: new RegExp(LOGIN_DEMO_PASSWORD.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')) },
  { rule: 'one-time code beside a code label', pattern: /\b(code|otp|passcode|verification)\b[^\n]{0,40}\b\d{6}\b/i },
  { rule: 'authenticator secret (base32)', pattern: /\b(?:[A-Z2-7]{4}\s?){8,}\b/ },
  { rule: 'JSON web token', pattern: /\beyJ[\w-]{10,}\.[\w-]{10,}\./ },
  { rule: 'API key or token', pattern: /\b(?:sk|pk|api|key|token|secret)[-_][A-Za-z0-9]{20,}\b/i },
  { rule: 'connection string password', pattern: /\b(password|pwd)\s*=\s*[^;\s]+/i },
  { rule: 'US social security number', pattern: /\b\d{3}-\d{2}-\d{4}\b/ },
];

const EMAIL = /[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/g;

/** Returns what on the page breaks a rule; an empty list means the frame is safe. */
export async function scanPage(page: Page): Promise<Finding[]> {
  const text = await page.evaluate(() => {
    const values = [...document.querySelectorAll<HTMLInputElement | HTMLTextAreaElement>('input, textarea')]
      .filter(el => el.type !== 'hidden' && el.type !== 'password' && el.getClientRects().length > 0)
      .map(el => el.value);
    return `${document.body?.innerText ?? ''}\n${values.join('\n')}\n${location.href}`;
  });
  const findings: Finding[] = [];
  for (const { rule, pattern } of RULES) {
    const m = text.match(pattern);
    if (m) findings.push({ rule, sample: redact(m[0]) });
  }
  for (const address of text.match(EMAIL) ?? []) {
    if (!SYNTHETIC_DOMAINS.test(address)) findings.push({ rule: 'e-mail address outside the example domains', sample: redact(address) });
  }
  return findings;
}

export async function assertSafe(page: Page, where: string): Promise<void> {
  const findings = await scanPage(page);
  if (findings.length) {
    throw new Error(`Privacy check failed at ${where}: ${findings.map(f => `${f.rule} (${f.sample})`).join('; ')}`);
  }
}

/** Keeps a little of a match for the error message without repeating the sensitive part. */
function redact(value: string): string {
  return value.length <= 4 ? '****' : `${value.slice(0, 2)}…${'*'.repeat(Math.min(6, value.length - 2))}`;
}
