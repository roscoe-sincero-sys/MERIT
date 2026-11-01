import { angularReady, expect, expectAccessible, test } from './helpers';

test('grid exposes one active sort and one row Tab stop with a direct pagination route', async ({ asUser }) => {
  const page = await asUser('supervisor');
  const items = Array.from({ length: 50 }, (_, i) => ({ workTaskId: `00000000-0000-0000-0000-${String(i + 1).padStart(12, '0')}`,
    queueCode: 'caseFollowUp', title: `Task ${i + 1}`, detail: null, isUrgent: false, dueDate: '2026-11-01', taskStatus: 'open',
    createdUtc: '2026-10-31T12:00:00Z', personId: null, personName: null, exposureCaseId: null, caseNumber: null,
    assignedToUserId: null, assignedToName: null, canEdit: true }));
  await page.route('**/api/v1/tasks?**', route => route.fulfill({ json: { items, totalCount: 100, pageNumber: 1, pageSize: 50 } }));
  await page.goto('/app/queues'); await angularReady(page);
  const grid = page.locator('merit-data-grid');
  const rows = grid.locator('tr[cdk-row]');
  await expect(rows).toHaveCount(50);
  await expect(grid.locator('th[aria-sort]')).toHaveCount(1);
  await expect(grid.locator('th[aria-sort=none]')).toHaveCount(0);
  await expect(grid.locator('tr[cdk-row][tabindex="0"]')).toHaveCount(1);
  await rows.first().focus(); await page.keyboard.press('End');
  await expect(rows.last()).toBeFocused();
  await expect(rows.last()).toHaveAttribute('tabindex', '0');
  await page.keyboard.press('ArrowUp'); await expect(rows.nth(48)).toBeFocused();
  await page.keyboard.press('Home'); await expect(rows.first()).toBeFocused();
  const checkbox = page.getByLabel('Select row 1', { exact: true });
  await checkbox.focus(); await page.keyboard.press('Space'); await expect(checkbox).toBeChecked();
  await page.keyboard.press('ArrowDown'); await expect(checkbox).toBeFocused();
  const skip = grid.getByRole('link', { name: 'Skip table to pagination' });
  await skip.focus(); await page.keyboard.press('Enter');
  const target = await skip.getAttribute('href');
  await expect(page.locator(target!)).toBeFocused();
  await expectAccessible(page, 'grid sorting and row navigation');
});
