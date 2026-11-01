import { expect, expectAccessible, test } from './helpers';

test('grant rows append without losing edits or duplicating field IDs', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/account/profiles/new');
  await page.locator('#ProfileName').fill('Keep this draft');
  const rows = page.locator('.merit-grant-row');
  const count = await rows.count();
  await rows.first().locator('[name$=AccessLevel]').selectOption('write');
  await page.getByRole('button', { name: 'Add another grant' }).click();
  await page.getByRole('button', { name: 'Add another grant' }).click();
  await expect(rows).toHaveCount(count + 2);
  await expect(page.locator('#ProfileName')).toHaveValue('Keep this draft');
  await expect(rows.first().locator('[name$=AccessLevel]')).toHaveValue('write');
  await expect(rows.last().locator('select').first()).toBeFocused();
  await expect(rows.last().locator('select').first()).toHaveAttribute('name', `Grants[${count + 1}].TenantId`);
  const duplicates = await page.locator('[id]').evaluateAll(elements => { const ids = elements.map(e => e.id); return ids.filter((id, i) => ids.indexOf(id) !== i); });
  expect(duplicates).toEqual([]);
  await expectAccessible(page, 'dynamic profile grants');
});

test('users filter accepts partial names and county checkboxes survive validation', async ({ browser }) => {
  const context = await browser.newContext({ storageState: '.auth/admin.json', ignoreHTTPSErrors: true });
  const page = await context.newPage();
  await page.goto('/admin/users');
  const filter = page.getByRole('searchbox', { name: 'Filter listed users by name, user name, or tenant' });
  await filter.fill('Avery');
  await expect(page.locator('[data-user-row]:visible')).toHaveCount(1);
  await filter.fill('no-such-person-xyz');
  await expect(page.locator('[data-user-count]')).toContainText('No users match');
  await filter.fill('');
  expect(await page.locator('[data-user-row]:visible').count()).toBeGreaterThan(1);
  await expect(page.getByLabel('Give access to someone from another tenant: their exact user name')).toBeHidden();
  await page.goto('/admin/tenants');
  const county = page.locator('[name=SelectedCountyFips]').first();
  await expect(county).toBeAttached(); await county.focus(); await page.keyboard.press('Space');
  const value = await county.inputValue();
  await page.getByRole('button', { name: 'Add tenant', exact: true }).click();
  await expect(page.locator('.merit-error-summary')).toBeVisible();
  await expect(page.locator(`[name=SelectedCountyFips][value="${value}"]`)).toBeChecked();
  await expectAccessible(page, 'tenant county selection');
  await context.close();
});

test('profile link carries the current local page and provides a return link', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/search?q=Rivera');
  await page.getByRole('button', { name: 'Menu', exact: true }).click();
  await page.locator('.merit-header__profile').click();
  await expect(page.getByRole('link', { name: 'Return to previous page' })).toHaveAttribute('href', '/search?q=Rivera');
});
