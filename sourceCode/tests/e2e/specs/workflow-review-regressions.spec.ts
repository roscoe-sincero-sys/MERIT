import { angularReady, expect, test } from './helpers';

const id = '00000000-0000-0000-0000-000000000122';
const json = (body: unknown) => ({ contentType: 'application/json', body: JSON.stringify(body) });
const task = { workTaskId: id, queueCode: 'caseFollowUp', title: 'Review task', detail: null, isUrgent: false,
  dueDate: '2026-11-01', taskStatus: 'open', createdUtc: '2026-10-31T12:00:00Z', personId: null, personName: null,
  exposureCaseId: null, caseNumber: null, assignedToUserId: null, assignedToName: null, canEdit: true };

test('a real middleware permission denial leaves the residence dialog and its inputs intact', async ({ asUser }) => {
  const page = await asUser('cmbalt');
  await page.goto('/search?q=Rivera');
  await page.getByRole('link', { name: /Rivera, Mia/ }).first().click();
  await page.getByRole('button', { name: 'Set as primary', exact: true }).first().click();
  await page.locator('#sp-start').fill('2026-10-01');
  await page.locator('#sp-reason').selectOption({ index: 1 });
  await page.getByRole('button', { name: 'Preview the change' }).click();
  const confirm = page.getByRole('button', { name: 'Confirm and save' });
  await expect(confirm).toBeEnabled();
  // Submit to a real endpoint this case manager cannot use. Authorization runs before binding or any write.
  await confirm.evaluate(el => el.closest('form')!.setAttribute('hx-post', '/admin/tenants'));
  const denied = page.waitForResponse(r => new URL(r.url()).pathname === '/admin/tenants' && r.request().method() === 'POST');
  await confirm.click();
  const response = await denied;
  expect(response.status()).toBe(403);
  expect(response.headers()['x-merit-denied']).toBe('true');
  expect(await response.text()).toBe('');
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByRole('alert')).toContainText('Your current profile no longer permits this action');
  await expect(page.locator('#sp-start')).toHaveValue('2026-10-01');
  await expect(confirm).toBeVisible();
  await expect(page.locator('#alert-region')).toBeEmpty();
});

test('early session expiry cancels later timers and does not interrupt copying a draft', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.clock.install();
  await page.goto('/investigations');
  await page.locator('a[href^="/investigations/"]').first().click();
  await page.locator('#findings').fill('Keep this draft while signing in');
  await page.route('**/investigations/*', route => route.request().method() === 'POST'
    ? route.fulfill({ status: 401, body: '' }) : route.continue());
  await page.locator('#investigation-save-form button[type=submit]').click();
  const alert = page.getByRole('alert');
  await expect(alert).toContainText('Your session ended');
  await alert.evaluate(el => el.setAttribute('data-original-alert', 'true'));
  await page.locator('#findings').focus();
  const remaining = await page.evaluate(() => Number(document.body.dataset.sessionExpires) - Date.now());
  await page.clock.fastForward(remaining + 60000);
  await expect(alert).toContainText('Your session ended');
  await expect(alert).toHaveAttribute('data-original-alert', 'true');
  await expect(page.locator('#findings')).toBeFocused();
  await expect(page.locator('#findings')).toHaveValue('Keep this draft while signing in');
});

test('one API 401 emits one session-expired event', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.route(`**/api/v1/clusters/${id}`, route => route.request().method() === 'GET'
    ? route.fulfill(json({ clusterId: id, title: 'Review cluster', clusterType: 'building', status: 'open', windowStart: '2026-01-01',
      windowEnd: '2026-10-31', notes: '', owningTenantName: 'Maryland', rowVersion: 'AAAAAAAAAAA=', members: [], tasks: [], canEdit: true }))
    : route.fulfill({ status: 401, body: '' }));
  await page.goto(`/app/clusters/${id}`);
  await angularReady(page);
  await page.evaluate(() => {
    const w = window as typeof window & { expiryEvents: number };
    w.expiryEvents = 0;
    document.addEventListener('merit:sessionExpired', () => w.expiryEvents++);
  });
  await page.getByRole('textbox', { name: 'Notes', exact: true }).fill('Draft');
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.getByRole('link', { name: 'Sign in in a new tab' })).toBeVisible();
  await page.waitForLoadState('networkidle');
  expect(await page.evaluate(() => (window as typeof window & { expiryEvents: number }).expiryEvents)).toBe(1);
});

test('report measure changes and history reuse the report while changed filters and Apply refresh it', async ({ asUser }) => {
  const page = await asUser('supervisor');
  let reads = 0;
  await page.route('**/api/v1/reports/*', route => { reads++; return route.continue(); });
  await page.goto('/app/reports?from=2026-01-01&to=2026-10-31');
  await angularReady(page);
  const options = await page.locator('#r-measure option').evaluateAll(opts => opts.map(o => (o as HTMLOptionElement).value));
  expect(options.length).toBeGreaterThan(1);
  expect(reads).toBe(1);
  await page.locator('#r-measure').selectOption(options[1]);
  await expect(page).toHaveURL(/measure=/);
  await page.waitForLoadState('networkidle');
  expect(reads).toBe(1);
  await page.goBack();
  await expect(page.locator('#r-measure')).toHaveValue(options[0]);
  await page.goForward();
  await expect(page.locator('#r-measure')).toHaveValue(options[1]);
  await page.waitForLoadState('networkidle');
  expect(reads).toBe(1);
  await page.locator('#r-from').fill('2026-02-01');
  await page.getByRole('button', { name: 'Apply', exact: true }).click();
  await expect.poll(() => reads).toBe(2);
  await page.waitForLoadState('networkidle');
  await page.getByRole('button', { name: 'Apply', exact: true }).click();
  await expect.poll(() => reads).toBe(3);
  await page.waitForLoadState('networkidle');
});

test('a failed queue write restarts the cancelled side-nav read and keeps selection and the write error', async ({ asUser }) => {
  const page = await asUser('supervisor');
  let reads = 0;
  let finishInitial!: () => Promise<void>;
  const counts = [{ queueCode: 'caseFollowUp', queueName: 'Recovered queue', openCount: 9, urgentCount: 0, overdueCount: 0, mineCount: 0 }];
  await page.route('**/api/v1/queues', route => {
    reads++;
    if (reads === 1) { finishInitial = () => route.fulfill(json([])).catch(() => {}); return; }
    return route.fulfill(json(counts));
  });
  await page.route('**/api/v1/tasks?**', route => route.fulfill(json({ items: [task], totalCount: 1, pageNumber: 1, pageSize: 50 })));
  await page.route('**/api/v1/tasks/bulk', route => route.fulfill({ status: 409, ...json({ detail: 'Task changed; retry.' }) }));
  await page.goto('/app/queues');
  await expect(page.locator('app-root h1')).toBeVisible();
  await expect.poll(() => reads).toBe(1);
  await page.getByLabel('Select row 1', { exact: true }).focus();
  await page.keyboard.press('Space');
  await page.getByRole('button', { name: 'Assign to me', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Task changed; retry.');
  await expect(page.getByRole('navigation', { name: 'Queues', exact: true })).toContainText('Recovered queue (9)');
  expect(reads).toBe(2);
  await finishInitial();
  await expect(page.getByLabel('Select row 1', { exact: true })).toBeChecked();
});

test('a failed held write restarts the cancelled overview read without losing corrections', async ({ asUser }) => {
  const page = await asUser('supervisor');
  let reads = 0;
  let finishInitial!: () => Promise<void>;
  const overview = { groups: [{ reasonCode: 'invalidValue', reportedValue: 'FIX-RETRY', senderOrganizationId: null, senderName: null,
    rowCount: 1, urgentCount: 0, oldestHeldUtc: '2026-10-31T12:00:00Z', batchCount: 1, sampleMessage: null }], totalCount: 1, batchFileName: null };
  await page.route('**/api/v1/held/overview**', route => {
    reads++;
    if (reads === 1) { finishInitial = () => route.fulfill(json({ groups: [], totalCount: 0, batchFileName: null })).catch(() => {}); return; }
    return route.fulfill(json(overview));
  });
  await page.route('**/api/v1/held/rows?**', route => route.fulfill(json({ items: [{ stagingRowId: id, intakeBatchId: id, fileName: 'batch.csv',
    senderName: 'Lab', rowNumber: 1, reported: { value: '2' }, corrected: {}, isUrgent: false, heldSinceUtc: '2026-10-31T12:00:00Z',
    assignedToName: null, reasons: 'Invalid value', reasonCodes: ['invalidValue'] }], totalCount: 1, pageNumber: 1, pageSize: 50 })));
  await page.route('**/api/v1/held/release', route => route.fulfill({ status: 409, ...json({ detail: 'Row changed; retry.' }) }));
  await page.goto('/app/held');
  await expect(page.locator('app-root h1')).toBeVisible();
  await expect.poll(() => reads).toBe(1);
  await page.getByLabel('Select row 1', { exact: true }).focus();
  await page.keyboard.press('Space');
  await page.getByLabel('Correct Value', { exact: true }).fill('3');
  await page.getByRole('button', { name: 'Release selected', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Row changed; retry.');
  await expect(page.getByRole('button', { name: /FIX-RETRY/ })).toBeVisible();
  expect(reads).toBe(2);
  await finishInitial();
  await expect(page.getByLabel('Correct Value', { exact: true })).toHaveValue('3');
  await expect(page.getByLabel('Select row 1', { exact: true })).toBeChecked();
});

test('a clamped page updates the URL without a duplicate read and preserves later Back navigation', async ({ asUser }) => {
  const page = await asUser('supervisor');
  const reads: string[] = [];
  await page.route('**/api/v1/tasks?**', route => {
    reads.push(new URL(route.request().url()).searchParams.get('page')!);
    return route.fulfill(json({ items: [task], totalCount: 1, pageNumber: 1, pageSize: 50 }));
  });
  await page.goto('/app/queues?page=2');
  await angularReady(page);
  await expect(page).toHaveURL(/page=1/);
  expect(reads).toEqual(['2']);
  await page.locator('#urgent').focus();
  await page.keyboard.press('Space');
  await expect(page).toHaveURL(/urgent=true/);
  await page.waitForLoadState('networkidle');
  expect(reads).toEqual(['2', '1']);
  await page.goBack();
  await expect(page.locator('#urgent')).not.toBeChecked();
  await page.waitForLoadState('networkidle');
  expect(reads).toEqual(['2', '1', '1']);
});
