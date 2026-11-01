import { angularReady, expect, expectAccessible, test } from './helpers';

const id = (n: number) => `00000000-0000-0000-0000-${String(n).padStart(12, '0')}`;

test('classification shapes match the legend and edge tooltips stay inside the map', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.route('**/api/v1/cases/map**', route => route.fulfill({ json: ['confirmed', 'suspect', 'notACase'].map((classification, i) => ({
    exposureCaseId: id(i + 1), personId: id(i + 10), caseNumber: `PB-2026-00000${i + 1}`, personName: 'Example patient',
    classification, latitude: 39 + i * .2, longitude: -76 - i * .2, openedDate: '2026-01-01',
    analyteCode: 'PB', population: 'child', addressLabel: 'Example address', inOpenCluster: false })) }));
  await page.goto('/app/map'); await angularReady(page);
  const points = page.locator('merit-point-map .point');
  await expect(points).toHaveCount(3);
  const shapes = await points.evaluateAll(elements => elements.map(e => e.getAttribute('d')));
  expect(new Set(shapes).size).toBe(3);
  expect(await page.locator('.legend-marker path').evaluateAll(elements => elements.map(e => e.getAttribute('d')))).toEqual(shapes);
  for (let i = 0; i < 3; i++) {
    await points.nth(i).focus();
    const tooltip = page.locator('merit-point-map .tooltip');
    await expect(tooltip).toBeVisible();
    await expect.poll(async () => {
      const tip = await tooltip.boundingBox(); const plot = await page.locator('merit-point-map .plot').boundingBox();
      return !!tip && !!plot && tip.x >= plot.x && tip.y >= plot.y && tip.x + tip.width <= plot.x + plot.width && tip.y + tip.height <= plot.y + plot.height;
    }).toBe(true);
  }
  await points.first().focus(); await page.keyboard.press('Space');
  await expect(page.locator('.usa-summary-box')).toContainText('PB-2026-000001');
  await expectAccessible(page, 'map classification shapes and Space activation');
});

test('cluster map points open the corresponding case with Space', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.route('**/api/v1/clusters/*', route => route.fulfill({ json: { clusterId: id(1), title: 'Map cluster', clusterType: 'building',
    status: 'open', windowStart: '2026-01-01', windowEnd: '2026-10-31', canEdit: false, notes: '', owningTenantName: 'Maryland', tasks: [],
    members: [{ exposureCaseId: id(2), personId: id(3), caseNumber: 'PB-2026-000002', personName: 'Example patient', classification: 'confirmed',
      analyteCode: 'PB', population: 'child', joinedDate: '2026-01-01', latitude: 39.2, longitude: -76.5 }] } }));
  await page.route(`**/cases/${id(2)}`, route => route.fulfill({ contentType: 'text/html', body: '<h1>Case details</h1>' }));
  await page.goto(`/app/clusters/${id(1)}`); await angularReady(page);
  await page.locator('merit-point-map .point').focus(); await page.keyboard.press('Space');
  await expect(page).toHaveURL(new RegExp(`/cases/${id(2)}$`));
});

test('timeline result and interval buttons activate with Space without scrolling', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.route('**/api/v1/people/*/timeline', route => route.fulfill({ json: { personId: id(1), personName: 'Example patient', events: [
    { kind: 'result', date: '2026-01-01', label: 'Lead result', value: 5, unit: 'ug/dL', sampleType: 'venous' },
    { kind: 'place', date: '2026-01-01', endDate: '2026-02-01', label: 'Primary residence' } ] } }));
  await page.goto(`/app/people/${id(1)}/timeline`); await angularReady(page);
  for (const label of ['Lead result', 'Primary residence']) {
    await page.getByRole('button', { name: new RegExp(label) }).focus();
    const before = await page.evaluate(() => window.scrollY);
    await page.keyboard.press('Space');
    await expect(page.getByRole('status')).toContainText(label);
    expect(await page.evaluate(() => window.scrollY)).toBe(before);
  }
});
