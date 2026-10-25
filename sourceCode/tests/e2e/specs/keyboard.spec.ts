import { angularReady, expect, test } from './helpers';

// Key flows done with the keyboard alone, at tablet width.

test('the skip link is the first stop and moves focus to the main content', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/');
  await page.keyboard.press('Tab');
  await expect(page.locator(':focus')).toHaveText(/Skip to main content/);
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/#main-content$/);
});

test('search from the header and open a person', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/');
  await page.getByLabel('Search people, cases and accession numbers').focus();
  await page.keyboard.type('Rivera');
  await page.keyboard.press('Enter');
  await page.waitForURL(/\/search/);
  await page.locator('main table a[href^="/people/"]').first().focus();
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\/people\/[0-9a-f-]{36}$/);
  await expect(page.locator('h1')).toContainText('Rivera');
});

test('work queues: select a task with the keyboard and the bulk actions turn on', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/app/queues');
  await angularReady(page);
  const assign = page.getByRole('button', { name: 'Assign to me' });
  await expect(assign).toBeDisabled();
  await page.locator('table input[type=checkbox]').nth(1).focus();
  await page.keyboard.press('Space');
  await expect(assign).toBeEnabled();
});

test('match review moves through the queue with J and K', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/app/match-review');
  await angularReady(page);
  const current = page.locator('nav [aria-current="true"]');
  const count = await page.locator('nav ol li').count();
  test.skip(count < 2, 'Needs two records waiting for review (upload samples/chesapeake_match_review_sample.csv).');
  const first = await current.innerText();
  await page.locator('h1').focus();
  await page.keyboard.press('j');
  await expect(current).not.toHaveText(first);
  await page.keyboard.press('k');
  await expect(current).toHaveText(first);
});

test('reports: change the charted measure and open another report from the keyboard', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/app/reports');
  await angularReady(page);
  const chart = page.locator('merit-series-chart svg');
  const before = await chart.getAttribute('aria-label');
  await page.getByLabel('Chart', { exact: true }).focus();
  await page.keyboard.press('ArrowDown');
  await expect(chart).not.toHaveAttribute('aria-label', before ?? '');
  await page.getByRole('link', { name: 'Case timeliness' }).focus();
  await page.keyboard.press('Enter');
  await expect(page.locator('#report-title')).toHaveText('Case timeliness');
});

test('case map: zoom, pan and read a point with the keyboard', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/app/map');
  await angularReady(page);
  await page.locator('merit-point-map .plot').focus();
  await page.keyboard.press('+');
  await page.keyboard.press('ArrowLeft');
  await page.getByRole('button', { name: 'Fit all points' }).focus();
  await page.keyboard.press('Enter');
  await page.locator('circle.point').first().focus();
  await expect(page.locator('merit-point-map .tooltip')).toBeVisible();
});

test('a county case manager cannot open a case another tenant owns', async ({ asUser }) => {
  const city = await asUser('cmbalt');
  const mine = await (await city.request.get('/api/v1/cases/map?openOnly=false')).json() as { exposureCaseId: string }[];
  const state = await asUser('supervisor');
  const all = await (await state.request.get('/api/v1/cases/map?openOnly=false')).json() as { exposureCaseId: string }[];
  const hidden = all.find(c => !mine.some(x => x.exposureCaseId === c.exposureCaseId));
  test.skip(!hidden, 'Every demo case is visible to Baltimore City.');
  const response = await city.goto(`/cases/${hidden!.exposureCaseId}`);
  expect(response?.status()).toBe(404);
});
