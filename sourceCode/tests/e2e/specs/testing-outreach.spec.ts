import { randomUUID } from 'node:crypto';
import { expect, expectAccessible, test } from './helpers';
import { ISOLATED_DATA_ONLY, sql } from '../database';

function setup(patient: string, task: string, source: string): void {
  sql(`INSERT dbo.person(personId,rootTenantId,owningTenantId,lastName,lastNameKey,dob)
    SELECT TOP(1) '${patient}',rootTenantId,owningTenantId,N'Outreach browser ${patient.slice(0,8)}',N'OUTREACHBROWSER','2018-01-01'
    FROM dbo.person WHERE lastName=N'Rivera' AND firstName=N'Mia';
    INSERT dbo.labResult(labResultId,rootTenantId,owningTenantId,personId,analyteSpecimenId,collectedDate,receivedUtc,resultStatus,valueReported,unitReported,valueCanonical,unitCanonical,labOrganizationId,reportedSnapshotJson,intakeBatchId,population,ageAtCollectionMonths,sampleType,operator)
    SELECT TOP(1) '${source}',r.rootTenantId,r.owningTenantId,'${patient}',r.analyteSpecimenId,'2020-08-01','2020-08-03T14:00:00','final',4,r.unitReported,4,r.unitCanonical,r.labOrganizationId,N'{}',r.intakeBatchId,'child',31,'capillary','='
    FROM dbo.labResult r JOIN config.analyteSpecimen s ON s.analyteSpecimenId=r.analyteSpecimenId JOIN config.analyte a ON a.analyteId=s.analyteId WHERE a.code='PB' AND s.specimen='wholeBlood';
    INSERT dbo.workTask(workTaskId,rootTenantId,owningTenantId,personId,queueCode,title,dueDate,testingPlanJson)
    SELECT '${task}',rootTenantId,owningTenantId,personId,'testingFollowUp',N'Outreach browser plan',DATEADD(DAY,3,CONVERT(DATE,SYSUTCDATETIME() AT TIME ZONE 'UTC' AT TIME ZONE 'Eastern Standard Time')),JSON_OBJECT('kind':'later')
    FROM dbo.person WHERE personId='${patient}';`);
}
function cleanup(patient: string): void {
  sql(`DELETE dbo.workTask WHERE personId='${patient}'; DELETE dbo.labResult WHERE personId='${patient}';
    DELETE dbo.personAccess WHERE personId='${patient}'; DELETE dbo.person WHERE personId='${patient}';`);
}

test('historical upload form explains receipt provenance and rejects missing attestation', async ({ asUser }) => {
  test.skip(!!ISOLATED_DATA_ONLY, ISOLATED_DATA_ONLY ?? '');
  const page = await asUser('supervisor'); await page.goto('/intake');
  await page.getByText('Previously received reports / historical import', { exact: true }).click();
  await expect(page.getByText('If receipt is unknown, verify it before importing.', { exact: false })).toBeVisible();
  await page.locator('label[for=historical]').click();
  await page.locator('#departmentReceivedAt').fill('2020-08-03T10:00');
  await page.locator('#receiptEvidence').fill('Synthetic verified receipt ledger reference.');
  await page.locator('#file').setInputFiles({ name: `historical-browser-${randomUUID()}.csv`, mimeType: 'text/csv', buffer: Buffer.from('header\nrow\n') });
  for (const width of [1280, 390, 320]) {
    await page.setViewportSize({ width, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
    await expectAccessible(page, `historical receipt form ${width}px`);
  }
  await page.getByRole('button', { name: 'Upload', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Historical import requires');
});

test('testing roster filters, export and patient warnings work at desktop and phone widths', async ({ asUser }) => {
  test.skip(!!ISOLATED_DATA_ONLY, ISOLATED_DATA_ONLY ?? '');
  const patient = randomUUID(), task = randomUUID(), source = randomUUID(); setup(patient, task, source);
  try {
    const page = await asUser('supervisor'); await page.goto('/testing?dueFilter=next7&planKind=later');
    const card = page.locator('article').filter({ has: page.locator(`#test-${task}`) });
    await expect(card).toContainText('Due within 7 days'); await expect(card).toContainText('No caregiver recorded');
    for (const width of [1280, 390, 320]) {
      await page.setViewportSize({ width, height: 900 });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
      await expectAccessible(page, `testing outreach ${width}px`);
    }
    const csv = await page.request.get('/testing.csv?dueFilter=next7&planKind=later');
    expect(csv.status()).toBe(200); expect(await csv.text()).toContain(patient);
    await card.getByRole('link', { name: 'Patient overview', exact: true }).click();
    const summary = page.getByRole('region', { name: 'Next blood lead test and clinical reviews' });
    await expect(summary).toContainText('Due within 7 days');
    await expectAccessible(page, 'patient testing summary');
    await summary.getByRole('link').click();
    await expect(page.locator(`#plan-${task}`)).toContainText('Due within 7 days');
    await page.goto('/testing?dueFilter=overdue&planKind=later');
    await expect(page.locator(`#test-${task}`)).toHaveCount(0);
  } finally { cleanup(patient); }
});

test('historical clinical draw requires an explicit current-care decision and retains it', async ({ asUser }) => {
  test.skip(!!ISOLATED_DATA_ONLY, ISOLATED_DATA_ONLY ?? '');
  const patient = randomUUID(), task = randomUUID(), source = randomUUID(); setup(patient, task, source);
  // Remove the roster-only fixture; the following plan is recorded through the actual clinical form.
  sql(`DELETE dbo.workTask WHERE workTaskId='${task}';`);
  try {
    const page = await asUser('supervisor'); await page.goto(`/people/${patient}/care/testing`);
    await page.locator('#planKind').selectOption('confirmation'); await page.locator('#testingSource').selectOption(source);
    await page.locator('#testingEvidence').fill('Reviewed original source and current clinician plan.');
    await page.locator('label[for=clinicianConfirmed]').click(); await page.getByRole('button', { name: 'Save reviewed testing plan', exact: true }).click();
    await expect(page.getByRole('alert')).toContainText('Historical policy reconstruction is not supported');
    await page.getByText('Source draw or screening target before March 2025', { exact: true }).click();
    await page.locator('label[for=currentGuidanceConfirmed]').click();
    await page.locator('#currentGuidanceReason').fill('Clinician chose current guidance for current care, not historical reconstruction.');
    if (!await page.locator('#clinicianConfirmed').isChecked()) await page.locator('label[for=clinicianConfirmed]').click();
    await page.getByRole('button', { name: 'Save reviewed testing plan', exact: true }).click();
    await expect(page.getByRole('status').filter({ hasText: 'Reviewed testing plan saved' })).toBeVisible();
    await expect(page.locator('article')).toContainText('Current-care policy decision');
    await expectAccessible(page, 'explicit current care decision');
  } finally { cleanup(patient); }
});
