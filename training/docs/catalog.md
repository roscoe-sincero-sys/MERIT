# Training catalog: MERIT's workflows by role

This analysis of MERIT's features, navigation and roles draws on the application and the
[operations](../../docs/guides/operations-guide.html) and [access](../../docs/guides/access-guide.html) guides. It also
judges which workflows suit a short video.

## How MERIT is organized

- **Header:** global search, the signed-in person and their current **profile** (switchable), **Sign-in methods** and
  **Sign out**.
- **Menu:** Home, Work queues, People, then Intake, Surveillance, Registry & reports and Administration. Each opens to
  the screens the person's role allows.
- **Data:** people are at the centre. A person page holds identity, relationships, places, jobs, results and cases. Lab
  results arrive by file (CSV, HL7) or by hand. Results at or above a configured level open or update a case, and cases
  generate tasks in work queues.
- **Access:** a tree of tenants (state, county, program). Roles are granted at a node and, optionally, below it. A user
  works through a *profile*, a chosen subset of their access.

## What suits a short video

A good candidate:

- is done often or is easy to get wrong;
- is complete in two to four minutes;
- can be reset by soft-deleting what it creates;
- needs nothing outside the browser.

These are weaker candidates, better covered by the written guides or a longer walkthrough:

- one-off configuration;
- anything that depends on the background worker's timing, such as HL7 intake (its video waits for the worker, as the
  e2e test does);
- screens whose purpose is reading a large report.

## Catalog

Status: **built** has a script in `workflows/`. **Planned** is proposed, in rough priority order within each role.

| Role | Video | Feature | Account | Suitability | Status |
|---|---|---|---|---|---|
| All users | Signing in, setting up an authenticator, saving recovery codes | signIn | a fresh synthetic account | High. Every user does it once. Codes are masked; the QR code and secret are never shown. | Planned |
| All users | **Profiles: the right access for the work in hand** (EPA Region 3 staff: every state read only, all of Maryland, Harford County only) | profiles | `r3staff` | High. Every user benefits; uses the Region 3 scenario. | **Built** |
| All users | Finding your way: menu and search | navigation | `cmboth` | High | Planned |
| All users | Changing your password; a forgotten password | signIn | `cmbalt` | Medium. The reset link is read from Mailpit off camera. | Planned |
| Intake clerk | **Entering a result from a paper report** | peopleAndResults | `intake` | High. Done daily; covers search first, the form, the lookup and the outcome. | **Built** (proof of concept) |
| Intake clerk | Uploading a lab file and reading the batch | intake | `intake` | High. Uses `samples/chesapeake_lab_sample.csv`. Needs the worker. | Planned |
| Intake clerk | Clearing held records | intake | `intake` | High | Planned |
| Intake clerk | Match review: is this the same person? | intake | `intake` | High. Uses `chesapeake_match_review_sample.csv`. | Planned |
| Intake clerk | Adding a person | peopleAndResults | `intake` | Medium | Planned |
| Intake clerk | Checking a reporter (NPI/CLIA) | registry | `intake` | Medium | Planned |
| Case manager | Working a case from the work queue | cases | `cmbalt` | High | Planned |
| Case manager | Recording a contact and follow-up | cases | `cmbalt` | High | Planned |
| Investigator | Opening a home investigation; samples and findings | investigations | `supervisor` | High. Uses `environmental_samples.csv`. | Planned |
| Occupational investigator | A workplace investigation | investigations | `occinv` | Medium | Planned |
| Supervisor | Weekly checks: escalated matches, reassignments | cases | `supervisor` | Medium | Planned |
| Supervisor | Clusters and the map | surveillance | `supervisor` | Medium | Planned |
| Supervisor | Requesting and submitting a CBLS or ABLES extract | extracts | `supervisor` | High. Quarterly and easy to get wrong. | Planned |
| Epidemiologist | Reports without names | surveillance | `epi` | Medium | Planned |
| Administrator | Adding a user and giving access | access | `admin` | High | Planned |
| Administrator | Unlocking, resetting MFA, requiring a new password | access | `admin` | High | Planned |
| Administrator | Reading sign-in activity | security | `admin` | Medium | Planned |
| Administrator | Importing the NPI/CLIA registry | registry | `admin` | Medium. Uses `registry_npi_clia_sample.csv`. | Planned |
| User administrator | Managing accounts in one county | access | `cityadmin` | Medium | Planned |
| Platform administrator | Adding a tenant | access | `platform` | Low. Rare. | Planned |
| Platform administrator | Investigating unknown user names | security | `platform` | Medium. *Show names* needs a fresh code, entered off camera. | Planned |

The shipped demo data has one state (Maryland) directly under the platform. The profiles video adds the access guide's
EPA Region 3 tree to the training database through [lib/scenarios.ts](../lib/scenarios.ts): the region, Delaware, the
District of Columbia, Maryland, Pennsylvania, Virginia and West Virginia under it, Harford County Health Department
under that Maryland, the `r3staff` account (Sky Region, Supervisor at the region and everything under it) and one
labelled task per place.

Each planned video needs its own `prepare()` reset. Workflows that create users, tenants or batches need a reset in
[lib/data.ts](../lib/data.ts) for that kind of record, following `resetTrainingResults`.
