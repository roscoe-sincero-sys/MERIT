import { randomUUID } from 'node:crypto';
import { expect, expectAccessible, test } from './helpers';
import { ISOLATED_DATA_ONLY, sql } from '../database';

test('a program administrator previews and publishes a task that a worker completes once with evidence', async ({ asUser }) => {
  test.skip(!!ISOLATED_DATA_ONLY, ISOLATED_DATA_ONLY ?? '');
  const patient = randomUUID(), other = randomUUID(), result = randomUUID();
  const name = `Browser evidence requirement ${randomUUID().slice(0, 8)}`;
  let definition: string | undefined;
  try {
    sql(`INSERT dbo.person(personId,rootTenantId,owningTenantId,lastName,lastNameKey,dob)
      SELECT '${patient}',rootTenantId,owningTenantId,N'Browser program patient',N'BROWSERPROGRAM','2022-01-01' FROM dbo.person WHERE lastName=N'Rivera' AND firstName=N'Mia';
      INSERT dbo.person(personId,rootTenantId,owningTenantId,lastName,lastNameKey,dob)
      SELECT '${other}',rootTenantId,owningTenantId,N'Browser other patient',N'BROWSEROTHER','1990-01-01' FROM dbo.person WHERE personId='${patient}';
      INSERT dbo.labResult(labResultId,rootTenantId,owningTenantId,personId,analyteSpecimenId,collectedDate,receivedUtc,resultStatus,valueReported,unitReported,valueCanonical,unitCanonical,labOrganizationId,reportedSnapshotJson,intakeBatchId,population,ageAtCollectionMonths,sampleType)
      SELECT TOP(1) '${result}',r.rootTenantId,r.owningTenantId,'${patient}',r.analyteSpecimenId,'2026-09-01','2026-10-01T16:00:00','final',9999999,r.unitReported,9999999,r.unitCanonical,r.labOrganizationId,N'{}',r.intakeBatchId,'child',56,'venous'
      FROM dbo.labResult AS r JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId=r.analyteSpecimenId JOIN config.analyte AS a ON a.analyteId=s.analyteId WHERE a.code='PB' AND s.specimen='wholeBlood';`);
    const admin = await asUser('admin'); await admin.setViewportSize({ width: 1280, height: 900 });
    await admin.goto('/config/workflows');
    definition = await admin.locator('#workflow-definition-form input[name=WorkflowDefinitionId]').inputValue();
    await admin.locator('#Name').fill(name); await admin.locator('#Description').fill('Confirm the source delivery and retain the letter reference.');
    await admin.locator('#TriggerEvent').selectOption('receivedResult'); await admin.locator('#CompletionKind').selectOption('delivery');
    await admin.locator('#AnalyteSpecimenId').selectOption({ label: 'Lead / wholeBlood / ug/dL' });
    await admin.locator('#MinimumValue').fill('9999998'); await admin.locator('#evidenceFields').fill('docRef | Document reference | text | true');
    await admin.locator('#EffectiveFrom').fill('2020-01-01');
    await admin.getByRole('button', { name: 'Save draft for preview' }).click();
    await expect(admin.getByRole('status')).toContainText('Draft saved');
    await admin.locator('#samplePersonId').fill(patient); await admin.locator('#secondSamplePersonId').fill(other);
    await admin.getByRole('button', { name: 'Preview eligibility' }).click();
    await expect(admin.getByRole('heading', { name: `Sample ${patient} — Eligible` })).toBeVisible();
    await expect(admin.getByRole('heading', { name: `Sample ${other} — Needs review` })).toBeVisible();
    await expectAccessible(admin, 'workflow definition preview on desktop');
    await admin.locator('#approvalReference').fill('Synthetic browser program approval'); await admin.locator('label[for=approved]').click();
    await admin.getByRole('button', { name: 'Publish approved version' }).click();
    await expect(admin.getByRole('status')).toContainText('Approved version published');
    const page = await asUser('supervisor'); await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(`/people/${patient}/work`); await page.getByRole('button', { name: 'Evaluate approved program tasks' }).click();
    await page.getByRole('link', { name, exact: true }).click();
    await expect(page.getByLabel('Document reference (required for completion)')).toBeVisible();
    await page.locator('#note').fill('Actual patient delivery verified.'); await page.locator('label[for=confirmed]').click();
    await page.getByRole('button', { name: 'Save task outcome and activity' }).click();
    await expect(page.getByRole('alert')).toContainText('Complete each required evidence field');
    await page.locator('#evidence_docRef').fill('BROWSER-LETTER-1'); await page.locator('label[for=confirmed]').click();
    await page.getByRole('button', { name: 'Save task outcome and activity' }).click();
    await expect(page.getByRole('status')).toContainText('recorded together');
    await expect(page.locator('main')).toContainText('Disposition: completed');
    expect(sql(`SELECT COUNT(*) FROM dbo.personActivity WHERE personId='${patient}'`).toString()).toMatch(/\b1\b/);
    await page.setViewportSize({ width: 320, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
    await expectAccessible(page, 'completed task and evidence at 320px');
  } finally {
    sql(`DELETE dbo.workTask WHERE personId IN('${patient}','${other}'); DELETE dbo.personActivity WHERE personId IN('${patient}','${other}');
      DELETE dbo.labResult WHERE personId IN('${patient}','${other}'); DELETE dbo.personAccess WHERE personId IN('${patient}','${other}');
      DELETE dbo.person WHERE personId IN('${patient}','${other}'); ${definition ? `DELETE config.workflowDefinition WHERE workflowDefinitionId='${definition}';` : ''}`);
  }
});

test('patient and case section jumps retain identity and keyboard focus on a phone', async ({ asUser }) => {
  test.skip(!!ISOLATED_DATA_ONLY, ISOLATED_DATA_ONLY ?? '');
  const guid = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i;
  const person = sql("SELECT TOP(1) personId FROM dbo.person WHERE lastName=N'Rivera' AND firstName=N'Mia'").toString().match(guid)![0];
  const caseId = sql(`SELECT TOP(1) exposureCaseId FROM dbo.exposureCase WHERE personId='${person}' AND caseStatus<>'closed' AND isDeleted=0`).toString().match(guid)![0];
  const page = await asUser('supervisor'); await page.setViewportSize({ width: 390, height: 844 });
  await page.goto(`/people/${person}`);
  const sections = page.getByRole('navigation', { name: 'Patient sections' });
  await sections.getByRole('link', { name: 'Lab results', exact: true }).focus(); await page.keyboard.press('Enter');
  await expect(page.locator('#section-results')).toBeFocused(); await expect(page.locator('.workspace-context')).toBeInViewport();
  await sections.getByRole('link', { name: 'Cases and history', exact: true }).click(); await expect(page.locator('#section-cases')).toBeFocused();
  await expectAccessible(page, 'patient section destinations at 390px');
  await page.goto(`/cases/${caseId}`);
  await page.getByRole('navigation', { name: 'Case sections' }).getByRole('link', { name: 'Activity', exact: true }).click();
  await expect(page.locator('#case-activities')).toBeFocused(); await expect(page.locator('.workspace-context')).toBeInViewport();
  await page.setViewportSize({ width: 320, height: 900 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
  await expectAccessible(page, 'case section destinations at 320px');
});
