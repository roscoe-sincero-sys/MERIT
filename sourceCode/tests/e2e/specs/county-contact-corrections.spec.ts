import { randomUUID } from 'node:crypto';
import { test, expect, expectAccessible } from './helpers';
import { ISOLATED_DATA_ONLY, sql } from '../database';

test('county requests a correction and owner reviews it at desktop and phone widths', async ({ asUser }) => {
  test.skip(!!ISOLATED_DATA_ONLY, ISOLATED_DATA_ONLY ?? '');
  const patient=randomUUID(), caregiver=randomUUID(), proposed=`correction-${randomUUID()}@example.test`;
  sql(`INSERT dbo.person(personId,rootTenantId,owningTenantId,lastName,lastNameKey,dob)
    SELECT TOP(1) '${patient}',rootTenantId,rootTenantId,N'Correction browser patient',N'CORRECTIONBROWSERPATIENT','2024-01-01' FROM dbo.person WHERE lastName=N'Rivera' AND firstName=N'Mia';
    INSERT dbo.person(personId,rootTenantId,owningTenantId,lastName,lastNameKey)
    SELECT '${caregiver}',rootTenantId,rootTenantId,N'Correction browser caregiver',N'CORRECTIONBROWSERCAREGIVER' FROM dbo.person WHERE personId='${patient}';
    INSERT dbo.personAccess(personAccessId,rootTenantId,personId,tenantId,accessReason)
    SELECT NEWID(),p.rootTenantId,p.personId,t.tenantId,'share' FROM dbo.person p JOIN auth.tenant t ON t.tenantCode='MD-BCHD' WHERE p.personId='${patient}';
    INSERT dbo.personRelationship(personRelationshipId,rootTenantId,owningTenantId,personId,relatedPersonId,role,source)
    SELECT NEWID(),rootTenantId,owningTenantId,personId,'${caregiver}','caregiver','labReport' FROM dbo.person WHERE personId='${patient}';`);
  try {
    const county=await asUser('cmbalt'); await county.goto(`/people/${patient}`);
    await county.getByRole('link',{name:'Request contact correction',exact:true}).click();
    await county.getByLabel('Contact to correct',{exact:true}).selectOption('email');
    await county.getByLabel('Proposed phone, email or street address',{exact:true}).fill(proposed);
    await county.getByLabel('Source evidence and which existing contact needs correction',{exact:true}).fill('Verified directly with caregiver in synthetic browser test.');
    for(const width of [1280,390,320]) {
      await county.setViewportSize({width,height:900});
      expect(await county.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true);
      await expectAccessible(county,`contact correction request ${width}px`);
    }
    await county.getByRole('button',{name:'Send correction request',exact:true}).click();
    const request=county.locator('article').filter({hasText:proposed});
    await expect(request).toContainText('Pending'); await expect(request).toContainText('Responsible team:');
    await expect(request.getByRole('button',{name:'Record review'})).toHaveCount(0);
    const owner=await asUser('supervisor'); await owner.goto('/contact-corrections');
    const review=owner.locator('article').filter({hasText:proposed});
    await review.getByLabel('Review evidence and disposition of outdated contacts',{exact:true}).fill('Synthetic source could not be confirmed; no contact change made.');
    await review.getByLabel('Review outcome',{exact:true}).selectOption('rejected');
    for(const width of [1280,390,320]) {
      await owner.setViewportSize({width,height:900});
      expect(await owner.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true);
      await expectAccessible(owner,`contact correction owner review ${width}px`);
    }
    await review.getByRole('button',{name:'Record review',exact:true}).click();
    await county.reload(); await expect(county.locator('article').filter({hasText:proposed})).toContainText('Rejected');
  } finally {
    sql(`DELETE dbo.contactCorrection WHERE personId='${patient}'; DELETE dbo.personRelationship WHERE personId='${patient}'; DELETE dbo.personAccess WHERE personId='${patient}'; DELETE dbo.person WHERE personId IN('${patient}','${caregiver}');`);
  }
});

test('program configuration makes responsible-agency routing explicit', async ({ asUser }) => {
  const page=await asUser('admin'); await page.goto('/config/workflows');
  await expect(page.getByLabel('Responsible agency for patient-only work',{exact:true})).toHaveValue('servingJurisdiction');
  await page.getByLabel('Responsible agency for patient-only work',{exact:true}).selectOption('definitionAgency');
  for(const width of [1280,390,320]) {
    await page.setViewportSize({width,height:900});
    expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true);
    await expectAccessible(page,`program routing ${width}px`);
  }
});
