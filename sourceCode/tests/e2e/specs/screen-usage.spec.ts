import { angularReady, expect, expectAccessible, test } from './helpers';
import { ISOLATED_DATA_ONLY, sql } from '../database';

test.skip(!!ISOLATED_DATA_ONLY, ISOLATED_DATA_ONLY ?? '');

test('Razor and router visits reconcile, preserve predecessors and omit queries', async ({ asUser }) => {
  const page = await asUser('supervisor');
  const payloads: { visitId: string; screenCode: string; previousVisitId: string | null; navigationType: string }[] = [];
  page.on('request', request => {
    if (request.url().endsWith('/api/v1/usage/visits')) payloads.push(request.postDataJSON());
  });
  await page.goto('/intake?usage_test=not-collected');
  await expect.poll(() => payloads.length).toBe(1);
  const first = payloads[0];
  expect(first.screenCode).toBe('intake');
  expect(JSON.stringify(first)).not.toContain('not-collected');
  await expect.poll(() => sql(`SELECT COUNT(*) FROM logs.screenVisit WHERE screenVisitId='${first.visitId}' AND tabId IS NOT NULL`).toString()).toMatch(/\b1\b/);
  expect(sql(`SELECT COUNT(*) FROM logs.screenVisit WHERE screenVisitId='${first.visitId}'`).toString()).toMatch(/\b1\b/);

  await page.goto('/app/queues'); await angularReady(page);
  await expect.poll(() => payloads.length).toBe(2);
  expect(payloads[1].screenCode).toBe('queues');
  expect(payloads[1].previousVisitId).toBe(first.visitId);
  // A changed query is the same router screen; the screen event must not fire again.
  await page.evaluate(() => {
    // Native back/forward traversals also pass through the router's popstate handling.
    history.pushState({}, '', '/app/queues?usage_test=not-collected');
    window.dispatchEvent(new PopStateEvent('popstate', { state: history.state }));
  });
  await page.waitForLoadState('networkidle');
  expect(payloads).toHaveLength(2);
  // Exercise Angular navigation in the same host without creating another Razor response.
  await page.evaluate(() => {
    history.pushState({}, '', '/app/held');
    window.dispatchEvent(new PopStateEvent('popstate', { state: history.state }));
  });
  await expect.poll(() => payloads.length).toBe(3);
  expect(payloads[2].screenCode).toBe('held');
  await page.goBack();
  await expect.poll(() => payloads.length).toBe(4);
  expect(payloads[3].screenCode).toBe('queues');
  expect(payloads[3].navigationType).toBe('back');
  // A focus checkpoint posts only cumulative duration, an opaque visit ID and the antiforgery token.
  const duration = page.waitForRequest(r => r.url().endsWith('/api/v1/usage/duration'));
  await page.evaluate(() => window.dispatchEvent(new Event('blur')));
  expect((await duration).postData()).not.toContain('not-collected');
});

test('Usage reports, session sequence and CSV remain accessible at desktop and mobile widths', async ({ asUser }) => {
  test.setTimeout(120000); // Four axe runs over up to 100 retained sessions can exceed the default 30 seconds.
  const page = await asUser('supervisor');
  await page.goto('/intake');
  await page.goto('/admin/usage');
  await expect(page.getByRole('heading', { name: 'Usage', exact: true })).toBeVisible();
  await expect(page.locator('#usage-days')).toHaveValue('30');
  for (const width of [1280, 390, 320]) {
    await page.setViewportSize({ width, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth)).toBe(false);
    await expectAccessible(page, `Usage at ${width}px`);
  }
  const download = page.waitForEvent('download');
  await page.getByRole('link', { name: 'Download summary CSV' }).click();
  expect((await download).suggestedFilename()).toBe('merit-usage-summary.csv');
  await page.getByRole('link', { name: /^View session/ }).first().click();
  await expect(page.getByRole('heading', { name: 'Usage session', exact: true })).toBeVisible();
  await expectAccessible(page, 'Usage session');
  expect(await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth)).toBe(false);
});
