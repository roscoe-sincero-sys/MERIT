import { expect, expectAccessible, test } from './helpers';

test('pregnancy validation uses a focused summary and linked field errors', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/search?q=Rivera');
  await page.getByRole('link', { name: /Rivera, Mia/ }).first().click();
  await page.getByRole('button', { name: 'Add episode', exact: true }).click();
  const form = page.locator('#pregnancy-form form');
  await expect(form).toHaveAttribute('novalidate', '');
  await expect(form.locator('label[for=pregnancy-StartDate] abbr')).toHaveText('*');
  await form.getByRole('button', { name: 'Save episode' }).click();
  const summary = page.locator('#pregnancy-form .merit-error-summary');
  await expect(summary).toBeFocused();
  await expect(summary.locator('a[href="#pregnancy-StartDate"]')).toBeVisible();
  await expect(page.locator('#pregnancy-StartDate')).toHaveAttribute('aria-invalid', 'true');
  await expect(page.locator('#pregnancy-StartDate')).toHaveAttribute('aria-describedby', 'pregnancy-StartDate-error');
  await expect(page.locator('#pregnancy-StartDate-error')).toBeVisible();
  await expect(form.locator('.usa-form-group--error')).toHaveCount(1);
  await summary.locator('a').first().click();
  await expect(page.locator('#pregnancy-StartDate')).toBeFocused();
  await expectAccessible(page, 'pregnancy validation');
});

test('sign-in required fields use linked server errors without browser popups', async ({ browser }) => {
  const context = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await context.newPage();
  await page.goto('/account/signin');
  await expect(page.locator('form')).toHaveAttribute('novalidate', '');
  await page.getByRole('button', { name: 'Continue', exact: true }).click();
  await expect(page.locator('.merit-error-summary')).toBeFocused();
  await expect(page.locator('#UserName')).toHaveAttribute('aria-invalid', 'true');
  await expect(page.locator('#Password')).toHaveAttribute('aria-describedby', 'Password-error');
  await expect(page.locator('#UserName-error')).toBeVisible();
  await expectAccessible(page, 'sign-in validation');
  await context.close();
});
