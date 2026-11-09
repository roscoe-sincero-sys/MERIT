import { randomUUID } from 'node:crypto';
import { test, expect, expectAccessible } from './helpers';
import { ISOLATED_DATA_ONLY, sql } from '../database';

test('administrator configures and versions a CSV format at desktop and phone sizes', async ({ asUser }) => {
  test.skip(!!ISOLATED_DATA_ONLY, ISOLATED_DATA_ONLY ?? '');
  const name = `Browser laboratory format ${randomUUID()}`;
  const page = await asUser('admin');
  try {
    await page.goto('/config/intake-formats');
    await page.getByLabel('Format name', { exact: true }).fill(name);
    await page.getByLabel('File format', { exact: true }).selectOption('csv');
    await page.getByText('Spreadsheet columns', { exact: true }).click();
    for (const [field, column] of Object.entries({ lastName: 'Surname', dob: 'Birth Date', loinc: 'Test', value: 'Reading', collectedDate: 'Drawn' }))
      await page.locator(`#col-${field}`).fill(column);
    for (const width of [1280, 390, 320]) {
      await page.setViewportSize({ width, height: 900 });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
      await expectAccessible(page, `laboratory format ${width}px`);
    }
    await page.getByRole('button', { name: 'Save laboratory format', exact: true }).click();
    await expect(page.getByRole('status')).toContainText('Laboratory format saved');
    const row = page.locator('li').filter({ hasText: name });
    await row.getByRole('link', { name: 'Create updated version' }).click();
    await expect(page.locator('#col-lastName')).toHaveValue('Surname');
    await page.getByLabel('Format name', { exact: true }).fill(`${name} revised`);
    await page.getByRole('button', { name: 'Save laboratory format', exact: true }).click();
    await expect(page.locator('li').filter({ hasText: `${name} revised` })).toContainText('version 2');
    await expect(page.locator('li').filter({ hasText: name }).filter({ hasNotText: 'revised' })).toContainText('Earlier version');
  } finally { sql(`DELETE dbo.intakeProfile WHERE profileName LIKE N'${name}%';`); }
});

test('staff can explicitly verify a reported caregiver in the patient workspace', async ({ asUser }) => {
  test.skip(!!ISOLATED_DATA_ONLY, ISOLATED_DATA_ONLY ?? '');
  const patient=randomUUID(), caregiver=randomUUID(), relationship=randomUUID();
  sql(`INSERT dbo.person(personId,rootTenantId,owningTenantId,lastName,lastNameKey,dob)
    SELECT TOP(1) '${patient}',rootTenantId,owningTenantId,N'Browser caregiver patient',N'BROWSERCAREGIVERPATIENT','2024-01-01' FROM dbo.person WHERE lastName=N'Rivera' AND firstName=N'Mia';
    INSERT dbo.person(personId,rootTenantId,owningTenantId,lastName,lastNameKey)
    SELECT '${caregiver}',rootTenantId,owningTenantId,N'Browser caregiver',N'BROWSERCAREGIVER' FROM dbo.person WHERE personId='${patient}';
    INSERT dbo.personRelationship(personRelationshipId,rootTenantId,owningTenantId,personId,relatedPersonId,role,source)
    SELECT '${relationship}',rootTenantId,owningTenantId,'${patient}','${caregiver}','caregiver','labReport' FROM dbo.person WHERE personId='${patient}';`);
  try {
    const page=await asUser('supervisor'); await page.goto(`/people/${patient}`);
    await page.getByText('Verify or update caregiver', { exact:true }).click();
    await page.getByLabel('Verified relationship', { exact:true }).selectOption('parent');
    await page.locator(`label[for="primary-${relationship}"]`).click();
    await page.getByText('I verified identity, relationship and contact information.',{exact:true}).click();
    for(const width of [1280,390,320]) {
      await page.setViewportSize({width,height:900});
      expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true);
      await expectAccessible(page,`caregiver verification ${width}px`);
    }
    await page.getByRole('button',{name:'Save verified relationship',exact:true}).click();
    await expect(page.locator('#section-relationships')).not.toContainText('(unverified)');
    await expect(page.locator('#section-relationships')).toContainText('Primary contact');
    expect(sql(`SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.personRelationship WHERE personRelationshipId='${relationship}' AND role='parent' AND verifiedDate IS NOT NULL AND isPrimaryContact=1 AND hasLegalCustody=0;`).toString()).toMatch(/1/);
  } finally {
    sql(`DELETE dbo.personRelationship WHERE personRelationshipId='${relationship}'; DELETE dbo.person WHERE personId IN('${patient}','${caregiver}');`);
  }
});
