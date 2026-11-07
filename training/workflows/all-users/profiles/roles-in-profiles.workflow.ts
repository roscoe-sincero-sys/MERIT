import type { Stage } from '../../../lib/stage.ts';
import { forgetSession } from '../../../lib/auth.ts';
import { resetOwnProfiles } from '../../../lib/scenarios.ts';
import { sql } from '../../../lib/sql.ts';
import { defineWorkflow } from '../../../lib/workflow.ts';

// A separate account keeps the existing Region 3 videos and their profiles unchanged.
// Fixed keys and the application procedures make this scenario repeatable.
const USER = 'rolesdemo';
const INTAKE = 'Maryland intake duties';
const CASES = 'Maryland case duties';
const REVIEW = 'Maryland case review';
const COMBINED = 'Maryland intake and cases';
const PLACE = 'Maryland Lead and Metals Program (demo)';
const profileBox = (s: Stage) => s.page.locator('header a.merit-profile-switch');

function prepareRoles(): void {
  sql(`SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @user UNIQUEIDENTIFIER = CAST(HASHBYTES('MD5', N'merit-training:user:rolesdemo') AS UNIQUEIDENTIFIER);
DECLARE @home UNIQUEIDENTIFIER = (SELECT tenantId FROM auth.tenant WHERE tenantCode = 'MD-LEAD' AND isDeleted = 0);
DECLARE @hash NVARCHAR(500) = (SELECT passwordHash FROM auth.appUser WHERE userName = N'supervisor' AND isDeleted = 0);
EXEC auth.uspSaveUser @appUserId = @user, @userName = N'${USER}', @displayName = N'Riley Roles',
     @email = N'rolesdemo@merit.example', @passwordHash = @hash, @homeTenantId = @home;
DECLARE @intake UNIQUEIDENTIFIER = (SELECT roleId FROM auth.role WHERE rootTenantId = @home AND roleCode = 'intakeClerk' AND isDeleted = 0);
DECLARE @cases UNIQUEIDENTIFIER = (SELECT roleId FROM auth.role WHERE rootTenantId = @home AND roleCode = 'caseManager' AND isDeleted = 0);
DECLARE @ig UNIQUEIDENTIFIER = CAST(HASHBYTES('MD5', N'merit-training:grant:rolesdemo:intake') AS UNIQUEIDENTIFIER);
DECLARE @cg UNIQUEIDENTIFIER = CAST(HASHBYTES('MD5', N'merit-training:grant:rolesdemo:cases') AS UNIQUEIDENTIFIER);
EXEC auth.uspSaveUserAssignment @userAssignmentId = @ig, @appUserId = @user, @roleId = @intake,
     @tenantId = @home, @includeDescendants = 1, @accessLevel = 'full', @startDate = '2026-01-01';
EXEC auth.uspSaveUserAssignment @userAssignmentId = @cg, @appUserId = @user, @roleId = @cases,
     @tenantId = @home, @includeDescendants = 1, @accessLevel = 'full', @startDate = '2026-01-01';
COMMIT;`);
  resetOwnProfiles(USER);
  forgetSession(USER);
}

async function openProfiles(s: Stage): Promise<void> {
  await s.click(profileBox(s));
  await s.see(s.page.getByRole('heading', { name: 'My profiles', level: 1 }));
}

async function create(s: Stage, name: string): Promise<void> {
  await s.click(s.page.getByRole('link', { name: 'Create a profile' }));
  await s.see(s.page.getByRole('heading', { name: 'Create a profile', level: 1 }));
  await s.type(s.page.locator('#ProfileName'), name);
}

async function grant(s: Stage, row: number, role: string, access: string): Promise<void> {
  await s.select(s.page.locator(`#g${row}t`), PLACE);
  await s.select(s.page.locator(`#g${row}r`), role);
  await s.select(s.page.locator(`#g${row}a`), access);
}

async function save(s: Stage, name: string): Promise<void> {
  await s.click(s.page.getByRole('button', { name: 'Save profile' }));
  await s.expectText(s.page.locator('main .usa-alert').first(), `Profile "${name}" saved`);
}

async function use(s: Stage, name: string): Promise<void> {
  await s.click(s.page.getByRole('button', { name: `Use ${name}`, exact: true }));
  await s.expectText(profileBox(s), name);
  await s.highlight(profileBox(s));
}

async function intakeMenu(s: Stage, present: boolean): Promise<void> {
  const item = s.page.locator('#merit-nav summary', { hasText: 'Intake' });
  if (present) await s.see(item);
  else if (await item.count()) throw new Error('Intake should be absent from this profile.');
  await s.highlight(s.page.locator('#merit-nav'));
}

export default defineWorkflow({
  id: 'roles-in-profiles',
  title: 'Profiles: choosing roles for your work',
  summary: 'Choose roles, compare them with access levels, and switch between intake, case work and review profiles.',
  role: 'allUsers',
  feature: 'profiles',
  user: USER,
  start: '/',
  prepare: prepareRoles,
  scenes: [
    {
      title: 'Roles come from your master profile',
      steps: [
        {
          say: 'Riley handles both intake and case work in Maryland. Open My profiles using the profile box in the header.',
          run: async s => {
            await s.expectText(profileBox(s), /Master profile/);
            await openProfiles(s);
          },
        },
        {
          say: 'The administrator assigned two roles: Intake clerk and Case manager. Both are in Riley\'s master profile, at Maryland and everything under it.',
          run: async s => {
            const master = s.page.locator('main tbody tr', { hasText: 'Master profile' });
            await s.expectText(master, /Intake clerk/);
            await s.expectText(master, /Case manager/);
            await s.highlight(master);
          },
        },
        {
          say: 'A role supplies permissions for particular duties. A profile chooses which of your assigned roles to use, and where to use them.',
          run: async s => create(s, INTAKE),
        },
        {
          say: 'Place sets where a grant applies. Role sets the permitted tasks. Access limits those permissions to reading, writing, or full access.',
          run: async s => s.highlight(s.page.locator('.merit-grant-row').first()),
        },
        {
          say: 'The Role list comes from your master profile. If a needed role is missing, ask your administrator; creating a profile cannot assign it to you.',
          run: async s => {
            const labels = await s.page.locator('#g0r option').allTextContents();
            if (labels.length !== 2 || !labels.includes('Intake clerk') || !labels.includes('Case manager')) {
              throw new Error(`Unexpected assigned roles: ${labels.join(', ')}`);
            }
            await s.select(s.page.locator('#g0r'), 'Intake clerk');
          },
        },
      ],
    },
    {
      title: 'Same place and access, different roles',
      steps: [
        {
          say: 'For intake duties, choose Maryland, Intake clerk, and Full access. Keep everything under this place selected.',
          run: async s => grant(s, 0, 'Intake clerk', 'Full (read, write and delete)'),
        },
        {
          say: 'Full access still stays within the role\'s permissions. It does not make an Intake clerk a supervisor or an administrator. Save the profile.',
          run: async s => save(s, INTAKE),
        },
        {
          say: 'Create a second profile for case duties, using the same Maryland place and Full access, but choose Case manager for Role.',
          run: async s => {
            await create(s, CASES);
            await grant(s, 0, 'Case manager', 'Full (read, write and delete)');
          },
        },
        {
          say: 'Case manager includes case editing and investigation work. Intake clerk includes file upload and held-record work. Save the case profile.',
          run: async s => save(s, CASES),
        },
        {
          say: 'Choose Use beside Maryland intake duties. The header confirms the active profile; no sign-out is needed.',
          run: async s => use(s, INTAKE),
        },
        {
          say: 'The Intake menu is available for uploading files and handling held records.',
          run: async s => {
            await intakeMenu(s, true);
            await s.click(s.page.locator('#merit-nav summary', { hasText: 'Intake' }));
            await s.see(s.page.locator('#merit-nav').getByRole('link', { name: 'Held records', exact: true }));
            await s.highlight(s.page.locator('#merit-nav'));
          },
        },
        {
          say: 'Switch to Maryland case duties. The place and access level stay the same, but the Intake menu disappears because Case manager lacks those intake permissions.',
          run: async s => {
            await use(s, CASES);
            await intakeMenu(s, false);
          },
        },
      ],
    },
    {
      title: 'Keep a role, reduce its access',
      steps: [
        {
          say: 'For a review session, create Maryland case review. Keep Case manager and the same place, but choose Read only.',
          run: async s => {
            await create(s, REVIEW);
            await grant(s, 0, 'Case manager', 'Read only');
          },
        },
        {
          say: 'Read only removes writing permissions from that role. It is useful when checking records without making changes. Save and use this profile.',
          run: async s => {
            await save(s, REVIEW);
            await use(s, REVIEW);
          },
        },
        {
          say: 'People and Work queues remain available for review. The role is still Case manager; Access is what limits it to reading.',
          run: async s => {
            await intakeMenu(s, false);
            await s.see(s.page.locator('#merit-nav').getByRole('link', { name: 'People', exact: true }));
            await s.click(s.page.locator('#merit-nav').getByRole('link', { name: 'Work queues', exact: true }));
            await s.see(s.page.getByRole('heading', { name: 'Work queues', level: 1 }));
            await s.highlight(s.page.locator('main'));
          },
        },
      ],
    },
    {
      title: 'Combine roles when the work needs both',
      steps: [
        {
          say: 'A profile can combine roles too. Return to My profiles and create Maryland intake and cases.',
          run: async s => {
            await openProfiles(s);
            await create(s, COMBINED);
          },
        },
        {
          say: 'In the first row, choose Maryland and Intake clerk, with Read and write without delete.',
          run: async s => grant(s, 0, 'Intake clerk', 'Read and write (no delete)'),
        },
        {
          say: 'In the second row, choose the same Maryland place, but Case manager, also with Read and write without delete.',
          run: async s => grant(s, 1, 'Case manager', 'Read and write (no delete)'),
        },
        {
          say: 'The grants add together. Both sets of permissions apply; a read-only grant would not cancel writing allowed by another grant at the same place.',
          run: async s => s.highlight(s.page.locator('[data-profile-grants]')),
        },
        {
          say: 'Every grant must fit within your master profile. Check the master summary, then save the combined profile.',
          run: async s => {
            await s.highlight(s.page.locator('aside[aria-labelledby="master-heading"]'));
            await save(s, COMBINED);
          },
        },
        {
          say: 'Use the combined profile. Intake is available again, alongside case work, because this profile includes both roles.',
          run: async s => {
            await use(s, COMBINED);
            await intakeMenu(s, true);
            await s.highlight(s.page.locator('main tbody tr', { hasText: COMBINED }));
          },
        },
        {
          say: 'Choose the profile that fits today\'s duties, and check its name in the header. Use Master profile to return to everything you were assigned.',
          run: async s => use(s, 'Master profile'),
        },
      ],
    },
  ],
  recap: [
    'Chose assigned roles for intake and case duties at the same place',
    'Used Read only to reduce the permissions of a role',
    'Combined roles in one profile and checked the header when switching',
  ],
});
