# A typical day in MERIT: sign in, profiles, HL7 intake and manual entry

**Role:** Intake clerk · **Feature:** Lab file intake · **Length:** 5:08

Reset a forgotten password, switch between read-only and full profiles, import an HL7 file, match records, enter results and sign out.

*Everything shown is synthetic demonstration data. No real person, result or credential appears in this video.*

**[0:00]** A typical day in MERIT: sign in, profiles, HL7 intake and manual entry. Reset a forgotten password, switch between read-only and full profiles, import an HL7 file, match records, enter results and sign out.

## 1. Reset a forgotten password and sign in

**[0:19]** Jordan starts the day on the sign-in screen, enters a user name and password, and realizes the password does not work.
- *Action:* Types “logindaily” in User name
- *Action:* Types into Password (hidden)
- *Action:* Selects “Continue”
- *On screen:* The user name or password is not right.

**[0:27]** Choose Forgot your password? to request a private reset link by email.
- *Action:* Selects “Forgot your password?”
- *Action:* Types “logindaily” in User name
- *Action:* Selects “Email me a reset link”
- *On screen:* Check your email · If MERIT has an account with that user name and an email address, we've sent it a link to reset the password. The link works once, for 20 minutes. · Email can take a minute or two to arrive. Check your junk or spam folder too. Only the newest link works. · No email? You may have mistyped the user name, or the account may have no email address. Contact your MERIT administrator. ·

**[0:34]** Open the reset link from email, verify with an authenticator code, and set a new passphrase.
- *Action:* Types into Code from your authenticator app, or a recovery code (hidden)
- *Action:* Types into New password (hidden)
- *Action:* Types into New password again (hidden)
- *Action:* Selects “Reset password”

**[0:48]** Return to sign in with the new password and complete the authenticator step.
- *Action:* Selects “Sign in”
- *Action:* Types “logindaily” in User name
- *Action:* Types into Password (hidden)
- *Action:* Selects “Continue”
- *Action:* Types into Six-digit code from your authenticator app (hidden)
- *Action:* Selects “Verify and sign in”

## 2. Review data with a read-only profile

**[1:00]** Before making changes, open My profiles from the account menu and pick a read-only profile to review data safely.
- *Action:* Selects “Profile: Master profile — Jordan Daily, switch profile or jurisdiction”
- *Action:* Selects “Switch profile or jurisdiction”
- *Action:* Selects “Use Maryland review (read only)”

**[1:08]** With the read-only profile active, review Work queues and existing records. Write tools like Upload lab results stay hidden.
- *Action:* Selects “Work queues”
- *On screen:* Task · ⇅ · Quick view Queue · ⇅ · Person Case Due · ▲ · Assigned · Notify the prenatal care provider · Case PB-2026-000003 is for a pregnant person. · Quick view Prenatal provider notifications Okafor, Grace PB-2026-000003 Nov 11, 2026 · Overdue · Newborn testing follow-up · Arrange testing of the newborn after delivery (case PB-2026-000003). · Quick view Newborn testing Okafor, Grace PB-2026-0000

## 3. Switch to a full CRUD profile

**[1:18]** Ready for daily intake work, open My profiles again and switch to the full access profile.
- *Action:* Selects “Profile: Maryland review (read only) — Jordan Daily, switch profile or jurisdict”
- *Action:* Selects “Switch profile or jurisdiction”
- *Action:* Selects “Use Maryland daily intake (full CRUD)”

**[1:25]** The header confirms the full profile is active, and Lab imports now offers Upload lab results and Match review.
- *Action:* Selects “Lab imports”
- *On screen:* Lab imports · Upload lab results · Held records · Match review · Receipt quality

## 4. Import an HL7 file

**[1:33]** Open Upload lab results, choose the HL7 profile for Chesapeake Reference Laboratory, and attach the HL7 file.
- *Action:* Selects “Upload lab results”
- *Action:* Chooses “HL7 message from Chesapeake Reference Laboratory” for Sender and intake profile

**[1:42]** Choose Upload. MERIT queues the HL7 file and processes each message in the background.
- *Action:* Selects “Upload”
- *On screen:* daily-hl7-training.hl7 · All rows held Chesapeake Reference Laboratory · Nov 26, 2026 1:58 PM · Jordan Daily · Received 2 · loaded 0 · held 2 · rejected 0

## 5. Match imported records to existing patients

**[1:50]** Two imported rows need person matching. Open Match review under Lab imports.
- *Action:* Selects “Lab imports (current section)”
- *Action:* Selects “Match review”

**[1:57]** First is Jim Carter. Date of birth and address agree with James Carter, and Jim is a nickname. Choose Same person to link it.
- *Action:* Selects “Carter, Jim · 1980-08-19”
- *Action:* Selects “Same person as James Carter (Alt+Shift+S)”

**[2:07]** Next is Rosa Martines. The date of birth and address match Rosa Martinez with a one-letter typo. Choose Same person to link it.
- *Action:* Selects “Same person as Rosa Martinez (Alt+Shift+S)”

## 6. Manually enter results for existing patients

**[2:17]** Next, enter two paper lab reports for existing patients. Open People and search for Leo Rivera first.
- *Action:* Selects “People”
- *Action:* Types “Rivera Leo 2021-02-02” in Search
- *Action:* Selects “Search people”
- *Action:* Selects “Rivera, Leo”

**[2:26]** Open Enter a result on Leo Rivera, fill in the venous lead result from the report, and save.
- *Action:* Selects “Enter a result”
- *Action:* Chooses “Lead” for Substance tested (analyte)
- *Action:* Chooses “Venous” for Sample type
- *Action:* Types “4.1” in Result value
- *Action:* Types “2026-10-28” in Sample collection date
- *Action:* Types “2026-10-30” in Laboratory report date
- *Action:* Chooses “ICP-MS” for Test method
- *Action:* Types “TRN-DAY-MAN-01” in Accession number
- *Action:* Chooses “Screening” for Reason for test
- *Action:* Types “Chesa” in Laboratory
- *Action:* Selects “Chesapeake Reference Laboratory 100 Research Drive, Columbia, CLIA 21D0712345”
- *Action:* Selects “Save result”
- *On screen:* Result saved · Lead 4.1 ug/dL → canonical 4.1000 ug/dL · population Child

**[2:49]** Now search for Mia Rivera and enter her paper lead result the same way.
- *Action:* Selects “People”
- *Action:* Types “Rivera Mia 2023-05-14” in Search
- *Action:* Selects “Search people”
- *Action:* Selects “Rivera, Mia”
- *Action:* Selects “Enter a result”
- *Action:* Chooses “Lead” for Substance tested (analyte)
- *Action:* Chooses “Venous” for Sample type
- *Action:* Types “2.4” in Result value
- *Action:* Types “2026-10-28” in Sample collection date
- *Action:* Types “2026-10-30” in Laboratory report date
- *Action:* Chooses “ICP-MS” for Test method
- *Action:* Types “TRN-DAY-MAN-02” in Accession number
- *Action:* Chooses “Screening” for Reason for test
- *Action:* Types “Chesa” in Laboratory
- *Action:* Selects “Chesapeake Reference Laboratory 100 Research Drive, Columbia, CLIA 21D0712345”
- *Action:* Selects “Save result”
- *On screen:* Result saved · Lead 2.4 ug/dL → canonical 2.4000 ug/dL · population Child

## 7. Add a new patient and enter new records

**[3:24]** Finally, a new patient has two paper reports. Search People first to confirm Tessa Holloway is not already in MERIT.
- *Action:* Selects “People”
- *Action:* Types “Holloway Tessa 2022-06-15” in Search
- *Action:* Selects “Search people”
- *On screen:* Not the person you are looking for? Check the spelling and date of birth, then: · Add a person

**[3:33]** Choose Add a person. The name and date of birth carry over from the search. Select sex and save the new record.
- *Action:* Selects “Add a person”
- *Action:* Chooses “Female” for Sex
- *Action:* Selects “Save”
- *On screen:* Tessa Holloway · Enter a result · Edit identity · Child Born Jun 15, 2022 (age 4) · Female · County: no current primary residence Contact only · Preferred language: Not recorded · Aliases, race and ethnicity, contact methods

**[3:41]** Choose Enter a result on the new person page to record her initial capillary screening result.
- *Action:* Selects “Enter a result”
- *Action:* Chooses “Lead” for Substance tested (analyte)
- *Action:* Chooses “Capillary” for Sample type
- *Action:* Types “4.8” in Result value
- *Action:* Types “2026-10-20” in Sample collection date
- *Action:* Types “2026-10-22” in Laboratory report date
- *Action:* Chooses “Point-of-care analyzer” for Test method
- *Action:* Types “TRN-DAY-NEW-01” in Accession number
- *Action:* Chooses “Screening” for Reason for test
- *Action:* Types “Chesa” in Laboratory
- *Action:* Selects “Chesapeake Reference Laboratory 100 Research Drive, Columbia, CLIA 21D0712345”
- *Action:* Selects “Save result”
- *On screen:* Result saved · Lead 4.8 ug/dL → canonical 4.8000 ug/dL · population Child

**[4:09]** Choose Enter another result to record her follow-up venous confirmation on the same patient record.
- *Action:* Selects “Enter another result”
- *Action:* Chooses “Lead” for Substance tested (analyte)
- *Action:* Chooses “Venous” for Sample type
- *Action:* Types “4.5” in Result value
- *Action:* Types “2026-10-28” in Sample collection date
- *Action:* Types “2026-10-30” in Laboratory report date
- *Action:* Chooses “ICP-MS” for Test method
- *Action:* Types “TRN-DAY-NEW-02” in Accession number
- *Action:* Chooses “Confirmation” for Reason for test
- *Action:* Types “Chesa” in Laboratory
- *Action:* Selects “Chesapeake Reference Laboratory 100 Research Drive, Columbia, CLIA 21D0712345”
- *Action:* Selects “Save result”
- *On screen:* Thresholds and case outcome · Attached to open case PB-2026-000029; classification changed from suspect to confirmed. · Level Type Value This result · Case Case 3.5 ug/dL At or above · Reportable Reportable 0 ug/dL At or above · Child: one venous result at or above the level, by a confirming method. · The open case is upgraded from Suspect to Confirmed.

## 8. Sign out

**[4:35]** At the end of the day, open the account menu in the header and choose Sign out to end the session cleanly.
- *Action:* Selects “Profile: Maryland daily intake (full CRUD) — Jordan Daily, switch profile or jur”
- *Action:* Selects “Sign out”

## Recap

**[4:42]** In this video you reset a forgotten password and signed in with an authenticator code, reviewed data with a read-only profile, then switched to a full access profile, imported an HL7 file and matched incoming records to existing patients, and entered manual results for existing patients, added a new patient with records, and signed out.

- Reset a forgotten password and signed in with an authenticator code
- Reviewed data with a read-only profile, then switched to a full access profile
- Imported an HL7 file and matched incoming records to existing patients
- Entered manual results for existing patients, added a new patient with records, and signed out

---

Generated 2026-11-26 from `training/workflows/intake-clerk/intake/daily-intake-workflow.workflow.ts` against MERIT 8410bca 2026-11-24.
