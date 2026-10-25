import AxeBuilder from '@axe-core/playwright';
import { BrowserContext, expect, Page, test as base } from '@playwright/test';

type User = 'supervisor' | 'cmbalt';

/** asUser opens a page in a fresh context carrying that user's saved session (see global-setup.ts). */
export const test = base.extend<{ asUser: (user: User) => Promise<Page> }>({
  asUser: async ({ browser }, use) => {
    const contexts: BrowserContext[] = [];
    await use(async user => {
      const context = await browser.newContext({ storageState: `.auth/${user}.json`, ignoreHTTPSErrors: true, viewport: { width: 768, height: 1024 } });
      contexts.push(context);
      return context.newPage();
    });
    for (const c of contexts) await c.close();
  },
});
export { expect };

/** WCAG 2.1 A and AA rules only; every violation is listed with its first nodes so a failure says what to fix. */
export async function expectAccessible(page: Page, label: string): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze();
  const summary = results.violations.map(v =>
    `${v.id} (${v.impact}): ${v.help}\n    ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join('\n    ')}`);
  expect(summary, `${label} has WCAG 2.1 AA violations`).toEqual([]);
}

/** Waits for the Angular screen to replace its loading text. */
export async function angularReady(page: Page): Promise<void> {
  await expect(page.locator('app-root h1')).toBeVisible();
  await page.waitForLoadState('networkidle');
}
