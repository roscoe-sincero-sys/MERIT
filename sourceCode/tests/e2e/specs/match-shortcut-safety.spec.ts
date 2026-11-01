import { angularReady, expect, test } from './helpers';

test('bare characters cannot decide; modified shortcuts ignore drafts and require safeguard confirmation', async ({ asUser }) => {
  const page = await asUser('supervisor');
  const row = '00000000-0000-0000-0000-000000000107';
  const person = '00000000-0000-0000-0000-000000000207';
  const decisions: unknown[] = [];
  await page.route('**/api/v1/match-review**', async route => {
    if (route.request().method() === 'POST') {
      decisions.push(route.request().postDataJSON());
      return route.fulfill({ json: { released: false, remainingCandidates: 1, message: 'Reviewed.' } });
    }
    if (new URL(route.request().url()).pathname === '/api/v1/match-review')
      return route.fulfill({ json: { items: [{ stagingRowId: row, patientName: 'Incoming record', fileName: 'review.csv', rowNumber: 1, isEscalated: false }], totalCount: 1, pageNumber: 1, pageSize: 200 } });
    return route.fulfill({ json: { stagingRowId: row, fileName: 'review.csv', rowNumber: 1, isUrgent: false, isEscalated: false,
      reasonMessage: 'Uncertain match', incoming: [], candidates: [{ personId: person, rank: 1, personName: 'Candidate patient', fields: [],
        warnings: ['Date of birth differs.'], places: [], results: [], resultCount: 0 }] } });
  });
  await page.goto('/app/match-review');
  await angularReady(page);
  const same = page.getByRole('button', { name: 'Same person as Candidate patient (Alt+Shift+S)', exact: true });
  await expect(same).toBeEnabled();
  await expect(same).toHaveAttribute('aria-keyshortcuts', 'Alt+Shift+S');
  for (const target of [page.locator('app-root h1'), same, page.getByRole('link', { name: 'Skip to main content' })]) {
    await target.focus();
    for (const key of ['s', 'd', 'n', 'c', 'h', 'j', 'k', '1']) await page.keyboard.press(key);
  }
  expect(decisions).toEqual([]);
  await page.locator('#note').fill('Keep this draft');
  await page.keyboard.press('Alt+Shift+D');
  await expect(page.locator('#note')).toHaveValue('Keep this draft');
  await page.locator('app-root h1').focus();
  for (const extra of [{ repeat: true }, { isComposing: true }])
    await page.locator('app-root h1').dispatchEvent('keydown', { key: 'd', altKey: true, shiftKey: true, bubbles: true, ...extra });
  expect(decisions).toEqual([]);
  page.once('dialog', async dialog => { expect(dialog.message()).toContain('Date of birth differs'); await dialog.dismiss(); });
  await page.keyboard.press('Alt+Shift+S');
  expect(decisions).toEqual([]);
  page.once('dialog', dialog => dialog.accept());
  await same.click();
  await expect.poll(() => decisions).toEqual([{ decision: 'same', personId: person, note: 'Keep this draft' }]);
});
