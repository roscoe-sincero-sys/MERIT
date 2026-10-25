# Metals Exposure Registry and Investigation Tracking (MERIT) Specification

Oct 24, 2026 · @Roscoe

## Purpose and scope

The Metals Exposure Registry and Investigation Tracking (MERIT) system receives, stores and acts on laboratory results for lead and other metals for people of every age. Children, adults and pregnant people are first-class populations rather than variations of a child record.

It is built to be adopted by any U.S. state or local health jurisdiction. Anything that differs between jurisdictions, from reportable levels to the adult age cutoff, is a setting rather than code. Maryland examples appear only as examples.

**In scope**

- Lab results for any analyte in the analyte lookup table, starting with lead, mercury, cadmium and arsenic, in blood or urine.
- One person registry for children and adults. Population is derived from date of birth, collection date and pregnancy episodes.
- Residences, child care, schools and workplaces, each tracked over time.
- Case classification, case management, residential and occupational investigations, and prenatal follow-up.
- Federal extracts: quarterly files for CDC childhood blood lead surveillance (CBLS) and annual files for NIOSH adult surveillance (ABLES).

**Out of scope:** housing hazards unrelated to metals (mold, pests, radon), the lead rental registry, and billing or fee collection.

**Why a new model.** HHLPSS, built on California's RASSCLE II and the successor to STELLAR, assumes the subject is a child. That shows up in three places: guardian fields on the person, a single address, and an investigation that always targets a home. Adult and heavy-metal records get forced into that shape. One example is New York instructing labs to put a worker's employer in the guardian field.

**Design principles**

1. Population is derived from data, never set with a checkbox.
2. People link to people, and people link to organizations. The two kinds of link are never mixed.
3. Places are shared records. People attach to them by role and date range.
4. Every rule value is effective-dated data, not code. This covers thresholds, age cutoffs, units, lookback windows, environmental standards and federal extract layouts.
5. The as-reported lab record is never changed. Corrections live on the linked records, and a lab's own correction arrives as a new as-reported record that supersedes the old one.
6. Every create, update, merge, view and export of a person, result or case is audited.
7. Automation proposes and staff decide. Cross-links, clusters, merges, address changes and closures triggered by other events create review tasks; they never act on their own.

## Glossary

| Term | Meaning in this spec |
| --- | --- |
| Analyte | The substance measured: lead, mercury, cadmium, arsenic, and any later addition. |
| Specimen | What was tested: whole blood or urine. |
| Sample type | How a blood specimen was drawn: venous, capillary or unknown. Applies to blood only. |
| Test method | How the lab measured the result, such as ICP-MS, graphite furnace AA or a point-of-care analyzer. Decides whether a venous result can confirm a case. |
| Speciation | The chemical form reported, such as total vs. inorganic arsenic or total vs. methyl mercury. |
| Population | Child, adult or pregnant; unknown when DOB is missing. Derived per result from date of birth, collection date and pregnancy episodes. |
| Primary residence | The single residence used for jurisdiction and federal reporting on a given date. |
| Reportable level | The value at or above which a lab must report. Some analytes are reportable at any value. |
| Action level | A value that triggers a workflow step, such as opening a case or scheduling a retest. Action levels are tiered, so several can apply to one analyte at once. |
| Case | One person's elevated exposure to one analyte, from opening to closure. |
| Investigation | A site visit or inquiry about one target: a home, a child-care site, a workplace or a non-occupational source. One investigation can serve several cases. |
| Take-home exposure | Metal carried from a workplace into a home on clothing, skin or vehicles. |
| Contact-only person | A person record with no lab results, such as a guardian or a household member. |
| ELR | Electronic laboratory reporting, normally HL7 v2.5.1 ORU^R01 messages. |
| eICR | Electronic initial case report sent from a provider's electronic health record through electronic case reporting (eCR). |
| LOINC | Standard codes identifying a lab test, used to map incoming results to an analyte and specimen. |
| NAICS | North American Industry Classification System; codes an employer's industry. |
| COC | Census occupation code; codes a worker's job. |
| CBLS | CDC childhood blood lead surveillance quarterly extract. |
| ABLES | NIOSH Adult Blood Lead Epidemiology and Surveillance annual data submission. |
| OSHA or state plan | Federal OSHA, or the state's own occupational safety agency in states that run an approved plan (for example, MOSH in Maryland). |
| Intake profile | A sender's saved description of its own file layout, used to parse that sender's files. |
| Held record | An incoming row that could not load on its own; it waits in a queue with named reasons. |
| Tenant | An agency or program that uses the system. Tenants form a tree under a root tenant, normally a state. |
| Cluster | A group of cases flagged for staff review because they share a place, employer, provider or product. |

## Data model overview

The core model has 18 entities in four groups. The person record carries identity only; guardians, addresses and employers are separate linked records with their own dates.

&#91;embedded content: data model · 18 core entities in 4 groups\]

Person location ties people to places, and employment ties people to organizations at a worksite. Each lab result belongs to one person and one analyte specimen. A case groups one person's results for one analyte. Each investigation targets exactly one place, worksite or exposure source, and links to every case it serves.

Supporting tables are not drawn: contact methods, race values and external identifiers on the person; organization sites and location owners; the investigation-case link; environmental standards; intake batches, intake profiles and staging rows; consumer products, clusters and custom fields; and the tenant and security tables described under Architecture.

## Entity definitions

Every table carries `id` (UUID), `created_at`, `created_by`, `updated_at`, `updated_by` and `row_version` for optimistic locking. Tenant-owned tables also carry `root_tenant_id` and `owning_tenant_id` (see Architecture). These are not repeated below. "Req" means required to save; "Ext" means required before the record can go into a federal extract.

Enumerations come in two kinds. Values that drive program logic (population, statuses, operators, comparators, hold reasons) are system-defined. Descriptive values (roles, sources, closure reasons, sample types, source types) are lookup tables that administrators maintain, each mapped to the extract codes it feeds.

### Person

The person record holds identity only. It has no address, guardian or employer fields.

| Field | Type | Req | Rules |
| --- | --- | --- | --- |
| last\_name | text(60) | Y | Trimmed; stored as reported and as a normalized match key. |
| first\_name | text(60) | Ext | Labs sometimes send "BABY BOY"; flag these for review rather than reject. |
| middle\_name | text(60) |  |  |
| dob | date | Ext | Not in the future, not before 1900. Missing DOB makes population unknown and holds the person out of population rules. |
| dob\_verified | boolean |  | True once staff confirm against a second source. |
| sex | enum | Ext | Female, male, other, unknown; mapped to each extract's codes. |
| race | multi-enum |  | Child table; any number of values from the race lookup. The lookup includes other, refused and unknown, so all eight ABLES race flags (Race1–Race8) can be derived. |
| ethnicity | enum |  | Hispanic, not Hispanic, unknown. |
| preferred\_language | enum |  | Drives letter templates. |
| phone, email | text |  | Validated format; multiple values in a contact-method child table. |
| identifiers | child table |  | External IDs as reported, such as a medical record number with its assigning facility or a Medicaid ID, each with type, source and date. Used by matching; shown only with person.view\_identifiers. |
| deceased\_date | date |  | On or after dob. Creates a task listing open cases to close with reason "deceased"; staff confirm. |
| merged\_into\_id | FK person |  | Set when the record is merged; merged records are read-only. |

### Person alias

Names as they arrived on lab reports (nicknames, maiden names, misspellings, placeholders such as "BABY BOY"), each with source and date. Used by matching; never shown as the primary name.

| Field | Type | Req | Rules |
| --- | --- | --- | --- |
| person\_id | FK person | Y |  |
| last\_name, first\_name, middle\_name | text(60) | Y (last name) | As reported, plus normalized match keys. |
| alias\_type | enum | Y | As reported, nickname, former name, placeholder, misspelling. |
| source | enum | Y | Lab report, eICR, interview, staff. |
| source\_result\_id | FK result |  | The result that carried this name, when there is one. |
| first\_seen, last\_seen | date | Y | last\_seen updates each time the same name arrives again. |

### Pregnancy episode

| Field | Type | Req | Rules |
| --- | --- | --- | --- |
| person\_id | FK | Y | Warn, rather than block, when the person's sex is male; staff confirm. |
| start\_date | date | Y | Estimated conception or first known pregnant date. |
| expected\_end\_date | date |  |  |
| actual\_end\_date | date |  | On or after start\_date. |
| outcome | enum |  | Live birth, loss, unknown. A live birth prompts linking the newborn. |
| source | enum | Y | Lab report, eICR, provider, interview. |

A result collected during an open episode takes the pregnant population. An episode is open from start\_date through actual\_end\_date; with no actual end, through expected\_end\_date; with neither, for the configured maximum length (default 42 weeks). A work queue lists open episodes past their expected end.

### Person relationship

Links a person to another person. This replaces guardian fields. The role says what the related person is to the person: role parent means the related person is this person's parent, and the related person's page shows the inverse label (child of).

| Field | Type | Req | Rules |
| --- | --- | --- | --- |
| person\_id | FK person | Y |  |
| related\_person\_id | FK person | Y | Not the same as person\_id. Related person may be contact-only. |
| role | enum | Y | Parent, legal guardian, foster parent, other caregiver, spouse or partner, sibling, household member, emergency contact. Each role has an inverse label. |
| is\_primary\_contact | boolean |  | At most one current primary contact per person. |
| has\_legal\_custody | boolean |  |  |
| start\_date, end\_date | date |  | end\_date ≥ start\_date; null end = current. |
| source | enum | Y | Lab report, eICR, interview, case manager, other agency. |
| verified\_date | date |  | Set when staff confirm a relationship that arrived on a report. |

### Organization

Employers, child-care providers, schools, labs, provider practices and health departments. Individual practitioners are Provider records, not organizations.

| Field | Type | Req | Rules |
| --- | --- | --- | --- |
| name | text(120) | Y | Duplicate warning on normalized name plus any shared site address. |
| org\_type | multi-enum | Y | One organization can be several types, such as a school that is also an employer. |
| naics\_code | text(6) | Ext for employers | Validated against the NAICS lookup for the stored code version (NAICS 2022 for ABLES today); 999 = unknown. |
| npi | text(10) |  | NPI-2 for practices and labs; check digit validated (see Provider and laboratory reference data). |
| clia\_number | text(10) | For labs | Format checked: two digits, the letter D, seven digits. |
| parent\_org\_id | FK org |  | Corporate parent of a site-level employer. |
| contact\_name, phone, email | text |  |  |
| active | boolean | Y |  |

Sites are LOCATION records linked through an organization-site table (org\_id, location\_id, site\_name, start/end).

### Location

One record per physical address and unit, shared by everyone linked to it. Addresses outside the US and city-only locations, for people without a fixed address, are allowed.

| Field | Type | Req | Rules |
| --- | --- | --- | --- |
| country | text(2) | Y | ISO 3166 code; defaults to US. ABLES codes Canada and Mexico separately. |
| address\_line1 | text | Y | Optional only for a city-only location. |
| unit | text |  | Part of identity: two apartments are two locations. The row with no unit is the building record. |
| city | text | Y |  |
| state, zip5 | text | Y for US | State is a 2-letter code; zip5 is 5 digits. |
| region, postal\_code | text |  | For addresses outside the US. |
| county\_fips | text(3) | Ext | From geocoding; 999 = unknown. |
| std\_status | enum | Y | Standardized, standardized with changes, failed, not attempted. |
| latitude, longitude | decimal |  | Set by the geocoder, or by staff placing a pin in the field. |
| geocode\_quality | enum |  | Rooftop, parcel, street, ZIP centroid, manual pin. |
| census\_tract | text(11) |  | Stored with the census vintage it came from (2020 today). |
| building\_id | FK location |  | The building record: same address, no unit. Groups units for cluster detection and common-area investigations. |
| year\_built | int |  | 1700 to current year; source recorded. Built before 1978 shows as a risk flag. |
| housing\_type | enum |  | Single-family, multi-unit, mobile home, institutional, commercial. |
| tenure | enum |  | Owner-occupied, rental, unknown. |
| owners | child table |  | Owner person or organization (exactly one per row), with start and end dates and source, so notices and orders go to the owner at the time. |

### Person location

Where a person lives or spends time. Rules in the next section.

| Field | Type | Req | Rules |
| --- | --- | --- | --- |
| person\_id, location\_id | FK | Y |  |
| role | enum | Y | Residence, child care, school, relative or sitter home, other regular site. |
| is\_primary | boolean | Y | Residence role only. |
| primary\_reason | enum | When overlapping | Custody order, most overnights, guardian designation, lab-reported address, staff determination. |
| schedule\_type | enum |  | Full time, weekdays, weekends, alternating weeks, part time, seasonal. |
| hours\_per\_week | decimal |  | 0 to 168. Compared with the configured minimum when deciding which places get an investigation. |
| schedule\_note | text |  |  |
| start\_date, end\_date | date |  | Null end = current. |
| source | enum | Y | Lab report, eICR, interview, case manager, other agency. |
| verified\_date | date |  |  |

### Employment

A person's job at an organization. A person can hold several at once.

| Field | Type | Req | Rules |
| --- | --- | --- | --- |
| person\_id | FK | Y |  |
| org\_id | FK org | Y | Self-employed workers get an organization of type self-employed. |
| worksite\_location\_id | FK location |  | Must be one of the organization's sites, or a job site such as a construction address. |
| job\_title | text |  | As stated. |
| industry\_text | text |  | Industry as stated; kept for coding. |
| occupation\_code | text(4) | Ext if occupational | Census occupation code, stored with its code version. ABLES currently expects Census 2002 codes, with 990 = unknown. |
| naics\_override | text(6) |  | When this job's industry differs from the organization's. |
| metal\_tasks | text |  | Description of tasks involving metals. |
| analytes\_exposed | multi FK analyte |  |  |
| ppe\_notes | text |  | Respirator, change rooms, laundering. |
| start\_date, end\_date | date |  | Null end = current. |
| source | enum | Y | Lab report, eICR, interview, employer, case manager. |
| verified\_date | date |  | Set when staff confirm a job that arrived on a report. |

### Exposure source

Non-occupational sources, for any population.

| Field | Type | Req | Rules |
| --- | --- | --- | --- |
| person\_id | FK | Y |  |
| case\_id | FK |  |  |
| source\_type | enum | Y | Renovation, hobby, casting, firearms or range, consumer product, spice or food, traditional remedy, cosmetic, cookware, pica, retained bullet fragment, travel, water, other. Each type maps to an ABLES Process code. |
| description | text |  | Product name, brand, country of origin. Sent as the ABLES Process narrative, which NIOSH prefers to codes. |
| product\_id | FK product |  | Links to the product registry when the source is a product. |
| location\_id | FK |  | Where the exposure happens, if somewhere other than the residence. |
| start\_date, end\_date | date |  |  |
| confirmed | boolean |  | True when a sample confirms it. |

### Analyte, analyte specimen and threshold

Defined in the section on analytes and thresholds.

### Lab result

| Field | Type | Req | Rules |
| --- | --- | --- | --- |
| person\_id | FK | Y | Set by matching; null while in the unmatched queue. |
| analyte\_specimen\_id | FK | Y | From LOINC mapping or the analyte and specimen dropdowns. |
| collected\_date | date | Y | ≥ dob; ≤ reported\_date when present, and ≤ received\_at. |
| analyzed\_date | date |  | Between collected and reported. |
| reported\_date | date |  | When the lab released the result. |
| received\_at | timestamp | Y | When the agency received it; taken from the intake batch. |
| result\_status | enum | Y | Preliminary, final, corrected or cancelled, as the lab sent it. |
| supersedes\_result\_id | FK result |  | Set when a corrected or cancelled result replaces an earlier one with the same accession. |
| value\_reported | decimal | Y\* | \*Or value\_text when the lab sends a non-numeric result. |
| operator | enum |  | =, <, >. A "<" result is stored as below detection with the limit as value. |
| unit\_reported | text | Y | Must be in the analyte specimen's accepted units. |
| value\_canonical, unit\_canonical | decimal, text | Y | Computed at intake; read-only. |
| sample\_type | enum | Blood only | Venous, capillary, unknown. Must be null for urine. Taken from the LOINC code when the code implies it. |
| test\_method | enum |  | ICP-MS, graphite furnace AA, anodic stripping voltammetry, point-of-care analyzer, other, unknown. Used by classification. |
| device\_model, device\_serial, kit\_lot | text |  | When known; recall queries use them. |
| speciation | enum | When allowed | From the analyte specimen's speciation options. |
| creatinine\_value | decimal | When required | Needed to compute a creatinine-adjusted urine value. |
| value\_creatinine\_adj | decimal |  | Computed. |
| reason\_for\_test, payer\_type | enum |  | As sent. Reason: screening, confirmation, follow-up, occupational monitoring. Payer: Medicaid, private, uninsured, unknown. |
| lab\_org\_id | FK org | Y |  |
| ordering\_provider\_id | FK provider |  | Linked by NPI or matching; the reported name stays in the snapshot. |
| ordering\_facility\_id | FK org |  |  |
| accession\_number | text |  | Part of duplicate detection. |
| loinc\_reported | text |  | As sent. |
| reported\_snapshot | JSON | Y | Every patient, guardian, employer and address field exactly as received. Never edited. |
| reported\_location\_id | FK location |  | The geocoded as-reported address. |
| age\_at\_collection | interval |  | Computed. |
| population | enum |  | Computed: child, adult, pregnant, unknown. |
| status | enum | Y | Valid, duplicate, superseded, entered in error, rejected. |
| intake\_batch\_id | FK | Y | Points to the raw message or file. |

**Duplicates and corrections.** Same person, analyte specimen, collected date, lab and accession number (or same value when accession is missing) is a duplicate. A record that arrives with a corrected or cancelled status is not a duplicate: it supersedes the earlier result, which is marked superseded, and classification reruns. Preliminary results can trigger action-level tasks but never count toward classification or federal extracts; the final result supersedes them.

### Case

| Field | Type | Req | Rules |
| --- | --- | --- | --- |
| person\_id | FK | Y |  |
| analyte\_id | FK | Y | One open case per person per analyte. |
| population | enum | Y | Set from the qualifying result; changes only by staff action, with a reason. |
| classification | enum | Y | Confirmed, suspect, not a case (after review). |
| status | enum | Y | Open, monitoring, closed. |
| qualifying\_result\_id | FK result | Y |  |
| highest\_result\_id | FK result |  | Updated as results arrive. |
| exposure\_category | enum |  | Residential, occupational, both, other non-occupational, unknown. Maps to ABLES WorkRel. |
| assigned\_to | FK user |  |  |
| opened\_date, closed\_date | date |  |  |
| closure\_reason | enum | On close | Below level on retest, lost to follow-up, moved out of state, declined, deceased, duplicate, other. |

Case activities (a child table) hold contact attempts, education, referrals, medical management notes and letters, each with type, date, worker, outcome and note. A case-cluster table links cases to clusters.

### Investigation

| Field | Type | Req | Rules |
| --- | --- | --- | --- |
| inv\_type | enum | Y | Residential, child-care or school, workplace, non-occupational source. |
| location\_id | FK location | One of | Residential and child-care or school types; also a source investigation at a place, such as a firing range. |
| org\_id, worksite\_location\_id | FK | One of | Workplace type: the employer and the worksite. |
| exposure\_source\_id | FK | One of | A source investigation with no fixed place, such as a consumer product. |
| status | enum | Y | Requested, scheduled, in progress, findings issued, remediation, cleared, closed. |
| assigned\_to | FK user |  |  |
| requested, scheduled, completed dates | date |  | In order. |
| findings | text |  |  |
| regulatory\_action | enum |  | None, notice, order, referral to OSHA or state plan, referral to another agency. |
| referral\_date, outcome | date, text |  |  |

Exactly one target is set, matching the type. An investigation-case link table ties each investigation to every case it serves, and to the employments that brought a workplace into scope; an investigation started from a location or cluster can have no case. Before creating one, the system offers any open investigation of the same target, so siblings in one home share one investigation.

Environmental samples (a child table) hold sample type (XRF paint, dust wipe, soil, water, product, air, surface wipe), room or component, value, unit and the standard compared against. Standards come from an effective-dated environmental standards table (sample type, surface, value, unit, authority, dates), and exceeds\_standard is computed, not typed. EPA's 2024 dust rule shows why: it replaced fixed dust-lead hazard standards with any reportable level and set post-abatement action levels of 5, 40 and 100 µg/ft² for floors, window sills and troughs ([EPA](https://www.epa.gov/lead/hazard-standards-and-clearance-levels-lead-paint-dust-and-soil-tsca-sections-402-and-403)). Instrument readings, such as XRF exports, load through an intake profile instead of being retyped.

## Address and primary residence rules

Each person has exactly one primary residence on any date they have a residence. Every other place they live or spend time is still recorded, and is investigated when it meets the rules below.

### Rules

1. **One primary at a time.** No two person-location rows with role residence and is\_primary true may overlap in date range for the same person. Enforce in the stored procedure that saves person locations, inside a transaction that locks the person's rows, not only in the UI.
2. **No gaps once known.** If a person has any residence covering a date, one of them must be primary for that date. Saving a second concurrent residence forces the user to pick the primary.
3. **Reason required on overlap.** When two residences overlap, primary\_reason is required. Typical values: custody order, most overnights, guardian designation.
4. **Default when unknown.** With no other information, the most recent lab-reported address becomes primary with reason "lab-reported address" and verified\_date empty. A work queue lists unverified primaries on open cases.
5. **New addresses from labs go to review.** When a new result arrives with an address different from the current primary, the system creates an address review task. It does not move the primary automatically. This behavior is a configuration setting.
6. **Two counties, two jobs.** The reporting county is the county of the primary residence on the collection date; it drives federal extracts and surveillance counts. The assignment county is the county of the current primary residence; it drives case assignment and local access. If no residence is known, both use the result's as-reported address.
7. **Snapshots never change.** Correcting a person's address history never edits the result's reported\_snapshot or reported\_location\_id.
8. **Workplaces live on employment.** A work address is the worksite on an employment row, not a person-location row. This keeps residence history and job history separate and avoids double entry.
9. **Child care and school link to an organization.** The person-location row points to the organization's site, so several children at one provider share one location.
10. **Adults.** Multiple residences are allowed (seasonal homes, shelters) but the UI warns, rather than blocks, when an adult has two concurrent residences.
11. **Investigation scope.** Investigations draw on all locations active within a configurable lookback window before the qualifying collection date, not only the primary. Residences and child-care sites always get an investigation request. Other places get one when their hours per week meet the configured minimum, and a review task otherwise.
12. **Changing the primary splits rows.** The old primary row ends the day before the new start date. If the person still lives there, a new non-primary row continues from that date with the same schedule, so history stays exact.
13. **No fixed address.** A person without a fixed address gets a residence on a shelter's location or a city-only location, so jurisdiction still resolves.

### Example: shared custody

| Location | Role | Schedule | Primary | Reason |
| --- | --- | --- | --- | --- |
| Mother's home | Residence | Weekdays | Yes | Custody order |
| Father's home | Residence | Weekends | No |  |
| ABC Child Care | Child care | Weekdays, 40 h/week | n/a |  |
| Grandmother's home | Relative or sitter home | Two afternoons a week | n/a |  |

The county of the mother's home is used for reporting and assignment. Residential investigation requests are generated for both homes and the child-care site, in that order. The grandmother's home gets a request if two afternoons a week meets the configured minimum hours; otherwise it gets a review task.

## Analytes, thresholds and result normalization

Analytes are rows in three lookup tables, so adding a metal or changing a level is an administrator task, not a software release.

### Analyte

| Field | Type | Rules |
| --- | --- | --- |
| code | text(8) | Unique: PB, HG, CD, AS. |
| display\_name | text | Shown in drop-downs: Lead, Mercury, Cadmium, Arsenic. |
| active | boolean | Inactive analytes stay on old records but leave the drop-down. |
| sort\_order | int | Drop-down order. |
| extract\_targets | multi-enum | CBLS, ABLES, state report, none. |

### Analyte specimen

One row per analyte and specimen pair. This table drives which fields the result form shows.

| Field | Type | Rules |
| --- | --- | --- |
| analyte\_id | FK |  |
| specimen | enum | Whole blood, urine. |
| canonical\_unit | text | Unit stored for comparison, such as µg/dL for blood lead. |
| accepted\_units | child table | Unit text as labs send it, plus multiplier to canonical. A unit not listed sends the result to the error queue. |
| loinc\_codes | child table | Codes that map to this pair, each with any sample type or speciation the code implies. For blood lead, [LOINC 5671-3](https://loinc.org/5671-3) is discouraged in favor of 77307-7 (venous) and 10368-9 (capillary). Unknown codes go to the error queue. |
| uses\_sample\_type | boolean | True for blood lead. Shows the venous/capillary field. |
| requires\_creatinine | boolean | Shows the creatinine field and computes an adjusted value. |
| speciation\_options | multi-enum | For example, arsenic in urine: total, inorganic, organic. Empty hides the field. |

### Threshold

| Field | Type | Rules |
| --- | --- | --- |
| analyte\_specimen\_id | FK |  |
| population | enum | Child, adult, pregnant, all. |
| age\_min, age\_max | interval | Optional bounds within a population, for laws that set levels by age band. |
| speciation | enum | Optional; a level can apply only to inorganic arsenic, for instance. |
| sample\_type | enum | Optional; a level can apply only to venous or only to capillary results. |
| threshold\_type | enum | Reportable, case, action. |
| action\_code | FK | The workflow step this level triggers, such as "retest due" or "open investigation". |
| due\_interval | interval | For action rows: when the triggered step is due, such as 3 months. |
| value, unit | decimal, text | Compared in canonical units. |
| comparator | enum | ≥ or >. |
| effective\_from, effective\_to | date | No overlaps for the same analyte specimen, population, age band, speciation, sample type, type and action code, so tiered action levels run side by side. |
| authority | text | Regulation or guidance it comes from. |
| active | boolean | Reference rows ship inactive until an adopter turns them on. |

The current CDC blood lead reference value for children, 3.5 µg/dL, is the first child lead row. All other levels, including each state's reportable levels for mercury, cadmium and arsenic, are loaded from the adopting jurisdiction's regulations rather than written into this spec. Federal reference rows ship inactive with their authority: the 2023 national case definition (3.5 µg/dL venous, all ages) and NIOSH's adult elevated level (5 µg/dL).

Action levels are tiered. CDC's [schedule for a confirmatory venous test](https://www.cdc.gov/lead-prevention/hcp/clinical-guidance/index.html) after an elevated capillary result is one example: within 3 months at 3.5–9 µg/dL, 1 month at 10–19, 2 weeks at 20–44 and 48 hours at 45 or more. Each tier is its own action row with its own due interval.

### Intake normalization

Steps 1 to 5 run on every staging row before matching. Steps 6 and 7 need the matched person's DOB and pregnancy episodes, so they run after matching (see Intake pipeline).

1. Map LOINC (or the form's analyte and specimen) to an analyte specimen row, taking any sample type or speciation the code implies.
2. Parse the operator and numeric value. Keep the raw text.
3. Convert to the canonical unit using the accepted-unit multiplier.
4. For blood, store sample type; for urine, store null.
5. When creatinine is required and present, compute the adjusted value.
6. Derive age at collection and population from the matched person, falling back to the reported DOB.
7. Compare against effective thresholds for the collection date and run case rules.

**Below-detection results.** A "<" result is below a threshold only when its limit is at or below that threshold. When the limit is above it, as with a "<5" result against a 3.5 level, the result is indeterminate: it opens no case, joins a "detection limit above level" queue for follow-up with the reporter, and counts as incomplete in completeness reports. A ">" result is at or above a threshold when its limit is.

**Arsenic note.** Total urine arsenic is often raised by harmless organic arsenic from seafood. A total arsenic result above the action level should create a "speciation recommended" task rather than a confirmed case, unless the jurisdiction configures otherwise.

## Case classification

Classification runs per result, per analyte, using the population derived for that result and the thresholds in force on its collection date.

### Population derivation

1. No DOB → population unknown; the result goes to a review queue and opens no case automatically. If the value is at or above the lowest case level for any population, the review task is marked urgent.
2. Open pregnancy episode on the collection date → pregnant.
3. Age at collection below the configured adult cutoff → child.
4. Otherwise → adult.

The adult cutoff is a setting because the sources disagree. ABLES and the national case definition treat people 16 and older as adults, while some state reporting laws define children as 0–18 (Maryland's, for example). Federal extracts use their own age rules regardless of this setting, computed from DOB and collection date.

### Child lead

Follows CDC's [Lead in Blood 2023 case definition](https://ndc.services.cdc.gov/case-definitions/lead-in-blood-2023) (CSTE 22-EH-01), with the level taken from the threshold table:

- **Confirmed:** one venous result at or above the level, measured by a confirming method, or two capillary results at or above it collected within 12 weeks of each other.
- **Suspect:** one capillary result at or above the level, or two capillary results at or above it collected more than 12 weeks apart.
- Unknown sample type counts as capillary for children, as the definition specifies. A venous result from a method that does not confirm also counts as capillary.
- Confirming methods are a setting. The default follows the definition, where graphite furnace AA and ICP-MS confirm; a result with no reported method confirms unless the jurisdiction configures otherwise.
- A suspect case opens with a "confirmatory venous test due" task, with the due date set by the action tier the result falls in.
- For annual counts and CBLS, a confirmed case counts as new once per calendar year unless it was confirmed in the previous year. When the two capillary results straddle a year end, it counts in the year of the first.

### Adult lead

- A result at or above the adult case level opens a case. Unknown sample type counts as venous for adults, as the national definition specifies. A capillary result opens a suspect case with a confirmatory venous test task.
- ABLES Status follows NIOSH's own rules, not the case level. The extract takes the person's highest result in the calendar year and compares it with 10 µg/dL: at or above, the person is a new or existing case depending on the prior year's registry record; below, a new or existing adult (codes 1 to 5, or 9 when unknown).
- Exposure category stays unknown until the worker interview; the extract codes it as unknown until then.

### Other metals

- A result at or above the case level for its analyte, specimen, speciation and population opens a case for that analyte.
- Mercury, cadmium and arsenic cases are separate from any lead case for the same person, but appear together on the person page.

### General rules

- One open case per person per analyte. A new qualifying result attaches to the open case and may upgrade suspect to confirmed.
- Re-run classification when a result is corrected or superseded, a DOB changes, sample type or method changes, a merge happens, or a pregnancy episode is added, changed or removed. Show the user what changed.
- A threshold change never reclassifies closed cases, and open cases keep their classification; new results are evaluated under the new rows. A one-time report lists people whose recent results would qualify under the new level, for outreach. Reports for past periods can be rerun under either rule set.
- Staff can override a classification with a required reason; the computed value is kept beside it.

## Investigation workflows

The case's population picks a workflow template; templates are configurable lists of steps, each with an owner role, a due-date rule and completion criteria. Three default templates ship: child residential, adult occupational and pregnancy. They differ in who is contacted and what gets investigated. Closure criteria, such as a number of results below the case level a minimum time apart with all investigations cleared, are template settings.

### Child: residential workflow

1. **Open and assign.** Assign a case manager by assignment county (the current primary residence). Notify the local health department if configured.
2. **Confirm.** Track the confirmatory or follow-up test due date from the action tier.
3. **Reach the guardian.** Log each contact attempt against the person's primary contact relationship, or, if none is recorded yet, against the guardian created from the lab report at intake. Send a letter after the configured number of failed attempts.
4. **Exposure interview.** Record all residences and their schedules, child care, school, relative homes, household members' jobs, consumer products, remedies, pica, travel and any residence outside the US.
5. **Investigate each site.** Create one investigation per qualifying place (address rule 11), primary residence first, reusing any open investigation of the same place.
6. **Sample and act.** Record environmental samples, hazards found, notices or orders to the owner, remediation and clearance.
7. **Medical follow-up.** Record referrals (early intervention, nutrition, provider) and follow-up tests.
8. **Close.** Close when closure criteria are met, with a required reason.

### Adult: occupational workflow

1. **Open and assign.** Assign an occupational investigator.
2. **Reach the worker.** Contact the person directly. No guardian step.
3. **Worker interview.** Record every current job with tasks, PPE and hours; non-occupational sources; pregnancy; and children in the household.
4. **Code the work.** Link or create each employer organization and worksite. Assign NAICS and occupation codes from the job title and industry text; an optional autocoding service can suggest codes for staff to accept.
5. **Employer follow-up.** Contact the employer or refer to the configured occupational safety agency, then track the referral outcome. Several workers at one worksite share one workplace investigation.
6. **Retest tracking.** Track repeat results against configured medical-removal and return-to-work levels.
7. **Close.** Close with a required reason.

An adult with a non-occupational source, such as renovation or a firing range, uses a non-occupational source investigation; if the source is in the home, a residential investigation is opened instead.

### Pregnancy workflow

CDC's [guidelines for pregnant and lactating women](https://stacks.cdc.gov/view/cdc/147837) set their own follow-up levels, so this template has its own rows in the threshold table.

1. **Open and assign.** Assign a case manager by assignment county, and notify the prenatal care provider.
2. **Reach the person.** Contact the pregnant person directly.
3. **Exposure interview.** Record jobs and take-home exposure, residences, renovation, pica, consumer products, remedies, cookware and food from abroad.
4. **Investigate.** Open residential, workplace or source investigations as the interview points, under the same rules as the other templates.
5. **Retest tracking.** Track follow-up results during pregnancy against the pregnancy action rows.
6. **Delivery and newborn.** Record the pregnancy outcome. On a live birth, link the newborn as a contact-only person with a parent relationship, and create a newborn testing task.
7. **Close.** Close with a required reason. If the case is still open after delivery, staff choose whether it continues under the adult template.

### Cross-links between children and adults

These are the links a separate child system and adult system cannot make. Each creates a task for a person to review, never an automatic case.

| Trigger | Action |
| --- | --- |
| Adult occupational case; children or a pregnant person share the adult's primary residence or a relationship | Task: recommend testing them; link the records. |
| Child case; a household member's employment lists metal tasks | Task: occupational inquiry for that adult, even if they are a contact-only person. |
| Child case; other children share the primary residence or a sibling relationship | Task: recommend testing the other children. |
| Two or more cases at one employer worksite within the configured window | Cluster alert to the occupational team. |
| Two or more child cases at one location, building or child-care site within the window | Cluster alert to the residential team. |
| Case for a pregnant person | Task: prenatal provider notification and newborn testing follow-up. |

## User interface

The person page has the same sections for everyone; the population only changes their order and which empty states appear. Nothing about a person is entered into a field meant for someone else.

### Person page

| Section | Contents | Child order | Adult order | Pregnant order |
| --- | --- | --- | --- | --- |
| Header | Name, DOB, age, population badge, open-case chips per analyte, current primary residence county; aliases one click away | 1 | 1 | 1 |
| Relationships | Guardians, parents, household members; "Add relationship" picks or creates a person | 2 | 6 | 7 |
| Places | Residences, child care, school, other sites; role chip, primary star, schedule, dates | 3 | 3 | 4 |
| Results | Table filtered by an analyte dropdown; trend chart per analyte with threshold lines, venous and capillary marked differently | 4 | 4 | 5 |
| Cases and investigations | One card per case with its workflow steps | 5 | 5 | 6 |
| Employment | Current and past jobs, worksite, codes, metal tasks | 6 | 2 | 3 |
| Exposure sources | Non-occupational sources | 7 | 7 | 8 |
| Pregnancy | Episodes with dates, outcome and linked newborn | 8 | 8 | 2 |
| Timeline | Results, address changes, job changes and case events on one time axis | 9 | 9 | 9 |

Empty sections show a short empty state, such as "No guardians recorded" or "No jobs recorded", instead of disappearing. A 17-year-old with a job sees the Employment section filled in with no special handling. The Pregnancy section is hidden for a person recorded as male with no episodes.

### Places behavior

- "Set as primary" asks for a reason and the start date, splits the rows as address rule 12 describes, and shows the county change.
- Adding an overlapping residence opens the primary choice inline; it cannot be saved without one.
- Address entry standardizes as the user types and offers matching existing locations before creating a new one.
- A child-care or school entry is picked from organizations first; the site address fills in.

### Result entry form

Fields cascade from the analyte lookup so users only see what applies:

1. Analyte drop-down from active analytes, in sort order.
2. Specimen drop-down filtered to that analyte's specimens.
3. Unit drop-down filtered to that pair's accepted units, canonical unit first.
4. Sample type shown only when uses\_sample\_type is true.
5. Speciation shown only when options exist.
6. Creatinine shown only when required.
7. Collection, analyzed and reported dates, value with operator, test method, lab, ordering provider, accession number.

On save the form shows the canonical value and whether any threshold was crossed.

### Work queues

- Unmatched results, with suggested person matches and scores.
- Unknown LOINC codes or units.
- Batches paused because a sender's layout changed.
- Address review: new lab addresses that differ from the primary.
- Unverified primary residences on open cases.
- Results whose detection limit is above the case level, by reporter.
- Pregnancy episodes past their expected end.
- Cases with overdue steps, by worker.
- Cross-link and cluster tasks.
- Results missing extract-required fields before the next CBLS or ABLES submission.

### General

- Global search by name, DOB, address, organization, accession number or case number.
- Lists export to CSV and Excel within the user's access scope, for users with the export permission; each export is audited with its row count.
- WCAG 2.1 AA, keyboard-only operation, and layouts that work at tablet width for field staff.

## Intake, matching, extracts and security

### Intake formats

Every format is parsed directly into one internal staging record, so no agency has to convert files to HL7 before loading them. Today some agencies do exactly that; for example, MDE converts CSV files to HL7 v2.3.1 for HHLPSS.

| Format | Handling |
| --- | --- |
| HL7 v2.5.1 ORU^R01 | Standard ELR. Ask-at-order-entry answers, such as the employer, occupation and guardian fields in [LOINC panel 80399-9](https://loinc.org/5671-3), are mapped when sent. |
| HL7 v2.3.1 ORU^R01 | Still widely sent. Its own parser, with per-sender differences in segment and field use handled in the sender's profile. |
| eICR (CDA; FHIR when adopted) | For jurisdictions that receive lead case reports through eCR. Its own parser; [carries pregnancy and occupational data](https://www.hl7.org/fhir/us/ecr/electronic_initial_case_report_eicr_transaction_and_profiles.html) that ELR often lacks. |
| CSV, TSV, pipe-delimited, fixed-width | Mapped by the reporter's intake profile. |
| Excel (.xlsx, .xls) | Any sheet and header row, mapped by the reporter's intake profile. |
| Web portal | Single or batch entry for clinics and point-of-care sites with no file feed. |
| Manual entry | Result entry form for fax and paper; the scan attaches to the result. |

Files arrive by upload, secure file drop (SFTP) or a feed from the state's integration engine. Parsers are plug-ins, so a new format (FHIR, for instance) can be added without touching the rest of the pipeline. When HL7 messages arrive directly rather than through the integration engine, the system returns acknowledgments in the mode the sender's profile names.

### Intake profiles

Labs ignore published templates, so intake cannot depend on them. Instead, each sender has a saved intake profile that describes its own layout.

- **What a profile holds:** file type, sheet and header row, delimiter, date formats, column-to-field mapping, value translations ("V" → venous, "M" → male), local test codes → LOINC, default units, and constants for values the file leaves out, such as the lab name.
- **Building one:** the user uploads a sample file. The screen shows each column with sample values beside the target fields, suggests matches from header names and content (dates, ZIP codes, names), and previews the first rows parsed and validated before the profile is saved.
- **Layout drift:** each profile stores the file's header signature. When a sender's columns change, the whole batch pauses before loading and asks a user to update the mapping, instead of loading values into the wrong fields. A paused batch resumes once the mapping is fixed; it is not a failed batch.
- **Versioning:** profiles are versioned. A batch loaded with a bad mapping can be reprocessed with the corrected version.
- **Recommended template:** the project still publishes a CSV and Excel template with a data dictionary, and shows each reporter's template compliance, but nothing breaks when a reporter ignores it.

### Intake pipeline

1. **Receive:** store the raw file or message unchanged.
2. **Parse:** apply the sender's profile to produce staging rows.
3. **Validate:** check each row; every problem gets a code, the field, and a plain-language message.
4. **Normalize:** convert units, codes and dates.
5. **Match:** link to an existing person, send to review, or create a new person. Guardian, household and employer fields on the report create or link contact-only persons, relationships and employments, marked unverified. A guardian value that looks like an organization goes to review as a possible employer.
6. **Commit or hold:** clean rows load; the rest are held with their reasons.
7. **Classify:** derive population, compare thresholds and run case rules for each loaded row.

Apart from a layout pause, a batch never fails whole. Each batch shows counts of received, loaded, held and rejected rows, linked to the rows themselves.

### Held records

A held record is a staging row that could not load on its own. The queue is built around fixing causes, not editing rows one at a time.

- **Specific reasons.** Every hold names its reasons from a fixed, system-defined list: unknown sender, unknown test code, unknown unit, inactive analyte, missing result value, missing DOB, unusable address, possible duplicate result, uncertain person match, out of jurisdiction, or impossible dates.
- **Grouped by cause.** The queue groups holds by reason, then by sender and batch. Mapping one unknown code releases every row held for that code.
- **Fix once, remember it.** A fix can be saved back to the sender's profile ("always translate this value for this lab"), so the same problem does not recur.
- **Edit in place.** The reported value stays visible beside the correction. Corrections are logged; the result's as-reported snapshot is never changed.
- **Urgent first.** Held results at or above an action level sort to the top and appear on the home dashboard, because an elevated result sitting in a queue delays follow-up. Without a usable DOB, the lowest action level for any population applies.
- **Ownership and age.** Holds can be assigned, and the queue shows how long each has waited.
- **Release and reject.** Releasing reruns validation and matching. Rejecting requires a reason, keeps the row, and can generate a follow-up letter to the reporter.

### Person matching

Each incoming record ends in one of three plain outcomes: linked automatically, needs review, or new person. Matching compares names and aliases, DOB, sex, address and external identifiers, such as a medical record number with its assigning facility.

**Review screen**

- The incoming record and up to five ranked candidates appear side by side, field by field.
- Each field is marked as agreeing, close (a nickname, or a DOB with day and month swapped), different or missing.
- A one-line explanation says why the candidate was suggested, such as: same last name and DOB; first name "Jon" vs. "Jonathan"; same address.
- The candidate's result history and address history are one click away.
- Three buttons: Same person, Different person, Not sure. "Not sure" sends the record to a supervisor. "Different person" is remembered, so that pair is never suggested again.

**Built-in safeguards**

- Placeholder names such as "BABY BOY" never link automatically.
- Same DOB and address with a different first name (possible twins) never link automatically.
- Merges made later from the person page show a preview of everything that will move, and can be undone. When both records have an open case for the same analyte, the merge combines them into the earlier case and reruns classification. A merge of records owned by different tenants goes to their nearest common ancestor.

The same review screen is used for duplicate organizations, locations and providers.

### Matching configuration

Administrators choose behavior in plain words, not weights and scores.

- **Presets.** Strict, Balanced and Lenient, each described by its effect. For example, Strict links only when name, DOB and sex agree exactly, and sends more records to review. Strict is the default.
- **Plain-language rules.** An advanced view lists rules as on/off switches: treat nicknames as the same first name; allow day and month swapped in DOB; use address to break ties; ignore hyphens and suffixes in last names. Numeric scores stay hidden unless expanded.
- **Test before saving.** A proposed change runs against the last 90 days of incoming records and past review decisions. It shows how many records would now link automatically, how many would go to review, and how many past "Different person" decisions it would have linked by mistake.
- **History.** Every configuration change is versioned and audited, and can be rolled back.

### Federal extracts

Both extracts are generated, validated and stored by the system on a schedule, with a validation report before submission. Names, street addresses, phone numbers, SSNs and medical record numbers never leave in either file; each carries only the fields its federal layout requires, such as date of birth, county and a stable surrogate ID.

- **CBLS (children):** quarterly ASCII files built to CDC's current variable specification. CDC [requires quarterly reporting](https://cdc.gov/lead-prevention/php/data/state-surveillance-data.html) from the states it funds.
- **ABLES (adults):** an annual file built to NIOSH's current [submission layout](https://cdc.gov/niosh/media/pdfs/2024/04/Data-submission-information-for-ABLES-Program.pdf). CDC's [information collection notice](https://public-inspection.federalregister.gov/2024-11589.pdf) counts one ABLES submission per state per year, against four CBLS files.
- **Layouts are configuration.** Field order, widths, code maps and cross-field rules are versioned layout rows, so a federal layout change is a configuration update, called out in release notes.

ABLES field sources:

| ABLES field | Source in this model |
| --- | --- |
| StateRep | Jurisdiction setting (2-letter postal code). |
| StateRes, CountyRes | Reporting county: primary residence on the collection date. CN or MX for Canada or Mexico; 99 or 999 when unknown. |
| StateExp, CountyExp | Worksite of the employment linked to the case; otherwise the exposure source's location. Left unknown unless the exposure place is known. |
| ID | Stable surrogate ID per person, never name or SSN; 15 characters at most. |
| Status | Computed with the layout's rules for the calendar year (see Adult lead). |
| BLLDate, DateType | Collection date, coded as date of draw. |
| BLL | Canonical value as a whole number, rounded by the layout's rule. |
| DOB, Age, Sex | Person; age computed at collection. |
| Ethnicity, Race1–Race8 | Person ethnicity and race values, mapped to the eight race flags. |
| WorkRel | Case exposure category: occupational → 1, residential or other non-occupational → 2, both → 3, unknown → 9. |
| NAICS | Organization code, or the employment's override, in NAICS 2022; 999 when unknown. |
| COC | Employment occupation code, crosswalked to the layout's Census 2002 codes; 990 when unknown. |
| Process | Exposure source description, with the source type's ABLES code as a fallback; NA when the exposure is work-related or unknown. |

The extract validator applies NIOSH's cross-field rules. For example, NAICS and COC are required when the exposure is work-related and blank when it is not.

### Security and audit

- Access comes from profiles assigned at tenant nodes (see the architecture section).
- Shipped profiles: intake clerk, case manager, investigator, occupational investigator, supervisor, epidemiologist (de-identified views), administrator.
- Local health department users see persons whose current primary residence is in their jurisdiction (a county, an independent city or a health district, as configured), plus persons on cases they own or that are shared with them.
- Encryption in transit and at rest, multi-factor sign-in, and session time-outs.
- Audit log of every view, change and export of a person, result or case, searchable by user and record. Search text goes to a separate, purgeable log.
- Application and error logs carry record IDs, never names, dates of birth, addresses or result values.
- Retention and destruction follow the state records schedule; the architecture section covers how purges reach history tables and backups.

### Sources

- [ABLES data submission information (NIOSH)](https://cdc.gov/niosh/media/pdfs/2024/04/Data-submission-information-for-ABLES-Program.pdf)
- [HHLPSS data management platform (CDC)](https://cdc.gov/lead-prevention/php/data/hhlpss.html)
- [About the data: blood lead surveillance (CDC)](https://cdc.gov/lead-prevention/php/data/blood-lead-surveillance.html)
- [Childhood blood lead surveillance: state data (CDC)](https://cdc.gov/lead-prevention/php/data/state-surveillance-data.html)
- [Lead in Blood 2023 case definition (CDC)](https://ndc.services.cdc.gov/case-definitions/lead-in-blood-2023)
- [Blood Lead Surveillance System information collection notice (Federal Register, 2024)](https://public-inspection.federalregister.gov/2024-11589.pdf)
- [NYS Heavy Metals Registry](https://healthweb-back.health.ny.gov/environmental/workplace/heavy_metals_registry/)
- [Maryland Childhood Lead Registry 2000 report (MDE)](https://mde.State.md.us/programs/land/Documents/LeadReports/LeadReportsAnnualChildhoodLeadRegistry/LeadReportCLR2000.pdf)

## Provider and laboratory reference data

Ordering providers are identified by NPI and laboratories by CLIA number, using free federal sources. Staff stop retyping names and addresses, and reports can group results by the real provider and lab rather than by spelling variants.

### Sources

| Source | What it provides | Access | Refresh |
| --- | --- | --- | --- |
| [NPPES NPI Registry API](https://npiregistry.cms.hhs.gov/api-page) | Individual (NPI-1) and organization (NPI-2) providers: name, taxonomy, practice address, phone | Read-only JSON over HTTPS, version 2.1 only; 1.0 and 2.0 are retired | Registry pulls from NPPES daily |
| [NPPES data dissemination file](https://download.cms.gov/nppes/NPI_Files.html) | The full NPI file | Bulk download | Monthly |
| [CMS Provider of Services file: Clinical Laboratories](https://data.cms.gov/provider-characteristics/hospitals-and-other-facilities/provider-of-services-file-clinical-laboratories) | CLIA laboratories: CMS certification number, name, address, testing services, other characteristics | CSV download and data.cms.gov API | Quarterly |

NPPES warns that holding an NPI does not mean a provider is licensed or credentialed. Registry data are treated as a lookup aid, not proof of licensure.

### Provider entity

A **Provider** entity holds individual practitioners, separate from Organization. Lab results link to it through ordering\_provider\_id.

| Field | Type | Req | Rules |
| --- | --- | --- | --- |
| npi | text(10) |  | Passes the NPI check digit (Luhn algorithm with the 80840 prefix) before any lookup. Unique when present. |
| last\_name, first\_name, credential | text | Y (last name) | From the registry when matched; editable. |
| primary\_taxonomy | text |  | Code and description from the registry. |
| practice\_location\_id | FK location |  | Geocoded practice address. |
| organizations | child table |  | Practices or clinics the provider works at, with dates. |
| registry\_status | enum |  | Active, deactivated, not found, not looked up. |
| registry\_checked\_at | timestamp |  |  |
| local\_overrides | JSON |  | Fields staff changed; kept through refreshes and shown beside registry values. |

Organization carries `npi` (NPI-2) and `clia_number` (see Organization). A CLIA number is checked for format (two digits, the letter D, seven digits) before lookup.

### Behavior

1. **At intake.** When a message or file carries an NPI or CLIA number, the system checks the local cache first, then the source. It links or creates the provider or lab, and flags reported names or addresses that disagree with the registry.
2. **Without an identifier.** Reported provider and lab names go through the same matching review as people. The review screen can search the NPI Registry by name, city and state, and staff pick the right record.
3. **Manual entry.** Typing an NPI or CLIA number fills in the name and address; typing a name offers registry matches.
4. **Refresh.** A scheduled job rechecks NPIs on recently used providers and loads each quarterly CLIA file. Deactivated NPIs and CLIA numbers missing from the latest file are flagged for review, never deleted.
5. **Point-of-care sites.** Clinics running point-of-care analyzers are linked to their CLIA record in the reporter registry. This gives a ready list of sites for device recall outreach.

### Privacy and network rules

- Lookups send only an NPI, CLIA number or provider name. Patient data never leaves the system.
- Results are cached, and calls are rate-limited and retried politely.
- Some state networks block outbound calls. Each jurisdiction can turn live lookups off and load the monthly NPI file and quarterly CLIA file instead; the choice is a jurisdiction setting (see Jurisdiction configuration).

## Problems other jurisdictions report

The best evidence is the 2019 CSTE assessment, in which all 50 states plus DC and New York City described their blood lead systems. Its top finding: 56% of child systems had no way to detect clusters, and nearly half found built-in reports too limited. State reports add a consistent picture of missing race, address and sample type, and point-of-care sites that report late or stop reporting.

| Problem | Who reported it | Design response |
| --- | --- | --- |
| No algorithm to find clusters; staff match addresses by eye | 56% of child systems, 49% of adult systems (CSTE) | Cluster detection jobs (below) |
| Too few built-in reports; custom queries hard | 46% overall; 10 of 12 HHLPSS users (CSTE) | Report library, query builder and analytics views (below) |
| Bugs take a long time to fix; vendor or CDC controls the code | 32% overall; half of HHLPSS users (CSTE) | Open source: agencies or contractors can fix and contribute |
| Data updates are slow and manual | 28% overall; 58% of HHLPSS users (CSTE) | Work queues, bulk actions, auto-generated tasks |
| No geocoding or mapping | 38% (CSTE); over 10% of Colorado records lacked enough address to assign a county | Pluggable geocoder, master location table, map views |
| Variables missing, so data goes in notes fields | 26% of child and 46% of adult systems (CSTE) | Jurisdiction-defined custom fields stored as discrete, typed data (below) |
| Employer typed into guardian, provider or ordering-facility fields | Several states (CSTE) | Employment and organization entities; intake flags organization-like guardian values |
| Every ELR arrives as a child and staff relabel adults by hand; adults show up in child alerts | States with integrated systems (CSTE) | Population derived from DOB; alerts and queues filtered by population |
| Pregnancy kept in notes, and missing from the ELR message | Several states (CSTE) | Pregnancy episodes; ELR pregnancy field mapped when sent; eICR intake |
| Children and adults with the same exposure linked by hand, if at all | Half of adult programs have no method; most others manual (CSTE) | Automatic cross-link tasks |
| No master address index; address not tied to each test; address history wrong | Several states (CSTE) | Location table independent of cases; address snapshot on every result |
| Home risk assessments stored as Word documents | One state (CSTE) | Structured risk assessment forms and sample tables; files attach but do not replace fields |
| Labs report by fax, Excel, flat files and portals; only some by HL7 | HL7 averaged 61% of reports (CSTE); Kentucky keys out-of-state labs by hand | Per-reporter intake profiles and a web portal for small reporters |
| Point-of-care sites report sporadically or stop | North Carolina | Reporter registry with expected cadence and silence alerts (below) |
| Recall retesting hampered by incomplete device-user lists and bad data | States responding to the LeadCare recall | Test method, device and kit lot on each result (below) |
| Race missing for most records | 72% of tested children in Tennessee (2015–2022); about two-thirds in Pennsylvania | Completeness dashboard; enrichment from vital records with provenance (below) |
| Sample type missing | About 19% of Washington tests (2018–2022) | Completeness tracking by reporter; follow-up letters |
| Local health departments can't use the state system, or their data drifts out of sync | Several states (CSTE) | One install with local users and jurisdiction-scoped access |
| Unclear whether state or local staff own adult vs. child cases | One state (CSTE) | Case ownership rules (below) |
| Lead lives inside NBS or Maven and can't easily be replaced | A third of child systems (CSTE) | Standard interfaces to sit beside a communicable disease system (below) |
| Cost and state IT approval block adoption | 92% need funding and 75% must meet state IT rules before switching (CSTE) | Free license, containers, security documentation |

Dependence on one federal team is a real risk, not a theoretical one. CDC's Lead Poisoning Prevention and Surveillance Branch was cut in the April 2025 HHS reduction in force, and HHS rehired the team in June 2025 ([Fierce Healthcare](https://www.fiercehealthcare.com/regulatory/hhs-reinstates-more-450-cdc-employees-fired-during-massive-reorganization-april)).

### Cluster detection

Scheduled jobs flag groups of cases for staff review. Groups are formed by:

- shared location, building or child-care site
- shared employer or worksite
- shared ordering provider
- shared consumer product
- cases within a set distance of each other

Each rule has a configurable time window and minimum count. A flagged group becomes a cluster record that cases join or leave, with its own notes and status. Nothing is merged automatically.

### Reports and analytics

- A library of standard reports: testing counts and rates, elevated counts by population and analyte, case timeliness, investigation outcomes, reporter timeliness and completeness.
- A query builder for ad hoc line lists, with saved queries shared across users.
- Read-only analytics views, with de-identified versions, published to a per-root-tenant analytics database refreshed on a schedule. Epidemiologists use SAS, R, Python or Tableau against that copy, never against the shared transactional database (see Row-level security).
- Every list and report exports to CSV and Excel, for users with the export permission.

### Custom fields

Jurisdictions can add typed fields (text, number, date, picklist) to person, result, case, investigation and interview forms without code changes. Custom fields are stored as discrete data, appear in the query builder and exports, and are versioned like other configuration.

### Reporter registry and compliance

Each reporting lab, clinic and point-of-care site is an organization with its intake profile, contact, devices and expected reporting cadence. The system alerts staff when a reporter goes silent past its cadence, and shows per-reporter timeliness and field completeness so staff know whom to call. Timeliness uses the collected, reported and received dates on each result.

### Test method, device and lot

Each result records method (for example, ICP-MS, graphite furnace AA or point-of-care analyzer), device model and serial when known, and test kit lot, in the lab result fields. When a device or lot is recalled, a query returns every affected result and patient for retest outreach. Method also decides whether a venous result can confirm a case.

### Data completeness and enrichment

- A dashboard tracks missing race, ethnicity, address, sample type, test method and DOB by reporter and by month.
- Matches against vital records, Medicaid or an immunization registry can fill missing fields. Each filled value records its source and date, and never overwrites a value the reporter sent.
- Reason for test and insurance or Medicaid status are captured when sent, since several states named them as missing.

### Product registry

Consumer products found during investigations (spices, remedies, cosmetics, cookware, ceremonial powders) are records with brand, product name, country of origin and lab results. Several cases can link to one product, which supports cluster detection and referrals to regulators.

### Case ownership rules

A rule table sets which agency and team owns a case by population, analyte, exposure category and jurisdiction. For example, local health departments own child cases and the state owns occupational adult cases. A local user who starts a case outside their ownership triggers a notice to the owner.

### Working beside an existing disease system

Many states will keep NBS or Maven for other diseases. The system can receive ELR and eICR forwarded by a state integration engine, and exchange cases and results through its REST API. This lets a state adopt it for metals without replacing its communicable disease platform.

### Sources for this section

- [2019 Blood Lead Surveillance System Assessment (CSTE)](https://stacks.cdc.gov/view/cdc/148390/cdc_148390_DS1.pdf)
- [Tennessee vital records matching for race data (CDC)](https://www.cdc.gov/lead-prevention/success-stories-by-state/tennessee.html)
- [Pennsylvania 2014 lead surveillance annual report](https://www.health.pa.gov:443/topics/Documents/Environmental%20Health/2014%20Lead%20Surveillance%20Annual%20Report.pdf)
- [Washington child blood lead dashboard data notes](https://doh.wa.gov/data-and-statistical-reports/washington-tracking-network-wtn/lead-risk-and-exposure/childhood-blood-lead-level-testing-dashboard-data-notes)
- [Evaluating Colorado's lead poisoning surveillance system (CSTE 2015)](https://cste.confex.com/cste/2015/webprogram/Paper5281.html)
- [Kentucky data resource guide 2024](https://www.chfs.ky.gov/agencies/dph/Documents/datarg2025KCLPPP.pdf)
- [North Carolina point-of-care reporting requirements](https://medicaid.ncdhhs.gov/documents/providers/bulletins/archives/0915-special-bulletin-leadtesting/download)
- [LeadCare recall response interviews (CDC)](https://stacks.cdc.gov/view/cdc/75633/cdc_75633_DS1.pdf)

## Architecture: tenancy, authorization and data access

One installation serves many agencies. Each agency is a node in a tenant tree, users get permissions through profiles assigned at a node, and the database itself filters rows, so a bug in application code cannot expose another tenant's records.

### Stack

- ASP.NET Core on the current .NET long-term support release, serving a REST API documented with OpenAPI. The same API serves the user interface and outside integrations.
- An Angular front end in TypeScript, served from the same origin as the API, calls the REST API through a client generated from the OpenAPI document. Sign-in uses secure, HTTP-only session cookies with CSRF protection, so no access tokens are kept in browser storage.
- Dapper for data access. Every read and write goes through a stored procedure; the application's database login has EXECUTE on procedures only, with no direct table access.
- SQL Server 2022 or later is the baseline database. PostgreSQL may come in a later release under the conditions in Database support below.

### Front end

Angular and TypeScript render every screen. USWDS supplies the look (CSS, markup patterns and design tokens) where it fits, and Angular with the Angular CDK supplies all interactive behavior.

- **Generated API client.** The TypeScript client is generated from the API's OpenAPI document during the build and never edited by hand. A build fails if the client and the API disagree.
- **Same origin.** ASP.NET Core serves the compiled Angular files from the same host as the API. No CORS configuration is needed, session cookies are SameSite=Strict, and a Content Security Policy limits scripts to the application's own files.
- **USWDS without its JavaScript.** USWDS styles and tokens are used; its JavaScript is not. Components that depend on it, such as the combo box and date picker, are rebuilt with Angular and the CDK using USWDS markup and classes.
- **Data-heavy screens.** USWDS tables do not provide server-side paging, filtering or bulk actions. Held records, work queues, results, line lists and query output use one shared data grid built on the Angular CDK table and virtual scrolling, styled with USWDS tokens so it matches the rest of the application.
- **Accessibility.** USWDS patterns plus the CDK's focus and keyboard tools, checked with automated accessibility tests on each build and manual testing before release.

| Screen type | Approach |
| --- | --- |
| Forms: person, result entry, case, investigation, interview | USWDS form markup and styles; Angular reactive forms for validation |
| Header, navigation, alerts, banners, step indicators | USWDS components; behavior in Angular |
| Held records, work queues, results, line lists, query output | Shared data grid: server-side paging, sorting and filtering, column chooser, saved views, bulk selection, keyboard navigation |
| Dialogs, menus, combo boxes, date pickers | Angular CDK overlays and accessibility tools, with USWDS styling |
| Matching review, person timeline, maps | Custom Angular components using USWDS tokens |

### Shared database

All tenants, including separate root tenants (different states), live in one shared database. Isolation rests entirely on tenant columns and row-level security, so the design has to make that airtight and fast.

- **Tenant columns on every row.** Every tenant-owned table carries `root_tenant_id` and `owning_tenant_id`. The root column is denormalized onto every row so the security predicate never needs a join to find it.
- **Person access table.** Persons and results are owned by the root but seen by jurisdiction. A person-access table lists the tenants that can see each person, maintained by the procedures that change a primary residence or a case's ownership or sharing, so the predicate is one indexed lookup and never joins to addresses.
- **Indexes lead with the root.** Clustered and main nonclustered indexes start with `root_tenant_id`, which keeps each state's rows together and keeps predicates cheap.
- **Keys.** Primary keys are UUIDs generated by the application, with a time-ordered part placed where SQL Server sorts first and a random part. Inserts stay in index order, yet IDs can't be guessed or used to count records. NEWSEQUENTIALID is not used, because [Microsoft warns](<https://learn.microsoft.com/en-us/previous-versions/SQL/SQL-server-2008-r2/ms189786(v=sql.105)>) that its next value can be guessed.
- **No tenant-specific schema.** Jurisdiction differences live in configuration and custom-field rows, never in per-tenant tables or columns.
- **Per-tenant export and removal.** Backups cover the whole database, so the project ships procedures to export one root tenant's complete data set and to purge it, including its rows in temporal history tables. This covers a state that leaves, or a records-retention request. The purge report states when the data will age out of backups.
- **Fair use.** Batch jobs (intake, cluster detection, extracts) run in per-tenant queues with limits, so one state's large file cannot starve the others.
- **Audit and history.** SQL Server temporal tables keep row history, and ledger tables make the audit log tamper-evident. Transparent data encryption protects data at rest. Ledger history [cannot be deleted](https://learn.microsoft.com/sql/relational-databases/security/ledger/ledger-limits), so the audit ledger holds only IDs, actions, users and times; names, dates of birth, addresses, result values and search text stay out of it.

### Database support

SQL Server 2022 is the minimum version and the reference implementation: every feature ships and is tested on it first. Later versions, such as SQL Server 2025, are supported once the cross-tenant test suite passes on them. PostgreSQL may be added in a later release, once SQL Server support is stable, adopters request it, and it does not affect SQL Server support. It would be a separate implementation behind the same interface.

| Concern | SQL Server 2022 (baseline) | PostgreSQL (optional) |
| --- | --- | --- |
| Stored logic | T-SQL stored procedures | PL/pgSQL functions returning tables, with the same names and parameters |
| Request context | `sp_set_session_context` with read-only values | `set_config(..., true)` scoped to the transaction |
| Row-level security | Security policies with filter and block predicates, including on history tables | Row security policies; tables forced to apply them, and the application role never owns tables |
| History and audit | Temporal tables, ledger tables | History tables kept by triggers; audit hashing in the application |
| Overlap rule for primary residence | Checked in the procedure with row locks | The same check; an exclusion constraint may be added |

Rules that keep PostgreSQL from affecting SQL Server:

1. The C# code talks to a data-access interface. A SQL Server implementation and an optional PostgreSQL implementation each provide their own procedures and Dapper calls.
2. Schema and procedures live in separate folders per database, with their own migration scripts.
3. The shared cross-tenant security test suite runs against both. PostgreSQL failures never block a SQL Server release.
4. No feature is held back or simplified to suit PostgreSQL. If something cannot be matched there, PostgreSQL documents the gap.

### Tenant tree

- A tenant is an organization that uses the system: a state agency, a program inside it, or a local health department.
- Tenants form a tree of any depth. Root tenants are fully isolated from each other; no data crosses between roots.
- Ancestry is kept in a closure table (ancestor, descendant, depth), so "this node and everything below it" is one indexed join.
- Each tenant maps to one or more sub-jurisdictions (counties, independent cities), which routes new cases.

Example of one state's tree:

| Tenant | Parent | Typical work |
| --- | --- | --- |
| State lead program | none (root) | Intake, matching, federal extracts, statewide view |
| Occupational health program | State lead program | Adult occupational cases |
| County A health department | State lead program | Child cases for County A residents |
| City B health department | State lead program | Child cases for City B residents |

A city that wants full independence runs as its own root, or as its own install; the state then receives that city's data through extracts or the API, not shared records.

### Data ownership

| Data | Owner | Visible to |
| --- | --- | --- |
| Shipped reference data: analytes, LOINC maps, NAICS and Census codes, extract layouts | System | Every tenant, read-only |
| NPI and CLIA cache | System | Every tenant |
| Configuration: thresholds, workflows, matching | Root tenant; child tenants override only what the root allows, such as letter templates | The tenant and its descendants |
| Persons and results | Root tenant | Users whose scope covers the person's current primary residence, or a case they can see |
| Cases, investigations, activities | Tenant set by the case ownership rules | Owning tenant, its ancestors, and tenants it is shared with |
| Locations, organizations, providers | Root tenant | Every user under that root |

Persons stay one record statewide, so a family moving between counties does not create duplicates; cases are owned locally.

### Profiles and permissions

- **Permission:** an action on a resource, such as person.view, person.view\_identifiers, result.release\_hold, case.edit, data.export, matching.configure, extract.submit or tenant.administer.
- **Profile:** a named bundle of permissions, such as "Local case manager". Profiles are defined at the root and inherited down the tree. A tenant administrator cannot grant permissions they do not hold.
- **Assignment:** user + profile + tenant node + scope (this node only, or this node and everything below), with start and end dates.
- A user can hold several assignments, for example statewide epidemiologist and county case manager.
- Effective permissions are resolved at sign-in and cached for the session; an assignment change invalidates the cache.
- Administrator profiles do not include case editing by default, to separate duties.

### Row-level security

1. On every request, the application opens a connection and calls `sp_set_session_context` with the user ID and a session scope key, marked read-only so nothing later in the request can change it. The key points to the user's resolved tenant set in a protected scope table, so predicates test membership with an index seek instead of parsing a list.
2. SQL Server security policies apply filter predicates (rows outside the user's scope are invisible) and block predicates (rows cannot be inserted or moved into a tenant outside scope) on every tenant-owned table. Each temporal history table gets its own policy, because [row-level security predicates are not copied](https://learn.microsoft.com/sql/relational-databases/security/ledger/ledger-limits) from a current table to its history table.
3. Person and result predicates read the person-access table, so a jurisdiction check never joins to addresses.
4. Stored procedures also check permissions. Row-level security is the second wall, not the only one.
5. Pooled connections get fresh context on every open. Predicates fail closed when context is missing.
6. De-identified profiles read through views that drop identifiers.
7. No profile runs ad hoc SQL against the shared database, because crafted queries can [infer filtered rows through errors](https://learn.microsoft.com/en-us/training/modules/implement-compliance-controls-sensitive-data/5-implement-row-level-security). Analysts use the per-tenant analytics database instead.
8. An automated test suite attempts cross-tenant reads and writes through every procedure on each build, including history tables and the analytics publish job.

### Cross-tenant work

- **Case transfer:** when a child moves to another county, the case moves by a transfer request the receiving tenant accepts. History stays with the case.
- **Cross-tenant links and clusters:** tasks that span tenants go to their nearest common ancestor, often the state program, which can share the case with each tenant involved.
- **Search outside scope:** returns only "a record exists in another jurisdiction", without details, unless the user holds a profile that covers it. The check runs only on an exact name and DOB match, through one procedure that reads a match-key table outside row-level security, and every hit is audited.

### Authentication

Every user signs in with multi-factor authentication. Built-in accounts are the default; Microsoft Entra ID is an option an adopter turns on.

**Built-in accounts (default)**

- User ID and password, stored with ASP.NET Core Identity's password hasher. Passwords are never logged or emailed.
- Password rules follow [NIST SP 800-63B-4](https://csrc.nist.gov/pubs/sp/800/63/b/4/final) (July 2025) by default: a minimum of at least 8 characters, which NIST permits only because every account has a second factor; a check against known breached passwords; no composition rules; and no forced rotation. Agencies whose policy requires rotation or complexity rules can turn them on, and the settings screen notes that NIST now says not to.
- Multi-factor authentication is required for every user and cannot be turned off. Users enroll at first sign-in with an authenticator app (time-based codes) or a security key or passkey (FIDO2/WebAuthn). Administrator profiles require a security key or passkey, because those resist phishing.
- Email codes and text-message codes are two separate settings, both off by default. Under [800-63B-4](https://pages.nist.gov/800-63-4/sp800-63b/authenticators/), email may not be used for out-of-band authentication and text messages are a restricted authenticator, so the settings screen says so before either is enabled. An adopter can enable either one, both, or neither, since some states allow email codes but not text messages.
- One-time recovery codes are issued at enrollment. Accounts lock after repeated failed attempts, and a tenant administrator can reset a user's MFA after verifying the person.

**Microsoft Entra ID (optional)**

- Enabled by configuration per root tenant, using OpenID Connect. Nothing about Entra is required for adopters who do not turn it on. The sign-in code is standard OpenID Connect, so another provider can be added by configuration later; Entra ID is the one the project tests.
- Users sign in through Entra, so the agency's own MFA and conditional access policies apply. If the sign-in token does not show that MFA was performed, the application asks for its own second factor.
- Entra users are linked to application users by their Entra object ID. Administrators add them by hand or through SCIM provisioning from Entra.
- Both methods can run side by side, for example state staff on Entra and local health department staff on built-in accounts.

**For both methods**

- Sign-in only establishes who the user is. Tenant assignments and profiles still decide what they can see and do.
- Sign-in method, MFA options and password rules are tenant settings, recorded and audited like other configuration.
- Sessions time out after inactivity, and tokens never carry patient data.

## Jurisdiction configuration

An adopting agency sets these at install and can change them later through the admin screens. Each change is effective-dated and audited.

| Setting | What the jurisdiction decides | Shipped default |
| --- | --- | --- |
| Jurisdiction identity | State postal code, state FIPS, agency name, letterhead | None; required at install |
| Sub-jurisdictions | Counties, independent cities or health districts, and which counties map to each | The state's county list |
| Access model | The tenant tree: programs and local health departments as child tenants, or a standalone local root | State with local users |
| Reportable analytes | Which metals and specimens are reportable, and at what levels | Blood lead active at all values; others present but inactive |
| Case and action levels | Child, adult and pregnancy case levels, action tiers and due intervals | CDC child blood lead reference value; federal adult rows shipped inactive; adult levels must be set |
| Confirming methods | Which test methods confirm a venous result, and whether an unreported method does | Graphite furnace AA and ICP-MS; unreported method confirms |
| Adult age cutoff | 16, 18 or another age for workflows | 16, matching ABLES and the national case definition |
| Pregnancy episode length | How long an episode with no end date stays open | 42 weeks from start |
| Primary residence policy | Whether a new lab address moves the primary or goes to review | Review |
| Investigation lookback | How far back past addresses and jobs count | Must be set at install |
| Site investigation minimum | Weekly hours at a relative or sitter home or other regular site that trigger an investigation request | Must be set at install |
| Workflow templates | Steps, owner roles, due-date rules and closure criteria per population | Child residential, adult occupational and pregnancy templates |
| Contact attempts before a letter | Failed contact attempts before a letter is queued | Must be set at install |
| Local notification | Whether opening a case notifies the local health department | Off |
| Medical removal and return-to-work levels | Levels for adult retest tracking | None; loaded from the OSHA or state-plan standard |
| Arsenic handling | Whether a high total urine arsenic opens a case or a "speciation recommended" task | Task |
| Occupational referral agency | Federal OSHA area office or the state-plan agency | Federal OSHA |
| Cluster rules | Time window, minimum count and distance for each cluster rule | Must be set at install |
| Letters | Templates, languages, signatures | English samples |
| Federal extracts | CBLS and ABLES on or off, schedule and layout version | CBLS quarterly and ABLES annually, both on |
| Matching preset | Strict, Balanced or Lenient, plus rule switches | Strict, sending more to review |
| Provider and lab lookups | Live NPPES and CMS lookups, or monthly and quarterly file loads | Live lookups |
| Sign-in | Built-in accounts, Entra ID or both; email and text-message codes; password rules | Built-in accounts; email and text codes off; NIST 800-63B-4 password rules |
| Records retention | Retention periods by record type, from the state records schedule | Must be set at install |

## Adoption and open-source considerations

Adoption depends less on features than on how cheaply an agency can move its existing data in and pass its own IT review.

- **Migration toolkit.** HHLPSS has no export feature, so each adopter builds its own migration. The project provides documented import staging tables, CSV templates and an import API, keeps a legacy ID on every imported record for traceability, and flags guardian values that look like employers for review.
- **Deployment.** One installation can host a state and its local health departments as a tenant tree, or an agency can run its own install, on its own cloud tenancy or servers. The project publishes container images. No dependency on a hosted service run by the project.
- **Reference data shipped with the code.** Analyte and LOINC mappings, NAICS and Census occupation code lists with their versions and crosswalks, county FIPS codes, federal extract layouts, and federal levels tagged with their authority and shipped inactive. State-specific levels are never shipped as defaults.
- **Standards in and out.** HL7 v2.5.1 ELR and eICR for intake, CDC and NIOSH layouts for extracts, and a documented REST API. A FHIR interface can follow.
- **No real data in the repository.** A synthetic data generator produces demo and test records, including shared-custody children, adults with several jobs and take-home exposure households.
- **Security documentation.** A controls mapping (for example, to NIST SP 800-53) and a data-flow diagram, which state IT security reviews usually ask for.
- **Accessibility.** WCAG 2.1 AA and Section 508 conformance, with an accessibility statement. WCAG 2.1 AA is also the standard in DOJ's ADA Title II web rule for state and local governments, now due April 26, 2027 for those serving 50,000 or more people and April 26, 2028 for smaller ones. HHS's Section 504 rule applies it to recipients of HHS funds, now due May 11, 2027 for those with 15 or more employees ([EDUCAUSE](https://er.educause.edu/articles/2026/6/doj-and-hhs-extend-web-accessibility-deadlines-to-2027-2028)).
- **Project basics.** The Apache License 2.0, a contributing guide, issue templates, a public roadmap, and release notes that call out any change to extract formats, layouts or classification rules.

## Decisions

| Topic | Decision |
| --- | --- |
| License | Apache License 2.0. Anyone may use, modify and distribute it, including commercially. It comes with no warranty, and redistributions must keep the copyright notice and the project's NOTICE file, which credits the source. |
| HHLPSS migration | HHLPSS has no export feature. Adopters build their own migration onto the project's import staging tables, CSV templates and import API. |
| Other exposure markers | Deferred to a later release if adopters request them. The analyte model already allows adding them. |
| Case definition | Child and adult classification follows CDC's 2023 Lead in Blood case definition; levels and confirming methods are settings. |
| Technology | ASP.NET Core, Dapper and stored procedures, with multi-tenant authentication and authorization: a hierarchical tenant tree, profile-based role assignment and row-level security. |
| Front end | Angular with TypeScript, served from the same origin as the API, using a client generated from OpenAPI. USWDS for CSS, markup and design tokens where it fits; interactive behavior in Angular and the Angular CDK; a shared CDK data grid for paged, filtered screens. |
| Database | SQL Server 2022 or later is the baseline. PostgreSQL may be added in a later release once SQL Server support is stable, adopters request it, and it does not affect SQL Server support. |
| Tenant isolation | All tenants share one database, isolated by tenant columns and row-level security. Analytics run on a separate per-tenant copy. |
| Sign-in | Built-in user ID and password with required multi-factor authentication. Microsoft Entra ID sign-in is optional and enabled by configuration, through standard OpenID Connect. |

## Open questions

- [ ] **Offline field entry.** Do investigators need to record samples and interviews without a connection? If so, which screens, and how are conflicts resolved on sync?
- [ ] **Service levels.** What availability, recovery point and recovery time objectives apply to a shared multi-state install, and who operates it?
- [ ] **Unreported test method.** Is counting a venous result with no reported method as confirmed acceptable for CBLS, or should CBLS confirmation require a method?
- [ ] **Adult default.** Should the shipped adult case row be the 2023 national definition (3.5 µg/dL venous) or NIOSH's 5 µg/dL level, or should both stay inactive?
- [ ] **Install-time defaults.** Should the project ship starting values for the site investigation minimum, contact attempts before a letter, investigation lookback and cluster rules?

## Revision notes

The October 24, 2026 revision fixes internal contradictions, closes rule gaps that would misclassify results, tightens tenant isolation and brings federal references up to date. Each row names the section changed and the reason.

| Section | Change | Why |
| --- | --- | --- |
| Purpose, glossary, configuration | ABLES extract is annual; CBLS stays quarterly | CDC's 2024 information collection notice counts one ABLES submission per state per year; the spec said quarterly for both. |
| Design principles | Added principle 7 (automation proposes, staff decide); principle 6 covers results, cases and exports | Matches how cross-links, clusters and address changes already worked, and the security section's audit scope. |
| Data model overview | 18 core entities; Exposure source and Provider added to the diagram; supporting tables listed | Both entities were defined but missing from the 16-entity overview. |
| Entity definitions | Enumerations split into system-defined and administrator-maintained | A fixed list of hold reasons contradicted "every enumeration is an admin lookup". |
| Person | Sex adds other; race maps to all eight ABLES flags; external identifiers; death creates a task instead of closing cases | ABLES Race1–Race8 are fixed category flags (including refused and unknown), not eight free slots, and ABLES sex has an other code. |
| Person alias | Field table added | The entity had no fields. |
| Pregnancy episode | End rule, outcome field; male sex warns instead of blocking | An episode with no end date stayed open forever; blocking rejects valid reports. |
| Person relationship | Direction defined; sibling role; verified\_date | "Parent" did not say which side was the parent. |
| Organization, Provider | npi added to Organization; duplicate clia\_number removed from the provider section; NAICS version stored | The provider section re-added a field Organization already had. |
| Location | Country and non-US fields, city-only locations, manual pins, building record defined, owner history, census vintage | US-only addresses blocked cross-border and homeless records; building\_id pointed at nothing; owners change during orders. |
| Employment | industry\_text, verified\_date, occupation code version | ABLES expects Census 2002 occupation codes; coding needs the stated industry. |
| Exposure source | Casting and pica types; ABLES Process mapping; product link | Aligns with ABLES Process codes and the product registry. |
| Lab result | Result status and corrections; reported date vs received time; method, device, lot, reason and payer fields; ordering provider links to Provider | Lab corrections were treated as duplicates; method decides confirmation; fields described elsewhere were missing from the table. |
| Investigation | Targets a place, a worksite or a source; links to many cases; reuses open investigations | One case per investigation duplicated work for siblings and co-workers; product investigations had no target. |
| Environmental samples | Effective-dated standards table; computed exceedance; instrument file import | EPA's 2024 dust rule changed the standards; a typed flag cannot follow such changes. |
| Address rules | Reporting county vs assignment county; hours threshold for other places; row splitting; no fixed address | The example skipped the grandmother's home though the rules said every place is investigated; follow-up belongs where the family lives now. |
| Thresholds | Action rows keyed by action code, with due intervals, optional age band and sample type; reference rows inactive | The overlap rule blocked tiered action levels such as CDC's confirmation schedule. |
| Normalization | Population and thresholds run after matching; below-detection rule | Population needs the matched person's DOB and pregnancies; a "<5" result cannot be judged against 3.5. |
| Child lead | Cites the 2023 Lead in Blood definition; confirming methods; unknown sample type; annual new-case rule | The spec cited the 2010 definition; the 2023 definition requires GFAAS or ICP-MS for venous confirmation. |
| Adult lead | Unknown sample type counts as venous; ABLES Status uses NIOSH's 10 µg/dL rule | ABLES status codes use their own line, not the adult case level. |
| General rules | Open cases keep classification on a threshold change; outreach report | The spec covered closed cases only. |
| Investigation workflows | Pregnancy template; closure criteria as settings; guardian from the lab report; shared workplace investigations | Pregnant cases had no template. |
| Cross-links | Testing tasks for siblings and pregnant household members | Common follow-up the table missed. |
| User interface | Pregnancy section and order column; export permission; new queues | Pregnancy episodes had no place on the person page. |
| Intake | eICR format; HL7 acknowledgments; classify step after commit; guardian and employer links from reports; layout pause defined | Layout drift stopped whole batches while the text said a batch never fails whole. |
| Matching | External identifiers; merge rules for open cases and cross-tenant records; preset names match configuration | Merging two open cases broke the one-open-case rule; the configuration table described scores. |
| Federal extracts | Direct identifiers named; BLL field added; field mappings and codes; layouts as configuration | Both files carry DOB, so "personal identifiers never leave" was inaccurate. |
| Security and audit | Local access by current residence or case; search text and application logs kept free of patient data | Aligns with Data ownership; ledger history cannot be purged. |
| Architecture | Person-access table; session scope table; history-table policies; analytics copy; UUID guidance; ledger without patient data | History tables do not inherit row-level security, ad hoc SQL can leak rows through errors, NEWSEQUENTIALID is guessable, and ledger history cannot be deleted. |
| Authentication | NIST SP 800-63B-4 rules; email and text-message status; phishing-resistant MFA for administrators; standard OpenID Connect | 800-63B-4 (July 2025) bars email for out-of-band authentication and restricts text messages. |
| Jurisdiction configuration | 11 settings added; matching row renamed | Settings referenced elsewhere had no row. |
| Adoption | Container images; DOJ and HHS accessibility dates | DOJ moved the Title II date for large entities to April 26, 2027. |
| Decisions, open questions | Case definition, database and sign-in rows updated; five open questions | "None at this time" hid decisions still to make. |
| Sources | 2010 case definition replaced with the 2023 Lead in Blood definition; extract and standards sources added | The 2010 definition is superseded. |
