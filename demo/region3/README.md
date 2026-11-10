# MERIT Region 3 local demo

Open **https://localhost:7243/account/signin**. Web and Worker use the new
**MERITRegion3Demo** database on **localhost\PORTAL**. Enhanced navigation is enabled.
The local HTTPS developer certificate is currently untrusted; the browser may show its local certificate interstitial.

All four accounts use **`MeritDemo!2026`**. Enroll your authenticator on first login,
enter its current code, save the recovery codes, then select **Continue to MERIT**.
Browser validation enrolled these accounts temporarily; their MFA was reset afterwards so you can enroll your own app.

| Account | Scope |
| --- | --- |
| `region3demo` | EPA Region 3 and all descendants; all 24 operational/administrative permissions, full data access within that scope. |
| `marylanddemo` | Maryland and descendants; the same 24 permissions and full data access within Maryland. |
| `countydemo` | Baltimore County and descendants; the same 24 permissions and full data access within that county. |
| `platformdemo` | Platform administration: accounts, roles, and tenant creation. MERIT's platform role intentionally has no patient access. |

The three data accounts combine Administrator and Supervisor grants at their assigned
tenant, with descendant inclusion and `full` access. Their account-administration access is
also scoped geographically. Platform-only screens belong to `platformdemo`.

The tenant tree includes Maryland, Delaware, Pennsylvania, Virginia, West Virginia,
and the District of Columbia under EPA Region 3. Maryland contains the shipped synthetic
people, results, cases, investigations, organizations, and work queues. The other five
jurisdictions each have a representative county/district tenant, synthetic person,
and follow-up task. These are representative demo jurisdictions, not a complete county census.
No existing MERIT database was changed.

## Import files

All records are invented. Each file has 20 distinct patients and one blood lead observation per patient.
All import patients reside in Baltimore County (FIPS 24005), allowing comparison across the three data scopes.
The files are left **unimported** so you can try intake yourself.

| Requested file | Source | Current MERIT upload file | Intake profile |
| --- | --- | --- | --- |
| `source_a.hl7` | Potomac Synthetic Laboratory, CLIA 21D0900001 | `source_a.hl7` | Region 3 Demo A - Potomac Synthetic Laboratory HL7 |
| `source_b.hl7` | Allegheny Synthetic Laboratory, CLIA 21D0900002 | `source_b.hl7` | Region 3 Demo B - Allegheny Synthetic Laboratory HL7 |
| `source_c.csv` | Chesapeake Synthetic Laboratory, CLIA 21D0900003 | `source_c.csv` | Region 3 Demo C - Chesapeake Synthetic Laboratory |

**Native HL7 intake is enabled in this local build.** The two `.hl7` files are HL7 v2.5.1
ORU^R01 messages with MSH/PID/OBR/OBX/SPM segments and CR delimiters. Upload them directly
with their matching HL7 profiles. CSV companions and `hl7_to_csv.py` remain available for
comparison with delimited intake. Use a CSV profile for those companions.

Sign in as `marylanddemo`, open **Intake**, select the matching profile, and upload
`source_a.hl7`, then `source_b.hl7`, then `source_c.csv`. Wait for each batch to finish.
Region users should first choose Maryland as the acting tenant through their profile/context
controls; source profiles belong to Maryland's data root. County users can access those
Maryland source profiles while retaining county data restrictions.

Every accession is unique across sources: shared patients are follow-up results rather than
duplicate laboratory results. Demographics, date of birth, and address match exactly.

| Intersection | Shared patient IDs |
| --- | --- |
| A and B: exactly 3 | R3DEMO001, R3DEMO002, R3DEMO003 |
| C and A: exactly 3 | R3DEMO004, R3DEMO005, R3DEMO006 |
| C and B: exactly 4 | R3DEMO021, R3DEMO022, R3DEMO023, R3DEMO024 |

There are **50 unique patients across 60 observations**. `patient_manifest.json` lists
the identities in each file. Source C's two overlap groups are disjoint.
Matching still follows the configured MERIT rules; held records, if any, can be resolved on Held records/Match review.

## Restart and verification

If Web and Worker have stopped, run from the repository root:

```powershell
.\demo\region3\Start-Demo.ps1
```

The helper reads the existing local development SQL credentials and overrides the database
and navigation setting only for the processes it starts. Existing application settings stay unchanged.
Build the solution first if its Debug assemblies have been removed.
Do not start another copy while ports 7243 and 5280 are occupied.
Process IDs are recorded in `Web.pid` and `Worker.pid`; output is in their `.stdout.log`/`.stderr.log` files.

Validation: solution build succeeded; all 173 unit tests passed; Edge successfully completed
password/MFA sign-in and 20 main pages for each data account and two administration pages
for the platform account. Patient detail requests verified geographic allow/deny boundaries.
See `browser-validation.json` for the screen and boundary results.
The HL7-to-CSV conversion was checked against both supplied CSV companions, and the exact
20-row counts and overlap counts were asserted. The three imports have not been executed
against the running demo, so the demo remains ready for your own upload exercise.

`prepare.py` regenerates the synthetic files and `seed.sql`; deploying `seed.sql` requires
an administrative SQL connection and is guarded to run only on MERITRegion3Demo.
`verify.cjs` is a one-time setup smoke check: it enrolls MFA on the four demo accounts.
The setup-only `reset-validation-mfa.sql` restores first-login enrollment and revokes their sessions;
do not run it after you have enrolled your authenticator unless you intend to reset those accounts.
