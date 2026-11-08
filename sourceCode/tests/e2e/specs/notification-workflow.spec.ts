import { randomUUID } from 'node:crypto';
import { expect, expectAccessible, test } from './helpers';
import { ISOLATED_DATA_ONLY, sql } from '../database';

test('an overdue receipt obligation needs actual caregiver delivery and keeps late evidence', async ({ asUser }) => {
  test.skip(!!ISOLATED_DATA_ONLY, ISOLATED_DATA_ONLY ?? '');
  const rule = randomUUID(), result = randomUUID();
  const guid = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i;
  const person = sql("SELECT TOP(1) personId FROM dbo.person WHERE lastName=N'Rivera' AND firstName=N'Mia'").toString().match(guid)![0];
  try {
    sql(`INSERT config.notificationRule (notificationRuleId,rootTenantId,owningTenantId,ruleName,analyteSpecimenId,population,
      minimumValue,recipientKind,triggerEvent,dueInDays,dayBasis,timeZoneId,calendarName,holidayDatesJson,effectiveFrom,approvalReference,approvedByUserId)
      SELECT TOP(1) '${rule}',r.rootTenantId,r.rootTenantId,N'Browser approved rule ${rule.slice(0, 8)}',r.analyteSpecimenId,'all',9000000,
      'caregiver','receipt',7,'calendar','Eastern Standard Time',N'Test calendar',N'[]','2000-01-01',N'Synthetic browser test',u.appUserId
      FROM dbo.labResult AS r CROSS JOIN auth.appUser AS u WHERE r.personId='${person}' AND u.userName=N'supervisor';
      INSERT dbo.labResult (labResultId,rootTenantId,owningTenantId,personId,analyteSpecimenId,collectedDate,receivedUtc,resultStatus,
      valueReported,unitReported,valueCanonical,unitCanonical,labOrganizationId,reportedSnapshotJson,intakeBatchId,population)
      SELECT TOP(1) '${result}',rootTenantId,owningTenantId,personId,analyteSpecimenId,'2020-01-01',DATEADD(DAY,-12,SYSUTCDATETIME()),'final',
      9999999,unitReported,9999999,unitCanonical,labOrganizationId,N'{}',intakeBatchId,'child' FROM dbo.labResult WHERE personId='${person}';`);
    const recipient = sql(`SELECT TOP(1) relatedPersonId FROM dbo.personRelationship WHERE personId='${person}' AND isDeleted=0
      AND (hasLegalCustody=1 OR isPrimaryContact=1 OR role IN ('parent','legalGuardian','fosterParent','caregiver'))`).toString().match(guid)![0];
    const page = await asUser('supervisor'); await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(`/people/${person}/care`);
    await page.getByRole('button', { name: 'Evaluate received results against approved rules' }).click();
    const obligation = page.locator('#notifications article').filter({ hasText: `Browser approved rule ${rule.slice(0, 8)}` });
    await expect(obligation).toContainText('Overdue'); await expect(obligation).toContainText('Eastern Standard Time');
    await expectAccessible(page, 'overdue notification on phone');
    await obligation.getByRole('link', { name: 'Record completed notification' }).click();
    await expect(page.locator('#activityType')).toHaveValue('notificationCompleted');
    await expect(page.locator('#audience')).toHaveValue('caregiver');
    await page.locator('#contactPersonId').selectOption(recipient.toLowerCase());
    await page.locator('#note').fill('Delivered the result and follow-up instructions to the recorded caregiver.');
    await page.locator('label[for=confirmed]').click();
    await page.setViewportSize({ width: 320, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
    await page.getByRole('button', { name: 'Save evidence and complete this obligation' }).click();
    await expect(page.getByRole('status').filter({ hasText: 'Activity recorded' })).toBeVisible();
    await expect(page.locator('#notifications article').filter({ hasText: `Browser approved rule ${rule.slice(0, 8)}` })).toContainText('Completed late');
    await page.goto('/notifications');
    await expect(page.getByRole('row').filter({ hasText: `Browser approved rule ${rule.slice(0, 8)}` })).toContainText('Completed late');
    await expectAccessible(page, 'completed notification report at 320px');
  } finally {
    sql(`DECLARE @tasks TABLE(id UNIQUEIDENTIFIER); INSERT @tasks SELECT workTaskId FROM dbo.notificationObligation WHERE notificationRuleId='${rule}';
      DELETE dbo.notificationObligation WHERE notificationRuleId='${rule}'; DELETE dbo.workTask WHERE workTaskId IN (SELECT id FROM @tasks);
      DELETE dbo.personActivity WHERE labResultId='${result}'; DELETE config.notificationRule WHERE notificationRuleId='${rule}';
      DELETE dbo.labResult WHERE labResultId='${result}';`);
  }
});
