# User guide sources

The PDF guides in `docs/` are printed from these HTML files:

| PDF | Source |
| --- | --- |
| `MERIT User Guide - Daily Operations.pdf` | `operations-guide.html` |
| `MERIT User Guide - Importing NPI and CLIA Records.pdf` | `npi-clia-import-guide.html` |
| `MERIT User Guide - Access and Profiles.pdf` | `access-guide.html` (mock screens drawn in HTML; no screenshots) |

Screenshots and diagram images live in `docs/images/guide`. The interactive diagrams are
`docs/daily-intake_workflow.html`, `docs/npi-clia-import_workflow.html` and `docs/cbls-extract_lifecycle.html`;
their Archify candidates are under `.archify/`.

## Refreshing the screenshots after a screen changes

1. Deploy a **fresh** demo database, so nothing from testing shows in the images, and run the web app and worker on it.
   A separate database leaves your everyday `MERIT` demo alone:

   ```powershell
   cd sourceCode/database
   .\Deploy-MeritDatabase.ps1 -DatabaseName MERIT_GUIDES -IncludeDemoData
   cd ..
   $env:ConnectionStrings__Merit = "<the Development connection string, with Database=MERIT_GUIDES>"
   dotnet run --project src/Merit.Web --launch-profile Merit.Web
   # in a second window, with the worker's connection string and cluster detection on:
   $env:Worker__ClusterDetection__RunAsUserName = "supervisor"; dotnet run --project src/Merit.Worker
   ```

2. Run any end-to-end test, so the saved sessions in `sourceCode/tests/e2e/.auth` are fresh:
   `cd sourceCode/tests/e2e && $env:MERIT_E2E_DATABASE="MERIT_GUIDES"; npx playwright test -g "skip link"`.
   Then do what the guides describe, once each, so the screens have something to show:
   - as `supervisor`, upload `samples/chesapeake_lab_sample.csv`, then `samples/chesapeake_match_review_sample.csv`, on **Intake**;
   - as `admin`, check and import `samples/registry_npi_clia_sample.csv` on **Reporters** &rarr; **Import NPI and CLIA records**;
   - as `supervisor`, enter a result for Ana Rivera (born 1995-11-30): Lead, 8.4 ug/dL, venous, ICP-MS, collected
     2026-10-25, laboratory Chesapeake Reference Laboratory. That opens the case shown in `11-case`;
   - as `supervisor`, request the CBLS file for 2026 Q3 on **Extracts** and wait until it is **Ready**;
   - wait for the worker's cluster detection (30 seconds after it starts) to flag the cluster at 1214 W Lombard Street.
3. Capture: `node docs/guides/tools/capture-screenshots.cjs` (all images), or name the ones to retake, for example
   `node docs/guides/tools/capture-screenshots.cjs 03-held 13-queues`. Each image is cropped to whole elements and
   captured at 2x.
4. Size the figures: `python docs/guides/tools/size-figures.py`. Every screenshot prints at the same scale, so text is
   the same size in each; narrow images take their caption beside them.
5. Print the PDFs (Microsoft Edge):

   ```
   node docs/guides/tools/print-pdf.mjs docs/guides/operations-guide.html "docs/MERIT User Guide - Daily Operations.pdf" "MERIT user guide: daily, weekly and quarterly work"
   node docs/guides/tools/print-pdf.mjs docs/guides/npi-clia-import-guide.html "docs/MERIT User Guide - Importing NPI and CLIA Records.pdf" "MERIT user guide: importing NPI and CLIA records"
   node docs/guides/tools/print-pdf.mjs docs/guides/access-guide.html "docs/MERIT User Guide - Access and Profiles.pdf" "MERIT user guide: who sees what, and your profiles"
   ```

6. Look at every page before committing: a figure that no longer fits moves to the next page whole.

The screenshots show the synthetic demo data, so they change when the demo data changes (for example a sample file
uploaded twice shows its rows twice).
