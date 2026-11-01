import { angularReady, expect, test } from './helpers';

test('report charts aggregate counts only, format months, and block dirty-filter exports', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.route('**/api/v1/reports', route => route.fulfill({ json: [{ code: 'integrity', title: 'Integrity report' }] }));
  await page.route('**/api/v1/reports/integrity**', route => {
    const q = new URL(route.request().url()).searchParams;
    return route.fulfill({ json: { code: 'integrity', title: 'Integrity report', description: 'Check aggregation', usesAnalyte: false, canExport: true,
      fromDate: q.get('from') || '2026-01-01', toDate: q.get('to') || '2026-10-31', analyteId: null,
      columns: [{ key: 'month', label: 'Month', kind: 'month' }, { key: 'testCount', label: 'Tests', kind: 'number' },
        { key: 'pct', label: 'On time', kind: 'percent' }, { key: 'median', label: 'Median', kind: 'number' }],
      rows: [['2026-07', 3, 80, 4], ['2026-07', 4, 90, 6], ['2026-08', 2, 30, 8]],
      chart: { kind: 'bar', x: 'month', group: null, measures: ['testCount', 'pct', 'median'] } } });
  });
  await page.goto('/app/reports?code=integrity'); await angularReady(page);
  await expect(page.locator('merit-series-chart .bar[aria-label="Jul 2026, Tests: 7"]')).toBeVisible();
  await page.locator('#r-measure').selectOption('pct');
  await expect(page.locator('merit-series-chart .bar')).toHaveCount(1);
  await expect(page.getByRole('status').filter({ hasText: 'cannot be summed' })).toBeVisible();
  await page.locator('#r-measure').selectOption('median');
  await expect(page.locator('merit-series-chart .bar')).toHaveCount(1);
  const csv = page.getByText('Export CSV', { exact: true });
  await expect(csv).toHaveAttribute('href', /from=2026-01-01/);
  await page.locator('#r-from').fill('2026-02-01');
  await expect(csv).not.toHaveAttribute('href');
  await expect(csv).toHaveAttribute('aria-disabled', 'true');
  await expect(page.getByText('Apply filter changes and wait for the report before exporting.')).toBeVisible();
  await page.getByRole('button', { name: 'Apply', exact: true }).click();
  await expect(csv).toHaveAttribute('href', /from=2026-02-01/);
  await expect(page.getByText('Export Excel', { exact: true })).toHaveAttribute('href', /from=2026-02-01/);
});
