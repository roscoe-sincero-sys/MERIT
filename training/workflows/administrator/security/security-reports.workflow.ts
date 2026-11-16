import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import type { Locator } from '@playwright/test';
import { ensureSession, forgetSession, sessionFile } from '../../../lib/auth.ts';
import { BASE_URL, PATHS } from '../../../lib/config.ts';
import { sql } from '../../../lib/sql.ts';
import type { Stage } from '../../../lib/stage.ts';
import { totp } from '../../../lib/totp.ts';
import { defineWorkflow } from '../../../lib/workflow.ts';

// Only the guarded synthetic training database is used. Fixed identities and soft deletion
// make report examples repeatable; real application requests create encrypted unknown names.
function prepare(): void {
  sql(`SET XACT_ABORT ON; BEGIN TRANSACTION;
DECLARE @root UNIQUEIDENTIFIER = (SELECT tenantId FROM auth.tenant WHERE tenantCode='MD-LEAD' AND isDeleted=0);
DECLARE @county UNIQUEIDENTIFIER = (SELECT tenantId FROM auth.tenant WHERE tenantCode='MD-BCHD' AND isDeleted=0);
DECLARE @other UNIQUEIDENTIFIER = (SELECT tenantId FROM auth.tenant WHERE tenantCode='MD-BCO' AND isDeleted=0);
DECLARE @hash NVARCHAR(500) = (SELECT passwordHash FROM auth.appUser WHERE userName=N'supervisor' AND isDeleted=0);
DECLARE @role UNIQUEIDENTIFIER = (SELECT roleId FROM auth.role WHERE rootTenantId=@root AND roleCode='securityOfficer' AND isDeleted=0);
DECLARE @officer UNIQUEIDENTIFIER = CAST(HASHBYTES('MD5', N'merit-training:securitydemo') AS UNIQUEIDENTIFIER);
EXEC auth.uspSaveUser @appUserId=@officer, @userName=N'securitydemo', @displayName=N'Sam Security (training)',
 @email=N'securitydemo@merit.example', @passwordHash=@hash, @homeTenantId=@county;
DECLARE @grant UNIQUEIDENTIFIER = CAST(HASHBYTES('MD5', N'merit-training:securitydemo:grant') AS UNIQUEIDENTIFIER);
EXEC auth.uspSaveUserAssignment @userAssignmentId=@grant, @appUserId=@officer, @roleId=@role,
 @tenantId=@county, @includeDescendants=0, @accessLevel='full', @startDate='2026-01-01';
UPDATE logs.signInEvent SET isDeleted=1, deletedBy=N'merit-training', deletedDateUtc=SYSUTCDATETIME()
 WHERE traceId='TRN-SECURITY-REPORTS' AND isDeleted=0;
DECLARE @i INT=1, @user UNIQUEIDENTIFIER, @name NVARCHAR(100), @display NVARCHAR(200), @email NVARCHAR(254);
WHILE @i<=6 BEGIN
 SET @name=CONCAT(N'securitystaff',@i);
 SET @display=CONCAT(N'Security Training Staff ',@i);
 SET @email=CONCAT(@name,N'@merit.example');
 SET @user=CAST(HASHBYTES('MD5',CONCAT(N'merit-training:',@name)) AS UNIQUEIDENTIFIER);
 DECLARE @home UNIQUEIDENTIFIER=CASE WHEN @i=6 THEN @other ELSE @county END;
 EXEC auth.uspSaveUser @appUserId=@user,@userName=@name,@displayName=@display,@email=@email,@passwordHash=@hash,@homeTenantId=@home;
 IF @i<=5 EXEC logs.uspRecordSignInEvent @eventType='passwordFailed',@step='password',@reason='badPassword',
  @appUserId=@user,@clientAddress='192.0.2.10',@userAgent=N'MERIT synthetic training browser',@traceId='TRN-SECURITY-REPORTS';
 SET @i+=1;
END;
SET @user=CAST(HASHBYTES('MD5',N'merit-training:securitystaff1') AS UNIQUEIDENTIFIER);
EXEC logs.uspRecordSignInEvent @eventType='passwordFailed',@step='password',@reason='badPassword',@appUserId=@user,@clientAddress='192.0.2.11',@traceId='TRN-SECURITY-REPORTS';
EXEC logs.uspRecordSignInEvent @eventType='mfaFailed',@step='secondFactor',@method='totp',@appUserId=@user,@clientAddress='192.0.2.12',@traceId='TRN-SECURITY-REPORTS';
EXEC logs.uspRecordSignInEvent @eventType='sessionStarted',@step='session',@appUserId=@user,@clientAddress='192.0.2.12',@traceId='TRN-SECURITY-REPORTS';
EXEC logs.uspRecordSignInEvent @eventType='accountLocked',@step='password',@appUserId=@user,@clientAddress='192.0.2.10',@traceId='TRN-SECURITY-REPORTS';
EXEC logs.uspRecordSignInEvent @eventType='rateLimited',@step='request',@clientAddress='192.0.2.10',@traceId='TRN-SECURITY-REPORTS';
-- An illustrative navigation sequence, never patient data or a claim of actual work.
UPDATE logs.screenVisit SET isDeleted=1,deletedBy=N'merit-training',deletedDateUtc=SYSUTCDATETIME()
 WHERE createdBy=N'merit-training:security-reports' AND isDeleted=0;
DECLARE @session UNIQUEIDENTIFIER=CAST(HASHBYTES('MD5',N'merit-training:security-session') AS UNIQUEIDENTIFIER);
DECLARE @tab UNIQUEIDENTIFIER=NEWID(),@prev UNIQUEIDENTIFIER=NULL,@visit UNIQUEIDENTIFIER,@screen VARCHAR(60);
SET @i=1;
WHILE @i<=6 BEGIN
 SET @visit=NEWID();
 SET @screen=CASE @i WHEN 1 THEN 'people.search' WHEN 2 THEN 'person' WHEN 3 THEN 'result.new' WHEN 4 THEN 'result.saved' WHEN 5 THEN 'person' ELSE 'result.new' END;
 INSERT logs.screenVisit(screenVisitId,eventUtc,rootTenantId,actingTenantId,appUserId,sessionReference,roleCodes,screenCode,tabId,previousVisitId,durationMs,createdBy)
 VALUES(@visit,DATEADD(SECOND,-300+@i*20,SYSUTCDATETIME()),@root,@county,@user,@session,'intakeClerk',@screen,@tab,@prev,12000,N'merit-training:security-reports');
 SET @prev=@visit; SET @i+=1;
END;
COMMIT;`);
  forgetSession('securitydemo');
}

async function switchUser(s: Stage, user: string, path: string): Promise<void> {
  const state = JSON.parse(readFileSync(sessionFile(user), 'utf8'));
  await s.page.context().clearCookies();
  await s.page.context().addCookies(state.cookies);
  await s.goto(path);
}

async function download(s: Stage, link: Locator): Promise<void> {
  const pending = s.page.waitForEvent('download');
  await s.click(link);
  const file = await pending;
  if (await file.failure()) throw new Error('Report download failed.');
  const stream = await file.createReadStream();
  if (!stream) throw new Error('Report download has no content.');
  let content = '';
  for await (const chunk of stream) content += chunk.toString();
  if (!content.includes(',') || content.includes('<html')) throw new Error('Expected CSV report.');
}

async function section(s: Stage, id: string): Promise<void> {
  const target = s.page.locator(`section[aria-labelledby="${id}"]`);
  await s.scrollTo(target);
  await s.highlight(target);
}

async function menu(s: Stage, name: string): Promise<void> {
  const group = s.page.locator('#merit-nav details').filter({ has: s.page.locator('summary', { hasText: 'Administration' }) });
  if (!await group.getAttribute('open').then(value => value !== null)) await s.click(group.locator('summary'));
  await s.click(group.getByRole('link', { name, exact: true }));
}

export default defineWorkflow({
  id: 'security-reports',
  title: 'Security reports: the complete walkthrough',
  summary: 'Use every security report as a county security officer, an administrator and a platform investigator.',
  role: 'administrator', feature: 'security', user: 'securitydemo', start: '/admin/users', prepare,
  setup: async page => {
    const browser = page.context().browser()!;
    await ensureSession(browser, 'admin');
    await ensureSession(browser, 'platform');
    // Generate a genuinely encrypted synthetic unknown name using the sign-in application.
    const context = await browser.newContext({ baseURL: BASE_URL, ignoreHTTPSErrors: true });
    const unknown = await context.newPage();
    await unknown.goto('/account/signin');
    await unknown.locator('#UserName').fill('trainingunknown');
    await unknown.locator('#Password').fill('Synthetic failed sign-in example');
    await unknown.getByRole('button', { name: 'Continue', exact: true }).click();
    await unknown.waitForLoadState('networkidle');
    await context.close();
  },
  scenes: [
    { title: 'Choose the right scope and review access', steps: [
      { say: 'We begin as Sam, a Baltimore City security officer. All people and events in this video are synthetic training examples.', run: async s => s.see(s.page.getByRole('heading', { name: 'Users', exact: true })) },
      { say: 'Security officers review users, sign-ins, audit events and usage within their granted places. This role does not provide patient records or account editing.', run: async s => {
        await s.highlight(s.page.locator('main > p').first());
        if (await s.page.getByRole('link', { name: 'Add a user', exact: true }).count()) throw new Error('Officer must be read only.');
        if (await s.page.locator('[data-user-row]', { hasText: 'securitystaff6' }).count()) throw new Error('Out-of-county fixture leaked.');
      } },
      { say: 'Check the active profile and working place before reviewing. A narrower profile narrows your reports; regional and platform administrators can cover several data roots.', run: async s => s.highlight(s.page.locator('header a.merit-profile-switch')) },
      { say: 'Filter listed users by name, user name or tenant. Review their home agency, master profile, MFA enrollment, last sign-in and account status.', run: async s => {
        await s.type(s.page.getByLabel('Filter listed users'), 'securitystaff');
        await s.highlight(s.page.locator('#users-table'));
      } },
      { say: 'Staff can appear because they work here or hold access here. A home agency alone does not describe all their permissions.', run: async s => s.highlight(s.page.locator('#users-table thead')) },
      { say: 'Export users and access for an access review. The export uses your permitted scope; the filter box only filters the on-screen list.', run: async s => download(s, s.page.getByRole('link', { name: 'Export users and access (CSV)', exact: true })) },
      { say: 'Ask an administrator to correct unexpected access or an account problem. The security officer reviews evidence without changing accounts.', run: async s => s.type(s.page.getByLabel('Filter listed users'), '') },
    ] },
    { title: 'Investigate all sign-in patterns', steps: [
      { say: 'Open Sign-in activity from Administration to investigate suspicious patterns. These are leads to check, not proof that an account was compromised.', run: async s => menu(s, 'Sign-in activity') },
      { say: 'Choose a window: one hour, one day, seven days or thirty days. Show refreshes the report for that window.', run: async s => { await s.select(s.page.getByLabel('Window'), '7 days'); await s.click(s.page.getByRole('button', { name: 'Show', exact: true })); } },
      { say: 'One address trying five or more accounts or unknown names suggests password spraying. Compare the target count, failures and first and last times.', run: async s => { await section(s, 'spray-heading'); await s.expectText(s.page.locator('section[aria-labelledby="spray-heading"]'), '192.0.2.10'); } },
      { say: 'One account tried from at least three addresses suggests credential stuffing. Follow the account link when you need its individual history.', run: async s => section(s, 'stuffing-heading') },
      { say: 'Signed in after repeated failures finds a full sign-in preceded by at least three failures in the previous hour. Compare the success address with the failed attempts.', run: async s => section(s, 'success-heading') },
      { say: 'Failures by address helps identify a noisy source. Compare affected accounts, unknown names, lockouts and rate-limit refusals.', run: async s => section(s, 'address-heading') },
      { say: 'Failures by account helps distinguish one person having trouble from failures spread across many accounts and addresses.', run: async s => section(s, 'account-heading') },
      { say: 'Lockouts and refusals shows when access was blocked and why. Account-free rate-limit events and unknown names require platform investigation.', run: async s => section(s, 'locks-heading') },
      { say: 'None means no matching pattern in this period and scope. It does not prove there were no attempts elsewhere or outside the selected window.', run: async s => { await s.scrollTo(s.page.getByLabel('Window')); await s.select(s.page.getByLabel('Window'), '1 hours'); await s.click(s.page.getByRole('button', { name: 'Show', exact: true })); } },
      { say: 'Export this report downloads every report section for the selected window. Section names identify the pattern represented by each CSV row.', run: async s => download(s, s.page.getByRole('link', { name: 'Export this report (CSV)', exact: true })) },
    ] },
    { title: 'Trace views, changes and exports in the audit log', steps: [
      { say: 'Open Audit log for the tamper-evident history of views, changes and exports. It records actions and identifiers, rather than copies of free-text record contents.', run: async s => { await menu(s, 'Audit log'); await s.highlight(s.page.locator('#audit-count')); } },
      { say: 'Choose Sign-in and account security (all), then Search, to see security actions together instead of choosing one action at a time.', run: async s => { await s.select(s.page.getByLabel('Action', { exact: true }), 'Sign-in and account security (all)'); await s.click(s.page.getByRole('button', { name: 'Search', exact: true })); } },
      { say: 'Clear filters to return to the whole ledger. Combine User, Action, Record type and dates to answer who did what and when.', run: async s => { await s.click(s.page.getByRole('link', { name: 'Clear filters', exact: true })); await s.highlight(s.page.locator('.merit-audit-filters')); } },
      { say: 'For example, find Sam\'s access-review exports. Choose Sam under User, Exported under Action, and Users and access under Record type.', run: async s => {
        const label = await s.page.locator('#userId option').filter({ hasText: 'Sam Security' }).innerText();
        await s.select(s.page.getByLabel('User', { exact: true }), label);
        await s.select(s.page.getByLabel('Action', { exact: true }), 'Exported');
        await s.select(s.page.getByLabel('Record type', { exact: true }), 'Users and access');
      } },
      { say: 'Set From and To for the review period, then Search. The count confirms whether your combined filters found events.', run: async s => {
        const now = new Date();
        await s.type(s.page.getByLabel('From', { exact: true }), new Date(now.getTime() - 7 * 86400000).toISOString().slice(0, 10));
        await s.type(s.page.getByLabel('To', { exact: true }), new Date(now.getTime() + 86400000).toISOString().slice(0, 10));
        await s.click(s.page.getByRole('button', { name: 'Search', exact: true }));
        await s.expectText(s.page.locator('#audit-count'), /Events/);
        await s.click(s.page.getByRole('link', { name: 'Clear filters', exact: true }));
      } },
      { say: 'For one record, use Record id or its all events link. A record link opens the record only when your permissions allow it.', run: async s => {
        const link = s.page.getByRole('link', { name: 'Every event for this record', exact: true }).first();
        await s.click(link); await s.highlight(s.page.getByLabel('Record id', { exact: true }));
      } },
      { say: 'Events are newest first. Choose twenty-five or one hundred per page; use the page links to review older events when more exist.', run: async s => { await s.select(s.page.getByLabel('Events per page'), '100'); await s.click(s.page.getByRole('button', { name: 'Search', exact: true })); } },
      { say: 'Download CSV preserves the current filters, with at most the newest ten thousand events. Narrow the dates if you need a smaller review set.', run: async s => download(s, s.page.getByRole('link', { name: /Download CSV/ })) },
      { say: 'County review includes scoped staff and events on records owned there when logged. Older events without an owner stamp rely on staff scope.', run: async s => { await s.click(s.page.getByRole('link', { name: 'Clear filters', exact: true })); await s.highlight(s.page.locator('#audit-count')); } },
    ] },
    { title: 'Understand screen usage and reconstruct a session', steps: [
      { say: 'Open Usage to review screen visits and navigation. Choose one, seven, thirty or ninety days, then Show usage.', run: async s => { await menu(s, 'Usage'); await s.select(s.page.getByLabel('Period'), 'Last 7 days'); await s.click(s.page.getByRole('button', { name: 'Show usage', exact: true })); } },
      { say: 'Visits by screen compares visits, sessions and timed visits. Visible time is optional, capped focused time; missing time means unknown.', run: async s => section(s, 'usage-screens') },
      { say: 'Expand Visits by active-profile role for role totals. One visit can have several roles, so adding role counts together would double-count visits.', run: async s => s.click(s.page.getByText('Visits by active-profile role', { exact: true })) },
      { say: 'Paths into Enter a result shows up to three preceding screens. Back-and-forth paths can be useful review, not necessarily mistakes.', run: async s => section(s, 'usage-paths') },
      { say: 'The observed search-to-saved flow is a strict four-screen sequence. Our seeded example illustrates navigation, not actual patient work or staff performance.', run: async s => s.highlight(s.page.locator('section[aria-labelledby="usage-paths"]')) },
      { say: 'Download summary CSV saves the report aggregates. Recent sessions is a limited list; the totals above cover the whole selected period.', run: async s => { await download(s, s.page.getByRole('link', { name: 'Download summary CSV', exact: true })); await section(s, 'usage-sessions'); } },
      { say: 'Choose View session to inspect the ordered screens, active profile and any retained sign-in or session-end events.', run: async s => { await s.click(s.page.getByRole('link', { name: /View session/ }).first()); await s.see(s.page.getByRole('heading', { name: 'Usage session', exact: true })); } },
      { say: 'Session timestamps are UTC with milliseconds. Tabs can interleave; expand Visit links and follow the predecessor when comparing a workflow.', run: async s => { await s.click(s.page.getByText('Visit links', { exact: true }).first()); await s.highlight(s.page.locator('main table').first()); } },
      { say: 'Download this page CSV exports the current page, up to one hundred visits. Use session paging for additional visits.', run: async s => download(s, s.page.getByRole('link', { name: 'Download this page CSV', exact: true })) },
      { say: 'You can also paste a session reference into Open a session reference. The reference is a reporting identifier, not a sign-in credential.', run: async s => {
        const reference = await s.page.locator('main > p code').innerText();
        await s.click(s.page.getByRole('link', { name: 'Back to usage', exact: true }));
        await s.type(s.page.getByLabel('Open a session reference'), reference);
        await s.click(s.page.getByRole('button', { name: 'Open session', exact: true }));
        await s.see(s.page.getByRole('heading', { name: 'Usage session', exact: true }));
      } },
      { say: 'No retained events or no visits can reflect scope, period, missing telemetry or retention. Browser closure alone does not prove that someone signed out.' },
    ] },
    { title: 'Administrator: investigate one account', steps: [
      { say: 'We now switch to the Maryland administrator. Administrators can open account pages and act on problems that a security officer reports.', run: async s => switchUser(s, 'admin', '/admin/users') },
      { say: 'Filter for Security Training Staff 1 and open the account. Review the master profile to check places, roles, access levels and grant dates.', run: async s => {
        await s.type(s.page.getByLabel('Filter listed users'), 'securitystaff1');
        await s.click(s.page.getByRole('link', { name: 'Security Training Staff 1', exact: true }));
        await s.scrollTo(s.page.getByRole('heading', { name: 'Master profile', exact: true }));
      } },
      { say: 'Sign-in history shows the latest fifty events: time, event, reason, address, browser and shortened session and attempt references.', run: async s => section(s, 'signin-history-heading') },
      { say: 'Correlate password and second-factor failures, successful sessions, expirations, sign-outs and administrator actions before deciding what needs follow-up.', run: async s => s.highlight(s.page.locator('.merit-signin-history')) },
      { say: 'Export this account\'s sign-in history downloads up to one thousand events. Keep the account history with the activity and audit exports for your review.', run: async s => download(s, s.page.getByRole('link', { name: "Export this account's sign-in history (CSV)", exact: true })) },
    ] },
    { title: 'Platform investigator: unknown user names', steps: [
      { say: 'We now switch to the platform administrator. Unknown user names belong to no county, so they require the security investigation permission.', run: async s => switchUser(s, 'platform', '/admin/sign-in-activity') },
      { say: 'Open Unknown user names. Hashes group repeated attempts at the same name without revealing what someone typed.', run: async s => { await s.click(s.page.getByRole('link', { name: 'Unknown user names', exact: true })); await section(s, 'names-heading'); } },
      { say: 'Compare attempts, addresses, first and last times, and addresses seen. A hash is useful for correlation even when a name remains hidden.', run: async s => s.highlight(s.page.locator('section[aria-labelledby="names-heading"]')) },
      { say: 'Look up a known suspected name without decrypting anything. Type the name and choose Look up to see its attempts over the last ninety days.', run: async s => { await s.type(s.page.getByLabel('User name someone may have tried'), 'trainingunknown'); await s.click(s.page.getByRole('button', { name: 'Look up', exact: true })); await section(s, 'attempts-heading'); } },
      { say: 'A lookup with no matches reports no attempts in that period. Lookups are recorded in the audit log.', run: async s => { await s.type(s.page.getByLabel('User name someone may have tried'), 'trainingnomatch'); await s.click(s.page.getByRole('button', { name: 'Look up', exact: true })); await s.expectText(s.page.locator('section[aria-labelledby="attempts-heading"]'), 'No attempts at that name'); } },
      { say: 'Export the hashed list saves hashes and counts. The typed names are never included in this export.', run: async s => download(s, s.page.getByRole('link', { name: 'Export the hashed list (CSV)', exact: true })) },
      { say: 'Use Show names only when the investigation needs it. A name may contain a password entered in the wrong box; do not copy or share the revealed text.', run: async s => { await s.scrollTo(s.page.locator('.usa-alert--warning')); await s.highlight(s.page.locator('.usa-alert--warning')); } },
      { say: 'Enter a current authenticator code, then Show names. The code stays masked in this video, and the reveal is recorded in the audit log.', run: async s => {
        await s.scrollTo(s.page.getByLabel('Six-digit code from your authenticator app'));
        // Do not send the code through Stage.type: it would enter the transcript action log.
        await s.page.getByLabel('Six-digit code from your authenticator app').fill(totp(readFileSync(join(PATHS.auth, 'platform.totp'), 'utf8').trim()));
        await s.click(s.page.getByRole('button', { name: 'Show names', exact: true }));
        await s.see(s.page.getByRole('heading', { name: 'Names (shown for this page only)', exact: true }));
      } },
      { say: 'Names are revealed for this page only. If an encryption key is no longer available, a name can instead appear as no longer readable.', run: async s => section(s, 'names-heading') },
      { say: 'Return to Unknown user names to restore the hashed view. This ends the temporary on-page reveal.', run: async s => { await s.goto('/admin/sign-in-activity/unknown-names'); await s.see(s.page.getByRole('heading', { name: 'Names (hashed)', exact: true })); } },
    ] },
    { title: 'Follow up and preserve the review trail', steps: [
      { say: 'Finally, open Audit log and filter Action to Export. Report exports, unknown-name lookups and reveals leave an audit trail of the investigation itself.', run: async s => { await s.goto('/admin/audit'); await s.page.getByLabel('Action', { exact: true }).selectOption('export'); await s.click(s.page.getByRole('button', { name: 'Search', exact: true })); } },
      { say: 'Use the report that matches the question: access for permissions, sign-ins for authentication, audit for recorded actions, and usage for observed navigation.' },
      { say: 'Record the scope, time window, affected account or session, and your findings. Refer account and grant changes to an authorized administrator.' },
    ] },
  ],
  recap: ['Reviewed users and access in scope', 'Investigated sign-ins and account history', 'Filtered and exported audit events', 'Traced usage paths and sessions', 'Investigated unknown names at platform level'],
});
