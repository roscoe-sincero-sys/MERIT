import { randomUUID } from 'node:crypto';
import { test, expect, expectAccessible } from './helpers';
import { ISOLATED_DATA_ONLY, sql } from '../database';

for (const action of ['set', 'remove', 'unspecified']) {
  test(`apartment instruction ${action} survives request validation and owner handoff`, async ({ asUser }) => {
    test.skip(!!ISOLATED_DATA_ONLY, ISOLATED_DATA_ONLY ?? '');
    const patient=randomUUID(), caregiver=randomUUID(), street=`260 Unit Test ${patient.slice(0,8)}`;
    sql(`INSERT dbo.person(personId,rootTenantId,owningTenantId,lastName,lastNameKey,dob)
      SELECT TOP(1) '${patient}',rootTenantId,rootTenantId,N'Unit browser patient',N'UNITBROWSERPATIENT','2024-01-01' FROM dbo.person WHERE lastName=N'Rivera' AND firstName=N'Mia';
      INSERT dbo.person(personId,rootTenantId,owningTenantId,lastName,lastNameKey)
      SELECT '${caregiver}',rootTenantId,rootTenantId,N'Unit browser caregiver',N'UNITBROWSERCAREGIVER' FROM dbo.person WHERE personId='${patient}';
      INSERT dbo.personAccess(personAccessId,rootTenantId,personId,tenantId,accessReason)
      SELECT NEWID(),p.rootTenantId,p.personId,t.tenantId,'share' FROM dbo.person p JOIN auth.tenant t ON t.tenantCode='MD-BCHD' WHERE p.personId='${patient}';
      INSERT dbo.personRelationship(personRelationshipId,rootTenantId,owningTenantId,personId,relatedPersonId,role,source)
      SELECT NEWID(),rootTenantId,owningTenantId,personId,'${caregiver}','caregiver','labReport' FROM dbo.person WHERE personId='${patient}';`);
    try {
      const county=await asUser('cmbalt'); await county.goto(`/contact-corrections?personId=${patient}&caregiverId=${caregiver}`);
      await county.getByLabel('Contact to correct',{exact:true}).selectOption('address');
      await county.getByLabel('Proposed phone, email or street address',{exact:true}).fill(street);
      await county.getByText('Address details: city, state, ZIP and apartment / unit',{exact:true}).click();
      await county.getByLabel('City',{exact:true}).fill('Baltimore'); await county.getByLabel('Two-letter state',{exact:true}).fill('MD');
      await county.getByLabel('Five-digit ZIP',{exact:true}).fill('21223');
      await county.getByLabel('Apartment / unit instruction',{exact:true}).selectOption(action);
      await county.getByLabel('Source evidence and which existing contact needs correction',{exact:true}).fill('Synthetic verified apartment instruction.');
      if(action==='set') {
        await county.getByRole('button',{name:'Send correction request',exact:true}).click();
        await expect(county.getByRole('alert')).toContainText('Enter the apartment or unit to set');
        await expect(county.getByLabel('Apartment / unit instruction',{exact:true})).toHaveValue('set');
        if(!await county.getByLabel('Proposed apartment / unit',{exact:true}).isVisible()) await county.getByText('Address details: city, state, ZIP and apartment / unit',{exact:true}).click();
        await county.getByLabel('Proposed apartment / unit',{exact:true}).fill('Apt. 3F');
      }
      for(const width of [1280,390,320]) {
        await county.setViewportSize({width,height:900});
        expect(await county.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true);
        await expectAccessible(county,`unit correction ${action} ${width}px`);
      }
      await county.getByRole('button',{name:'Send correction request',exact:true}).click();
      const label=action==='set'?'Apt. 3F':action==='remove'?'None — remove any current unit':'Not specified by this request';
      await expect(county.locator('article').filter({hasText:street})).toContainText(label);
      const owner=await asUser('supervisor'); await owner.goto('/contact-corrections');
      const row=owner.locator('article').filter({hasText:street}); await expect(row).toContainText(label);
      await row.getByLabel('Review outcome',{exact:true}).selectOption('rejected');
      await row.getByLabel('Review evidence and disposition of outdated contacts',{exact:true}).fill('Synthetic browser test; do not apply to real contacts.');
      await row.getByRole('button',{name:'Record review',exact:true}).click();
      await county.reload(); await expect(county.locator('article').filter({hasText:street})).toContainText('Rejected');
    } finally {
      sql(`DELETE dbo.contactCorrection WHERE personId='${patient}'; DELETE dbo.personRelationship WHERE personId='${patient}'; DELETE dbo.personAccess WHERE personId='${patient}'; DELETE dbo.person WHERE personId IN('${patient}','${caregiver}');`);
    }
  });
}
