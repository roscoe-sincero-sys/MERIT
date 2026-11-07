import { angularReady, expect, expectAccessible, test } from './helpers';
import type { Page } from '@playwright/test';

async function person(page: Page) {
  await page.goto('/search?q=Rivera');
  await page.getByRole('link', { name: /Rivera, Mia/ }).first().click();
  return new URL(page.url()).pathname;
}
async function peopleLink(page: Page) {
  const link = page.locator('#merit-nav a[href="/search"]');
  await link.click();
}
async function casePage(page: Page) {
  await person(page);
  await page.locator('#section-cases a[href^="/cases/"]').first().click();
}

for (const user of ['cmbalt', 'supervisor', 'admin'] as const) {
test(`${user}: people Search stays visible and named at phone widths`, async ({ asUser }) => {
  test.setTimeout(90000);
  const page = await asUser(user);
  await page.goto('/search?q=Rivera');
  const search = page.locator('main form[role=search] button[type=submit]');
  for (const width of [1280, 390, 320]) {
    await page.setViewportSize({ width, height: 900 });
    await expect(search).toHaveAccessibleName('Search people');
    await expect(search).toHaveText('Search');
    expect(await search.evaluate(el => getComputedStyle(el).display !== 'none' && el.getBoundingClientRect().width >= 44)).toBe(true);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await expectAccessible(page, `People search for ${user} at ${width}px`);
  }
});
}

test('Configuration navigation reflows and identifies its active page', async ({ asUser }) => {
  test.setTimeout(120000);
  const page = await asUser('admin');
  for (const route of ['thresholds', 'settings', 'analytes']) {
    await page.goto('/config/' + route);
    for (const width of [1280, 390, 320]) {
      await page.setViewportSize({ width, height: 900 });
      const nav = page.getByRole('navigation', { name: 'Configuration', exact: true });
      await expect(nav.locator('a[aria-current=page]')).toHaveAttribute('href', '/config/' + route);
      for (const link of await nav.locator('a').all()) {
        await link.focus(); await expect(link).toBeFocused();
        const box = (await link.boundingBox())!;
        expect(box.x).toBeGreaterThanOrEqual(0);
        expect(box.x + box.width).toBeLessThanOrEqual(width + 1);
      }
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      if (width === 320) await expectAccessible(page, `Configuration ${route} at 320px`);
    }
  }
});

test('Intake shows the selected sender requirements and downloads only its configured header', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/intake');
  const chooser = page.locator('#intakeProfileId');
  const options = await chooser.locator('option').evaluateAll(nodes => nodes.map(node => ({ value: (node as HTMLOptionElement).value, label: node.textContent ?? '' })));
  const csv = options.find(option => /spreadsheet|csv|comma/i.test(option.label))!;
  const hl7 = options.find(option => /HL7|message/i.test(option.label))!;
  expect(csv).toBeDefined(); expect(hl7).toBeDefined();
  await chooser.selectOption(csv.value);
  await expect(page.locator('[data-intake-format]:visible')).toHaveCount(1);
  const instructions = page.locator(`[data-intake-format="${csv.value}"]`);
  await expect(instructions).toContainText('comma-separated');
  await instructions.locator('summary').click();
  const download = page.waitForEvent('download');
  await instructions.getByRole('link', { name: /Download this format/ }).click();
  expect((await download).suggestedFilename()).toBe('laboratory-import-header.txt');
  await chooser.selectOption(hl7.value);
  await expect(page.locator('[data-intake-format]:visible')).toHaveCount(1);
  await expect(page.locator(`[data-intake-format="${hl7.value}"]`)).toContainText('HL7 result-message file');
  await page.setViewportSize({ width: 320, height: 900 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await expectAccessible(page, 'Configured Intake at 320px');
});

test('Patient headings and named sections contain only their concise titles', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await person(page);
  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: 900 });
    await expect(page.locator('main h2 button, main h2 a')).toHaveCount(0);
    for (const name of ['Places', 'Relationships', 'Results', 'Employment', 'Exposure sources', 'Pregnancy']) {
      await expect(page.getByRole('heading', { name, exact: true, level: 2 })).toBeVisible();
      await expect(page.getByRole('region', { name, exact: true })).toBeAttached();
    }
  }
  await page.getByRole('button', { name: 'Add residence', exact: true }).click();
  await expect(page.locator('#places-form form')).toBeVisible();
  await page.locator('#places-form').getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Add residence', exact: true })).toBeFocused();
});

test('The last Match review record becomes a named, announced empty state with predictable focus', async ({ asUser }) => {
  const page = await asUser('supervisor');
  const row = '00000000-0000-0000-0000-000000000209';
  let decided = false;
  await page.route('**/api/v1/match-review**', route => {
    if (route.request().method() === 'POST') {
      decided = true;
      return route.fulfill({ json: { released: true, remainingCandidates: 0, message: 'Record reviewed.' } });
    }
    if (new URL(route.request().url()).pathname === '/api/v1/match-review')
      return route.fulfill({ json: { items: decided ? [] : [{ stagingRowId: row, patientName: 'Synthetic record', fileName: 'review.csv', rowNumber: 1 }], totalCount: decided ? 0 : 1, pageNumber: 1, pageSize: 200 } });
    return route.fulfill({ json: { stagingRowId: row, fileName: 'review.csv', senderName: 'Test laboratory', rowNumber: 1, incoming: [], candidates: [], reasonMessage: 'No candidates.' } });
  });
  await page.goto('/app/match-review'); await angularReady(page);
  const brokenIds = () => page.locator('app-root').evaluate(root => [...root.querySelectorAll('[aria-labelledby], [aria-describedby]')].flatMap(el =>
    ['aria-labelledby', 'aria-describedby'].flatMap(attr => (el.getAttribute(attr) ?? '').split(/\s+/).filter(id => id && !document.getElementById(id)))));
  expect(await brokenIds()).toEqual([]);
  await page.getByRole('button', { name: /^Create as a new person/ }).click();
  await expect(page.getByRole('heading', { name: 'Record review', exact: true })).toBeVisible();
  await expect(page.locator('app-root [role=status]')).toContainText('Nothing to review');
  await expect(page.locator('app-root h1')).toBeFocused();
  await expect(page.locator('#single-keys')).toHaveCount(0);
  expect(await brokenIds()).toEqual([]);
  for (const width of [390, 320]) {
    await page.setViewportSize({ width, height: 900 });
    await expectAccessible(page, `Empty Match review at ${width}px`);
  }
});

test('Unsaved result entry offers Stay and Leave, restores focus, and warns on browser navigation', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.setViewportSize({ width: 1280, height: 900 });
  const path = await person(page);
  await page.goto(path + '/results/new');
  await page.locator('#ValueText').fill('4.2');
  await peopleLink(page);
  const warning = page.getByRole('dialog', { name: 'Leave without saving?' });
  await expect(warning.getByRole('button', { name: 'Stay', exact: true })).toBeFocused();
  await expectAccessible(page, 'Unsaved result warning');
  await warning.getByRole('button', { name: 'Stay', exact: true }).click();
  await expect(page.locator('#ValueText')).toHaveValue('4.2');
  await expect(page.locator('#merit-nav a[href="/search"]')).toBeFocused();
  const native = page.waitForEvent('dialog');
  const navigation = page.goto('/search').catch(() => null);
  const prompt = await native;
  expect(prompt.type()).toBe('beforeunload'); await prompt.dismiss(); await navigation;
  await expect(page.locator('#ValueText')).toHaveValue('4.2');
  await peopleLink(page);
  await warning.getByRole('button', { name: 'Leave without saving', exact: true }).click();
  await expect(page).toHaveURL(/\/search$/);
});

test('Unsaved case notes protect sign-out and sibling drafts, while saves and reverted fields clear the warning', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.setViewportSize({ width: 1280, height: 900 });
  await casePage(page);
  const path = new URL(page.url()).pathname;
  const fresh = await page.request.get(path, { headers: { 'HX-Request': 'true', 'HX-Request-Type': 'partial' } });
  const freshBody = await fresh.text();
  await page.locator('#note').fill('Synthetic unsaved case note');
  await page.locator('header form[action="/account/signout"]').evaluate(form => (form as HTMLFormElement).requestSubmit());
  const warning = page.getByRole('dialog', { name: 'Leave without saving?' });
  await expect(warning).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.locator('#note')).toHaveValue('Synthetic unsaved case note');
  await page.locator('#case-close-details summary').click();
  await page.locator('#closeNote').fill('Keep this sibling draft');
  await page.route('**' + path + '/activities', route => route.fulfill({
    contentType: 'text/html', body: freshBody,
  }));
  await page.locator('#case-activity-form button[type=submit]').click();
  await expect(page.locator('#note')).toHaveValue('');
  await expect(page.locator('#closeNote')).toHaveValue('Keep this sibling draft');
  await peopleLink(page);
  await expect(warning).toBeVisible();
  await warning.getByRole('button', { name: 'Stay', exact: true }).click();
  await page.locator('#closeNote').fill('');
  await peopleLink(page);
  await expect(page).toHaveURL(/\/search$/);
});
