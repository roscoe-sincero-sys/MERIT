# Sample intake file

`chesapeake_lab_sample.csv` is synthetic. Upload it on the Intake screen with the profile
"Chesapeake Reference Laboratory CSV" (created by the demo data). It exercises the pipeline:

| Row | What happens |
| --- | --- |
| 1 | Matches the demo child Mia Rivera exactly and attaches to her open case; the guardian already exists. |
| 2 | New child, capillary 12 ug/dL: new person, lab-reported address becomes primary, suspect case, confirmatory test task. |
| 3 | Exact duplicate of row 2 (same accession): stored as a duplicate, not reclassified. |
| 4 | Placeholder name "Baby Boy": never linked automatically, held as an uncertain match. |
| 5 | Matches Grace Okafor during her pregnancy episode: pregnant population, attaches to her case. |
| 6 | No date of birth and 48 ug/dL: held as missing DOB, marked urgent. |
| 7 | Unknown LOINC code and a guardian value that looks like a company: held as unknown test code. |
| 8 | Unit mg/L is not accepted for blood lead: held as unknown unit. |
| 9 | "<1.0" below every level: loaded, no case. |

## Matching review sample

`chesapeake_match_review_sample.csv` holds four records that each look like an existing demo person without matching
exactly, so all four wait on the **Match review** screen (`/app/match-review`):

| Row | Candidate | Why it is not linked automatically |
| --- | --- | --- |
| 1 | Mia Rivera | Same date of birth and address, different first name (Mya): flagged as a possible twin. |
| 2 | James Carter | First name "Jim" is a nickname for James. |
| 3 | Luis Rivera | Date of birth with day and month swapped. |
| 4 | Ana Rivera | Hyphenated last name (Rivera-Lopez) with the same first name and date of birth. |

Mark row 1 **Different person** and the pair is remembered; with no candidate left, the row loads as a new person.

## Reporter registry sample

`registry_npi_clia_sample.csv` loads laboratories, facilities and providers on **Reporters > Import NPI and CLIA records**
(`/reporters/import`). Checking it shows four rows ready and four to fix:

| Line | Entry | Result |
| --- | --- | --- |
| 2 | Tidewater Clinical Laboratory, CLIA 21D0456789 | Ready (laboratory). |
| 3 | Allegany Regional Lab, CLIA 21D04567 | Not loaded: a CLIA number is two digits, D, then seven digits. |
| 4 | Patapsco Family Health Center, NPI 1987654328 | Ready (facility). |
| 5 | Severn Pediatric Associates, NPI 1765432100 | Not loaded: the NPI check digit is wrong (the real one ends in 3). |
| 6 | Grace Okafor, MD, NPI 1543210987 | Ready (provider). |
| 7 | Erik Lindqvist, DO, NPI 1334455668 | Ready (provider). |
| 8 | Patel, NP, no NPI | Not loaded: a provider needs an NPI. |
| 9 | Grace Okafor again | Not loaded: same NPI as line 6. |
