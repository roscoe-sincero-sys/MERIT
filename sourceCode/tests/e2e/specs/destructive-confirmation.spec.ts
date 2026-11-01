import { angularReady, expect, test } from './helpers';

test('held rejection cancels without writing and clears the reason only on success', async ({ asUser }) => {
  const page = await asUser('supervisor');
  const row = '00000000-0000-0000-0000-000000000108';
  let writes = 0; let success = false;
  await page.route('**/api/v1/held/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (route.request().method() === 'POST') {
      writes++;
      return route.fulfill(success ? { json: { updated: 1 } } : { status: 500, json: { title: 'Try again' } });
    }
    if (path.endsWith('/overview')) return route.fulfill({ json: { groups: [], totalCount: 1, batchFileName: null } });
    return route.fulfill({ json: { items: [{ stagingRowId: row, intakeBatchId: row, rowNumber: 1, fileName: 'held.csv', reported: {}, corrected: {},
      reasonCodes: ['missingDob'], reasons: 'Missing date', isUrgent: false, heldSinceUtc: '2026-01-01T00:00:00Z' }], totalCount: 1, pageNumber: 1, pageSize: 50 } });
  });
  await page.goto('/app/held'); await angularReady(page);
  await page.getByLabel('Select row 1', { exact: true }).focus(); await page.keyboard.press('Space');
  const reason = page.getByLabel('Reject reason', { exact: true });
  await reason.fill('Duplicate');
  const reject = page.getByRole('button', { name: 'Reject selected', exact: true });
  page.once('dialog', async d => { expect(d.message()).toContain('1 selected held record'); await d.dismiss(); });
  await reject.click(); expect(writes).toBe(0); await expect(reason).toHaveValue('Duplicate');
  page.once('dialog', d => d.accept()); await reject.click();
  await expect(page.getByRole('alert')).toBeVisible(); await expect(reason).toHaveValue('Duplicate');
  success = true;
  page.once('dialog', d => d.accept()); await reject.click();
  await expect(reason).toHaveValue(''); expect(writes).toBe(2);
});

test('plain admin forms confirm MFA reset and ending a grant before POST', async ({ browser }) => {
  const context = await browser.newContext({ storageState: '.auth/admin.json', ignoreHTTPSErrors: true });
  const page = await context.newPage();
  await page.goto('/admin/users');
  await page.getByRole('link', { name: /Avery Admin/ }).first().click();
  let writes = 0;
  await page.route('**/admin/users/**', async route => {
    if (route.request().method() !== 'POST') return route.continue();
    writes++; await route.fulfill({ status: 303, headers: { location: page.url() } });
  });
  for (const button of [page.getByRole('button', { name: /Reset MFA after/ }), page.getByRole('button', { name: /^End / }).first()]) {
    page.once('dialog', async d => { expect(d.message()).toContain('sessions'); await d.dismiss(); });
    await button.click(); expect(writes).toBe(0);
  }
  page.once('dialog', d => d.accept());
  await page.getByRole('button', { name: /Reset MFA after/ }).click();
  await expect.poll(() => writes).toBe(1);
  await context.close();
});

test('cluster removal names its case and cluster and cancellation sends nothing', async ({ asUser }) => {
  const page = await asUser('supervisor');
  let writes = 0;
  const id = '00000000-0000-0000-0000-000000000108';
  const cluster = { clusterId: id, title: 'Harbor review', clusterType: 'building', status: 'open', canEdit: true,
    windowStart: '2026-01-01', windowEnd: '2026-10-31', owningTenantName: 'Maryland', notes: '', tasks: [], rowVersion: 'AAAAAAAAAAA=',
    members: [{ exposureCaseId: id, personId: id, caseNumber: 'PB-2026-000123', personName: 'Example patient',
      analyteCode: 'PB', classification: 'confirmed', population: 'child', joinedDate: '2026-01-01', leftDate: null,
      latitude: null, longitude: null, highestValue: null, highestUnit: null, addressLabel: null }] };
  await page.route('**/api/v1/clusters/**', route => {
    if (route.request().method() === 'POST') writes++;
    return route.fulfill({ json: cluster });
  });
  await page.goto(`/app/clusters/${id}`); await angularReady(page);
  const button = page.getByRole('button', { name: /Remove from cluster/ });
  page.once('dialog', async d => { expect(d.message()).toContain('PB-2026-000123'); expect(d.message()).toContain('Harbor review'); await d.dismiss(); });
  await button.click(); expect(writes).toBe(0);
  page.once('dialog', d => d.accept()); await button.click();
  await expect.poll(() => writes).toBe(1);
});
