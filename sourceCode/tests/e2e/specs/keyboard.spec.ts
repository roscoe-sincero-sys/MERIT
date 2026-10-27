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
  // Assigning to someone else waits until a person is chosen: the list starts on no one (issue #41).
  const assignTo = page.getByRole('button', { name: 'Assign', exact: true });
  const person = page.getByLabel('Assign to');
  await expect(person).toHaveValue('');
  await expect(assignTo).toBeDisabled();
  await person.selectOption({ index: 1 });
  await expect(assignTo).toBeEnabled();
});

test('a heading focused by script shows the ring for keyboard users only', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/app/queues');
  await angularReady(page);
  // Issue #51: USWDS rings every :focus, so a clicked or script-focused heading was boxed for mouse users.
  const heading = page.locator('h1');
  const outline = () => heading.evaluate(h => getComputedStyle(h).outlineStyle);
  await heading.click();
  await expect(heading).toBeFocused();
  expect(await outline()).toBe('none');
  await page.keyboard.press('Tab');
  await heading.evaluate(h => (h as HTMLElement).focus());
  expect(await outline()).not.toBe('none');
});

test('held records: a row held for an uncertain match leads to Match review, not Release', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/app/held');
  await angularReady(page);
  // Issue #42: releasing reruns matching, which holds the row again; the decision is made on Match review.
  const group = page.locator('.groups button', { hasText: /^\s*Uncertain person match/ }).first();
  test.skip(await group.count() === 0, 'Needs a row held for an uncertain match (the demo data has some).');
  await group.click();
  await page.locator('table tbody input[type=checkbox]').first().waitFor();
  await page.locator('table tbody input[type=checkbox]').first().focus();
  await page.keyboard.press('Space');
  await expect(page.getByRole('button', { name: 'Release selected' })).toBeDisabled();
  await expect(page.locator('#release-match-note')).toContainText('Decide the match first');
  const review = page.getByRole('link', { name: 'Review the match' }).first();
  const row = new URL(await review.getAttribute('href') ?? '', 'https://x').searchParams.get('row');
  // The record it was sent to is the one Match review opens.
  const opened = page.waitForRequest(r => r.url().endsWith(`/api/v1/match-review/${row}`));
  await review.focus();
  await page.keyboard.press('Enter');
  await opened;
  expect(new URL(page.url()).pathname).toBe('/app/match-review');
  expect(new URL(page.url()).searchParams.get('row')).toBe(row);
  await angularReady(page);
  await expect(page.locator('nav ol li [aria-current="true"]')).toHaveCount(1);
});

test('match review moves through the queue with J and K', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/app/match-review');
  await angularReady(page);
  const items = page.locator('nav ol li');
  const count = await items.count();
  test.skip(count < 2, 'Needs two records waiting for review (upload samples/chesapeake_match_review_sample.csv).');
  // Compare positions, not text: a file uploaded twice puts identical records next to each other.
  const position = () => items.evaluateAll(lis => lis.findIndex(li => li.querySelector('[aria-current="true"]') !== null));
  const first = await position();
  await page.locator('h1').focus();
  await page.keyboard.press('j');
  await expect.poll(position).toBe(first + 1);
  await page.keyboard.press('k');
  await expect.poll(position).toBe(first);
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

test('adding a tenant: choosing the parent with the keyboard changes the kinds offered', async ({ asUser }) => {
  const page = await asUser('admin');
  await page.goto('/admin/tenants');
  const kind = page.getByLabel('Kind of tenant');
  await expect(kind.locator('option')).toHaveText([/Program/, /Local jurisdiction/]);
  await expect(page.getByLabel('Counties it serves')).toBeVisible();
  await page.getByLabel('Goes under').focus();
  await page.keyboard.press('ArrowDown');
  await expect(page.getByLabel('Counties it serves')).toBeVisible();
  await expect(page.getByLabel('Postal code')).toHaveCount(0);
});

test('reporter import: check the sample file and see the rows to fix', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/reporters/import');
  await page.getByLabel(/CSV file/).setInputFiles('../../samples/registry_npi_clia_sample.csv');
  await page.getByRole('button', { name: 'Check the file' }).focus();
  await page.keyboard.press('Enter');
  await expect(page.getByText('row(s) need attention')).toBeVisible();
  await expect(page.locator('table tbody tr')).toHaveCount(4);
  await expect(page.getByRole('button', { name: /Import 4 ready row/ })).toBeVisible();
});
