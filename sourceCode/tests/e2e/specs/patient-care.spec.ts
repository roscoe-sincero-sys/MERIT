import { randomUUID } from 'node:crypto';
import { expect, expectAccessible, test } from './helpers';
import { ISOLATED_DATA_ONLY, sql } from '../database';

function patient(id: string): void {
  sql(`INSERT dbo.person (personId,rootTenantId,owningTenantId,lastName,lastNameKey)
    SELECT TOP(1) '${id}',rootTenantId,owningTenantId,N'Care browser ${id.slice(0, 8)}',N'CAREBROWSER'
    FROM dbo.person WHERE lastName=N'Rivera' AND firstName=N'Mia';`);
}
function cleanup(id: string): void {
  sql(`DELETE dbo.personDocument WHERE personId='${id}' AND supersedesDocumentId IS NOT NULL;
    DELETE dbo.personDocument WHERE personId='${id}'; DELETE dbo.personActivity WHERE personId='${id}';
    DELETE dbo.personAccess WHERE personId='${id}'; DELETE dbo.person WHERE personId='${id}';`);
}

test('staff record an internal patient activity without a case at phone widths', async ({ asUser }) => {
  test.skip(!!ISOLATED_DATA_ONLY, ISOLATED_DATA_ONLY ?? '');
  const id = randomUUID(); patient(id);
  try {
    const page = await asUser('supervisor');
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(`/people/${id}`);
    await page.getByRole('link', { name: 'Patient activities, notification deadlines and documents' }).click();
    await expect(page.getByRole('heading', { name: 'Patient follow-up and documents', exact: true })).toBeVisible();
    await page.locator('#note').fill('Coordinated pre-case follow-up with the clinical team.');
    await page.getByRole('button', { name: 'Record activity', exact: true }).click();
    await expect(page.getByRole('status').filter({ hasText: 'Activity recorded' })).toBeVisible();
    const activity = page.locator('#activity article').filter({ hasText: 'Coordinated pre-case follow-up' });
    await expect(activity).toHaveCount(1);
    await expect(activity).toContainText('Internal'); await expect(activity).toContainText('Patient only');
    for (const width of [390, 320, 1280]) {
      await page.setViewportSize({ width, height: 900 });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
      await expectAccessible(page, `patient care ${width}px`);
    }
  } finally { cleanup(id); }
});

test('private document invalid-type feedback, retry, progress and retained revision work on a phone', async ({ asUser }) => {
  test.skip(!!ISOLATED_DATA_ONLY, ISOLATED_DATA_ONLY ?? '');
  const id = randomUUID(); patient(id);
  try {
    const page = await asUser('supervisor'); await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(`/people/${id}/care`);
    const original = await page.locator('input[name="personDocumentId"]').inputValue();
    await page.locator('#title').fill('Clinic report original'); await page.locator('#source').fill('Clinic fax');
    await page.locator('#file').setInputFiles({ name: 'bad.exe', mimeType: 'application/octet-stream', buffer: Buffer.from('bad') });
    await page.locator('#upload-document').click();
    await expect(page.locator('#upload-status')).toContainText('Choose a PDF');
    await expect(page.locator('#title')).toHaveValue('Clinic report original');
    await page.locator('#file').setInputFiles({ name: 'report.pdf', mimeType: 'application/pdf', buffer: Buffer.from('%PDF-1.7\noriginal') });
    // Hold a real browser request to exercise cancellation without guessing how fast local SQL will save it.
    const uploadPath = `**/people/${id}/care/documents`;
    let release!: () => void;
    const held = new Promise<void>(resolve => { release = resolve; });
    await page.route(uploadPath, async route => { await held; await route.abort().catch(() => {}); });
    await page.locator('#upload-document').click(); await expect(page.locator('#cancel-upload')).toBeVisible();
    await page.locator('#cancel-upload').click(); await expect(page.locator('#upload-status')).toContainText('Upload not confirmed');
    release(); await page.unroute(uploadPath);
    await page.locator('#upload-document').click();
    await expect(page.locator('#upload-status')).toContainText('Document saved');
    await expect(page.locator('#upload-progress')).toHaveAttribute('value', '100');
    await page.locator('#upload-refresh').click();
    await expect(page.locator('#documents article')).toHaveCount(1);
    await expect(page.locator('#unsaved-work-dialog')).not.toBeVisible();
    await page.locator('#title').fill('Clinic report corrected'); await page.locator('#source').fill('Clinic revision');
    await page.locator('#supersedesDocumentId').selectOption(original);
    await page.locator('#file').setInputFiles({ name: 'corrected.pdf', mimeType: 'application/pdf', buffer: Buffer.from('%PDF-1.7\ncorrected') });
    await page.locator('#upload-document').click(); await expect(page.locator('#upload-status')).toContainText('Document saved');
    await page.locator('#upload-refresh').click();
    await expect(page.locator('#documents article')).toHaveCount(2);
    await expect(page.getByRole('link', { name: 'earlier original' })).toBeVisible();
    const download = await page.request.get(`/people/${id}/care/documents/${original}`);
    expect(download.status()).toBe(200); expect(download.headers()['content-disposition']).toContain('attachment');
    expect(await download.text()).toBe('%PDF-1.7\noriginal');
    await page.locator('#title').fill('Site photograph'); await page.locator('#source').fill('Staff photo');
    await page.locator('#category').selectOption('photo');
    await page.locator('#file').setInputFiles({ name: 'photo.png', mimeType: 'image/png',
      buffer: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=', 'base64') });
    await page.locator('#upload-document').click(); await expect(page.locator('#upload-status')).toContainText('Document saved');
    await page.locator('#upload-refresh').click(); await expect(page.locator('#documents article')).toHaveCount(3);
    await page.locator('#documents article').filter({ hasText: 'Site photograph' }).getByRole('link', { name: 'Preview / details' }).click();
    const image = page.getByRole('img', { name: 'Site photograph' }); await expect(image).toBeVisible();
    expect(await image.evaluate(el => (el as HTMLImageElement).naturalWidth)).toBe(1);
    await page.setViewportSize({ width: 320, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
    await expectAccessible(page, 'patient documents on phone');
  } finally { cleanup(id); }
});

test('approved-rule configuration is accessible and preserves invalid calendar input', async ({ asUser }) => {
  const page = await asUser('admin'); await page.goto('/config/notification-rules');
  await page.locator('#ruleName').fill('Browser calendar validation');
  await page.locator('#analyteSpecimenId').selectOption({ index: 1 });
  await page.locator('#minimumValue').fill('9000'); await page.locator('#dueInDays').fill('10');
  await page.locator('#dayBasis').selectOption('business'); await page.locator('#calendarName').fill('Unapproved test calendar');
  await page.locator('#holidays').fill('not-a-date'); await page.locator('#approvalReference').fill('Test rejection only');
  await page.locator('label[for=approved]').click();
  await page.getByRole('button', { name: 'Activate approved rule' }).click();
  await expect(page.getByRole('alert')).toContainText('YYYY-MM-DD');
  await expect(page.locator('#holidays')).toHaveValue('not-a-date');
  for (const width of [1280, 390, 320]) {
    await page.setViewportSize({ width, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
    await expectAccessible(page, `notification policy ${width}px`);
  }
});
