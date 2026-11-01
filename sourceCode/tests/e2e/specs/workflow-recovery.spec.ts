import { angularReady, expect, expectAccessible, test } from './helpers';

const id = (n: number) => `00000000-0000-0000-0000-${String(n).padStart(12, '0')}`;
const task = (n = 1, canEdit = true) => ({ workTaskId: id(n), queueCode: 'caseFollowUp', title: `Task ${n}`, detail: null,
  isUrgent: false, dueDate: '2026-11-01', taskStatus: 'open', createdUtc: '2026-10-31T12:00:00Z', personId: null,
  personName: null, exposureCaseId: null, caseNumber: null, assignedToUserId: null, assignedToName: null, canEdit });
const held = { stagingRowId: id(1), intakeBatchId: id(2), fileName: 'batch.csv', senderName: 'Lab', rowNumber: 1,
  reported: { lastName: 'Sample', value: '2' }, corrected: {}, isUrgent: false, heldSinceUtc: '2026-10-31T12:00:00Z',
  assignedToName: null, reasons: 'Invalid value', reasonCodes: ['invalidValue'] };
const cluster = (canEdit = true) => ({ clusterId: id(1), title: 'Review cluster', clusterType: 'building', status: 'open',
  windowStart: '2026-01-01', windowEnd: '2026-10-31', notes: 'Existing notes', owningTenantName: 'Maryland',
  rowVersion: 'AAAAAAAAAAA=', members: [], tasks: [], canEdit });
const json = (body: unknown) => ({ contentType: 'application/json', body: JSON.stringify(body) });

test('match decisions retain keyboard focus, cross pages, and contain five-candidate scrolling at phone width', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.setViewportSize({ width: 390, height: 844 });
  const items = Array.from({ length: 201 }, (_, i) => ({ stagingRowId: id(i + 1), rowNumber: i + 1,
    patientName: `Person ${i + 1}`, fileName: 'long_intake_filename_for_match_review.csv', senderName: 'Lab', dob: '2021-01-01',
    isUrgent: false, isEscalated: false, escalationNote: null, heldSinceUtc: '2026-10-31T12:00:00Z', reasonMessage: 'Uncertain match' }));
  const decisions: string[] = [];
  await page.route('**/api/v1/match-review**', async route => {
    const url = new URL(route.request().url());
    if (route.request().method() === 'POST') {
      decisions.push(url.pathname.split('/').at(-2)!);
      await route.fulfill(json({ released: false, remainingCandidates: 5, message: 'Sent to supervisor.' }));
    } else if (url.pathname === '/api/v1/match-review') {
      const p = Number(url.searchParams.get('page') ?? 1);
      await route.fulfill(json({ items: items.slice((p - 1) * 200, p * 200), totalCount: items.length, pageNumber: p, pageSize: 200 }));
    } else {
      const row = items.find(r => r.stagingRowId === url.pathname.split('/').at(-1))!;
      await route.fulfill(json({ ...row, incoming: [], candidates: Array.from({ length: 5 }, (_, i) => ({
        personId: id(i + 300), rank: i + 1, personName: `Candidate with a long family name ${i + 1}`, explanation: 'Close identity.', warnings: [],
        fields: [{ field: 'name', label: 'Name', incoming: 'Sample person', candidate: `Candidate ${i + 1}`, agreement: 'close', note: null }],
        aliases: null, resultCount: 0, lastCollectedDate: null, openCaseNumbers: null, places: [], results: [],
      })) }));
    }
  });
  await page.goto('/app/match-review');
  await angularReady(page);
  const notSure = page.getByRole('button', { name: 'Not sure (N)' });
  await notSure.focus();
  await page.keyboard.press('n');
  await expect(page.locator('#record-heading')).toContainText('Row 2');
  await expect(page.locator('app-root h1')).toBeFocused();
  await page.keyboard.press('n');
  await expect(page.locator('#record-heading')).toContainText('Row 3');
  expect(decisions).toEqual([id(1), id(2)]);
  await page.getByRole('button', { name: 'Next page', exact: true }).click();
  await expect(page.locator('#record-heading')).toContainText('Row 201');
  await page.keyboard.press('k');
  await expect(page.locator('#record-heading')).toContainText('Row 200');
  await page.keyboard.press('j');
  await expect(page.locator('#record-heading')).toContainText('Row 201');
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
  const comparison = page.getByRole('region', { name: 'Incoming record compared with candidates' });
  expect(await comparison.evaluate(e => e.scrollWidth > e.clientWidth)).toBe(true);
  await notSure.scrollIntoViewIfNeeded();
  await expect(notSure).toBeInViewport();
  await expectAccessible(page, 'Five candidate match review at phone width');
});

test('clusters expose the 201st record without resolving earlier records', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.route('**/api/v1/clusters?**', async route => {
    const p = Number(new URL(route.request().url()).searchParams.get('page') ?? 1);
    const all = Array.from({ length: 201 }, (_, i) => ({ ...cluster(), clusterId: id(i + 1), title: `Cluster ${i + 1}`, memberCount: 2 }));
    await route.fulfill(json({ items: all.slice((p - 1) * 200, p * 200), totalCount: 201, pageNumber: p, pageSize: 200 }));
  });
  await page.goto('/app/clusters');
  await angularReady(page);
  await page.getByRole('button', { name: 'Next page', exact: true }).click();
  await expect(page.getByRole('link', { name: 'Cluster 201', exact: true })).toBeVisible();
  await expect(page.getByRole('navigation', { name: 'Clusters pages' })).toContainText('201–201 of 201');
  await page.getByLabel('Status', { exact: true }).selectOption('closed');
  await expect(page.getByRole('navigation', { name: 'Clusters pages' })).toContainText('Page 1 of 2');
});

test('read-only capabilities hide edits and prevent selecting tasks in a read-only tenant', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.route('**/api/v1/tasks?**', route => route.fulfill(json({ items: [task(1, false), task(2, true)], totalCount: 2, pageNumber: 1, pageSize: 50 })));
  await page.goto('/app/queues');
  await angularReady(page);
  const boxes = page.locator('tbody input[type=checkbox]');
  await expect(boxes.nth(0)).toBeDisabled();
  await page.getByLabel('Select all on this page').focus(); await page.keyboard.press('Space');
  await expect(boxes.nth(0)).not.toBeChecked();
  await expect(boxes.nth(1)).toBeChecked();

  await page.route('**/api/v1/me', async route => {
    const response = await route.fetch(); const me = await response.json();
    await route.fulfill(json({ ...me, permissions: me.permissions.filter((p: string) => p !== 'task.edit') }));
  });
  await page.reload();
  await angularReady(page);
  await expect(page.getByRole('group', { name: 'Bulk actions on selected tasks' })).toHaveCount(0);
  await expect(page.locator('tbody input[type=checkbox]')).toHaveCount(0);
  await page.route(`**/api/v1/clusters/${id(1)}`, route => route.fulfill(json(cluster(false))));
  await page.goto(`/app/clusters/${id(1)}`);
  await angularReady(page);
  await expect(page.getByText('Existing notes', { exact: true })).toBeVisible();
  await expect(page.getByRole('textbox', { name: 'Notes' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Save', exact: true })).toHaveCount(0);
  await expect(page.getByRole('textbox', { name: 'Add a case by number' })).toHaveCount(0);
});

test('queue confirmation, pending-write guard, failure selection, reopen, and page clamping work together', async ({ asUser }) => {
  const page = await asUser('supervisor');
  let resolve!: (fail?: boolean) => Promise<void>;
  let writes = 0;
  let emptied = false;
  const actions: string[] = [];
  await page.route('**/api/v1/tasks**', async route => {
    if (route.request().method() === 'POST') {
      writes++;
      actions.push(route.request().postDataJSON().action);
      resolve = async (fail = false) => {
        if (!fail) emptied = true;
        await route.fulfill({ ...json(fail ? { detail: 'Try again later.' } : { updated: 1 }), status: fail ? 409 : 200 });
      };
      return;
    }
    const requested = Number(new URL(route.request().url()).searchParams.get('page') ?? 1);
    const p = emptied ? 1 : requested;
    await route.fulfill(json({ items: [task(p === 2 ? 51 : 1)], totalCount: emptied ? 50 : 51, pageNumber: p, pageSize: 50 }));
  });
  await page.goto('/app/queues?page=2');
  await angularReady(page);
  await page.getByLabel('Select row 1', { exact: true }).focus(); await page.keyboard.press('Space');
  page.once('dialog', d => d.dismiss());
  await page.getByRole('button', { name: 'Complete', exact: true }).click();
  expect(writes).toBe(0);
  page.once('dialog', d => d.accept());
  await page.getByRole('button', { name: 'Complete', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Dismiss', exact: true })).toBeDisabled();
  await expect(page.getByLabel('Select row 1', { exact: true })).toBeDisabled();
  await expect.poll(() => writes).toBe(1);
  await resolve(true);
  await expect(page.getByRole('alert')).toContainText('Try again later');
  await expect(page.getByLabel('Select row 1', { exact: true })).toBeChecked();
  page.once('dialog', d => d.accept());
  await page.getByRole('button', { name: 'Complete', exact: true }).click();
  await expect.poll(() => writes).toBe(2);
  await resolve();
  await expect(page).toHaveURL(/page=1/);
  await expect(page.getByRole('navigation', { name: 'Tasks pages' })).toContainText('Page 1 of 1');
  await page.getByLabel('Status', { exact: true }).selectOption('done');
  await expect(page.getByRole('button', { name: 'Complete', exact: true })).toHaveCount(0);
  await page.getByLabel('Select row 1', { exact: true }).focus(); await page.keyboard.press('Space');
  await page.getByRole('button', { name: 'Reopen selected' }).click();
  await expect.poll(() => writes).toBe(3);
  await resolve();
  expect(actions).toEqual(['complete', 'complete', 'reopen']);
});

for (const action of ['release', 'reject', 'correction']) {
  test(`held ${action} blocks competing writes and keeps inputs on failure`, async ({ asUser }) => {
    const page = await asUser('supervisor');
    let finish!: () => Promise<void>; let writes = 0;
    await page.route('**/api/v1/held/**', async route => {
      if (route.request().method() !== 'GET') {
        writes++;
        finish = () => route.fulfill({ status: 409, ...json({ detail: 'Please retry.' }) });
      } else if (new URL(route.request().url()).pathname.endsWith('/overview')) {
        await route.fulfill(json({ groups: [], totalCount: 1, batchFileName: null }));
      } else await route.fulfill(json({ items: [held], totalCount: 1, pageNumber: 1, pageSize: 50 }));
    });
    await page.goto('/app/held');
    await angularReady(page);
    await page.getByLabel('Select row 1', { exact: true }).focus(); await page.keyboard.press('Space');
    await page.getByLabel('Reject reason', { exact: true }).fill('Duplicate');
    await page.getByLabel('Correct Value', { exact: true }).fill('3');
    const button = page.getByRole('button', { name: action === 'release' ? 'Release selected' : action === 'reject' ? 'Reject selected' : 'Save correction', exact: true });
    await button.click();
    await expect.poll(() => writes).toBe(1);
    await expect(page.getByRole('button', { name: 'Release selected', exact: true })).toBeDisabled();
    await expect(page.getByRole('button', { name: 'Reject selected', exact: true })).toBeDisabled();
    await expect(page.getByRole('button', { name: 'Save correction', exact: true })).toBeDisabled();
    await finish();
    await expect(page.getByRole('alert')).toContainText('Please retry.');
    await expect(page.getByLabel('Select row 1', { exact: true })).toBeChecked();
    await expect(page.getByLabel('Correct Value', { exact: true })).toHaveValue('3');
    await expect(page.getByLabel('Reject reason', { exact: true })).toHaveValue('Duplicate');
    expect(writes).toBe(1);
  });
}

test('expired Razor session retains findings and original version, and offers a return URL and session check', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/investigations');
  await page.locator('a[href^="/investigations/"]').first().click();
  const original = page.url();
  const version = await page.locator('#investigation-save-form [name=rowVersion]').inputValue();
  await page.locator('#findings').fill('Unsaved confidential findings');
  await page.route('**/investigations/*', async route => {
    if (route.request().method() === 'POST') await route.fulfill({ status: 401, body: '' });
    else await route.continue();
  });
  await page.locator('#investigation-save-form button[type=submit]').click();
  await expect(page).toHaveURL(original);
  await expect(page.locator('#findings')).toHaveValue('Unsaved confidential findings');
  await expect(page.locator('#investigation-save-form [name=rowVersion]')).toHaveValue(version);
  const link = page.getByRole('link', { name: 'Sign in in a new tab' });
  await expect(link).toHaveAttribute('target', '_blank');
  expect(new URL(await link.getAttribute('href') ?? '', page.url()).searchParams.get('ReturnUrl')).toBe(new URL(original).pathname);
  await page.getByRole('button', { name: 'Check session', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('You are signed in again');
  await expect(page.locator('#findings')).toHaveValue('Unsaved confidential findings');
  expect(await page.evaluate(() => JSON.stringify({ local: { ...localStorage }, session: { ...sessionStorage } }))).not.toContain('confidential');
});

test('Angular session expiry keeps cluster note drafts in place', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.route(`**/api/v1/clusters/${id(1)}`, route => route.request().method() === 'GET'
    ? route.fulfill(json(cluster())) : route.fulfill({ status: 401, body: '' }));
  await page.goto(`/app/clusters/${id(1)}`);
  await angularReady(page);
  await page.getByRole('textbox', { name: 'Notes', exact: true }).fill('My unsaved cluster note');
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.getByRole('link', { name: 'Sign in in a new tab' })).toBeVisible();
  await expect(page.getByRole('textbox', { name: 'Notes', exact: true })).toHaveValue('My unsaved cluster note');
  await expect(page).toHaveURL(new RegExp(`/app/clusters/${id(1)}$`));
});

test('idle expiration warns before the deadline and leaves typed work in place', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.clock.install();
  await page.goto('/investigations');
  await page.locator('a[href^="/investigations/"]').first().click();
  await page.locator('#findings').fill('Work typed during an idle period');
  const remaining = await page.evaluate(() => Number(document.body.dataset.sessionExpires) - Date.now());
  expect(remaining).toBeGreaterThan(2 * 60000);
  await page.clock.fastForward(remaining - 2 * 60000 + 1000);
  await expect(page.getByRole('alert')).toContainText('may expire soon');
  await page.clock.fastForward(2 * 60000);
  await expect(page.getByRole('alert')).toContainText('Your session may have ended');
  // Browser time alone cannot establish server expiry: the actual session is still valid.
  expect((await page.request.get('/api/v1/me')).ok()).toBe(true);
  await expect(page.locator('#findings')).toHaveValue('Work typed during an idle period');
});

for (const status of [403, 409, 422, 500]) {
  test(`dialog ${status} errors stay inside the modal and preserve its inputs`, async ({ asUser }) => {
    const page = await asUser('supervisor');
    await page.goto('/search?q=Rivera');
    await page.getByRole('link', { name: /Rivera, Mia/ }).first().click();
    await page.getByRole('button', { name: 'Set as primary', exact: true }).first().click();
    const dialog = page.getByRole('dialog');
    await page.locator('#sp-start').fill('2026-10-01');
    await page.locator('#sp-reason').selectOption({ index: 1 });
    await page.getByRole('button', { name: 'Preview the change' }).click();
    const confirm = page.getByRole('button', { name: 'Confirm and save' });
    await expect(confirm).toBeEnabled();
    let sentDialogHeader: string | undefined;
    const action = await confirm.evaluate(el => (el.closest('form') as HTMLFormElement).getAttribute('hx-post'));
    await page.route(`**${action}`, async route => {
      sentDialogHeader = route.request().headers()['x-merit-dialog'];
      await route.fulfill(status === 403 ? { status, body: '' } : { status, contentType: 'text/html',
        headers: { 'HX-Retarget': '#dialog-alert-region', 'HX-Reswap': 'innerHTML' },
        body: '<div role="alert" class="usa-alert usa-alert--warning"><p>Review the residence dates.</p></div>' });
    });
    await confirm.click();
    await expect(dialog.getByRole('alert')).toBeVisible();
    await expect(page.locator('#sp-start')).toHaveValue('2026-10-01');
    expect(sentDialogHeader).toBe('true');
    await expect.poll(() => dialog.evaluate(el => el.contains(document.activeElement))).toBe(true);
    await expect(page.locator('#alert-region')).toBeEmpty();
  });
}

test('result filters survive detail navigation, reload and browser Back', async ({ asUser }) => {
  const page = await asUser('supervisor');
  const first = await (await page.request.get('/api/v1/results?pageSize=1')).json();
  await page.route('**/api/v1/results?**', route => route.fulfill(json({ ...first, totalCount: 100, pageNumber: Number(new URL(route.request().url()).searchParams.get('page') ?? 1), pageSize: 50 })));
  await page.goto('/app/results?from=2026-01-01&to=2026-10-31&population=child&minValue=3&page=2&sort=valueCanonical&desc=false&keep=yes');
  await angularReady(page);
  await expect(page.locator('#f-from')).toHaveValue('2026-01-01');
  await expect(page.locator('#f-pop')).toHaveValue('child');
  const listUrl = page.url();
  await page.locator('tbody a[href^="/results/"]').first().click();
  await page.goBack();
  await angularReady(page);
  await expect(page).toHaveURL(listUrl);
  await page.locator('tbody a[href^="/results/"]').first().click();
  await page.getByRole('link', { name: 'Return to filtered results' }).click();
  await angularReady(page);
  await expect(page).toHaveURL(listUrl);
  await page.locator('#f-min').fill('5');
  await page.getByRole('button', { name: 'Apply', exact: true }).click();
  await expect(page).toHaveURL(/minValue=5/);
  await expect(page).toHaveURL(/keep=yes/);
  await page.reload();
  await angularReady(page);
  await expect(page.locator('#f-min')).toHaveValue('5');
  await page.goBack();
  await expect(page.locator('#f-min')).toHaveValue('3');
  await expect(page.getByRole('navigation', { name: 'Lab results pages' })).toContainText('Page 2 of 2');
});

test('queue links retain mine and reports retain applied period and measure through reload and Back', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/app/queues?mine=true&keep=yes');
  await angularReady(page);
  await page.getByRole('navigation', { name: 'Queues', exact: true }).getByRole('link').last().click();
  await expect(page).toHaveURL(/mine=true/);
  await expect(page).toHaveURL(/keep=yes/);
  await page.reload();
  await angularReady(page);
  await expect(page.locator('#mine')).toBeChecked();
  await page.goto('/app/reports?from=2026-01-01&to=2026-10-31&keep=yes');
  await angularReady(page);
  await expect(page.locator('#r-from')).toHaveValue('2026-01-01');
  const options = await page.locator('#r-measure option').evaluateAll(opts => opts.map(o => (o as HTMLOptionElement).value));
  expect(options.length).toBeGreaterThan(1);
  await page.locator('#r-measure').selectOption(options[1]);
  await expect(page).toHaveURL(/measure=/);
  await page.reload();
  await angularReady(page);
  await expect(page.locator('#r-measure')).toHaveValue(options[1]);
  await expect(page.locator('#r-from')).toHaveValue('2026-01-01');
  await page.goBack();
  await expect(page.locator('#r-measure')).toHaveValue(options[0]);
});

test('reapplying the current queue refreshes without locking selection', async ({ asUser }) => {
  const page = await asUser('supervisor');
  let reads = 0;
  await page.route('**/api/v1/tasks?**', route => {
    reads++;
    return route.fulfill(json({ items: [task()], totalCount: 1, pageNumber: 1, pageSize: 50 }));
  });
  await page.goto('/app/queues?status=open&mine=false&urgent=false&sort=dueDate&desc=false&page=1&pageSize=50');
  await angularReady(page);
  await page.getByRole('link', { name: 'All queues', exact: true }).click();
  await expect.poll(() => reads).toBe(2);
  await page.getByLabel('Select row 1', { exact: true }).focus();
  await page.keyboard.press('Space');
  await expect(page.getByRole('button', { name: 'Assign to me', exact: true })).toBeEnabled();
});

test('timeline point heights do not compare unlike analytes or units', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.route('**/api/v1/people/*/timeline', route => route.fulfill(json({ personId: id(1), personName: 'Sample Person', events: [
    { kind: 'result', date: '2026-01-01', endDate: null, label: 'Lead, blood: 3 ug/dL', value: 3, unit: 'ug/dL', sampleType: 'venous', detail: null },
    { kind: 'result', date: '2026-02-01', endDate: null, label: 'Arsenic, urine: 900 ug/L', value: 900, unit: 'ug/L', sampleType: null, detail: null },
  ] })));
  await page.goto(`/app/people/${id(1)}/timeline`);
  await angularReady(page);
  const points = page.locator('merit-timeline-chart circle');
  await expect(points).toHaveCount(2);
  expect(await points.nth(0).getAttribute('cy')).toBe(await points.nth(1).getAttribute('cy'));
  await points.nth(1).click();
  await expect(page.getByRole('status')).toContainText('Arsenic, urine: 900 ug/L');
});
