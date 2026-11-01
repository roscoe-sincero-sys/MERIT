import { angularReady, expect, expectAccessible, test } from './helpers';

const id = '00000000-0000-0000-0000-000000000106';
const cluster = { clusterId: id, title: 'Review cluster', clusterType: 'building', status: 'open',
  windowStart: '2026-01-01', windowEnd: '2026-10-31', notes: '', owningTenantName: 'Maryland',
  rowVersion: 'AAAAAAAAAAA=', members: [], tasks: [], canEdit: true };

test('failed adds preserve the case number; keyboard search selection resets only after success', async ({ asUser }) => {
  const page = await asUser('supervisor');
  let succeed = false;
  let submitted = '';
  let finish!: () => void;
  const gate = new Promise<void>(resolve => { finish = resolve; });
  await page.route(`**/api/v1/clusters/${id}`, route => route.fulfill({ json: cluster }));
  await page.route('**/api/v1/cases/picker**', route => route.fulfill({ json: [{ caseNumber: 'PB-2026-000123', personName: 'Mia Rivera' }] }));
  await page.route(`**/api/v1/clusters/${id}/members`, async route => {
    submitted = route.request().postDataJSON().caseNumber;
    if (!succeed) return route.fulfill({ status: 404, json: { title: 'Not found', detail: 'Check the case number.' } });
    await gate;
    await route.fulfill({ json: cluster });
  });
  await page.goto(`/app/clusters/${id}`);
  await angularReady(page);
  const input = page.locator('#cl-case');
  await input.fill('PB-2026-000124');
  await page.getByRole('button', { name: 'Add case', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Check the case number');
  await expect(input).toHaveValue('PB-2026-000124');
  await input.fill('Rivera');
  const option = page.getByRole('button', { name: 'PB-2026-000123: Mia Rivera', exact: true });
  await expect(option).toBeVisible();
  await option.focus();
  await page.keyboard.press('Enter');
  await expect(input).toHaveValue('PB-2026-000123');
  await expect(input).toBeFocused();
  succeed = true;
  await page.getByRole('button', { name: 'Add case', exact: true }).click();
  await expect(input).toBeDisabled();
  await expect(input).toHaveValue('PB-2026-000123');
  finish();
  await expect(input).toHaveValue('');
  expect(submitted).toBe('PB-2026-000123');
  await expectAccessible(page, 'cluster case picker');
});
