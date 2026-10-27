import { expect, test } from './helpers';

// Form errors (issue #46): required fields are marked, an empty submit reaches the server (no browser bubbles), and the
// server's messages show as a USWDS error summary that links to each field, and beside the fields. Nothing is saved.

test('add a person: required fields are marked and an empty submit shows the error summary', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/people/new');
  const form = page.locator('#person-form form');
  await expect(form).toHaveAttribute('novalidate', '');
  await expect(page.locator('label[for=LastName] abbr[title=required]')).toHaveText('*');
  await expect(page.locator('label[for=FirstName] abbr')).toHaveCount(0);

  // The name boxes line up: an empty error message took space beside the last name only.
  const [last, first] = [await page.locator('#LastName').boundingBox(), await page.locator('#FirstName').boundingBox()];
  expect(Math.abs(last!.y - first!.y)).toBeLessThanOrEqual(1);

  await page.getByRole('button', { name: 'Save' }).click();
  await page.waitForLoadState('networkidle');
  const summary = page.locator('#error-summary');
  await expect(summary).toBeFocused();
  await expect(summary.getByRole('heading')).toHaveText('Some fields need attention');
  const link = summary.getByRole('link', { name: 'Last name is required.' });
  await expect(link).toHaveAttribute('href', '#LastName');
  await expect(page.locator('#LastName')).toHaveAttribute('aria-invalid', 'true');
  await expect(page.locator('#LastName')).toHaveAttribute('aria-describedby', 'LastName-error');
  await expect(page.locator('#LastName-error')).toHaveText('Last name is required.');
  await expect(page.locator('#FirstName')).not.toHaveAttribute('aria-invalid', /.*/);
  await link.click();
  await expect(page.locator('#LastName')).toBeFocused();
});

test('enter a result: an empty submit lists every missing field in form order', async ({ asUser }) => {
  const page = await asUser('supervisor');
  const results = await (await page.request.get('/api/v1/results?pageSize=20')).json() as { items: { personId: string | null }[] };
  const personId = results.items.find(r => r.personId)!.personId!;
  await page.goto(`/people/${personId}/results/new`);
  await expect(page.locator('#result-form form')).toHaveAttribute('novalidate', '');
  for (const id of ['AnalyteId', 'ValueText', 'CollectedDate', 'LabOrganizationId-text']) {
    await expect(page.locator(`label[for="${id}"] abbr[title=required]`), id).toHaveCount(1);
  }
  await page.locator('#CollectedDate').fill('');
  await page.getByRole('button', { name: 'Save result' }).click();
  const summary = page.locator('#error-summary');
  await expect(summary).toBeFocused();
  const links = summary.getByRole('link');
  const hrefs = await links.evaluateAll(as => as.map(a => a.getAttribute('href')));
  // Value, collection date and laboratory are missing; they are listed in the order they appear on the form.
  expect(hrefs).toEqual(expect.arrayContaining(['#ValueText', '#CollectedDate', '#LabOrganizationId-text']));
  expect(hrefs.indexOf('#ValueText')).toBeLessThan(hrefs.indexOf('#LabOrganizationId-text'));
  await expect(page.locator('#LabOrganizationId-text')).toHaveAttribute('aria-describedby', 'LabOrganizationId-hint LabOrganizationId-text-error');
});
