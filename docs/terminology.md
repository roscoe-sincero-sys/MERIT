# MERIT interface terminology

Use these terms across Razor and Angular. Keep technical identifiers in configuration and source/audit details where needed.

| Concept | Visible term | Explanation |
| --- | --- | --- |
| Intake group | Lab imports | Contains **Upload lab results**, Held records and Match review. |
| Surveillance group | Case management | Cases, investigations, clusters and maps. |
| Registry/results group | Lab results & reports | Lab results, reports, reporting organizations and extracts. |
| Analyte | Substance tested (analyte) | Show the substance name, retaining its code where useful; **Lead (PB)** is readable without hover. |
| Specimen | Sample material (specimen) | Blood, urine or other tested material; sample type retains venous/capillary precision. |
| Unit on the original report | Reported unit | MERIT converts to a stated comparison unit for reference-level checks. |
| Canonical unit | Comparison unit | Retain the exact unit symbol; explain conversion in visible help. |
| collectedDate | Sample collection date | The draw/collection event, separate from laboratory final result and Department receipt. |
| reportedDate | Laboratory report date | Does not establish the actual final-result date or Department receipt. |
| resultStatus | Laboratory result status | Separate from case classification and case lifecycle status. |
| Task done | Completed with evidence | Do not equate an unsuccessful attempt, waiver, transfer or closure dismissal with completed notification. |

These are design/content decisions based on the audit. Validate destination predictability with representative clerks and case managers; no human participant study is claimed by the automated regression suite.
