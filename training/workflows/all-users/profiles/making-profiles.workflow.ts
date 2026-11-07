import type { Stage } from '../../../lib/stage.ts';
import { forgetSession } from '../../../lib/auth.ts';
import { ensureRegion3, REGION3_USER, resetOwnProfiles } from '../../../lib/scenarios.ts';
import { defineWorkflow } from '../../../lib/workflow.ts';

/**
 * Profiles, with the access guide's EPA Region 3 example. Sky Region, a Region 3 staff member (Supervisor at EPA Region 3
 * and everything under it), makes three profiles from that master profile: every state in the region read only, all of
 * Maryland, and Harford County only. Work queues holds one synthetic task per place, so each switch shows what the
 * profile can see. prepare() builds the region in the training database, soft-deletes Sky's own profiles and starts
 * from a fresh sign-in on the master profile.
 */
const REGION = 'Region 3, all states (read only)';
const MARYLAND = 'Maryland only';
const HARFORD = 'Harford County only';

const profileBox = (s: Stage) => s.page.locator('header a.merit-profile-switch');
const rows = (s: Stage) => s.page.locator('main').getByText(/^\d+ rows?$/);

async function openProfiles(s: Stage): Promise<void> {
  await s.click(profileBox(s));
  await s.see(s.page.getByRole('heading', { name: 'My profiles', level: 1 }));
}

async function createProfile(s: Stage, name: string): Promise<void> {
  await s.click(s.page.getByRole('link', { name: 'Create a profile' }));
  await s.see(s.page.getByRole('heading', { name: 'Create a profile', level: 1 }));
  await s.type(s.page.locator('#ProfileName'), name);
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
  id: 'making-profiles',
  title: 'Profiles: the right access for the work in hand',
  summary: 'See your master profile, make smaller profiles for a region, a state and a county, and switch between them.',
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
      title: 'Your master profile',
      steps: [
        {
          say: 'Sky works for EPA Region 3. The profile box in the header shows the profile in use: right now, the master profile.',
          run: async s => {
            await s.expectText(profileBox(s), /Master profile/);
            await s.highlight(profileBox(s));
          },
        },
        {
          say: 'Work queues shows tasks from every state in the region, from Delaware to West Virginia.',
          run: async s => queues(s, 8),
          hold: 900,
        },
        {
          say: 'Select the profile box to open My profiles.',
          run: async s => openProfiles(s),
        },
        {
          say: 'The master profile is everything your administrator gave you: Supervisor at EPA Region 3 and everything under it.',
          run: async s => s.highlight(s.page.locator('main tbody tr', { hasText: 'Master profile' })),
        },
        {
          say: 'A profile is a smaller copy of it, like a day key cut from a master key. It can open fewer doors, never more.',
          hold: 600,
        },
      ],
    },
    {
      title: 'Every state, read only',
      steps: [
        {
          say: 'First, a profile that sees every state in the region but cannot change anything, for reviews and screen sharing.',
          run: async s => createProfile(s, REGION),
        },
        {
          say: 'Each grant is a place, a role and an access level. Choose EPA Region 3, and keep everything under it.',
          run: async s => {
            await s.select(s.page.locator('#g0t'), 'EPA Region 3');
            await s.highlight(s.page.locator('label[for="g0d"]'));
          },
        },
        {
          say: 'Set the access to read only.',
          run: async s => s.select(s.page.locator('#g0a'), 'Read only'),
        },
        {
          say: 'Beside the form is your master profile, the most a profile can have. MERIT refuses to save one that goes beyond it.',
          run: async s => s.highlight(s.page.locator('aside[aria-labelledby="master-heading"]')),
        },
        {
          say: 'Save the profile.',
          run: async s => save(s, REGION),
        },
      ],
    },
    {
      title: 'All of Maryland',
      steps: [
        {
          say: 'Next, a profile for all of Maryland.',
          run: async s => createProfile(s, MARYLAND),
        },
        {
          say: 'Choose Maryland as the place. Everything under it, including Harford County, comes with it.',
          run: async s => s.select(s.page.locator('#g0t'), 'Maryland'),
        },
        {
          say: 'Read and write without delete lets you work normally while protecting records from being deleted.',
          run: async s => {
            await s.select(s.page.locator('#g0a'), 'Read and write (no delete)');
            await save(s, MARYLAND);
          },
        },
      ],
    },
    {
      title: 'Harford County only',
      steps: [
        {
          say: 'Last, a profile for a day of Harford County work.',
          run: async s => createProfile(s, HARFORD),
        },
        {
          say: 'Choose the Harford County Health Department, with full access, and save.',
          run: async s => {
            await s.select(s.page.locator('#g0t'), 'Harford County Health Department');
            await s.select(s.page.locator('#g0a'), 'Full (read, write and delete)');
            await save(s, HARFORD);
          },
        },
        {
          say: 'My profiles now lists the master profile and the three new ones, each with what it gives you.',
          run: async s => {
            await s.expectText(s.page.locator('main tbody'), HARFORD);
            await s.highlight(s.page.locator('main table'));
          },
          hold: 900,
        },
      ],
    },
    {
      title: 'Switching profiles',
      steps: [
        {
          say: 'To switch, choose Use beside a profile. MERIT changes straight away, without signing out.',
          run: async s => use(s, MARYLAND),
        },
        {
          say: 'The header now shows Maryland only, and Work queues lists just the Maryland tasks, Harford County included.',
          run: async s => {
            await s.highlight(profileBox(s));
            await queues(s, 2);
          },
          hold: 900,
        },
        {
          say: 'Switch to Harford County only, and only the Harford County task is left.',
          run: async s => {
            await openProfiles(s);
            await use(s, HARFORD);
            await queues(s, 1);
          },
          hold: 900,
        },
        {
          say: 'With the read-only regional profile, every state is back. Intake, which adds records, is gone from the menu.',
          run: async s => {
            await openProfiles(s);
            await use(s, REGION);
            await queues(s, 8);
            const menu = s.page.getByRole('navigation');
            if (await menu.getByText('Intake', { exact: true }).count()) throw new Error('Intake is still in the menu of a read-only profile.');
            await s.highlight(menu.first());
          },
          hold: 900,
        },
        {
          say: 'You can go back to your master profile at any time: choose Use beside Master profile.',
          run: async s => {
            await openProfiles(s);
            await use(s, 'Master profile');
          },
        },
      ],
    },
  ],
  recap: [
    'Saw that the master profile holds everything you were given',
    'Made profiles for every Region 3 state read only, for all of Maryland, and for Harford County',
    'Switched between them and saw Work queues follow the profile',
  ],
});
