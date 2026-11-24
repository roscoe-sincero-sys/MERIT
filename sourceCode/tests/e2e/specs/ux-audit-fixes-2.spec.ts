import { angularReady, expect, expectAccessible, test } from './helpers';

// Usability audit fixes, second batch: issues #147, #148, #151, #152, #153, #161, #165, #168, #170 and #171.
// #150 is covered in RecordCorrectionTests, #156 in MfaRecoveryTests and #158's grouping in HeldRecordsTests.
const id = (n: number) => `00000000-0000-0000-0000-${String(n).padStart(12, '0')}`;

test('people search offers Add a person from the start, once, and starts the form from the search (#147)', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/search');
  await expect(page.getByRole('search')).toHaveCount(1);
  await expect(page.getByRole('link', { name: 'Add a person', exact: true })).toHaveAttribute('href', '/people/new');
  await page.locator('#search-q').fill('Zzyzx Quentin 2019-04-02');
  const add = page.getByRole('link', { name: 'Add a person', exact: true });
  await expect(add).toHaveAttribute('href', /\/people\/new\?q=Zzyzx/);
  await add.click();
  await expect(page.locator('h1')).toHaveText('Add a person');
  await expect(page.locator('#LastName')).toHaveValue('Zzyzx');
  await expect(page.locator('#FirstName')).toHaveValue('Quentin');
  await expect(page.locator('#Dob')).toHaveValue('2019-04-02');
  await page.goto('/people/new');
  await expect(page.locator('#LastName')).toHaveValue('');
});

test('the header links the user guides, intake reads in plain words, and the profile reads as a control (#148, #152)', async ({ asUser }) => {
  const page = await asUser('supervisor');
  // At tablet width the user block folds behind Menu; read it where it shows.
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/');
  const switcher = page.locator('.merit-profile-switch');
  await expect(switcher).toHaveAccessibleName(/Profile.*Master profile.*switch profile or jurisdiction/);
  expect(await switcher.evaluate(e => getComputedStyle(e).borderTopStyle)).toBe('solid');
  // Operate the account disclosure by keyboard and verify the user guide link inside it.
  await switcher.focus();
  await page.keyboard.press('Enter');
  const guide = page.getByRole('link', { name: 'User guide (opens in a new tab)' });
  await expect(guide).toBeVisible();
  await expect(guide).toHaveAttribute('target', '_blank');
  await page.keyboard.press('Escape');
  await expect(guide).toBeHidden();
  await expect(switcher).toBeFocused();
  // Quick Jump (Ctrl+K): filtering hides non-matching options visually and keyboard Enter opens the visible match.
  await page.keyboard.press('Control+k');
  const paletteInput = page.locator('#palette-input');
  await expect(paletteInput).toBeFocused();
  await paletteInput.fill('audit');
  const visibleOptions = page.locator('#palette-list [role="option"]:visible');
  await expect(visibleOptions).toHaveCount(1);
  await expect(visibleOptions.first()).toContainText('Audit log');
  await page.keyboard.press('Escape');
  await expect(page.locator('#command-palette')).toBeHidden();
  const help = await page.context().newPage();
  await help.goto('/help');
  await expect(help.getByRole('heading', { level: 2 })).toHaveCount(3);
  const response = await help.request.get('/help/guides/operations-guide.html');
  expect(response.status()).toBe(200);
  expect(response.headers()['content-security-policy']).toContain("default-src 'none'");
  expect((await help.request.get('/help/images/guide/01-home.jpg')).status()).toBe(200);
  expect((await help.request.get('/help/guides/..%2F..%2Fappsettings.json')).status()).toBe(404);
  await help.close();
  await page.goto('/intake');
  const text = await page.locator('main').innerText();
  expect(text).not.toContain('sourceCode/samples');
  expect(text).not.toMatch(/ORU\^R01/);
  const options = await page.locator('#intakeProfileId option').allTextContents();
  expect(options.some(o => /^HL7 message from /.test(o))).toBe(true);
  for (const o of options) expect(o.split('Chesapeake Reference Laboratory').length).toBeLessThanOrEqual(2);
  const select = await page.locator('#intakeProfileId').evaluate((e: HTMLSelectElement) => e.scrollWidth <= e.clientWidth + 1);
  expect(select).toBe(true);
});

test('map points are one Tab stop, with arrow keys between them (#151)', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.route('**/api/v1/cases/map**', route => route.fulfill({ json: Array.from({ length: 30 }, (_, i) => ({
    exposureCaseId: id(i + 1), personId: id(i + 100), caseNumber: `PB-2026-${String(i + 1).padStart(6, '0')}`, personName: `Patient ${i + 1}`,
    classification: 'confirmed', latitude: 39.2 + (i % 6) * .02, longitude: -76.7 + i * .01, openedDate: '2026-01-01',
    analyteCode: 'PB', population: 'child', addressLabel: 'Example address', inOpenCluster: false })) }));
  await page.goto('/app/map'); await angularReady(page);
  const points = page.locator('merit-point-map .point');
  await expect(points).toHaveCount(30);
  await expect(page.locator('merit-point-map .point[tabindex="0"]')).toHaveCount(1);
  await page.locator('merit-point-map .plot').focus();
  await page.keyboard.press('Tab');
  const first = await page.evaluate(() => document.activeElement?.getAttribute('data-point-id'));
  expect(first).toBeTruthy();
  await page.keyboard.press('ArrowRight');
  const second = await page.evaluate(() => document.activeElement?.getAttribute('data-point-id'));
  expect(second).toBeTruthy();
  expect(second).not.toBe(first);
  await expect(page.locator(`merit-point-map .point[data-point-id="${second}"]`)).toHaveAttribute('tabindex', '0');
  await page.keyboard.press('End');
  await page.keyboard.press('Tab');
  expect(await page.evaluate(() => !document.activeElement?.closest('merit-point-map .plot'))).toBe(true);
  await page.keyboard.press('Shift+Tab');
  expect(await page.evaluate(() => document.activeElement?.classList.contains('point'))).toBe(true);
  await page.keyboard.press('Escape');
  await expect(page.locator('merit-point-map .plot')).toBeFocused();
  await expectAccessible(page, 'map roving points');
});

test('match review: single keys only when chosen, a decision pressed twice, and guidance for the record (#153, #168)', async ({ asUser }) => {
  const page = await asUser('supervisor');
  const row = id(107);
  const decisions: unknown[] = [];
  await page.addInitScript(() => { try { localStorage.removeItem('merit.match-review.single-keys'); } catch { /* none */ } });
  await page.route('**/api/v1/match-review**', async route => {
    if (route.request().method() === 'POST') {
      decisions.push(route.request().postDataJSON());
      return route.fulfill({ json: { released: false, remainingCandidates: 0, message: 'Reviewed.' } });
    }
    if (new URL(route.request().url()).pathname === '/api/v1/match-review')
      return route.fulfill({ json: { items: [{ stagingRowId: row, patientName: 'Baker, Baby Boy', fileName: 'review.csv', rowNumber: 1, isEscalated: false }], totalCount: 1, pageNumber: 1, pageSize: 200 } });
    return route.fulfill({ json: { stagingRowId: row, fileName: 'review.csv', rowNumber: 1, isUrgent: false, isEscalated: false,
      reasonMessage: 'Placeholder first name', candidates: [],
      incoming: [{ key: 'lastName', label: 'Last name', value: 'Baker' }, { key: 'firstName', label: 'First name', value: 'Baby Boy' },
        { key: 'dob', label: 'Date of birth', value: '2026-10-01' }, { key: 'collectedDate', label: 'Collected', value: '2026-10-05' }] } });
  });
  await page.goto('/app/match-review'); await angularReady(page);
  await expect(page.getByLabel('Show only records sent to a supervisor')).toBeVisible();
  // The record once, with guidance for its placeholder name; creating the person is the main decision.
  await expect(page.getByText('Baby Boy', { exact: true })).toHaveCount(1);
  await expect(page.locator('.guidance')).toContainText('placeholder name');
  await expect(page.getByRole('button', { name: /^Create as a new person/ })).not.toHaveClass(/usa-button--outline/);
  // Off: bare keys do nothing.
  await page.locator('app-root h1').focus();
  await page.keyboard.press('c'); await page.keyboard.press('c');
  expect(decisions).toEqual([]);
  // On: one press arms, the second decides; typing in the note never does.
  await page.locator('label[for="single-keys"]').click();
  await expect(page.getByRole('button', { name: 'Create as a new person (C twice)' })).toHaveAttribute('aria-keyshortcuts', 'C Alt+Shift+C');
  await page.locator('#note').fill('cc');
  expect(decisions).toEqual([]);
  await page.locator('app-root h1').focus();
  await page.keyboard.press('c');
  await expect(page.getByRole('status').filter({ hasText: 'press C again' })).toHaveCount(1);
  expect(decisions).toEqual([]);
  await page.keyboard.press('Escape');
  await page.keyboard.press('c');
  expect(decisions).toEqual([]);
  await page.keyboard.press('c');
  await expect.poll(() => decisions).toEqual([{ decision: 'newPerson', personId: null, note: 'cc' }]);
});

test('Angular screens say Not permitted without the permission, and no empty state before a load (#161)', async ({ asUser }) => {
  const page = await asUser('cmbalt');
  for (const path of ['/app/held', '/app/match-review']) {
    await page.goto(path); await angularReady(page);
    await expect(page.locator('app-root h1')).toHaveText('Not permitted');
    await expect(page).toHaveURL(new RegExp(path + '$'));
    await expect(page.getByText('Nothing is held.')).toHaveCount(0);
    await expect(page.getByText('Nothing to review.')).toHaveCount(0);
  }
  await expectAccessible(page, 'not permitted page');
});

test('result entry lists each unit once (#165)', async ({ asUser }) => {
  const page = await asUser('supervisor');
  const results = await (await page.request.get('/api/v1/results?pageSize=5')).json() as { items: { personId: string | null }[] };
  await page.goto(`/people/${results.items.find(r => r.personId)!.personId}/results/new`);
  const units = page.locator('#UnitReported option, #Unit option, select[name="UnitReported"] option, select[name="Unit"] option');
  await page.waitForTimeout(100);
  const texts = (await units.allTextContents()).map(t => t.trim().toLowerCase().replace('µ', 'u').replace('mcg', 'ug'));
  expect(new Set(texts).size).toBe(texts.length);
});

test('the session alert spaces its actions inside the alert, and the users filter is a plain field (#170, #171)', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/cases');
  await page.locator('tbody a[href^="/cases/"]').first().click();
  await page.context().clearCookies();
  await page.locator('#case-activity-form').getByRole('button', { name: 'Log activity' }).click();
  const link = page.getByRole('link', { name: 'Sign in in a new tab' });
  const check = page.getByRole('button', { name: 'Check session' });
  await expect(link).toBeVisible();
  const [a, b, body] = [await link.boundingBox(), await check.boundingBox(), await page.locator('#alert-region .usa-alert__body').boundingBox()];
  expect(b!.x - (a!.x + a!.width) >= 8 || b!.y - (a!.y + a!.height) >= 8).toBe(true);
  expect(a!.x).toBeGreaterThanOrEqual(body!.x);

  const admin = await asUser('admin');
  await admin.setViewportSize({ width: 1280, height: 900 });
  await admin.goto('/admin/users');
  const box = await admin.locator('#user-filter').boundingBox();
  const count = await admin.locator('[data-user-count]').boundingBox();
  expect(count!.y).toBeGreaterThanOrEqual(box!.y + box!.height + 8);
});

test('match review undo guard pauses countdown on Cancel, submits once on OK, and shares in-flight writes (#307)', async ({ asUser }) => {
  test.setTimeout(60000);
  const page = await asUser('supervisor');
  const row = id(209);
  const candidateId = id(309);
  const decisions: unknown[] = [];
  let delayMs = 0;

  await page.route('**/api/v1/held**', route => {
    const url = new URL(route.request().url());
    if (url.pathname.endsWith('/rows')) {
      return route.fulfill({ json: { items: [{ stagingRowId: row, intakeBatchId: id(1), fileName: 'review.csv', senderName: 'Demo Lab', rowNumber: 1,
        reported: { lastName: 'Rivera', firstName: 'Mia' }, corrected: {}, isUrgent: false, heldSinceUtc: '2026-10-01T12:00:00Z',
        assignedToName: null, reasons: '1 possible match needs review.', reasonCodes: ['uncertainMatch'] }], totalCount: 1, pageNumber: 1, pageSize: 50 } });
    }
    return route.fulfill({
      json: { totalCount: 1, batchFileName: null, groups: [{ reasonCode: 'uncertainMatch', reportedValue: null, senderOrganizationId: null, senderName: 'Demo Lab', rowCount: 1, urgentCount: 0, oldestHeldUtc: null, batchCount: 1, sampleMessage: '1 possible match needs review.' }] },
    });
  });
  await page.route('**/api/v1/match-review**', async route => {
    if (route.request().method() === 'POST') {
      if (delayMs > 0) await new Promise(r => setTimeout(r, delayMs));
      decisions.push(route.request().postDataJSON());
      return route.fulfill({ json: { released: true, remainingCandidates: 0, message: 'Recorded.' } });
    }
    if (new URL(route.request().url()).pathname === '/api/v1/match-review') {
      return route.fulfill({ json: { items: [{ stagingRowId: row, patientName: 'Rivera, Mia', fileName: 'review.csv', rowNumber: 1, isEscalated: false }], totalCount: 1, pageNumber: 1, pageSize: 200 } });
    }
    return route.fulfill({ json: { stagingRowId: row, fileName: 'review.csv', rowNumber: 1, isUrgent: false, isEscalated: false,
      reasonMessage: '1 possible match needs review.',
      incoming: [{ key: 'lastName', label: 'Last name', value: 'Rivera' }, { key: 'firstName', label: 'First name', value: 'Mia' }],
      candidates: [{ personId: candidateId, rank: 1, personName: 'Mia Rivera', explanation: 'Close name match', warnings: [],
        fields: [{ field: 'lastName', label: 'Last name', incoming: 'Rivera', candidate: 'Rivera', agreement: 'agree', note: null }],
        aliases: null, resultCount: 1, lastCollectedDate: '2026-10-01', openCaseNumbers: null, places: [], results: [] }] } });
  });

  // Navigate via Angular router from /app/held -> /app/match-review so page.goBack() invokes canDeactivate.
  await page.goto('/app/held'); await angularReady(page);
  await page.locator('tbody .usa-checkbox__label').first().click();
  await page.getByRole('link', { name: 'Open Match review' }).click();
  await expect(page).toHaveURL(/\/app\/match-review/);
  await expect(page.getByRole('button', { name: /^Different person/ })).toBeVisible();

  // Case 1: Trigger Different person (5s undo toast), open leave dialog, trigger a second overlapping navigation
  // while the dialog is open, wait past the original 5s deadline, choose Cancel -> decision MUST NOT commit,
  // and Undo cancels cleanly with 0 POSTs.
  await page.getByRole('button', { name: /^Different person/ }).click();
  await expect(page.getByRole('button', { name: 'Undo' })).toBeVisible();
  await page.goBack();
  const dialog = page.locator('#confirmation-dialog');
  await expect(dialog).toBeVisible();
  // Fire a second SPA popstate navigation while the confirmation dialog is already open
  // so canDeactivate() is invoked concurrently without leaving the Angular app.
  await page.evaluate(() => {
    window.history.pushState(null, '', '/app/match-review');
    window.history.back();
  });
  await expect(dialog).toBeVisible();
  await page.waitForTimeout(5500);
  expect(decisions).toEqual([]);
  await dialog.getByRole('button', { name: 'Cancel' }).click();
  await expect(dialog).toBeHidden();
  await expect(page).toHaveURL(/\/app\/match-review/);
  expect(decisions).toEqual([]);
  await page.getByRole('button', { name: 'Undo' }).click();
  expect(decisions).toEqual([]);

  // Case 2: Trigger Different person, open leave dialog, wait past original deadline, choose Continue ->
  // submits EXACTLY ONCE and navigates back to /app/held.
  await page.goto('/app/held'); await angularReady(page);
  await page.locator('tbody .usa-checkbox__label').first().click();
  await page.getByRole('link', { name: 'Open Match review' }).click();
  await expect(page).toHaveURL(/\/app\/match-review/);
  await page.getByRole('button', { name: /^Different person/ }).click();
  await expect(page.getByRole('button', { name: 'Undo' })).toBeVisible();
  await page.goBack();
  await expect(dialog).toBeVisible();
  await page.waitForTimeout(5500);
  expect(decisions).toEqual([]);
  await dialog.getByRole('button', { name: 'Continue' }).click();
  await expect(page.locator('app-root h1')).toHaveText('Held records');
  expect(decisions).toHaveLength(1);

  // Case 3: Navigate during an in-flight Save now -> shares the single in-flight write (never submits twice).
  decisions.length = 0;
  delayMs = 500;
  await page.locator('tbody .usa-checkbox__label').first().click();
  await page.getByRole('link', { name: 'Open Match review' }).click();
  await expect(page.getByRole('button', { name: /^Different person/ })).toBeVisible();
  await page.getByRole('button', { name: /^Different person/ }).click();
  await page.getByRole('button', { name: 'Save now' }).click();
  await page.goBack();
  await expect(page.locator('app-root h1')).toHaveText('Held records');
  expect(decisions).toHaveLength(1);
});
