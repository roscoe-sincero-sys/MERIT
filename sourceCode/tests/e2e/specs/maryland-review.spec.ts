import { expect, expectAccessible, test } from './helpers';
import { ISOLATED_DATA_ONLY, sql } from '../database';

test('Maryland drafts distinguish laboratory and Department clocks without activating policies', async ({ asUser }) => {
  const page = await asUser('admin');
  await page.goto('/config/notification-rules?template=mdLabElevated');
  await expect(page.locator('#rulePurpose')).toHaveValue('labReporting');
  await expect(page.locator('#eligibilityMode')).toHaveValue('mdReporting');
  await expect(page.locator('#dueInDays')).toHaveValue('1');
  await expect(page.locator('#maximumAgeMonths')).toHaveValue('227');
  await expect(page.locator('#approved')).not.toBeChecked();
  await expect(page.locator('#calendarName')).toHaveValue('');
  await page.goto('/config/notification-rules?template=mdChild');
  await expect(page.locator('#rulePurpose')).toHaveValue('notification');
  await expect(page.locator('#minimumValue')).toHaveValue('3.5');
  await expect(page.locator('#dueInDays')).toHaveValue('10');
  await expect(page.locator('#maximumAgeMonths')).toHaveValue('71');
  await expectAccessible(page, 'Maryland policy review on desktop');
  await page.setViewportSize({ width: 320, height: 900 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
  await expectAccessible(page, 'Maryland policy review at 320px');
});

test('patient receipt review exposes source evidence and explicit cutoff decisions on a phone', async ({ asUser }) => {
  test.skip(!!ISOLATED_DATA_ONLY, ISOLATED_DATA_ONLY ?? '');
  const person = sql("SELECT TOP(1) personId FROM dbo.person WHERE lastName=N'Rivera' AND firstName=N'Mia'")
    .toString().match(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i)![0];
  const page = await asUser('supervisor');
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto(`/receipt-quality?personId=${person}`);
  await expect(page.getByRole('heading', { name: 'Laboratory receipt quality', exact: true })).toBeVisible();
  await expect(page.locator('#verify-lab-timeline')).toBeVisible();
  await expect(page.locator('#finalResultDate')).toHaveValue('');
  await expect(page.locator('#same-day-checked')).not.toBeChecked();
  await expect(page.locator('#late-confirmed')).not.toBeChecked();
  await expect(page.locator('#timeline-confirmed')).not.toBeChecked();
  await expect(page.locator('#verify-lab-timeline input[name="__RequestVerificationToken"]')).toHaveCount(1);
  await expectAccessible(page, 'patient receipt review at 390px');
  await page.setViewportSize({ width: 320, height: 900 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
  await expectAccessible(page, 'patient receipt review at 320px');
});
