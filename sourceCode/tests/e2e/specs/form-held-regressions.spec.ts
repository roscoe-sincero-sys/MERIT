import { angularReady, expect, test } from './helpers';

test('place and relationship editors have independent roles and School queries school sites', async ({ asUser }) => {
  const page = await asUser('supervisor');
  const results = await (await page.request.get('/api/v1/results?pageSize=20')).json();
  const person = results.items.find((r: { personId: string | null }) => r.personId).personId;
  const site = '00000000-0000-0000-0000-000000000068:00000000-0000-0000-0000-000000000069';
  const types: string[] = [];
  await page.route('**/lookup/sites**', async route => {
    types.push(new URL(route.request().url()).searchParams.get('type')!);
    await route.fulfill({ contentType: 'text/html', body: `<li role="option" id="place-school-option" data-value="${site}" data-label="Harbor School">Harbor School</li>` });
  });
  await page.goto(`/people/${person}`);
  await page.getByRole('button', { name: 'Add relationship', exact: true }).click();
  await page.getByRole('button', { name: 'Add child care or school' }).click();
  await page.locator('#place-Role').selectOption('school');
  await page.locator('#place-OrganizationSite-text').fill('Harbor');
  await page.locator('#place-school-option').click();
  expect(types).toEqual(['school']);
  await expect(page.locator('#place-OrganizationSite')).toHaveValue(site);
  await page.locator('#relationship-Role').selectOption({ index: 1 });
  await expect(page.locator('#place-OrganizationSite')).toHaveValue(site);
  await expect(page.locator('#place-OrganizationSite-text')).toBeVisible();
  const duplicateIds = await page.evaluate(() => {
    const ids = [...document.querySelectorAll('[id]')].map(e => e.id);
    return ids.filter((id, i) => ids.indexOf(id) !== i);
  });
  expect(duplicateIds).toEqual([]);
});

test('editing a primary-residence preview disables its stale confirmation', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/search?q=Rivera');
  await page.getByRole('link', { name: /Rivera, Mia/ }).first().click();
  await page.getByRole('button', { name: 'Set as primary', exact: true }).first().click();
  await page.locator('#sp-start').fill('2026-10-01');
  await page.locator('#sp-reason').selectOption({ index: 1 });
  await page.getByRole('button', { name: 'Preview the change' }).click();
  const confirm = page.getByRole('button', { name: 'Confirm and save' });
  await expect(confirm).toBeEnabled();
  await expect(page.locator('#set-primary')).toContainText('Effective date:');
  await page.locator('#sp-start').fill('2026-10-02');
  await expect(confirm).toBeDisabled();
  await expect(page.locator('[data-preview-stale]')).toBeVisible();
  await page.getByRole('button', { name: 'Preview the change' }).click();
  await expect(confirm).toBeEnabled();
  await page.locator('#sp-reason').selectOption({ index: 2 });
  await expect(confirm).toBeDisabled();
});

test('case validation retains the submitted activity and saving a sibling keeps its drafts', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/cases');
  await page.locator('tbody a[href^="/cases/"]').first().click();
  await page.locator('#note').fill('Activity draft to keep');
  await page.locator('#activityDate').fill('2099-01-01');
  await page.getByRole('button', { name: 'Log activity' }).click();
  await expect(page.locator('#case-body [role=alert]')).toBeVisible();
  await expect(page.locator('#note')).toHaveValue('Activity draft to keep');
  await expect(page.locator('#activityDate')).toHaveValue('2099-01-01');
  await page.locator('#closeNote').fill('Independent closure draft');
  const response = await page.request.get(page.url(), { headers: { 'HX-Request': 'true', 'HX-Request-Type': 'partial' } });
  const body = await response.text();
  await page.route('**/cases/*/update', route => route.fulfill({ contentType: 'text/html', body }));
  // Return a freshly rendered body without writing the demo record.
  await page.locator('#case-update-form').getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.locator('#case-body [role=alert]')).toHaveCount(0);
  await expect(page.locator('#note')).toHaveValue('Activity draft to keep');
  await expect(page.locator('#closeNote')).toHaveValue('Independent closure draft');
});

test('investigation validation retains findings and sample saves preserve sibling drafts', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/investigations');
  await page.locator('a[href^="/investigations/"]').first().click();
  await page.locator('#findings').fill('Investigation findings draft');
  await page.locator('#investigationStatus').selectOption('closed');
  await page.locator('#completedDate').fill('');
  await page.locator('#investigation-save-form').getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.locator('#investigation-body [role=alert]')).toBeVisible();
  await expect(page.locator('#findings')).toHaveValue('Investigation findings draft');
  await expect(page.locator('#investigationStatus')).toHaveValue('closed');
  await page.locator('#referralOutcome').fill('Pending callback draft');
  const response = await page.request.get(page.url(), { headers: { 'HX-Request': 'true', 'HX-Request-Type': 'partial' } });
  const body = await response.text();
  await page.route('**/investigations/*/samples', route => route.fulfill({ contentType: 'text/html', body }));
  await page.locator('#sampleValue').fill('10');
  await page.getByRole('button', { name: 'Add sample', exact: true }).click();
  await expect(page.locator('#sampleValue')).toHaveValue('');
  await expect(page.locator('#findings')).toHaveValue('Investigation findings draft');
  await expect(page.locator('#referralOutcome')).toHaveValue('Pending callback draft');
});

test('held batch scope uses a distinct total, scopes causes and edits a missing field', async ({ asUser }) => {
  const page = await asUser('supervisor');
  const batch = '00000000-0000-0000-0000-000000000071';
  const row = '00000000-0000-0000-0000-000000000073';
  const groupBatches: (string | null)[] = [];
  const corrected: Record<string, string> = { customField: 'Existing correction' };
  const groups = ['missingDob', 'unknownUnit'].map(reasonCode => ({ reasonCode, reportedValue: null, senderOrganizationId: null,
    senderName: null, rowCount: 1, urgentCount: 0, oldestHeldUtc: '2026-10-01T12:00:00Z', batchCount: 1, sampleMessage: null }));
  await page.route('**/api/v1/held/**', async route => {
    const url = new URL(route.request().url());
    let body: unknown;
    if (url.pathname.endsWith('/summary')) body = { totalCount: 1, batchFileName: url.searchParams.has('batch') ? 'short-row.csv' : null };
    else if (url.pathname.endsWith('/groups')) { groupBatches.push(url.searchParams.get('batch')); body = groups; }
    else if (url.pathname.endsWith('/correction')) {
      Object.assign(corrected, route.request().postDataJSON().corrections);
      await route.fulfill({ status: 204 }); return;
    } else body = { items: [{ stagingRowId: row, intakeBatchId: batch, fileName: 'short-row.csv', senderName: null, rowNumber: 1,
      reported: { lastName: 'Short' }, corrected, isUrgent: false, heldSinceUtc: '2026-10-01T12:00:00Z', assignedToName: null,
      reasons: 'Missing date of birth', reasonCodes: ['missingDob', 'unknownUnit'] }], totalCount: 1, pageNumber: 1, pageSize: 50 };
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) });
  });
  await page.goto(`/app/held?batch=${batch}`);
  await angularReady(page);
  await expect(page.getByText('1 held row(s).', { exact: false })).toBeVisible();
  await expect(page.getByText('Showing batch:', { exact: false })).toContainText('short-row.csv');
  expect(groupBatches).toEqual([batch]);
  await page.locator('merit-data-grid tbody input[type=checkbox]').focus();
  await page.keyboard.press('Space');
  await expect(page.locator('#fix-customField')).toHaveValue('Existing correction');
  await expect(page.locator('#fix-dob').locator('xpath=ancestor::tr')).toContainText('(not supplied)');
  await page.locator('#fix-dob').fill('2021-02-03');
  await page.getByRole('button', { name: 'Save correction' }).click();
  await expect.poll(() => corrected.dob).toBe('2021-02-03');
  await page.getByRole('button', { name: 'Show all batches' }).click();
  await expect(page).not.toHaveURL(/batch=/);
  await expect.poll(() => groupBatches.at(-1)).toBeNull();
});

test('held paging clears match-review selection and releases only the eligible visible row', async ({ asUser }) => {
  const page = await asUser('supervisor');
  const released: string[][] = [];
  await page.route('**/api/v1/held/**', async route => {
    const url = new URL(route.request().url());
    const second = url.searchParams.get('page') === '2';
    let body: unknown;
    if (url.pathname.endsWith('/release')) { released.push(route.request().postDataJSON().stagingRowIds); body = { updated: 1 }; }
    else if (url.pathname.endsWith('/groups')) body = [];
    else if (url.pathname.endsWith('/summary')) body = { totalCount: 51, batchFileName: null };
    else body = { items: [{ stagingRowId: second ? 'eligible' : 'uncertain', intakeBatchId: 'batch', fileName: 'batch.csv', senderName: null,
      rowNumber: second ? 51 : 1, reported: { lastName: 'Held' }, corrected: {}, isUrgent: false,
      heldSinceUtc: '2026-10-01T12:00:00Z', assignedToName: null, reasons: null, reasonCodes: second ? ['missingDob'] : ['uncertainMatch'] }],
      totalCount: 51, pageNumber: second ? 2 : 1, pageSize: 50 };
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) });
  });
  await page.goto('/app/held'); await angularReady(page);
  await page.locator('merit-data-grid tbody input[type=checkbox]').focus();
  await page.keyboard.press('Space');
  await expect(page.getByRole('button', { name: 'Release selected' })).toBeDisabled();
  await page.getByRole('button', { name: 'Next', exact: true }).click();
  await expect(page.locator('.grid-count')).toHaveText('51 rows');
  await expect(page.getByRole('button', { name: 'Release selected' })).toBeDisabled();
  await page.locator('merit-data-grid tbody input[type=checkbox]').focus();
  await page.keyboard.press('Space');
  await page.getByRole('button', { name: 'Release selected' }).click();
  await expect.poll(() => released).toEqual([['eligible']]);
});
