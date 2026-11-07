import { angularReady, expect, expectAccessible, test } from './helpers';
import { ISOLATED_DATA_ONLY, sql } from '../database';

test.skip(!!ISOLATED_DATA_ONLY, ISOLATED_DATA_ONLY ?? '');

test('a full-page predecessor arriving late is reconciled by one bounded browser retry', async ({ asUser }) => {
  const page = await asUser('supervisor');
  let predecessor: { body: any; token: string } | undefined;
  let attempts = 0;
  let successor = '';
  await page.route('**/api/v1/usage/visits', async route => {
    const body = route.request().postDataJSON();
    if (body.screenCode === 'intake') {
      predecessor = { body, token: route.request().headers()['x-xsrf-token'] };
      // Its Razor row exists, but the previous document's browser reconciliation is delayed.
      return route.fulfill({ status: 204 });
    }
    if (body.screenCode === 'queues') {
      attempts++;
      successor = body.visitId;
      if (attempts === 1) {
        const response = await route.fetch();
        expect(response.status()).toBe(202);
        expect(sql(`SELECT COUNT(*) FROM logs.screenVisit WHERE screenVisitId='${successor}' AND previousVisitId IS NULL`).toString()).toMatch(/\b1\b/);
        const late = await page.request.post('/api/v1/usage/visits', {
          data: predecessor!.body, headers: { 'X-XSRF-TOKEN': predecessor!.token },
        });
        expect(late.status()).toBe(204);
        return route.fulfill({ response });
      }
    }
    return route.continue();
  });
  await page.goto('/intake');
  await expect.poll(() => predecessor !== undefined).toBe(true);
  await page.goto('/app/queues'); await angularReady(page);
  await expect.poll(() => attempts).toBe(2);
  await expect.poll(() => sql(`SELECT COUNT(*) FROM logs.screenVisit WHERE screenVisitId='${successor}' AND previousVisitId='${predecessor!.body.visitId}'`).toString()).toMatch(/\b1\b/);
  await page.waitForLoadState('networkidle');
  expect(attempts).toBe(2);
});

test('an unresolved predecessor is retried at most once and rapid blur checkpoints are bounded', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.clock.install();
  let attempts = 0, checkpoints = 0;
  await page.route('**/api/v1/usage/visits', route => { attempts++; return route.fulfill({ status: 202 }); });
  page.on('request', request => { if (request.url().endsWith('/api/v1/usage/duration')) checkpoints++; });
  await page.goto('/intake');
  await expect.poll(() => attempts).toBe(2);
  await page.clock.fastForward(2000);
  expect(attempts).toBe(2);
  await page.bringToFront();
  await page.evaluate(() => { for (let n = 0; n < 20; n++) window.dispatchEvent(new Event('blur')); });
  await expect.poll(() => checkpoints).toBe(1);
  await page.clock.fastForward(1000);
  await page.evaluate(() => window.dispatchEvent(new Event('blur')));
  await page.waitForLoadState('networkidle');
  expect(checkpoints).toBe(1);
  // Terminal events flush changed focused time even inside the blur throttle interval.
  await page.evaluate(() => window.dispatchEvent(new Event('pagehide')));
  await expect.poll(() => checkpoints).toBe(2);
});

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
