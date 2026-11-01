import { angularReady, expect, test, expectAccessible } from './helpers';

test('create a new person remains explicit and available with candidates and clinical context', async ({ asUser }) => {
  const page = await asUser('supervisor'); const row = '00000000-0000-0000-0000-000000000088'; const candidate = '00000000-0000-0000-0000-000000000188';
  let released = false; let decision: unknown;
  const json = (body: unknown) => ({ contentType: 'application/json', body: JSON.stringify(body) });
  await page.route('**/api/v1/match-review**', route => {
    const path = new URL(route.request().url()).pathname;
    if (route.request().method() === 'POST') { decision = route.request().postDataJSON(); released = true; return route.fulfill(json({ released: true, remainingCandidates: 0, message: 'New person selected.' })); }
    if (path === '/api/v1/match-review') return route.fulfill(json({ items: released ? [] : [{ stagingRowId: row, patientName: 'Review sample', fileName: 'review.csv', rowNumber: 1, isEscalated: false }], totalCount: released ? 0 : 1, pageNumber: 1, pageSize: 200 }));
    return route.fulfill(json({ stagingRowId: row, fileName: 'review.csv', rowNumber: 1, isUrgent: false, isEscalated: false, reasonMessage: 'Uncertain match',
      incoming: [{ key: 'loinc', label: 'Test code', value: '5671-3' }, { key: 'value', label: 'Result value', value: '19.5' }, { key: 'unit', label: 'Unit', value: 'ug/dL' }, { key: 'sampleType', label: 'Sample type', value: 'venous' }],
      candidates: [{ personId: candidate, rank: 1, personName: 'Potential candidate', fields: [], warnings: [], places: [], results: [], resultCount: 0 }] }));
  });
  await page.goto('/app/match-review'); await angularReady(page);
  await expect(page.getByLabel('Incoming clinical report')).toContainText('19.5');
  await expect(page.getByRole('button', { name: 'Create as a new person (Alt+Shift+C)', exact: true })).toBeEnabled();
  expect(decision).toBeUndefined();
  await page.locator('app-root h1').focus(); await page.keyboard.press('Alt+Shift+c');
  await expect.poll(() => decision).toMatchObject({ decision: 'newPerson', personId: null });
});

test('held inspection stays independent of bulk selection and Save and release targets the inspected row', async ({ asUser }) => {
  const page = await asUser('supervisor'); const id = (n: number) => `00000000-0000-0000-0000-${String(n).padStart(12, '0')}`;
  const row = (n: number) => ({ stagingRowId: id(n), intakeBatchId: id(3), rowNumber: n, fileName: 'inspect.csv', reported: { lastName: 'Sample', value: 'bad' }, corrected: {}, reasons: 'Invalid value', reasonCodes: ['missingResultValue'], heldSinceUtc: '2026-10-31T12:00:00Z' });
  let corrected = ''; let released: string[] = [];
  const json = (body: unknown) => ({ contentType: 'application/json', body: JSON.stringify(body) });
  await page.route('**/api/v1/held/**', route => {
    const path = new URL(route.request().url()).pathname;
    if (route.request().method() === 'PUT') { corrected = path; return route.fulfill({ status: 204, body: '' }); }
    if (path.endsWith('/release')) { released = route.request().postDataJSON().stagingRowIds; return route.fulfill(json({ updated: 1 })); }
    if (path.endsWith('/overview')) return route.fulfill(json({ groups: [], totalCount: 2, batchFileName: null }));
    return route.fulfill(json({ items: [row(1), row(2)], totalCount: 2, pageNumber: 1, pageSize: 50 }));
  });
  await page.goto('/app/held'); await angularReady(page);
  await page.getByRole('button', { name: 'Inspect / edit', exact: true }).first().click();
  await expect(page.locator('#row-detail')).toBeFocused();
  await page.locator('#fix-value').fill('2.5');
  await page.getByLabel('Select all on this page', { exact: true }).focus(); await page.keyboard.press('Space');
  await expect(page.locator('#row-detail')).toContainText('Row 1'); await expect(page.locator('#fix-value')).toHaveValue('2.5');
  await expect(page.locator('#fix-dob')).toHaveAttribute('type', 'date');
  await page.getByRole('button', { name: 'Save and release this row', exact: true }).click();
  await expect.poll(() => released).toEqual([id(1)]); expect(corrected).toContain(`/rows/${id(1)}/correction`);
});

test('tablet menu stays compact and People starts with duplicate search', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/');
  expect((await page.locator('header.merit-header').boundingBox())!.height).toBeLessThan(170);
  const menu = page.getByRole('button', { name: 'Menu', exact: true });
  await expect(menu).toBeVisible(); await menu.click();
  await page.getByRole('navigation', { name: 'Primary' }).getByRole('link', { name: 'People', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Find or add a person', exact: true })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Add a person', exact: true })).toHaveCount(0);
  await page.locator('#search-q').fill('Rivera');
  await expect(page.getByRole('link', { name: 'Add a person', exact: true })).toBeVisible();
  await expect(page.locator('#search-results')).toContainText('Mia');
});

test('map filters survive reload and browser history, with one product title suffix', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/app/map'); await angularReady(page);
  await expect(page).toHaveTitle('Case map - MERIT');
  await page.locator('#m-from').fill('2026-01-01');
  await page.getByRole('button', { name: 'Apply', exact: true }).click();
  await expect(page).toHaveURL(/from=2026-01-01/);
  await page.reload(); await angularReady(page);
  await expect(page.locator('#m-from')).toHaveValue('2026-01-01');
  await page.locator('#m-from').fill('2026-02-01');
  await page.getByRole('button', { name: 'Apply', exact: true }).click(); await expect(page).toHaveURL(/from=2026-02-01/);
  await page.goBack(); await expect(page.locator('#m-from')).toHaveValue('2026-01-01');
});

test('client navigation updates current location, title, focus and return filters', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/app/clusters?status=open'); await angularReady(page);
  await page.locator('main table a').first().click(); await angularReady(page);
  await expect(page).toHaveTitle('Cluster - MERIT');
  await expect(page.locator('app-root h1')).toBeFocused();
  await expect(page.locator('.merit-nav a[href="/app/clusters"]')).toHaveAttribute('aria-current', 'page');
  await expect(page.locator('#live-region')).toContainText('MERIT');
  await page.getByRole('link', { name: 'Return to clusters', exact: true }).click();
  await expect(page).toHaveURL(/\/app\/clusters\?status=open/);
  await expect(page.locator('app-root h1')).toBeFocused();
  await expect(page).toHaveTitle('Clusters - MERIT');
});

test('unknown client route explains the missing page without loading queues', async ({ asUser }) => {
  const page = await asUser('supervisor'); let queueReads = 0;
  await page.route('**/api/v1/tasks**', route => { queueReads++; return route.continue(); });
  await page.goto('/app/does-not-exist'); await angularReady(page);
  await expect(page.getByRole('heading', { name: 'Page not found', exact: true })).toBeVisible();
  await expect(page).toHaveTitle('Page not found - MERIT'); expect(queueReads).toBe(0);
});

test('sample types constrain units and allow visible sample correction', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/investigations');
  await page.locator('table a[href^="/investigations/"]').first().click();
  await page.locator('#sampleType').selectOption('xrfPaint');
  await expect(page.locator('#unit')).toHaveValue('mg/cm2');
  await expect(page.locator('#unit option')).toHaveText(['mg/cm2']);
  await expect(page.locator('#surface')).toBeDisabled();
  await page.locator('#sampleType').selectOption('dustWipe');
  await expect(page.locator('#unit')).toHaveValue('ug/ft2'); await expect(page.locator('#surface')).toBeEnabled();
  await page.getByRole('button', { name: 'Edit reading', exact: true }).first().click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await expect(page.locator('#se-value')).not.toHaveValue('');
  await expectAccessible(page, 'sample correction dialog');
});

test('clinical reference navigation is labelled for a case manager', async ({ asUser }) => {
  const page = await asUser('cmbalt'); await page.goto('/'); await page.getByRole('button', { name: 'Menu', exact: true }).click();
  await expect(page.getByRole('navigation', { name: 'Primary' }).getByRole('link', { name: 'Reference levels', exact: true })).toBeVisible();
});
