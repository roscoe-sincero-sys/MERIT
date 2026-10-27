import { angularReady, expect, test } from './helpers';

// Layout checks at the narrow desktop width the user guides are captured at, and at phone width (390 px).

test('intake: a long file name wraps inside its batch card', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.setViewportSize({ width: 1040, height: 900 });
  await page.goto('/intake');
  const heading = page.locator('#batches .usa-card__heading').first();
  await expect(heading).toBeVisible();
  // A name with no spaces, longer than any card is wide (issue #16).
  await heading.evaluate(h => { h.textContent = 'chesapeake_reference_laboratory_match_review_sample_2026_10.csv'; });
  const overflowing = await page.locator('#batches *').evaluateAll(els =>
    els.filter(e => e.scrollWidth > e.clientWidth + 1).map(e => `${e.tagName}.${e.className}`));
  expect(overflowing).toEqual([]);
});

test('reporters: the filter box is a plain input, not a USWDS search box', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.setViewportSize({ width: 1040, height: 900 });
  await page.goto('/reporters');
  const box = page.getByLabel('Name, CLIA number or NPI');
  const kind = page.locator('#kind');
  // Issue #15: the USWDS search-box styles made it 32 px tall, floated left, with no vertical padding, beside a 40 px list.
  const [b, k] = [await box.boundingBox(), await kind.boundingBox()];
  expect(Math.abs(b!.height - k!.height)).toBeLessThanOrEqual(1);
  const style = await box.evaluate(e => { const s = getComputedStyle(e); return { float: s.float, paddingTop: parseFloat(s.paddingTop) }; });
  expect(style.float).toBe('none');
  expect(style.paddingTop).toBeGreaterThan(0);
});

test('reporters: the add form uses the width and shows only the chosen kind\'s fields', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.setViewportSize({ width: 1040, height: 900 });
  await page.goto('/reporters');
  // Issue #18: .usa-form capped the grid at 320 px, one column.
  const form = page.locator('form.merit-reporter-form');
  expect((await form.boundingBox())!.width).toBeGreaterThan(600);
  expect((await form.evaluate(f => getComputedStyle(f).gridTemplateColumns)).split(' ').length).toBeGreaterThan(1);
  await expect(page.locator('#Clia')).toBeVisible();
  await expect(page.locator('#LastName')).toBeHidden();
  await page.locator('#Kind').selectOption('provider');
  await expect(page.locator('#Clia')).toBeHidden();
  await expect(page.locator('#LastName')).toBeVisible();
});

test('extracts: a Download button keeps its padding and fits its label', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.setViewportSize({ width: 1040, height: 900 });
  await page.goto('/extracts');
  // Issue #17: as the card body's last child, USWDS removed the button's bottom padding and its second line sat on the border.
  const buttons = await page.locator('#extract-runs a.usa-button').evaluateAll(as => as.map(a => ({
    padding: parseFloat(getComputedStyle(a).paddingBottom), overflows: a.scrollHeight > a.clientHeight + 1 })));
  expect(buttons.length).toBeGreaterThan(0);
  for (const b of buttons) {
    expect(b.padding).toBeGreaterThan(0);
    expect(b.overflows).toBe(false);
  }
});

test('case numbers and dates stay on one line in lists', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.setViewportSize({ width: 1040, height: 900 });
  // Issue #39: "PB-2026- / 000004" and "2026-11- / 01" broke across lines while text columns had room.
  for (const [url, selector] of [['/cases', 'main table a[href^="/cases/"]'], ['/app/queues', 'td.nowrap']] as const) {
    await page.goto(url);
    await page.locator(selector).first().waitFor();
    // The text's own lines: distinct line tops of the rectangles its characters occupy.
    const broken = await page.locator(selector).evaluateAll(els => els.filter(e => {
      const range = document.createRange();
      range.selectNodeContents(e);
      return new Set([...range.getClientRects()].map(r => Math.round(r.top))).size > 1;
    }).map(e => e.textContent!.trim()));
    expect(broken, url).toEqual([]);
  }
  // One date format: "Oct 27, 2026", not 2026-10-27.
  await page.goto('/app/queues');
  await page.locator('td.nowrap').first().waitFor();
  expect(await page.locator('main table').innerText()).not.toMatch(/\b20\d\d-\d\d-\d\d\b/);
});

test('phone: the menu folds behind a button and search has its own row', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/');
  // Issue #47: the box was about 20 px wide beside its button, and the menu wrapped over four lines above the page.
  const menu = page.getByRole('button', { name: 'Menu' });
  const nav = page.getByRole('navigation', { name: 'Primary' });
  await expect(menu).toHaveAttribute('aria-expanded', 'false');
  await expect(nav).toBeHidden();
  expect((await page.locator('#header-search').boundingBox())!.width).toBeGreaterThan(200);
  expect((await page.locator('header.merit-header').boundingBox())!.height).toBeLessThan(160);
  await menu.click();
  await expect(menu).toHaveAttribute('aria-expanded', 'true');
  await expect(nav.getByRole('link', { name: 'Work queues' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(nav).toBeHidden();
  await expect(menu).toBeFocused();
});

test('phone: no page scrolls sideways', async ({ asUser }) => {
  test.setTimeout(120000);
  const page = await asUser('supervisor');
  await page.setViewportSize({ width: 390, height: 844 });
  // Issue #47: Work queues, Clusters, a cluster and Reports were wider than the screen; tables scroll in their own regions.
  await page.goto('/app/clusters');
  await angularReady(page);
  const cluster = await page.locator('main a[href^="/app/clusters/"]').first().getAttribute('href');
  const pages = ['/', '/app/queues', '/app/held', '/app/match-review', '/app/results', '/cases', '/app/clusters', '/app/map',
    '/app/reports', '/investigations', '/intake', '/extracts', '/reporters', '/search?q=Rivera', ...(cluster ? [cluster] : [])];
  const wide: string[] = [];
  for (const url of pages) {
    await page.goto(url);
    if (url.startsWith('/app')) {
      await angularReady(page);
      await page.locator('main h1').first().waitFor();
    }
    const [scroll, client] = await page.evaluate(() => [document.documentElement.scrollWidth, document.documentElement.clientWidth]);
    if (scroll > client) wide.push(`${url} is ${scroll} px`);
  }
  expect(wide).toEqual([]);
});

test('timeline: axis labels name years, months or days by the span shown', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.setViewportSize({ width: 1040, height: 900 });
  const results = await (await page.request.get('/api/v1/results?pageSize=20')).json() as { items: { personId: string | null }[] };
  const personId = results.items.find(r => r.personId)!.personId!;
  await page.goto(`/app/people/${personId}/timeline`);
  await angularReady(page);
  // Issue #49: six years were labelled "May 21" to "May 26", a two-digit year that reads as six days in May.
  const range = page.locator('.controls .merit-meta');
  const check = async () => {
    const [from, to] = (await range.innerText()).split(' to ').map(t => Date.parse(t));
    const months = (to - from) / (30 * 86_400_000);
    const labels = await page.locator('.tick-label').allTextContents();
    expect(labels.length).toBeGreaterThan(1);
    const shape = months > 36 ? /^\d{4}$/ : months > 2 ? /^[A-Z][a-z]{2} \d{4}$/ : /^[A-Z][a-z]{2} \d{1,2}$/;
    for (const l of labels) expect(l, `${months.toFixed(1)} months`).toMatch(shape);
  };
  await check();
  // Zoom in to the shortest span: day labels.
  for (let i = 0; i < 8; i++) await page.getByRole('button', { name: 'Zoom in' }).click();
  await check();
  expect(await page.locator('.tick-label').first().textContent()).toMatch(/^[A-Z][a-z]{2} \d{1,2}$/);
});

test('maps: county outlines give context, and the cluster map stays small', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.setViewportSize({ width: 1280, height: 900 });
  // Issue #48: points floated on a blank grid, and the cluster page's map was 760 px tall with two points in it.
  await page.goto('/app/map');
  await angularReady(page);
  await expect(page.locator('merit-point-map path.area').first()).toBeAttached();
  expect(await page.locator('merit-point-map path.area').count()).toBeGreaterThan(1);
  expect(await page.locator('merit-point-map text.area-label').count()).toBeGreaterThan(0);
  // A point still names its case on focus.
  await page.locator('circle.point').first().focus();
  await expect(page.locator('merit-point-map .tooltip')).toContainText(/PB-\d{4}-\d+/);

  await page.goto('/app/clusters');
  await angularReady(page);
  const cluster = page.locator('main a[href^="/app/clusters/"]').first();
  test.skip(await cluster.count() === 0, 'Needs a cluster (the demo data has some).');
  await cluster.click();
  await angularReady(page);
  const map = page.locator('merit-point-map .plot');
  await expect(map).toBeVisible();
  expect((await map.boundingBox())!.height).toBeLessThanOrEqual(322);
  await expect(page.locator('merit-point-map path.area').first()).toBeAttached();
  // Its open tasks open their work queue.
  for (const href of await page.locator('main h2:has-text("Open tasks") + ul a').evaluateAll(as => as.map(a => a.getAttribute('href')))) {
    expect(href).toMatch(/^\/app\/queues\?queue=[a-zA-Z]+$/);
  }
});
