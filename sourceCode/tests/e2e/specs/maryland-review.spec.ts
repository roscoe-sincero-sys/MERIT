import { expect, expectAccessible, test } from './helpers';
import { ISOLATED_DATA_ONLY, sql } from '../database';
import { randomUUID } from 'node:crypto';

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

test('reviewed confirmation keeps its initial draw and the next venous follow-up uses its own draw', async ({ asUser }) => {
  test.skip(!!ISOLATED_DATA_ONLY, ISOLATED_DATA_ONLY ?? '');
  const person = randomUUID(), initial = randomUUID(), venous = randomUUID();
  const insertResult = (id: string, date: string, sample: string) => sql(`INSERT dbo.labResult
    (labResultId,rootTenantId,owningTenantId,personId,analyteSpecimenId,collectedDate,receivedUtc,resultStatus,valueReported,unitReported,
     valueCanonical,unitCanonical,labOrganizationId,reportedSnapshotJson,intakeBatchId,population,ageAtCollectionMonths,sampleType)
    SELECT TOP(1) '${id}',r.rootTenantId,r.owningTenantId,'${person}',r.analyteSpecimenId,'${date}',r.receivedUtc,'final',4,r.unitReported,4,r.unitCanonical,
      r.labOrganizationId,N'{}',r.intakeBatchId,'child',48,'${sample}' FROM dbo.labResult AS r
    JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId=r.analyteSpecimenId JOIN config.analyte AS a ON a.analyteId=s.analyteId
    WHERE a.code='PB' AND s.specimen='wholeBlood' AND s.canonicalUnit=N'ug/dL';`);
  try {
    sql(`INSERT dbo.person(personId,rootTenantId,owningTenantId,lastName,lastNameKey,dob)
      SELECT TOP(1) '${person}',rootTenantId,owningTenantId,N'Browser clinical plan',N'BROWSERCLINICALPLAN','2022-01-01'
      FROM dbo.person WHERE lastName=N'Rivera' AND firstName=N'Mia';`);
    insertResult(initial, '2026-03-01', 'capillary');
    const page = await asUser('supervisor'); await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(`/people/${person}/care/testing`);
    await page.locator('#planKind').selectOption('confirmation');
    await page.locator('#testingSource').selectOption(initial);
    await page.locator('#testingEvidence').fill('Original initial capillary draw and clinician-approved Maryland confirmation plan reviewed.');
    await page.locator('label[for=clinicianConfirmed]').click();
    await page.getByRole('button', { name: 'Save reviewed testing plan' }).click();
    await expect(page.getByRole('status')).toContainText('Reviewed testing plan saved');
    const confirmation = page.locator('article').filter({ hasText: 'Maryland testing: confirmation' });
    await expect(confirmation).toContainText('2026-05-24');
    await expectAccessible(page, 'reviewed confirmation at 390px');
    insertResult(venous, '2026-04-01', 'venous'); await page.reload();
    await confirmation.getByText('Record actual test or clinical reason to stop this plan', { exact: true }).click();
    await confirmation.locator('select[name=completedLabResultId]').selectOption(venous);
    await confirmation.locator('textarea[name=evidence]').fill('Actual confirmatory venous draw verified against the original report.');
    await confirmation.locator('label').filter({ hasText: 'I verified the actual test' }).click();
    await confirmation.getByRole('button', { name: 'Save testing outcome' }).click();
    await expect(confirmation).toContainText('Done');
    await page.locator('#planKind').selectOption('early'); await page.locator('#testingSource').selectOption(venous);
    await page.locator('#testingEvidence').fill('Clinician selected early follow-up from the April 1 venous result.');
    await page.locator('label[for=clinicianConfirmed]').click();
    await page.getByRole('button', { name: 'Save reviewed testing plan' }).click();
    await expect(page.locator('article').filter({ hasText: 'Maryland testing: early' })).toContainText('2026-07-01');
    await page.setViewportSize({ width: 320, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
    await expectAccessible(page, 'testing history at 320px');
  } finally {
    sql(`DELETE dbo.workTask WHERE personId='${person}'; DELETE dbo.labResult WHERE personId='${person}';
      DELETE dbo.personAccess WHERE personId='${person}'; DELETE dbo.person WHERE personId='${person}';`);
  }
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
