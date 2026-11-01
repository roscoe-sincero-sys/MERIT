import { expect, expectAccessible, test } from './helpers';

test('person trend points expose exact values, focusable links, and a wrapping reference legend', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/search?q=Rivera');
  await page.getByRole('link', { name: /Rivera, Mia/ }).first().click();
  const chart = page.locator('svg.merit-chart').first();
  await expect(chart).toHaveAttribute('role', 'group');
  const point = chart.locator('a[href^="/results/"]').first();
  await expect(point).toHaveAttribute('aria-label', /\d{4}-\d{2}-\d{2}: .*\d/);
  expect(await point.locator('circle title').textContent()).toBe(await point.getAttribute('aria-label'));
  const figure = chart.locator('..');
  await expect(figure.locator('[aria-label="Current reference levels"]')).toBeVisible();
  await expect(figure).toContainText('they do not reconstruct historical classifications');
  await point.focus(); await expect(point).toBeFocused();
  const href = await point.getAttribute('href');
  await expectAccessible(page, 'person trend links and reference legend');
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(new RegExp(href + '$'));
});
