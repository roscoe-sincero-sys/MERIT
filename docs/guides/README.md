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

1. Deploy the demo database and run the web app (see `sourceCode/README.md`).
2. Run any end-to-end test, so the saved sessions in `sourceCode/tests/e2e/.auth` are fresh:
   `cd sourceCode/tests/e2e && npx playwright test -g "skip link"`.
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
