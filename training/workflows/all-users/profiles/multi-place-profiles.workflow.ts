import type { Stage } from '../../../lib/stage.ts';
import { forgetSession } from '../../../lib/auth.ts';
import { ensureRegion3, REGION3_USER, resetOwnProfiles } from '../../../lib/scenarios.ts';
import { defineWorkflow } from '../../../lib/workflow.ts';

/**
 * Expands on making-profiles: demonstrates using the second and third Place rows (#g1t, #g2t) and their related fields
 * (Role #g0r/#g1r/#g2r, Access #g0a/#g1a/#g2a, Include everything under this place #g0d/#g1d/#g2d, Description
 * #ProfileDescription, and Working in #actingTenantId) on the My profiles screens.
 *
 * Sky Region (Supervisor at EPA Region 3 and everything under it) builds three multi-grant profiles:
 *   1. "Maryland state and Delaware" — uses two Place rows (Maryland with "Include everything under this place" cleared
 *      so Harford County is excluded, plus Delaware with everything under it), showing 2 state-level tasks.
 *   2. "West Virginia, Virginia and Pennsylvania" — uses all three Place rows with Full, Read and write (no delete),
 *      and Read only access across the three states, showing 3 tasks.
 *   3. "Region read, Maryland write, Harford full" — uses all three Place rows to layer read-only regional visibility
 *      with write access in Maryland and full access in Harford County, showing all 8 regional tasks while keeping
 *      Intake in the menu and setting "Working in" to Harford County Health Department.
 */
const TWO_PLACES = 'Maryland state and Delaware';
const THREE_STATES = 'West Virginia, Virginia and Pennsylvania';
const LAYERED = 'Region read, Maryland write, Harford full';

const profileBox = (s: Stage) => s.page.locator('header a.merit-profile-switch');
const rows = (s: Stage) => s.page.locator('main').getByText(/^\d+ rows?$/);

async function openProfiles(s: Stage): Promise<void> {
  await s.click(profileBox(s));
  await s.see(s.page.getByRole('heading', { name: 'My profiles', level: 1 }));
}

async function createProfile(s: Stage, name: string, description?: string): Promise<void> {
  await s.click(s.page.getByRole('link', { name: 'Create a profile' }));
  await s.see(s.page.getByRole('heading', { name: 'Create a profile', level: 1 }));
  await s.type(s.page.locator('#ProfileName'), name);
  if (description) await s.type(s.page.locator('#ProfileDescription'), description);
}

async function save(s: Stage, name: string): Promise<void> {
  await s.click(s.page.getByRole('button', { name: 'Save profile' }));
  await s.expectText(s.page.locator('main .usa-alert').first(), `Profile "${name}" saved`);
}

async function use(s: Stage, name: string): Promise<void> {
  const row = s.page.locator('main tbody tr', { hasText: name });
  await s.highlight(row, { describe: false });
  await s.click(row.getByRole('button', { name: `Use ${name}` }));
  await s.expectText(profileBox(s), name);
}

async function queues(s: Stage, count: number): Promise<void> {
  if (!s.page.url().includes('/app/queues')) await s.click(s.page.getByRole('navigation').getByRole('link', { name: 'Work queues' }));
  await s.expectText(rows(s), new RegExp(`^${count} rows?$`));
  await s.highlight(s.page.locator('main table'));
}

export default defineWorkflow({
  id: 'multi-place-profiles',
  title: 'Profiles: combining places and access levels',
  summary: 'Use the second and third grant rows to combine places, mix access levels, control child jurisdictions, and choose where new records belong.',
  role: 'allUsers',
  feature: 'profiles',
  user: REGION3_USER,
  start: '/',
  prepare() {
    ensureRegion3();
    resetOwnProfiles(REGION3_USER);
    forgetSession(REGION3_USER);
  },
  scenes: [
    {
      title: 'More than one grant in a profile',
      steps: [
        {
          say: 'A profile can hold more than one place. Select the profile box in the header to open My profiles.',
          run: async s => {
            await s.expectText(profileBox(s), /Master profile/);
            await openProfiles(s);
          },
        },
        {
          say: 'Choose Create a profile to see how the three grant rows work together.',
          run: async s => {
            await s.click(s.page.getByRole('link', { name: 'Create a profile' }));
            await s.see(s.page.getByRole('heading', { name: 'Create a profile', level: 1 }));
          },
        },
        {
          say: 'Under Grants, MERIT gives you three rows of Place, Role, Access, and Include everything under this place.',
          run: async s => s.highlight(s.page.locator('[data-profile-grants]')),
          hold: 700,
        },
      ],
    },
    {
      title: 'Two places, without child jurisdictions',
      steps: [
        {
          say: 'First, make a profile for state-level work in Maryland and Delaware, and add a short description.',
          run: async s => {
            await s.type(s.page.locator('#ProfileName'), TWO_PLACES);
            await s.type(s.page.locator('#ProfileDescription'), 'State-level cases along the Maryland and Delaware border');
          },
        },
        {
          say: 'In the first row, choose Maryland for Place, Supervisor for Role, and Read and write without delete for Access.',
          run: async s => {
            await s.select(s.page.locator('#g0t'), 'Maryland');
            await s.select(s.page.locator('#g0r'), 'Supervisor');
            await s.select(s.page.locator('#g0a'), 'Read and write (no delete)');
          },
        },
        {
          say: 'Clear Include everything under this place on Maryland, so county agencies like Harford County are left out.',
          run: async s => {
            await s.click(s.page.locator('label[for="g0d"]'));
            if (await s.page.locator('#g0d').isChecked()) throw new Error('Expected #g0d to be unchecked.');
          },
        },
        {
          say: 'In the second row, choose Delaware, keep Supervisor and Read and write without delete, and keep everything under it.',
          run: async s => {
            await s.select(s.page.locator('#g1t'), 'Delaware');
            await s.select(s.page.locator('#g1r'), 'Supervisor');
            await s.select(s.page.locator('#g1a'), 'Read and write (no delete)');
            await s.highlight(s.page.locator('label[for="g1d"]'));
          },
        },
        {
          say: 'Leave the third Place on none to skip it, then choose Save profile.',
          run: async s => {
            await s.highlight(s.page.locator('#g2t'));
            await save(s, TWO_PLACES);
          },
        },
      ],
    },
    {
      title: 'Three places with different access levels',
      steps: [
        {
          say: 'Next, create a tri-state profile that uses all three Place rows, each with its own access level.',
          run: async s => createProfile(s, THREE_STATES, 'Full in West Virginia, write in Virginia, read only in Pennsylvania'),
        },
        {
          say: 'In the first row, choose West Virginia, keep the Supervisor role, and set Access to Full.',
          run: async s => {
            await s.select(s.page.locator('#g0t'), 'West Virginia');
            await s.select(s.page.locator('#g0r'), 'Supervisor');
            await s.select(s.page.locator('#g0a'), 'Full (read, write and delete)');
          },
        },
        {
          say: 'In the second row, choose Virginia, Supervisor, and Read and write without delete.',
          run: async s => {
            await s.select(s.page.locator('#g1t'), 'Virginia');
            await s.select(s.page.locator('#g1r'), 'Supervisor');
            await s.select(s.page.locator('#g1a'), 'Read and write (no delete)');
          },
        },
        {
          say: 'In the third row, choose Pennsylvania, Supervisor, and Read only, so you can check neighbouring cases without changing them.',
          run: async s => {
            await s.select(s.page.locator('#g2t'), 'Pennsylvania');
            await s.select(s.page.locator('#g2r'), 'Supervisor');
            await s.select(s.page.locator('#g2a'), 'Read only');
            await s.highlight(s.page.locator('label[for="g2d"]'));
          },
        },
        {
          say: 'Save the three-state profile.',
          run: async s => save(s, THREE_STATES),
        },
      ],
    },
    {
      title: 'Layered access across region, state and county',
      steps: [
        {
          say: 'Third, use all three rows to read the whole region, write in Maryland, and have full access in Harford County.',
          run: async s => createProfile(s, LAYERED),
        },
        {
          say: 'Set the first row to EPA Region 3 with Read only access, keeping everything under it.',
          run: async s => {
            await s.select(s.page.locator('#g0t'), 'EPA Region 3');
            await s.select(s.page.locator('#g0r'), 'Supervisor');
            await s.select(s.page.locator('#g0a'), 'Read only');
          },
        },
        {
          say: 'Set the second row to Maryland with Read and write without delete, and clear Include everything under this place.',
          run: async s => {
            await s.select(s.page.locator('#g1t'), 'Maryland');
            await s.select(s.page.locator('#g1r'), 'Supervisor');
            await s.select(s.page.locator('#g1a'), 'Read and write (no delete)');
            await s.click(s.page.locator('label[for="g1d"]'));
          },
        },
        {
          say: 'Set the third row to Harford County Health Department with Full access, and save.',
          run: async s => {
            await s.select(s.page.locator('#g2t'), 'Harford County Health Department');
            await s.select(s.page.locator('#g2r'), 'Supervisor');
            await s.select(s.page.locator('#g2a'), 'Full (read, write and delete)');
            await save(s, LAYERED);
          },
        },
        {
          say: 'My profiles lists each profile and every grant row inside it, including whether child places are included.',
          run: async s => {
            await s.expectText(s.page.locator('main tbody'), /Maryland only/);
            await s.highlight(s.page.locator('main table'));
          },
          hold: 900,
        },
      ],
    },
    {
      title: 'Switching profiles and setting Working in',
      steps: [
        {
          say: 'Switch to Maryland state and Delaware. Work queues shows the two state tasks, while Harford County stays out.',
          run: async s => {
            await use(s, TWO_PLACES);
            await queues(s, 2);
            const text = await s.page.locator('main table').innerText();
            if (text.includes('Harford County')) throw new Error('Harford County task should be excluded when #g0d is unchecked.');
          },
          hold: 900,
        },
        {
          say: 'Switch to West Virginia, Virginia and Pennsylvania, and Work queues shows the three tasks from those states.',
          run: async s => {
            await openProfiles(s);
            await use(s, THREE_STATES);
            await queues(s, 3);
          },
          hold: 900,
        },
        {
          say: 'Switch to the layered profile. All eight regional tasks appear, and Intake stays in the menu because Maryland and Harford allow writing.',
          run: async s => {
            await openProfiles(s);
            await use(s, LAYERED);
            await queues(s, 8);
            await s.see(s.page.locator('#merit-nav summary', { hasText: 'Intake' }));
            await s.highlight(s.page.locator('#merit-nav'));
          },
          hold: 900,
        },
        {
          say: 'When a profile covers several places, use Working in on My profiles to pick where new records belong.',
          run: async s => {
            await openProfiles(s);
            await s.select(s.page.locator('#actingTenantId'), 'Harford County Health Department');
            await s.click(s.page.getByRole('button', { name: 'Work here' }));
            await s.expectText(s.page.locator('.merit-header__who'), /Harford County Health Department/);
            await s.highlight(s.page.locator('.merit-header__who'));
          },
          hold: 800,
        },
        {
          say: 'Choose Use beside Master profile whenever you want to return to your full access.',
          run: async s => {
            await openProfiles(s);
            await use(s, 'Master profile');
          },
        },
      ],
    },
  ],
  recap: [
    'Combined two places in one profile and cleared Include everything under this place to leave out a child county',
    'Used all three grant rows to set different access levels across three states, and to layer regional, state and county access',
    'Switched between the multi-place profiles and chose Working in for new records',
  ],
});
