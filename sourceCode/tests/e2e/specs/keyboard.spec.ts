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

test('cases: one click to the unassigned, filter and sort with the keyboard', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/cases');
  // Issue #50: two checkboxes and no sort over every case.
  await page.getByRole('link', { name: 'Unassigned open cases' }).focus();
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/assigned=unassigned/);
  await expect(page.locator('#assigned')).toHaveValue('unassigned');
  const assignedCells = page.locator('#case-list tbody tr td:nth-child(7)');
  for (const text of await assignedCells.allTextContents()) expect(text.trim()).toBe('Unassigned');

  // A filter refreshes the list in place and keeps the address in step.
  await page.goto('/cases?openOnly=false');
  await page.locator('#classification').focus();
  await page.locator('#classification').selectOption('confirmed');
  await expect(page).toHaveURL(/classification=confirmed/);
  await expect(page.locator('#case-list .merit-badge--suspect')).toHaveCount(0);

  // Sorting by case number, from its column heading; the filters stay.
  await page.getByRole('link', { name: 'Case', exact: true }).focus();
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/sort=case/);
  await expect(page.locator('th[aria-sort="ascending"]')).toContainText('Case');
  await expect(page).toHaveURL(/classification=confirmed/);
  const numbers = (await page.locator('#case-list tbody td:first-child').allTextContents()).map(t => t.trim());
  expect(numbers).toEqual([...numbers].sort());
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

test('match review: delayed response for an earlier record never overwrites the active selection or unlocks wrong decisions', async ({ asUser }) => {
  // Issue #60: opening A then B while A was slow let A replace B's review and let S/D/N/C submit the wrong record.
  const page = await asUser('supervisor');
  const row0 = '00000000-0000-0000-0000-0000000000a0';
  const rowA = '00000000-0000-0000-0000-0000000000a1';
  const rowB = '00000000-0000-0000-0000-0000000000b2';
  const makeQueueItem = (id: string, rowNumber: number, patientName: string) => ({
    stagingRowId: id, fileName: 'batch.csv', senderName: 'Chesapeake Lab', rowNumber,
    patientName, dob: '2021-04-12', isUrgent: false, heldSinceUtc: '2026-10-27T12:00:00Z',
    reasonMessage: 'Uncertain person match', isEscalated: false, escalationNote: null,
  });
  const makeReview = (id: string, rowNumber: number, candidateName: string, personId: string) => ({
    stagingRowId: id, fileName: 'batch.csv', senderName: 'Chesapeake Lab', rowNumber,
    isUrgent: false, isEscalated: false, escalationNote: null, reasonMessage: 'Uncertain person match',
    incoming: [{ key: 'patientName', label: 'Name', value: candidateName }],
    candidates: [{
      personId, rank: 1, personName: candidateName, explanation: 'Name and date of birth are close.', warnings: [],
      fields: [{ field: 'name', label: 'Name', incoming: candidateName, candidate: candidateName, agreement: 'agree', note: null }],
      aliases: null, resultCount: 1, lastCollectedDate: '2026-09-01', openCaseNumbers: null, places: [], results: [],
    }],
  });

  let queueItems = [
    makeQueueItem(row0, 1, 'Zero, Carlos'),
    makeQueueItem(rowA, 2, 'Alvarez, Elena'),
    makeQueueItem(rowB, 3, 'Brooks, Marcus'),
  ];
  let sawA!: () => void;
  const reachedA = new Promise<void>(resolve => { sawA = resolve; });
  let releaseA!: () => Promise<void>;
  let sawB!: () => void;
  const reachedB = new Promise<void>(resolve => { sawB = resolve; });
  let releaseB!: () => Promise<void>;
  let sawDecision!: () => void;
  const reachedDecision = new Promise<void>(resolve => { sawDecision = resolve; });
  let releaseDecision!: () => Promise<void>;
  const decisions: string[] = [];

  await page.route('**/api/v1/match-review**', async route => {
    const req = route.request();
    const url = new URL(req.url());
    if (req.method() === 'GET' && url.pathname === '/api/v1/match-review') {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ items: queueItems, totalCount: queueItems.length, pageNumber: 1, pageSize: 200 }),
      });
      return;
    }
    if (req.method() === 'GET' && url.pathname === `/api/v1/match-review/${row0}`) {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(makeReview(row0, 1, 'Carlos Zero', '00000000-0000-0000-0000-000000000100')),
      });
      return;
    }
    if (req.method() === 'GET' && url.pathname === `/api/v1/match-review/${rowA}`) {
      releaseA = () => route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(makeReview(rowA, 2, 'Elena Alvarez', '00000000-0000-0000-0000-000000000101')),
      }).catch(() => {});
      sawA();
      return;
    }
    if (req.method() === 'GET' && url.pathname === `/api/v1/match-review/${rowB}`) {
      releaseB = () => route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(makeReview(rowB, 3, 'Marcus Brooks', '00000000-0000-0000-0000-000000000102')),
      });
      sawB();
      return;
    }
    if (req.method() === 'POST' && url.pathname.endsWith('/decision')) {
      decisions.push(url.pathname);
      queueItems = queueItems.filter(q => q.stagingRowId !== rowB);
      releaseDecision = () => route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ released: true, remainingCandidates: 0, message: 'Matched to Marcus Brooks.' }),
      });
      sawDecision();
      return;
    }
    await route.continue();
  });

  await page.goto('/app/match-review');
  await angularReady(page);
  await expect(page.locator('#record-heading')).toContainText('Row 1 of batch.csv');

  // Step onto row A (held pending): decisions must not fire while the selected record is still loading.
  await page.locator('h1').focus();
  await page.keyboard.press('j');
  await reachedA;
  await expect(page.locator('nav ol li [aria-current="true"]')).toContainText('Alvarez, Elena');
  await expect(page.locator('#record-heading')).toHaveCount(0);
  await expect(page.locator('.merit-indicator')).toContainText('Loading the record');
  await page.keyboard.press('s');
  expect(decisions).toEqual([]);

  // Step onto row B before row A responds; fulfill B first, then fulfill delayed A.
  await page.keyboard.press('j');
  await reachedB;
  await expect(page.locator('nav ol li [aria-current="true"]')).toContainText('Brooks, Marcus');
  await releaseB();
  await expect(page.locator('#record-heading')).toContainText('Row 3 of batch.csv');
  await expect(page.getByRole('button', { name: 'Same person as Marcus Brooks (S)' })).toBeEnabled();

  await releaseA();
  await page.waitForTimeout(100);
  await expect(page.locator('nav ol li [aria-current="true"]')).toContainText('Brooks, Marcus');
  await expect(page.locator('#record-heading')).toContainText('Row 3 of batch.csv');
  await expect(page.getByRole('button', { name: 'Same person as Marcus Brooks (S)' })).toBeEnabled();

  // Decide row B: while the decision POST is in flight, navigation and duplicate decisions are locked.
  await page.keyboard.press('s');
  await reachedDecision;
  expect(decisions).toEqual([`/api/v1/match-review/${rowB}/decision`]);
  await expect(page.getByRole('button', { name: 'Same person as Marcus Brooks (S)' })).toBeDisabled();
  await expect(page.locator('nav ol li button').first()).toBeDisabled();
  await page.keyboard.press('k');
  await page.keyboard.press('s');
  await expect(page.locator('nav ol li [aria-current="true"]')).toContainText('Brooks, Marcus');
  expect(decisions).toEqual([`/api/v1/match-review/${rowB}/decision`]);

  await releaseDecision();
  await expect(page.locator('.usa-alert--success')).toContainText('Matched to Marcus Brooks.');
});

test('reports: a slower earlier report selection cannot overwrite a newer report selection', async ({ asUser }) => {
  // Issue #60: switching reports without cancelling superseded reads let an older response replace the chosen report.
  const page = await asUser('supervisor');
  await page.goto('/app/reports');
  await angularReady(page);
  const links = page.locator('nav[aria-label="Report library"] a');
  const firstLink = links.nth(0);
  const secondLink = links.nth(1);
  const firstTitle = (await firstLink.innerText()).trim();
  const secondTitle = (await secondLink.innerText()).trim();
  const secondCode = new URL(await secondLink.getAttribute('href') ?? '', 'https://x').searchParams.get('code')!;

  let sawSecond!: () => void;
  const reachedSecond = new Promise<void>(resolve => { sawSecond = resolve; });
  let releaseSecond!: () => Promise<void>;

  await page.route(`**/api/v1/reports/${secondCode}**`, async route => {
    releaseSecond = () => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        code: secondCode, title: secondTitle, description: 'Delayed earlier report.',
        fromDate: '2026-01-01', toDate: '2026-10-28', analyteId: null, usesAnalyte: false, canExport: false,
        columns: [{ key: 'month', label: 'Month', kind: 'month' }, { key: 'count', label: 'Count', kind: 'number' }],
        chart: { kind: 'bar', x: 'month', group: null, measures: ['count'] },
        rows: [['2026-10', 1]],
      }),
    }).catch(() => {});
    sawSecond();
  });

  await secondLink.click();
  await reachedSecond;
  await expect(secondLink).toHaveClass(/usa-current/);
  await expect(page.locator('#report-title')).toHaveCount(0);
  await expect(page.locator('.merit-indicator')).toContainText('Running the report');

  await firstLink.click();
  await expect(firstLink).toHaveClass(/usa-current/);
  await expect(page.locator('#report-title')).toHaveText(firstTitle);

  await releaseSecond();
  await page.waitForTimeout(100);
  await expect(firstLink).toHaveClass(/usa-current/);
  await expect(page.locator('#report-title')).toHaveText(firstTitle);
});
