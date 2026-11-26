import { pbkdf2Sync, randomBytes } from 'node:crypto';
import { join } from 'node:path';
import { chromium } from '@playwright/test';
import { BASE_URL, CHANNEL, LOGIN_DEMO_PASSWORD, PATHS } from '../../../lib/config.ts';
import { resetTrainingResults } from '../../../lib/data.ts';
import {
  freshTotp,
  LoginAccount,
  NEW_PASSWORD,
  newPassword,
  openAccountMenu,
  password,
  requestReset,
  signOut,
  verify,
} from '../../../lib/login.ts';
import { lit, sql } from '../../../lib/sql.ts';
import type { Stage } from '../../../lib/stage.ts';
import { totp } from '../../../lib/totp.ts';
import { defineWorkflow } from '../../../lib/workflow.ts';

/**
 * End-to-end daily workflow for an existing intake user (Issue #322):
 *   1. Sign in screen: realize password does not work, then reset the password and sign in.
 *   2. Profile: pick a read-only profile to review data, then switch to a full CRUD profile for daily work.
 *   3. Work: import an HL7 file, match imported records to existing patients, manually enter results for existing
 *      patients, and add a new patient with new records.
 *   4. Sign out.
 *
 * Needs npm run app:login (Merit.Web with local Mailpit + Merit.Worker) and MERIT_TRAINING_URL=https://localhost:7343.
 */
const a = new LoginAccount('logindaily', 'Jordan Daily');
const READ_ONLY_PROFILE = 'Maryland review (read only)';
const FULL_CRUD_PROFILE = 'Maryland daily intake (full CRUD)';
const HL7_FILENAME = 'daily-hl7-training.hl7';
const HL7_FILE = join(PATHS.assets, HL7_FILENAME);
const NEW_LAST = 'Holloway';
const NEW_FIRST = 'Tessa';
const NEW_DOB = '2022-06-15';

const key = (value: string) => `CAST(HASHBYTES('MD5', N'merit-training:${value}') AS UNIQUEIDENTIFIER)`;
const BY = "N'merit-training'";

function initialPasswordHash(): string {
  const salt = randomBytes(16), header = Buffer.alloc(13);
  header[0] = 1; header.writeUInt32BE(2, 1); header.writeUInt32BE(220_000, 5); header.writeUInt32BE(salt.length, 9);
  const subkey = pbkdf2Sync(LOGIN_DEMO_PASSWORD.normalize('NFC'), salt, 220_000, 32, 'sha512');
  return `merit-nfc-v1$${Buffer.concat([header, salt, subkey]).toString('base64')}`;
}

async function enrollDailyOffCamera(): Promise<void> {
  const browser = await chromium.launch({ channel: CHANNEL });
  try {
    const context = await browser.newContext({ baseURL: BASE_URL, ignoreHTTPSErrors: true });
    const page = await context.newPage();
    await page.goto('/account/signin');
    await page.locator('#UserName').fill(a.user);
    await page.locator('#Password').fill(a.password);
    await page.getByRole('button', { name: 'Continue', exact: true }).click();
    await page.locator('code').first().waitFor();
    a.secret = (await page.locator('code').first().innerText()).replace(/\s/g, '');
    await page.locator('#Code').fill(totp(a.secret));
    await page.getByRole('button', { name: 'Turn on and sign in' }).click();
    await page.locator('[data-recovery-code]').first().waitFor();
    a.recovery = (await page.locator('[data-recovery-code]').allTextContents()).map(v => v.trim().replace(/-/g, ''));
    await page.locator('label[for="saved-codes"]').click();
    await page.getByRole('button', { name: 'Continue to MERIT', exact: true }).click();
    if (!(await context.request.get('/api/v1/me')).ok()) throw new Error('Daily training account enrollment did not finish.');
    await context.close();
  } finally {
    await browser.close();
  }
}

async function prepareDailyScenario(): Promise<void> {
  // 1. Clean up any results, cases, tasks, HL7 batches, staging rows, and new patient from prior runs.
  resetTrainingResults('TRN-DAY-');
  sql(`SET XACT_ABORT ON; BEGIN TRANSACTION;
DECLARE @batches TABLE (intakeBatchId UNIQUEIDENTIFIER);
INSERT @batches SELECT intakeBatchId FROM dbo.intakeBatch WHERE fileName = N'${HL7_FILENAME}' AND isDeleted = 0;
DECLARE @rows TABLE (stagingRowId UNIQUEIDENTIFIER);
INSERT @rows SELECT stagingRowId FROM dbo.stagingRow WHERE intakeBatchId IN (SELECT intakeBatchId FROM @batches) AND isDeleted = 0;
UPDATE dbo.stagingHoldReason SET isDeleted = 1, deletedBy = ${BY}, deletedDateUtc = SYSUTCDATETIME()
 WHERE isDeleted = 0 AND stagingRowId IN (SELECT stagingRowId FROM @rows);
UPDATE dbo.stagingRow SET isDeleted = 1, deletedBy = ${BY}, deletedDateUtc = SYSUTCDATETIME()
 WHERE isDeleted = 0 AND stagingRowId IN (SELECT stagingRowId FROM @rows);
UPDATE dbo.intakeBatch SET isDeleted = 1, deletedBy = ${BY}, deletedDateUtc = SYSUTCDATETIME()
 WHERE isDeleted = 0 AND intakeBatchId IN (SELECT intakeBatchId FROM @batches);
UPDATE dbo.jobQueue SET isDeleted = 1, deletedBy = ${BY}, deletedDateUtc = SYSUTCDATETIME()
 WHERE isDeleted = 0 AND payloadJson LIKE N'%${HL7_FILENAME}%';
DECLARE @newPeople TABLE (personId UNIQUEIDENTIFIER);
INSERT @newPeople SELECT personId FROM dbo.person
 WHERE isDeleted = 0 AND lastName = N'${NEW_LAST}' AND firstName = N'${NEW_FIRST}' AND dob = '${NEW_DOB}';
UPDATE dbo.workTask SET isDeleted = 1, deletedBy = ${BY}, deletedDateUtc = SYSUTCDATETIME()
 WHERE isDeleted = 0 AND personId IN (SELECT personId FROM @newPeople);
UPDATE dbo.exposureCase SET isDeleted = 1, deletedBy = ${BY}, deletedDateUtc = SYSUTCDATETIME()
 WHERE isDeleted = 0 AND personId IN (SELECT personId FROM @newPeople);
UPDATE dbo.labResult SET isDeleted = 1, deletedBy = ${BY}, deletedDateUtc = SYSUTCDATETIME()
 WHERE isDeleted = 0 AND personId IN (SELECT personId FROM @newPeople);
UPDATE dbo.personMatchKey SET isDeleted = 1, deletedBy = ${BY}, deletedDateUtc = SYSUTCDATETIME()
 WHERE isDeleted = 0 AND personId IN (SELECT personId FROM @newPeople);
UPDATE dbo.person SET isDeleted = 1, deletedBy = ${BY}, deletedDateUtc = SYSUTCDATETIME()
 WHERE isDeleted = 0 AND personId IN (SELECT personId FROM @newPeople);
COMMIT;`);

  // 2. Reset the synthetic existing user directly in MD-LEAD with read-only and full CRUD profiles.
  a.password = LOGIN_DEMO_PASSWORD; a.secret = ''; a.recovery = []; a.code = ''; a.link = '';
  sql(`SET XACT_ABORT ON;
DECLARE @md UNIQUEIDENTIFIER = (SELECT tenantId FROM auth.tenant WHERE tenantCode = 'MD-LEAD' AND isDeleted = 0);
DECLARE @user UNIQUEIDENTIFIER = ${key(`user:${a.user}`)};
DECLARE @hash NVARCHAR(500) = ${lit(initialPasswordHash())};
EXEC auth.uspSaveUser @appUserId = @user, @userName = ${lit(a.user)}, @displayName = ${lit(a.displayName)},
     @email = ${lit(a.email)}, @passwordHash = @hash, @homeTenantId = @md, @passwordChangeRequired = 0;
DECLARE @role UNIQUEIDENTIFIER = (SELECT roleId FROM auth.role WHERE rootTenantId = @md AND roleCode = 'intakeClerk' AND isDeleted = 0);
DECLARE @mdGrant UNIQUEIDENTIFIER = ${key(`grant:${a.user}:md-intake`)};
DECLARE @roProfile UNIQUEIDENTIFIER = ${key(`profile:${a.user}:readonly`)};
DECLARE @roGrant UNIQUEIDENTIFIER = ${key(`pgrant:${a.user}:readonly`)};
DECLARE @rwProfile UNIQUEIDENTIFIER = ${key(`profile:${a.user}:fullcrud`)};
DECLARE @rwGrant UNIQUEIDENTIFIER = ${key(`pgrant:${a.user}:fullcrud`)};

UPDATE auth.userAssignment SET isDeleted = 1, deletedBy = ${BY}, deletedDateUtc = SYSUTCDATETIME()
 WHERE appUserId = @user AND tenantId <> @md AND isDeleted = 0;
EXEC auth.uspSaveUserAssignment @userAssignmentId = @mdGrant, @appUserId = @user, @roleId = @role, @tenantId = @md,
     @includeDescendants = 1, @accessLevel = 'full', @startDate = '2026-01-01';

UPDATE auth.appUser SET isActive = 1, mfaEnabled = 0, mfaSecretProtected = NULL, failedSignInCount = 0, lockoutEndUtc = NULL,
     mfaEmailVerifiedUtc = NULL, mfaPreferredMethod = 'totp', securityStamp = NEWID() WHERE appUserId = @user;
UPDATE auth.sessionScope SET revokedUtc = SYSUTCDATETIME() WHERE appUserId = @user AND revokedUtc IS NULL;
UPDATE auth.mfaRecoveryCode SET isDeleted = 1 WHERE appUserId = @user AND isDeleted = 0;
UPDATE auth.mfaEmailChallenge SET isDeleted = 1 WHERE appUserId = @user AND isDeleted = 0;
UPDATE auth.passwordReset SET isDeleted = 1 WHERE appUserId = @user AND isDeleted = 0;

UPDATE auth.userProfileGrant SET isDeleted = 1, deletedBy = ${BY}, deletedDateUtc = SYSUTCDATETIME()
 WHERE isDeleted = 0 AND userProfileId IN (SELECT userProfileId FROM auth.userProfile WHERE appUserId = @user AND isMaster = 0);
UPDATE auth.userProfile SET isDeleted = 1, deletedBy = ${BY}, deletedDateUtc = SYSUTCDATETIME()
 WHERE appUserId = @user AND isMaster = 0 AND isDeleted = 0;

IF EXISTS (SELECT 1 FROM auth.userProfile WHERE userProfileId = @roProfile)
  UPDATE auth.userProfile SET profileName = N'${READ_ONLY_PROFILE}', profileDescription = N'Review records without editing',
    isActive = 1, isDeleted = 0 WHERE userProfileId = @roProfile;
ELSE
  INSERT auth.userProfile (userProfileId, appUserId, profileName, profileDescription, isMaster, isActive)
  VALUES (@roProfile, @user, N'${READ_ONLY_PROFILE}', N'Review records without editing', 0, 1);

IF EXISTS (SELECT 1 FROM auth.userProfileGrant WHERE userProfileGrantId = @roGrant)
  UPDATE auth.userProfileGrant SET roleId = @role, tenantId = @md, includeDescendants = 1, accessLevel = 'read',
    isDeleted = 0 WHERE userProfileGrantId = @roGrant;
ELSE
  INSERT auth.userProfileGrant (userProfileGrantId, userProfileId, roleId, tenantId, includeDescendants, accessLevel)
  VALUES (@roGrant, @roProfile, @role, @md, 1, 'read');

IF EXISTS (SELECT 1 FROM auth.userProfile WHERE userProfileId = @rwProfile)
  UPDATE auth.userProfile SET profileName = N'${FULL_CRUD_PROFILE}', profileDescription = N'Full intake, matching and manual entry',
    isActive = 1, isDeleted = 0 WHERE userProfileId = @rwProfile;
ELSE
  INSERT auth.userProfile (userProfileId, appUserId, profileName, profileDescription, isMaster, isActive)
  VALUES (@rwProfile, @user, N'${FULL_CRUD_PROFILE}', N'Full intake, matching and manual entry', 0, 1);

IF EXISTS (SELECT 1 FROM auth.userProfileGrant WHERE userProfileGrantId = @rwGrant)
  UPDATE auth.userProfileGrant SET roleId = @role, tenantId = @md, includeDescendants = 1, accessLevel = 'full',
    isDeleted = 0 WHERE userProfileGrantId = @rwGrant;
ELSE
  INSERT auth.userProfileGrant (userProfileGrantId, userProfileId, roleId, tenantId, includeDescendants, accessLevel)
  VALUES (@rwGrant, @rwProfile, @role, @md, 1, 'full');`);
  await enrollDailyOffCamera();
  sql(`UPDATE auth.appUser SET lastTotpStep = NULL WHERE ${a.where};
UPDATE auth.sessionScope SET revokedUtc = SYSUTCDATETIME() WHERE ${a.where} AND revokedUtc IS NULL;`);
}

async function openProfiles(s: Stage): Promise<void> {
  await openAccountMenu(s);
  await s.click(s.page.locator('header a.merit-header__profile'));
  await s.see(s.page.getByRole('heading', { name: 'My profiles', level: 1 }));
}

async function useProfile(s: Stage, name: string): Promise<void> {
  const row = s.page.locator('main tbody tr', { hasText: name });
  await s.highlight(row, { describe: false });
  await s.click(row.getByRole('button', { name: `Use ${name}` }));
  await s.see(s.page.locator(`header summary.merit-profile-switch[aria-label^="Profile: ${name} "]`));
}

async function openLabImportsLink(s: Stage, linkName: string): Promise<void> {
  const group = s.page.locator('header nav details.merit-nav-group', { hasText: 'Lab imports' });
  if (await group.getAttribute('open') === null) {
    await s.click(group.locator('summary'));
  }
  await s.click(group.getByRole('link', { name: linkName, exact: true }));
}

async function enterLeadResult(
  s: Stage,
  opts: { sampleType: string; value: string; collected: string; reported: string; method: string; accession: string; reason: string },
): Promise<void> {
  await s.select(s.page.getByLabel('Analyte'), 'Lead');
  await s.see(s.page.locator('#SampleType'));
  await s.select(s.page.locator('#SampleType'), opts.sampleType);
  await s.type(s.page.locator('#ValueText'), opts.value);
  await s.type(s.page.locator('#CollectedDate'), opts.collected);
  await s.type(s.page.locator('#ReportedDate'), opts.reported);
  await s.select(s.page.locator('#TestMethod'), opts.method);
  await s.type(s.page.locator('#AccessionNumber'), opts.accession);
  await s.select(s.page.locator('#ReasonForTest'), opts.reason);
  await s.pick(
    s.page.locator('#LabOrganizationId-text'),
    'Chesa',
    s.page.locator('#LabOrganizationId-list [role=option]', { hasText: 'Chesapeake Reference Laboratory' }),
  );
  await s.click(s.page.getByRole('button', { name: 'Save result' }));
  await s.see(s.page.locator('#result-saved'));
  await s.expectText(s.page.locator('#result-saved .usa-alert'), /Result saved/);
}

export default defineWorkflow({
  id: 'daily-intake-workflow',
  title: 'A typical day in MERIT: sign in, profiles, HL7 intake and manual entry',
  summary: 'Reset a forgotten password, switch between read-only and full profiles, import an HL7 file, match records, enter results and sign out.',
  role: 'intakeClerk',
  feature: 'intake',
  user: a.user,
  start: '/account/signin',
  anonymous: true,
  setup: page => a.protect(page),
  prepare: prepareDailyScenario,
  scenes: [
    {
      title: 'Reset a forgotten password and sign in',
      steps: [
        {
          say: 'Jordan starts the day on the sign-in screen, enters a user name and password, and realizes the password does not work.',
          run: async s => {
            await s.type(s.page.locator('#UserName'), a.user);
            await s.type(s.page.locator('#Password'), 'Wrong Old Passphrase 2025');
            await s.click(s.page.getByRole('button', { name: 'Continue', exact: true }));
            await s.expectText(s.page.getByRole('alert'), /not right/i);
            await s.highlight(s.page.getByRole('alert'));
          },
        },
        {
          say: 'Choose Forgot your password? to request a private reset link by email.',
          run: async s => {
            await s.click(s.page.getByRole('link', { name: 'Forgot your password?', exact: true }));
            await requestReset(s, a);
            await s.highlight(s.page.locator('main'));
          },
        },
        {
          say: 'Open the reset link from email, verify with an authenticator code, and set a new passphrase.',
          run: async s => {
            await s.goto(a.link);
            await s.see(s.page.getByRole('heading', { name: 'Choose a new password', exact: true }));
            await s.type(s.page.locator('#Code'), freshTotp(a));
            await newPassword(s);
            await s.click(s.page.getByRole('button', { name: 'Reset password', exact: true }));
            await s.see(s.page.getByRole('heading', { name: 'Your password has been reset', exact: true }));
            a.password = NEW_PASSWORD;
          },
        },
        {
          say: 'Return to sign in with the new password and complete the authenticator step.',
          run: async s => {
            await s.click(s.page.getByRole('link', { name: 'Sign in', exact: true }));
            await password(s, a);
            await verify(s, a);
          },
        },
      ],
    },
    {
      title: 'Review data with a read-only profile',
      steps: [
        {
          say: 'Before making changes, open My profiles from the account menu and pick a read-only profile to review data safely.',
          run: async s => {
            await openProfiles(s);
            await useProfile(s, READ_ONLY_PROFILE);
          },
        },
        {
          say: 'With the read-only profile active, review Work queues and existing records. Write tools like Upload lab results stay hidden.',
          run: async s => {
            await s.click(s.page.getByRole('navigation').getByRole('link', { name: 'Work queues', exact: true }));
            await s.see(s.page.locator('main table'));
            const menu = s.page.getByRole('navigation');
            if (await menu.getByRole('link', { name: 'Upload lab results', exact: true }).count()) {
              throw new Error('Upload lab results should not appear in a read-only profile.');
            }
            await s.highlight(s.page.locator('main table'));
          },
          hold: 900,
        },
      ],
    },
    {
      title: 'Switch to a full CRUD profile',
      steps: [
        {
          say: 'Ready for daily intake work, open My profiles again and switch to the full access profile.',
          run: async s => {
            await openProfiles(s);
            await useProfile(s, FULL_CRUD_PROFILE);
          },
        },
        {
          say: 'The header confirms the full profile is active, and Lab imports now offers Upload lab results and Match review.',
          run: async s => {
            const group = s.page.locator('header nav details.merit-nav-group', { hasText: 'Lab imports' });
            await s.click(group.locator('summary'));
            await s.see(group.getByRole('link', { name: 'Upload lab results', exact: true }));
            await s.see(group.getByRole('link', { name: 'Match review', exact: true }));
            await s.highlight(group);
          },
          hold: 700,
        },
      ],
    },
    {
      title: 'Import an HL7 file',
      steps: [
        {
          say: 'Open Upload lab results, choose the HL7 profile for Chesapeake Reference Laboratory, and attach the HL7 file.',
          run: async s => {
            await openLabImportsLink(s, 'Upload lab results');
            await s.see(s.page.getByRole('heading', { name: 'Upload lab results', level: 1 }));
            await s.select(s.page.locator('#intakeProfileId'), 'HL7 message from Chesapeake Reference Laboratory');
            await s.page.locator('#file').setInputFiles(HL7_FILE);
            await s.highlight(s.page.locator('[data-intake-dropzone]'), { describe: false });
          },
        },
        {
          say: 'Choose Upload. MERIT queues the HL7 file and processes each message in the background.',
          run: async s => {
            await s.click(s.page.locator('form[action="/intake/upload"]').getByRole('button', { name: 'Upload', exact: true }));
            const card = s.page.locator('#batches .usa-card__container', { hasText: HL7_FILENAME }).first();
            await s.see(card, { timeout: 20_000 });
            await s.expectText(card.locator('.merit-badge'), /Processed with holds|All rows held|Loaded/i, { timeout: 45_000 });
            await s.highlight(card);
          },
          hold: 1000,
        },
      ],
    },
    {
      title: 'Match imported records to existing patients',
      steps: [
        {
          say: 'Two imported rows need person matching. Open Match review under Lab imports.',
          run: async s => {
            await openLabImportsLink(s, 'Match review');
            await s.see(s.page.getByRole('heading', { name: 'Person matching review', level: 1 }));
            await s.expectText(s.page.locator('#shortcut-help'), /2 records waiting/);
          },
        },
        {
          say: 'First is Jim Carter. Date of birth and address agree with James Carter, and Jim is a nickname. Choose Same person to link it.',
          run: async s => {
            const firstItem = s.page.locator('nav[aria-label="Records to review"] button', { hasText: /Carter/i }).first();
            await s.click(firstItem);
            await s.see(s.page.locator('merit-match-comparison'));
            await s.highlight(s.page.locator('merit-match-comparison'), { describe: false });
            await s.click(s.page.getByRole('button', { name: /Same person as James Carter/i }));
            await s.expectText(s.page.locator('.usa-alert--success'), /Linked|Same person|saved/i);
          },
          hold: 700,
        },
        {
          say: 'Next is Rosa Martines. The date of birth and address match Rosa Martinez with a one-letter typo. Choose Same person to link it.',
          run: async s => {
            await s.see(s.page.locator('merit-match-comparison'));
            await s.highlight(s.page.locator('merit-match-comparison'), { describe: false });
            await s.click(s.page.getByRole('button', { name: /Same person as Rosa Martinez/i }));
            await s.expectText(s.page.locator('main'), /Nothing to review|No waiting records remain/i);
          },
          hold: 700,
        },
      ],
    },
    {
      title: 'Manually enter results for existing patients',
      steps: [
        {
          say: 'Next, enter two paper lab reports for existing patients. Open People and search for Leo Rivera first.',
          run: async s => {
            await s.click(s.page.getByRole('navigation').getByRole('link', { name: 'People', exact: true }));
            await s.see(s.page.getByRole('heading', { name: 'Find or add a person', level: 1 }));
            await s.type(s.page.locator('#search-q'), 'Rivera Leo 2021-02-02');
            await s.click(s.page.locator('main').getByRole('button', { name: 'Search' }));
            const row = s.page.locator('main tbody tr', { hasText: 'Rivera, Leo' });
            await s.click(row.getByRole('link', { name: 'Rivera, Leo' }));
            await s.see(s.page.getByRole('heading', { name: 'Leo Rivera', level: 1 }));
          },
        },
        {
          say: 'Open Enter a result on Leo Rivera, fill in the venous lead result from the report, and save.',
          run: async s => {
            await s.click(s.page.locator('#person-header').getByRole('link', { name: 'Enter a result' }));
            await enterLeadResult(s, {
              sampleType: 'Venous',
              value: '4.1',
              collected: '2026-10-28',
              reported: '2026-10-30',
              method: 'ICP-MS',
              accession: 'TRN-DAY-MAN-01',
              reason: 'Screening',
            });
            await s.highlight(s.page.locator('#result-saved .usa-alert'));
          },
        },
        {
          say: 'Now search for Mia Rivera and enter her paper lead result the same way.',
          run: async s => {
            await s.click(s.page.getByRole('navigation').getByRole('link', { name: 'People', exact: true }));
            await s.type(s.page.locator('#search-q'), 'Rivera Mia 2023-05-14');
            await s.click(s.page.locator('main').getByRole('button', { name: 'Search' }));
            const row = s.page.locator('main tbody tr', { hasText: 'Rivera, Mia' });
            await s.click(row.getByRole('link', { name: 'Rivera, Mia' }));
            await s.see(s.page.getByRole('heading', { name: 'Mia Rivera', level: 1 }));
            await s.click(s.page.locator('#person-header').getByRole('link', { name: 'Enter a result' }));
            await enterLeadResult(s, {
              sampleType: 'Venous',
              value: '2.4',
              collected: '2026-10-28',
              reported: '2026-10-30',
              method: 'ICP-MS',
              accession: 'TRN-DAY-MAN-02',
              reason: 'Screening',
            });
            await s.highlight(s.page.locator('#result-saved .usa-alert'));
          },
        },
      ],
    },
    {
      title: 'Add a new patient and enter new records',
      steps: [
        {
          say: 'Finally, a new patient has two paper reports. Search People first to confirm Tessa Holloway is not already in MERIT.',
          run: async s => {
            await s.click(s.page.getByRole('navigation').getByRole('link', { name: 'People', exact: true }));
            await s.type(s.page.locator('#search-q'), `${NEW_LAST} ${NEW_FIRST} ${NEW_DOB}`);
            await s.click(s.page.locator('main').getByRole('button', { name: 'Search' }));
            await s.expectText(s.page.locator('#search-results-body'), /No matches/);
            await s.highlight(s.page.locator('.merit-add-person'));
          },
        },
        {
          say: 'Choose Add a person. The name and date of birth carry over from the search. Select sex and save the new record.',
          run: async s => {
            await s.click(s.page.getByRole('link', { name: 'Add a person', exact: true }));
            await s.see(s.page.getByRole('heading', { name: 'Add a person', level: 1 }));
            await s.select(s.page.locator('#Sex'), 'Female');
            await s.click(s.page.locator('#person-identity-form').getByRole('button', { name: 'Save', exact: true }));
            await s.see(s.page.getByRole('heading', { name: `${NEW_FIRST} ${NEW_LAST}`, level: 1 }));
            await s.highlight(s.page.locator('#person-header'));
          },
        },
        {
          say: 'Choose Enter a result on the new person page to record her initial capillary screening result.',
          run: async s => {
            await s.click(s.page.locator('#person-header').getByRole('link', { name: 'Enter a result' }));
            await enterLeadResult(s, {
              sampleType: 'Capillary',
              value: '4.8',
              collected: '2026-10-20',
              reported: '2026-10-22',
              method: 'Point-of-care analyzer',
              accession: 'TRN-DAY-NEW-01',
              reason: 'Screening',
            });
            await s.highlight(s.page.locator('#result-saved .usa-alert'));
          },
        },
        {
          say: 'Choose Enter another result to record her follow-up venous confirmation on the same patient record.',
          run: async s => {
            await s.click(s.page.getByRole('link', { name: 'Enter another result', exact: true }));
            await enterLeadResult(s, {
              sampleType: 'Venous',
              value: '4.5',
              collected: '2026-10-28',
              reported: '2026-10-30',
              method: 'ICP-MS',
              accession: 'TRN-DAY-NEW-02',
              reason: 'Confirmation',
            });
            await s.highlight(s.page.locator('#result-saved .usa-summary-box'));
          },
          hold: 900,
        },
      ],
    },
    {
      title: 'Sign out',
      steps: [
        {
          say: 'At the end of the day, open the account menu in the header and choose Sign out to end the session cleanly.',
          run: async s => {
            await signOut(s);
            await s.highlight(s.page.locator('#UserName'), { describe: false });
          },
          hold: 700,
        },
      ],
    },
  ],
  recap: [
    'Reset a forgotten password and signed in with an authenticator code',
    'Reviewed data with a read-only profile, then switched to a full access profile',
    'Imported an HL7 file and matched incoming records to existing patients',
    'Entered manual results for existing patients, added a new patient with records, and signed out',
  ],
});
