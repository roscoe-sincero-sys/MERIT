import AxeBuilder from '@axe-core/playwright';
import { angularReady, expect, test } from './helpers';

const widths = [1440, 1280, 1024, 768, 640, 390, 320];
const tags = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'];

for (const [user, count] of [['cmbalt', 11], ['supervisor', 15], ['admin', 17]] as const) {
  test(`${user}: destinations, target sizes and reflow across seven widths`, async ({ asUser }) => {
    test.setTimeout(120000);
    const page = await asUser(user);
    let destinations: string[] = [];
    for (const width of widths) {
      await page.setViewportSize({ width, height: 900 });
      await page.goto('/');
      await expect(page.locator('header')).toHaveAttribute('data-enhanced-navigation', 'true');
      const nav = page.getByRole('navigation', { name: 'Primary', exact: true });
      if (width <= 1024) await page.getByRole('button', { name: 'Menu', exact: true }).click();
      const links = await nav.locator('a').evaluateAll(els => els.map(a => a.getAttribute('href')!));
      expect(links).toHaveLength(count);
      expect(new Set(links).size).toBe(count);
      if (!destinations.length) destinations = links;
      expect(links).toEqual(destinations);
      if (user === 'cmbalt') {
        await expect(nav.locator('a[href="/intake"], a[href="/admin/users"], a[href="/admin/tenants"]')).toHaveCount(0);
        await expect(nav.locator('summary').filter({ hasText: /^Intake/ })).toHaveCount(0);
      }
      const controls = nav.locator(':scope > ul > li > a, summary');
      const boxes = await controls.evaluateAll(els => els.map(e => { const r = e.getBoundingClientRect(); return { y: r.y, height: r.height }; }));
      expect(boxes.every(b => b.height >= 44)).toBe(true);
      if (width > 1024) expect(new Set(boxes.map(b => Math.round(b.y))).size).toBe(1);
      const overflow = () => page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth);
      expect(await overflow()).toBe(false);
      if (width === 320) expect((await page.locator('header').boundingBox())!.height).toBeLessThan(700);
      if (width === 1280 || width === 320) {
        let result = await new AxeBuilder({ page }).include('header').withTags(tags).analyze();
        expect(result.violations.map(v => ({ id: v.id, nodes: v.nodes.map(n => n.target) }))).toEqual([]);
        for (const group of await nav.locator('summary').all()) {
          await group.click();
          expect(await overflow()).toBe(false);
          result = await new AxeBuilder({ page }).include('header').withTags(tags).analyze();
          expect(result.violations.map(v => ({ id: v.id, nodes: v.nodes.map(n => n.target) }))).toEqual([]);
          await page.keyboard.press('Escape');
          await expect(group).toBeFocused();
        }
      }
    }
  });
}

test('disclosures support keyboard, outside dismissal and responsive focus restoration', async ({ asUser }) => {
  const page = await asUser('admin');
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/');
  // Chromium exposes native summary as DisclosureTriangle, rather than the ARIA button role.
  const summary = page.locator('summary').filter({ hasText: /^Surveillance/ });
  const cases = page.locator('#merit-nav a[href="/cases"]');
  await summary.focus(); await page.keyboard.press('Enter');
  await expect(cases).toBeVisible();
  await page.keyboard.press('Tab'); await expect(cases).toBeFocused();
  await page.keyboard.press('Escape'); await expect(summary).toBeFocused();
  await expect(cases).toBeHidden();
  await summary.press('Space'); await expect(cases).toBeVisible();
  await page.locator('main h1').click(); await expect(cases).toBeHidden();
  await page.locator('#merit-nav a[href="/"]').focus();
  await page.setViewportSize({ width: 768, height: 900 });
  const menu = page.getByRole('button', { name: 'Menu', exact: true });
  await expect(menu).toBeFocused();
  await menu.press('Enter'); await summary.click(); await cases.focus();
  await page.keyboard.press('Escape'); await expect(summary).toBeFocused();
  await page.keyboard.press('Escape'); await expect(menu).toBeFocused();
  await page.keyboard.press('Tab'); await expect(page.locator('#header-search')).toBeFocused();
  await menu.focus(); await page.setViewportSize({ width: 1280, height: 900 });
  await expect(page.locator('.merit-brand')).toBeFocused();
});

test('current section is announced for Razor and Angular destinations', async ({ asUser }) => {
  const page = await asUser('supervisor');
  for (const path of ['/cases', '/app/clusters']) {
    await page.goto(path);
    if (path.startsWith('/app')) await angularReady(page);
    await page.getByRole('button', { name: 'Menu', exact: true }).click();
    const summary = page.locator('.merit-nav-group').filter({ has: page.locator('a[aria-current="page"]') }).locator('summary');
    await expect(summary).toHaveAccessibleName('Surveillance (current section)');
    await expect(page.locator('.merit-nav a[aria-current="page"]')).toHaveAttribute('href', path);
  }
});

for (const [route, label, navName] of [['/app/queues', 'queues', 'Queues'], ['/app/reports', 'report library', 'Report library']]) {
  test(`${route}: contextual collapse reclaims width, persists and adapts without losing focus`, async ({ asUser }) => {
    const page = await asUser('supervisor');
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(route); await angularReady(page);
    const nav = page.getByRole('navigation', { name: navName, exact: true });
    const content = page.locator('merit-context-panel .content');
    const width = (await content.boundingBox())!.width;
    await content.locator('input, select').first().press('Escape');
    await expect(nav).toBeVisible();
    await page.getByRole('button', { name: `Hide ${label}`, exact: true }).click();
    await expect(nav).toBeHidden();
    expect((await content.boundingBox())!.width).toBeGreaterThan(width + 200);
    await page.reload(); await angularReady(page); await expect(nav).toBeHidden();
    const show = page.getByRole('button', { name: `Show ${label}`, exact: true });
    await show.press('Enter'); await nav.getByRole('link').first().focus();
    await page.setViewportSize({ width: 320, height: 900 });
    await expect(nav).toBeHidden(); await expect(show).toBeFocused();
    await show.press('Space'); await nav.getByRole('link').first().focus();
    await page.keyboard.press('Escape'); await expect(show).toBeFocused(); await expect(nav).toBeHidden();
    await page.setViewportSize({ width: 1280, height: 900 }); await expect(nav).toBeVisible();
    await expect(page.locator('merit-context-panel [aria-current="true"]')).toHaveCount(1);
    const result = await new AxeBuilder({ page }).include('merit-context-panel .panel').withTags(tags).analyze();
    expect(result.violations.map(v => v.id)).toEqual([]);
  });
}

test('context panels work with browser storage unavailable and do not appear on other pages', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.addInitScript(() => {
    Storage.prototype.getItem = () => { throw new Error('Storage disabled'); };
    Storage.prototype.setItem = () => { throw new Error('Storage disabled'); };
  });
  await page.goto('/app/queues'); await angularReady(page);
  await page.getByRole('button', { name: 'Show queues', exact: true }).click();
  await expect(page.getByRole('navigation', { name: 'Queues', exact: true })).toBeVisible();
  for (const route of ['/app/held', '/app/results', '/app/match-review', '/app/map']) {
    await page.goto(route); await angularReady(page);
    await expect(page.locator('merit-context-panel')).toHaveCount(0);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  }
});

test('server-rendered navigation is usable without JavaScript', async ({ browser }) => {
  const context = await browser.newContext({ baseURL: test.info().project.use.baseURL, storageState: '.auth/admin.json', javaScriptEnabled: false, ignoreHTTPSErrors: true, viewport: { width: 320, height: 900 } });
  try {
    const page = await context.newPage(); await page.goto('/');
    await page.locator('summary').filter({ hasText: /^Administration/ }).click();
    await expect(page.locator('#merit-nav a[href="/admin/users"]')).toBeVisible();
  } finally { await context.close(); }
});
