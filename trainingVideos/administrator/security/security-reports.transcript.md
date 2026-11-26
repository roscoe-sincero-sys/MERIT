# Security reports: the complete walkthrough

**Role:** Administrator · **Feature:** Sign-in security · **Length:** 9:46

Use every security report as a county security officer, an administrator and a platform investigator.

*Everything shown is synthetic demonstration data. No real person, result or credential appears in this video.*

**[0:00]** Security reports: the complete walkthrough. Use every security report as a county security officer, an administrator and a platform investigator.

## 1. Choose the right scope and review access

**[0:12]** We begin as Sam, a Baltimore City security officer. All people and events in this video are synthetic training examples.

**[0:22]** Security officers review users, sign-ins, audit events and usage within their granted places. This role does not provide patient records or account editing.
- *On screen:* People who work for, or have access to, the tenants you review. Read only: ask an administrator to change an account.

**[0:34]** Check the active profile and working place before reviewing. A narrower profile narrows your reports; regional and platform administrators can cover several data roots.
- *On screen:* SS · Sam Security (training) · Baltimore City Health Department · ▾

**[0:45]** Filter listed users by name, user name or tenant. Review their home agency, master profile, MFA enrollment, last sign-in and account status.
- *Action:* Types “securitystaff” in Filter listed users
- *On screen:* Name User name Works for Master profile MFA Last sign-in Status · Security Training Staff 1 securitystaff1 Baltimore City Health Department (another administrator) Not yet enrolled Never Active · Security Training Staff 2 securitystaff2 Baltimore City Health Department (another administrator) Not yet enrolled Never Active · Security Training Staff 3 securitystaff3 Baltimore City Health Department 

**[0:57]** Staff can appear because they work here or hold access here. A home agency alone does not describe all their permissions.
- *On screen:* Name User name Works for Master profile MFA Last sign-in Status

**[1:05]** Export users and access for an access review. The export uses your permitted scope; the filter box only filters the on-screen list.
- *Action:* Selects “Export users and access (CSV)”

**[1:15]** Ask an administrator to correct unexpected access or an account problem. The security officer reviews evidence without changing accounts.
- *Action:* Types “” in Filter listed users

## 2. Investigate all sign-in patterns

**[1:25]** Open Sign-in activity from Administration to investigate suspicious patterns. These are leads to check, not proof that an account was compromised.
- *Action:* Selects “Administration (current section)”
- *Action:* Selects “Sign-in activity”

**[1:36]** Choose a window: one hour, one day, seven days or thirty days. Show refreshes the report for that window.
- *Action:* Chooses “7 days” for Window
- *Action:* Selects “Show”

**[1:44]** One address trying five or more accounts or unknown names suggests password spraying. Compare the target count, failures and first and last times.
- *On screen:* One address trying many accounts · Five or more different accounts or unknown names from one address: a sign of password spraying. · Addresses trying many accounts · Address Accounts or names Failures First Last · 192.0.2.10 5 5 Nov 26, 2026 10:45 AM Nov 26, 2026 10:45 AM

**[1:54]** One account tried from at least three addresses suggests credential stuffing. Follow the account link when you need its individual history.
- *On screen:* One account tried from many addresses · Failures from three or more addresses: a sign of credential stuffing. · Accounts tried from many addresses · Account Addresses Failures Last · Security Training Staff 1 securitystaff1 3 3 Nov 26, 2026 10:45 AM

**[2:03]** Signed in after repeated failures finds a full sign-in preceded by at least three failures in the previous hour. Compare the success address with the failed attempts.
- *On screen:* Signed in after repeated failures · A full sign-in after three or more failures on the same account within the hour before it. · None.

**[2:14]** Failures by address helps identify a noisy source. Compare affected accounts, unknown names, lockouts and rate-limit refusals.
- *On screen:* Failures by address · Failures by address · Address Failures Accounts Unknown names Lockouts Refused (rate limit) Last · 192.0.2.10 5 5 0 1 0 Nov 26, 2026 10:46 AM · 192.0.2.12 1 1 0 0 0 Nov 26, 2026 10:45 AM · 192.0.2.11 1 1 0 0 0 Nov 26, 2026 10:45 AM

**[2:24]** Failures by account helps distinguish one person having trouble from failures spread across many accounts and addresses.
- *On screen:* Failures by account · Failures by account · Account Failures Addresses Lockouts Last · Security Training Staff 1 securitystaff1 3 3 1 Nov 26, 2026 10:46 AM · Security Training Staff 5 securitystaff5 1 1 0 Nov 26, 2026 10:45 AM · Security Training Staff 4 securitystaff4 1 1 0 Nov 26, 2026 10:45 AM · Security Training Staff 3 securitystaff3 1 1 0 Nov 26, 2026 10:45 AM · Security Training Staff 2 sec

**[2:32]** Lockouts and refusals shows when access was blocked and why. Account-free rate-limit events and unknown names require platform investigation.
- *On screen:* Lockouts and refusals · Lockouts and rate-limit refusals · When Event Account Address Detail · Nov 26, 2026 10:46 AM Account locked securitystaff1 192.0.2.10

**[2:43]** None means no matching pattern in this period and scope. It does not prove there were no attempts elsewhere or outside the selected window.
- *Action:* Chooses “1 hours” for Window
- *Action:* Selects “Show”

**[2:52]** Export this report downloads every report section for the selected window. Section names identify the pattern represented by each CSV row.
- *Action:* Selects “Export this report (CSV)”

## 3. Trace views, changes and exports in the audit log

**[3:02]** Open Audit log for the tamper-evident history of views, changes and exports. It records actions and identifiers, rather than copies of free-text record contents.
- *Action:* Selects “Administration (current section)”
- *Action:* Selects “Audit log”
- *On screen:* Events 1–100 of 197, newest first.

**[3:14]** Choose Sign-in and account security (all), then Search, to see security actions together instead of choosing one action at a time.
- *Action:* Chooses “Sign-in and account security (all)” for Action
- *Action:* Selects “Search”

**[3:24]** Clear filters to return to the whole ledger. Combine User, Action, Record type and dates to answer who did what and when.
- *Action:* Selects “Clear filters”
- *On screen:* User · Anyone · Casey Morgan (Baltimore City) · Ivy Intake · Sam Security (training) · Action · Any action · Sign-in and account security (all) · Granted access · Ended grant · Deactivated · Reactivated · Unlocked account · Administrator reset MFA · Deleted · Viewed · Created · Changed · Exported · Searched · Signed in · Sign-in failed · Signed out · Set up multi-factor authentication · Turned on 

**[3:34]** For example, find Sam's access-review exports. Choose Sam under User, Exported under Action, and Users and access under Record type.
- *Action:* Chooses “Sam Security (training)” for User
- *Action:* Chooses “Exported” for Action
- *Action:* Chooses “Users and access” for Record type

**[3:45]** Set From and To for the review period, then Search. The count confirms whether your combined filters found events.
- *Action:* Types “2026-11-19” in From
- *Action:* Types “2026-11-27” in To
- *Action:* Selects “Search”
- *Action:* Selects “Clear filters”

**[3:53]** For one record, use Record id or its all events link. A record link opens the record only when your permissions allow it.
- *Action:* Selects “Every event for this record (all events)”
- *On screen:* c6a3abc2-b553-4e01-adb2-c04056812e18

**[4:02]** Events are newest first. Choose twenty-five or one hundred per page; use the page links to review older events when more exist.
- *Action:* Chooses “100” for Events per page
- *Action:* Selects “Search”

**[4:11]** Download CSV preserves the current filters, with at most the newest ten thousand events. Narrow the dates if you need a smaller review set.
- *Action:* Selects “Download CSV”

**[4:22]** County review includes scoped staff and events on records owned there when logged. Older events without an owner stamp rely on staff scope.
- *Action:* Selects “Clear filters”
- *On screen:* Events 1–100 of 198, newest first.

## 4. Understand screen usage and reconstruct a session

**[4:32]** Open Usage to review screen visits and navigation. Choose one, seven, thirty or ninety days, then Show usage.
- *Action:* Selects “Administration (current section)”
- *Action:* Selects “Usage”
- *Action:* Chooses “Last 7 days” for Period
- *Action:* Selects “Show usage”

**[4:42]** Visits by screen compares visits, sessions and timed visits. Visible time is optional, capped focused time; missing time means unknown.
- *On screen:* Visits by screen · Screen Visits Sessions Timed visits Average visible time · Audit log 24 3 24 7.7 seconds · Sign-in activity 9 3 9 22.0 seconds · Usage 7 3 6 12.8 seconds · My profiles 4 4 4 0.1 seconds · Usage session 4 2 2 14.9 seconds · Users 4 4 4 41.5 seconds · Person 2 1 2 12.0 seconds · Enter a result 2 1 2 12.0 seconds · Find or add a person 1 1 1 12.0 seconds · Result saved outcome 1 1 

**[4:53]** Expand Visits by active-profile role for role totals. One visit can have several roles, so adding role counts together would double-count visits.
- *Action:* Selects “Visits by active-profile role”

**[5:05]** Paths into Enter a result shows up to three preceding screens. Back-and-forth paths can be useful review, not necessarily mistakes.
- *On screen:* Five most common paths into Enter a result · Up to three preceding screens in the same recorded tab chain. A missing, expired or out-of-scope predecessor starts a new path; query and filter changes are not separate screens. · Find or add a person → Person → Enter a result — 1 visits in 1 sessions · Enter a result → Result saved outcome → Person → Enter a result — 1 visits in 1 sessions · Back-and-

**[5:15]** The observed search-to-saved flow is a strict four-screen sequence. Our seeded example illustrates navigation, not actual patient work or staff performance.
- *On screen:* Five most common paths into Enter a result · Up to three preceding screens in the same recorded tab chain. A missing, expired or out-of-scope predecessor starts a new path; query and filter changes are not separate screens. · Find or add a person → Person → Enter a result — 1 visits in 1 sessions · Enter a result → Result saved outcome → Person → Enter a result — 1 visits in 1 sessions · Back-and-

**[5:27]** Download summary CSV saves the report aggregates. Recent sessions is a limited list; the totals above cover the whole selected period.
- *Action:* Selects “Download summary CSV”
- *On screen:* Recent sessions · Showing the latest 5 of 5 sessions in this period. Aggregate counts above cover the full period. · User First recorded visit Last recorded visit Visits Sequence · Sam Security (training) Nov 26, 2026 10:46 AM Nov 26, 2026 10:50 AM 14 View session · 690d564f-5956-4dd2-887b-a1249c9fa819 · Sam Security (training) Nov 26, 2026 10:38 AM Nov 26, 2026 10:44 AM 18 View session · fd4ad619

**[5:38]** Choose View session to inspect the ordered screens, active profile and any retained sign-in or session-end events.
- *Action:* Selects “View session 690d564f-5956-4dd2-887b-a1249c9fa819”

**[5:47]** Session timestamps are UTC with milliseconds. Tabs can interleave; expand Visit links and follow the predecessor when comparing a workflow.
- *Action:* Selects “Visit links”
- *On screen:* When UTC Screen Navigation hint Context Visible time · 2026-11-26 15:46:02.500 My profiles · /account/profiles · Visit links · Visit: f9213151-99c7-4717-b1fd-49aebe6bccfd · Predecessor: · Tab: 4f0595a9-5d76-47ba-8d73-4a2f45290114 · Direct Security officer · Master profile · 0.1 seconds · 2026-11-26 15:46:07.697 Users · /admin/users · Visit links · Direct Security officer · Master profile · 77.5 se

**[5:58]** Download this page CSV exports the current page, up to one hundred visits. Use session paging for additional visits.
- *Action:* Selects “Download this page CSV”

**[6:07]** You can also paste a session reference into Open a session reference. The reference is a reporting identifier, not a sign-in credential.
- *Action:* Selects “Back to usage”
- *Action:* Types “690d564f-5956-4dd2-887b-a1249c9fa819” in Open a session reference
- *Action:* Selects “Open session”

**[6:17]** No retained events or no visits can reflect scope, period, missing telemetry or retention. Browser closure alone does not prove that someone signed out.

## 5. Administrator: investigate one account

**[6:29]** We now switch to the Maryland administrator. Administrators can open account pages and act on problems that a security officer reports.

**[6:39]** Filter for Security Training Staff 1 and open the account. Review the master profile to check places, roles, access levels and grant dates.
- *Action:* Types “securitystaff1” in Filter listed users
- *Action:* Selects “Security Training Staff 1”

**[6:50]** Sign-in history shows the latest fifty events: time, event, reason, address, browser and shortened session and attempt references.
- *On screen:* Sign-in history · The latest 5 events. Times are local; addresses come from the client after any trusted proxy. · Sign-in history, newest first · When Event Detail Address Browser Session · attempt · Nov 26, 2026 10:46 AM Account locked 192.0.2.10 · Nov 26, 2026 10:45 AM Second factor failed authenticator app 192.0.2.12 · Nov 26, 2026 10:45 AM Signed in 192.0.2.12 · Nov 26, 2026 10:45 AM Wrong pas

**[7:00]** Correlate password and second-factor failures, successful sessions, expirations, sign-outs and administrator actions before deciding what needs follow-up.
- *On screen:* Sign-in history, newest first · When Event Detail Address Browser Session · attempt · Nov 26, 2026 10:46 AM Account locked 192.0.2.10 · Nov 26, 2026 10:45 AM Second factor failed authenticator app 192.0.2.12 · Nov 26, 2026 10:45 AM Signed in 192.0.2.12 · Nov 26, 2026 10:45 AM Wrong password Bad password 192.0.2.11 · Nov 26, 2026 10:45 AM Wrong password Bad password 192.0.2.10 MERIT synthetic train

**[7:12]** Export this account's sign-in history downloads up to one thousand events. Keep the account history with the activity and audit exports for your review.
- *Action:* Selects “Export this account's sign-in history (CSV)”

## 6. Platform investigator: unknown user names

**[7:23]** We now switch to the platform administrator. Unknown user names belong to no county, so they require the security investigation permission.

**[7:33]** Open Unknown user names. Hashes group repeated attempts at the same name without revealing what someone typed.
- *Action:* Selects “Unknown user names”
- *On screen:* Names (hashed) · Export the hashed list (CSV) · Unknown user names, most attempts first · Hash Attempts Addresses First Last Addresses seen · c3f24923733d 4 1 Nov 26, 2026 10:19 AM Nov 26, 2026 10:46 AM ::1 · f748833460c6 2 1 Nov 26, 2026 10:28 AM Nov 26, 2026 10:33 AM ::1

**[7:41]** Compare attempts, addresses, first and last times, and addresses seen. A hash is useful for correlation even when a name remains hidden.
- *On screen:* Names (hashed) · Export the hashed list (CSV) · Unknown user names, most attempts first · Hash Attempts Addresses First Last Addresses seen · c3f24923733d 4 1 Nov 26, 2026 10:19 AM Nov 26, 2026 10:46 AM ::1 · f748833460c6 2 1 Nov 26, 2026 10:28 AM Nov 26, 2026 10:33 AM ::1

**[7:52]** Look up a known suspected name without decrypting anything. Type the name and choose Look up to see its attempts over the last ninety days.
- *Action:* Types “trainingunknown” in User name someone may have tried
- *Action:* Selects “Look up”
- *On screen:* Attempts at "trainingunknown" · 4 attempts in the last 90 days. · Attempts at the name looked up · When Where Address Browser · Nov 26, 2026 10:46 AM Unknown user name ::1 Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/158.0.0.0 Safari/537.36 Edg/158.0.0.0 · Nov 26, 2026 10:38 AM Unknown user name ::1 Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleW

**[8:01]** A lookup with no matches reports no attempts in that period. Lookups are recorded in the audit log.
- *Action:* Types “trainingnomatch” in User name someone may have tried
- *Action:* Selects “Look up”

**[8:08]** Export the hashed list saves hashes and counts. The typed names are never included in this export.
- *Action:* Selects “Export the hashed list (CSV)”

**[8:15]** Use Show names only when the investigation needs it. A name may contain a password entered in the wrong box; do not copy or share the revealed text.
- *On screen:* Names may occasionally include a password someone typed into the user-name box by mistake. Showing names is recorded in the audit log. Use them for this investigation only; don't copy them out of this page or share them.

**[8:26]** Enter a current authenticator code, then Show names. The code stays masked in this video, and the reveal is recorded in the audit log.
- *Action:* Selects “Show names”

**[8:36]** Names are revealed for this page only. If an encryption key is no longer available, a name can instead appear as no longer readable.
- *On screen:* Names (shown for this page only) · Export the hashed list (CSV) · Unknown user names, most attempts first · Name Attempts Addresses First Last Addresses seen · trainingunknown · c3f24923733d · 4 1 Nov 26, 2026 10:19 AM Nov 26, 2026 10:46 AM ::1 · loginunknown · f748833460c6 · 2 1 Nov 26, 2026 10:28 AM Nov 26, 2026 10:33 AM ::1

**[8:46]** Return to Unknown user names to restore the hashed view. This ends the temporary on-page reveal.

## 7. Follow up and preserve the review trail

**[8:53]** Finally, open Audit log and filter Action to Export. Report exports, unknown-name lookups and reveals leave an audit trail of the investigation itself.
- *Action:* Selects “Search”

**[9:06]** Use the report that matches the question: access for permissions, sign-ins for authentication, audit for recorded actions, and usage for observed navigation.

**[9:17]** Record the scope, time window, affected account or session, and your findings. Refer account and grant changes to an authorized administrator.

## Recap

**[9:29]** In this video you reviewed users and access in scope, investigated sign-ins and account history, filtered and exported audit events, traced usage paths and sessions, and investigated unknown names at platform level.

- Reviewed users and access in scope
- Investigated sign-ins and account history
- Filtered and exported audit events
- Traced usage paths and sessions
- Investigated unknown names at platform level

---

Generated 2026-11-26 from `training/workflows/administrator/security/security-reports.workflow.ts` against MERIT 8410bca 2026-11-24.
