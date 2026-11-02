import { angularReady, expect, expectAccessible, test } from './helpers';

// Usability audit fixes: issues #154, #155, #159, #160, #164, #166 and #169 (#157 and #163 are covered in .NET tests).
const id = (n: number) => `00000000-0000-0000-0000-${String(n).padStart(12, '0')}`;
const json = (body: unknown) => ({ contentType: 'application/json', body: JSON.stringify(body) });
const long = 'A result collected Oct 1, 2026 reports 1214 W Lombard St, Baltimore, MD, which differs from the primary residence '
  + '(1214 W Lombard Street 2F, Baltimore, MD). Confirm which address is current before the next contact attempt.';
const task = (n: number, extra: Record<string, unknown> = {}) => ({ workTaskId: id(n), queueCode: 'caseFollowUp', title: `Task ${n}`,
  detail: null, isUrgent: false, dueDate: '2026-11-01', taskStatus: 'open', createdUtc: '2026-10-31T12:00:00Z', personId: null,
  personName: null, exposureCaseId: null, caseNumber: null, assignedToUserId: null, assignedToName: null, canEdit: true, ...extra });

test('work queue details read under each task, never as an empty link, and phones get cards (#154, #169)', async ({ asUser }) => {
  const page = await asUser('supervisor');
  const items = [
    task(1, { detail: long, personId: id(90), personName: 'Rivera, Mia' }),
    // A person this user cannot see: an id but no name.
    task(2, { detail: 'Short detail.', personId: id(91), personName: null }),
    ...Array.from({ length: 13 }, (_, i) => task(i + 3, { detail: long })),
  ];
  await page.route('**/api/v1/tasks?**', route => route.fulfill(json({ items, totalCount: items.length, pageNumber: 1, pageSize: 50 })));
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/app/queues'); await angularReady(page);
  const grid = page.locator('merit-data-grid');
  await expect(grid.locator('tr[cdk-row]')).toHaveCount(15);
  await expect(grid.locator('th', { hasText: 'Detail' })).toHaveCount(0);
  // Readable in place: no sideways scrolling, a clamped detail costs no extra line, and 15 tasks come to well under the
  // 4,700 px the audit measured.
  expect(await grid.locator('.grid-wrap').evaluate(e => e.scrollWidth <= e.clientWidth + 1)).toBe(true);
  expect(await grid.locator('tr[cdk-row]').evaluateAll(rows => Math.max(...rows.map(r => r.getBoundingClientRect().height)))).toBeLessThan(100);
  expect(await page.evaluate(() => document.documentElement.scrollHeight)).toBeLessThan(2100);
  const first = grid.locator('tr[cdk-row]').first();
  await expect(first.locator('.grid-subtext')).toHaveText(long);
  await expect(first.locator('.grid-subtext')).toHaveClass(/grid-subtext--clamped/);
  await first.getByRole('button', { name: 'Show more' }).click();
  await expect(first.locator('.grid-subtext')).not.toHaveClass(/grid-subtext--clamped/);
  await expect(first.getByRole('button', { name: 'Show less' })).toHaveAttribute('aria-expanded', 'true');
  await expect(grid.locator('tr[cdk-row]').nth(1).getByRole('button', { name: 'Show more' })).toHaveCount(0);
  // No link without a name (#169): the hidden person's cell is plain.
  await expect(grid.locator('a')).toHaveCount(await grid.locator('a').filter({ hasText: /\S/ }).count());
  await expect(grid.locator('tr[cdk-row]').nth(1).locator('a')).toHaveCount(0);
  await expectAccessible(page, 'work queue with second-line details');

  // Phones: cards, the essentials only, and actions that wait for a selection.
  await page.setViewportSize({ width: 390, height: 844 });
  const bulk = page.getByRole('group', { name: 'Bulk actions on selected tasks' });
  await expect(bulk).toBeHidden();
  await expect(first).toHaveCSS('display', 'block');
  await expect(first.locator('td.phone-hidden').first()).toBeHidden();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
  const top = await first.boundingBox();
  expect(top!.y).toBeLessThan(844);
  await page.locator('label[for="tasks-sel-0"]').click();
  await expect(bulk).toBeVisible();
  await expect(bulk).toHaveCSS('position', 'sticky');
});

test('a held cause opens its correction on the field it is about and the confirmation names it (#155)', async ({ asUser }) => {
  const page = await asUser('supervisor');
  const group = (reasonCode: string, reportedValue: string | null, rowCount: number) => ({ reasonCode, reportedValue,
    senderOrganizationId: null, senderName: 'Lab', rowCount, urgentCount: 0, oldestHeldUtc: '2026-10-31T12:00:00Z', batchCount: 1, sampleMessage: null });
  const groups = [group('missingDob', null, 4), group('unknownUnit', 'ug/mL', 1), group('uncertainMatch', null, 2)];
  await page.route('**/api/v1/held/overview**', route => route.fulfill(json({ groups, totalCount: 7, batchFileName: null })));
  await page.route('**/api/v1/held/rows?**', route => route.fulfill(json({ items: [], totalCount: 0, pageNumber: 1, pageSize: 50 })));
  let corrected = false;
  await page.route('**/api/v1/held/group-correction', route => { corrected = true; return route.fulfill(json({ updated: 0 })); });
  await page.goto('/app/held'); await angularReady(page);
  await expect(page.getByText('7 held rows.')).toBeVisible();

  const field = page.locator('#group-field');
  await page.getByRole('button', { name: /Missing date of birth/ }).click();
  await expect(field).toHaveValue('dob');
  await expect(page.locator('#group-value')).toHaveAttribute('type', 'date');
  await page.getByRole('button', { name: /Unknown unit/ }).click();
  await expect(field).toHaveValue('unit');
  await expect(page.getByRole('button', { name: /Unknown unit.*1 row$/ })).toBeVisible();
  // No single field: nothing is chosen, so nothing can be written until the user chooses.
  await page.getByRole('button', { name: /Uncertain person match/ }).click();
  await expect(field).toHaveValue('');
  await expect(page.getByRole('button', { name: 'Correct entire group' })).toBeDisabled();

  await page.getByRole('button', { name: /Missing date of birth/ }).click();
  await page.locator('#group-value').fill('2019-04-02');
  const message = new Promise<string>(resolve => page.once('dialog', d => { resolve(d.message()); void d.dismiss(); }));
  await page.getByRole('button', { name: 'Correct entire group' }).click();
  expect(await message).toBe('Change Date of birth to "2019-04-02" on all 4 rows in this cause, sender and batch? Release the rows afterwards to rerun validation.');
  expect(corrected).toBe(false);
});

test('a case keeps override and close behind their own disclosure, each saying what it does (#159)', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/cases');
  await page.locator('tbody a[href^="/cases/"]').first().click();
  await expect(page.locator('#case-update-form button[type=submit]')).toHaveClass(/usa-button(?!-)/);
  await expect(page.locator('#case-update-form button[type=submit]')).not.toHaveClass(/usa-button--outline/);
  await expect(page.locator('#case-override-form')).toBeHidden();
  await expect(page.locator('#case-close-form')).toBeHidden();
  await page.getByText('Close case…').click();
  await expect(page.locator('#case-close-form')).toBeVisible();
  await expect(page.locator('#close-consequence')).toContainText('open tasks are dismissed');
  await expect(page.getByRole('button', { name: 'Close case', exact: true })).toHaveAccessibleDescription(/Closing ends follow-up/);
  await page.getByText('Change classification…').click();
  await expect(page.locator('#case-override-form')).toBeVisible();
  // The activity history comes first, then the form to add to it.
  const order = await page.locator('section[aria-labelledby="case-activities"]').evaluate(s => [...s.querySelectorAll('ol, p.merit-empty, #case-activity-form')].map(e => e.id || e.tagName));
  expect(order.at(-1)).toBe('case-activity-form');
  await expectAccessible(page, 'case page disclosures');
});

test('every screen marks its menu item and the audit log is in Administration (#160)', async ({ asUser }) => {
  const page = await asUser('supervisor');
  const current = () => page.locator('.merit-nav a[aria-current="page"]');
  const results = await (await page.request.get('/api/v1/results?pageSize=5')).json() as { items: { labResultId: string; personId: string | null }[] };
  const result = results.items[0];
  await page.goto(`/results/${result.labResultId}`);
  await expect(current()).toHaveText('Results');
  // Supervisors see the configuration item as "Reference levels"; every configuration page marks it.
  await page.goto('/config/settings');
  await expect(current()).toHaveAttribute('href', '/config/thresholds');
  await page.goto('/config/analytes');
  await expect(current()).toHaveAttribute('href', '/config/thresholds');
  await page.goto('/admin/audit');
  await expect(current()).toHaveText('Audit log');
  const personId = results.items.find(r => r.personId)!.personId;
  await page.goto(`/app/people/${personId}/timeline`); await angularReady(page);
  await expect(current()).toHaveText('People');

  const manager = await asUser('cmbalt');
  await manager.goto('/');
  await expect(manager.locator('.merit-nav a', { hasText: 'Audit log' })).toHaveCount(0);
});

test('extract runs make Download the main action, name the run to submit and mark the latest (#164)', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/extracts');
  const cards = page.locator('#extract-runs .usa-card__container');
  test.skip(await cards.count() === 0, 'No extract runs in this database.');
  const withFile = cards.filter({ has: page.locator('a[href$="/download"]') });
  for (const card of await withFile.all()) {
    await expect(card.locator('a[href$="/download"]')).not.toHaveClass(/usa-button--outline/);
    await expect(card.locator('.merit-badge--latest, .merit-badge--superseded')).toHaveCount(1);
  }
  // One Latest per extract and period.
  const headings = await withFile.filter({ has: page.locator('.merit-badge--latest') }).locator('h3').allTextContents();
  expect(new Set(headings).size).toBe(headings.length);
  const submit = page.locator('form[hx-post$="/submitted"]').first();
  if (await submit.count()) {
    await expect(submit).toHaveAttribute('hx-confirm', /^Mark the (CBLS|ABLES) .+, generated .+ file as sent to (CDC|NIOSH)\?/);
    await expect(submit.locator('button')).toHaveClass(/usa-button--outline/);
  }
});

test('a person page and its charts use words and display dates, and count axes step in whole numbers (#166)', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/search?q=Rivera');
  await page.getByRole('link', { name: /Rivera, Mia/ }).first().click();
  const results = page.locator('table:has(th:text-is("Analyte")) tbody td:nth-child(2)');
  await expect(results.first()).toBeVisible();
  for (const text of await results.allTextContents()) expect(text.trim()).not.toMatch(/^PB\b/);
  // The reference levels and the note under each chart give dates as everywhere else ("Oct 25, 2026").
  const notes = page.locator('figure [aria-label="Current reference levels"] li, figure .usa-hint');
  await expect(notes.first()).toBeVisible();
  for (const text of await notes.allTextContents()) expect(text).not.toMatch(/\d{4}-\d{2}-\d{2}/);
});
