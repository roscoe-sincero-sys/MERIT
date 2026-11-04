import { sql } from './sql.ts';

/**
 * Training scenarios: synthetic tenants, accounts and records a video needs beyond the shipped demo data, created in
 * the training database through the same procedures the application and the integration tests use. Every scenario is
 * idempotent (fixed keys, procedures that converge on a re-run), so prepare() can call it before every build.
 */

/**
 * EPA Region 3, the access guide's example: the region with its six state-level data roots under it, Harford County
 * Health Department under Maryland, and Sky Region, a Region 3 staff member with Supervisor at the region and
 * everything under it (the guide's "EPA Region 3 staff member"). One open task per place makes it plain on the Work
 * queues page what each profile can see. Sky signs in with the published demo password (the hash is copied from the
 * demo supervisor account).
 */
export const REGION3_USER = 'r3staff';

const STATES: [code: string, name: string, postal: string, fips: string, counties: string][] = [
  ['R3-DE', 'Delaware', 'DE', '10', '[{"fips":"001","name":"Kent County"}]'],
  ['R3-DC', 'District of Columbia', 'DC', '11', '[]'],
  ['R3-MD', 'Maryland', 'MD', '24', '[{"fips":"025","name":"Harford County"},{"fips":"015","name":"Cecil County"}]'],
  ['R3-PA', 'Pennsylvania', 'PA', '42', '[{"fips":"101","name":"Philadelphia County"}]'],
  ['R3-VA', 'Virginia', 'VA', '51', '[{"fips":"760","name":"Richmond city"}]'],
  ['R3-WV', 'West Virginia', 'WV', '54', '[{"fips":"037","name":"Jefferson County"}]'],
];

const TASKS: [tenant: string, title: string][] = [
  ['EPA-R3', 'Region 3: prepare the quarterly summary for EPA'],
  ['R3-DE', 'Delaware: follow up an elevated child result in Kent County'],
  ['R3-DC', 'District of Columbia: confirm a capillary screen'],
  ['R3-MD', 'Maryland: confirm a venous result from Cecil County'],
  ['R3-MD-HAR', 'Harford County: schedule a home visit in Bel Air'],
  ['R3-PA', 'Pennsylvania: contact a Philadelphia family'],
  ['R3-VA', 'Virginia: review a Richmond child care result'],
  ['R3-WV', 'West Virginia: follow up a Jefferson County result'],
];

const key = (text: string) => `CAST (HASHBYTES ('MD5', N'merit-training:${text}') AS UNIQUEIDENTIFIER)`;
const q = (s: string) => s.replace(/'/g, "''");

export function ensureRegion3(): void {
  const lines: string[] = [
    'SET XACT_ABORT ON;',
    "EXEC config.uspCreateRootTenant @tenantCode = 'EPA-R3', @tenantName = N'EPA Region 3', @tenantType = 'region', @timeZone = 'Eastern Standard Time';",
  ];
  for (const [code, name, postal, fips, counties] of STATES) {
    lines.push(`EXEC config.uspCreateRootTenant @tenantCode = '${code}', @tenantName = N'${q(name)}', @tenantType = 'state', @statePostalCode = '${postal}',
      @stateFips = '${fips}', @parentTenantCode = 'EPA-R3', @countiesJson = N'${counties}', @timeZone = 'Eastern Standard Time';`);
  }
  lines.push(`EXEC config.uspCreateChildTenant @tenantCode = 'R3-MD-HAR', @tenantName = N'Harford County Health Department',
      @tenantType = 'jurisdiction', @parentTenantCode = 'R3-MD', @countyFipsList = '025';`);

  const tenant = (code: string) => `(SELECT tenantId FROM auth.tenant WHERE tenantCode = '${code}' AND isDeleted = 0)`;
  lines.push(`
DECLARE @user UNIQUEIDENTIFIER = ${key('user:r3staff')};
DECLARE @hash NVARCHAR (500) = (SELECT passwordHash FROM auth.appUser WHERE userName = N'supervisor' AND isDeleted = 0);
DECLARE @home UNIQUEIDENTIFIER = ${tenant('EPA-R3')};
EXEC auth.uspSaveUser @appUserId = @user, @userName = N'${REGION3_USER}', @displayName = N'Sky Region',
     @email = N'r3staff@merit.example', @passwordHash = @hash, @homeTenantId = @home;
DECLARE @role UNIQUEIDENTIFIER = (SELECT roleId FROM auth.role WHERE rootTenantId = @home AND roleCode = 'supervisor' AND isDeleted = 0);
DECLARE @grant UNIQUEIDENTIFIER = ${key('grant:r3staff:supervisor')};
DECLARE @start DATE = '2026-01-01';
EXEC auth.uspSaveUserAssignment @userAssignmentId = @grant, @appUserId = @user, @roleId = @role, @tenantId = @home,
     @includeDescendants = 1, @accessLevel = 'full', @startDate = @start;`);

  for (const [code, title] of TASKS) {
    lines.push(`INSERT dbo.workTask (workTaskId, rootTenantId, owningTenantId, queueCode, title)
SELECT ${key(`task:${code}`)}, t.rootTenantId, t.tenantId, 'actionLevel', N'${q(title)}' FROM auth.tenant AS t
 WHERE t.tenantCode = '${code}' AND t.isDeleted = 0 AND NOT EXISTS (SELECT 1 FROM dbo.workTask WHERE workTaskId = ${key(`task:${code}`)});`);
  }
  sql(lines.join('\n'));
}

/** Soft-deletes every profile but the master one that an account made for itself, so a video can make them again. */
export function resetOwnProfiles(userName: string): void {
  sql(`UPDATE p SET isDeleted = 1, deletedBy = N'merit-training', deletedDateUtc = SYSUTCDATETIME()
         FROM auth.userProfile AS p JOIN auth.appUser AS u ON u.appUserId = p.appUserId
        WHERE u.userName = N'${q(userName)}' AND p.isMaster = 0 AND p.isDeleted = 0;
       UPDATE g SET isDeleted = 1, deletedBy = N'merit-training', deletedDateUtc = SYSUTCDATETIME()
         FROM auth.userProfileGrant AS g JOIN auth.userProfile AS p ON p.userProfileId = g.userProfileId
         JOIN auth.appUser AS u ON u.appUserId = p.appUserId
        WHERE u.userName = N'${q(userName)}' AND p.isMaster = 0 AND g.isDeleted = 0;`);
}
