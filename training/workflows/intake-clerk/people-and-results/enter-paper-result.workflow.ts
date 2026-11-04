import { defineWorkflow } from '../../../lib/workflow.ts';
import { resetTrainingResults } from '../../../lib/data.ts';

/**
 * Proof of concept: an intake clerk enters a faxed blood lead result for a child who is already in MERIT.
 * Synthetic data: Leo Rivera (born 2021-02-02, Baltimore City) and Chesapeake Reference Laboratory are demo records.
 * The accession number starts TRN-, which marks results entered by the training tooling; prepare() soft-deletes them
 * and the case and tasks they opened, so every build starts from the same record. A venous ICP-MS result at 4.2 ug/dL
 * meets the child case level (3.5), so the outcome shows a confirmed case being opened.
 */
export default defineWorkflow({
  id: 'enter-paper-result',
  title: 'Entering a result from a paper report',
  summary: 'Search for the person first, enter a faxed lab result on their record, and read what MERIT did with it.',
  role: 'intakeClerk',
  feature: 'peopleAndResults',
  user: 'intake',
  start: '/',
  prepare: () => resetTrainingResults(),
  scenes: [
    {
      title: 'Search first',
      steps: [
        {
          say: 'A faxed lab report has arrived for a child named Leo Rivera. Before entering anything, search for him, so the result joins his existing record.',
          run: async s => {
            await s.highlight(s.page.getByRole('navigation').getByRole('link', { name: 'People', exact: true }));
            await s.click(s.page.getByRole('navigation').getByRole('link', { name: 'People', exact: true }));
            await s.see(s.page.getByRole('heading', { name: 'Find or add a person', level: 1 }));
          },
        },
        {
          say: 'Type the last name, then the first name. Adding the date of birth from the report narrows the search to one person.',
          run: async s => {
            await s.type(s.page.locator('#search-q'), 'Rivera Leo 2021-02-02');
            await s.click(s.page.locator('main').getByRole('button', { name: 'Search' }));
          },
        },
        {
          say: 'One match, with the same name and date of birth. Open his record.',
          run: async s => {
            const row = s.page.locator('main tbody tr', { hasText: 'Rivera, Leo' });
            await s.expectText(s.page.locator('main tbody'), /Rivera, Leo/);
            await s.highlight(row);
            await s.click(row.getByRole('link', { name: 'Rivera, Leo' }));
            await s.see(s.page.getByRole('heading', { name: 'Leo Rivera', level: 1 }));
          },
        },
      ],
    },
    {
      title: 'Open the result form',
      steps: [
        {
          say: 'The person page shows who he is and his history. Choose Enter a result.',
          run: async s => {
            await s.highlight(s.page.locator('#person-header'));
            await s.click(s.page.locator('#person-header').getByRole('link', { name: 'Enter a result' }));
            await s.see(s.page.getByRole('heading', { name: /Enter a result for Leo Rivera/, level: 1 }));
          },
        },
      ],
    },
    {
      title: 'Enter what the report says',
      steps: [
        {
          say: 'Choose the analyte first. The specimen and unit choices follow it.',
          run: async s => {
            await s.select(s.page.getByLabel('Analyte'), 'Lead');
            await s.see(s.page.locator('#SampleType'));
            await s.highlight(s.page.locator('#result-fields .merit-form-row', { has: s.page.locator('#AnalyteSpecimenId') }), { describe: false });
          },
        },
        {
          say: 'The report says the blood was drawn from a vein, so set the sample type to venous.',
          run: async s => s.select(s.page.locator('#SampleType'), 'Venous'),
        },
        {
          say: 'Type the value exactly as reported: 4.2 µg/dL.',
          run: async s => s.type(s.page.locator('#ValueText'), '4.2'),
        },
        {
          say: 'Enter the date the blood was collected, which is required, and the date the lab reported it.',
          run: async s => {
            await s.type(s.page.locator('#CollectedDate'), '2026-10-28');
            await s.type(s.page.locator('#ReportedDate'), '2026-10-30');
          },
        },
        {
          say: "Add the test method, the lab's accession number, and the reason for the test.",
          run: async s => {
            await s.select(s.page.locator('#TestMethod'), 'ICP-MS');
            await s.type(s.page.locator('#AccessionNumber'), 'TRN-26-0481');
            await s.select(s.page.locator('#ReasonForTest'), 'Screening');
          },
        },
        {
          say: 'For the laboratory, type part of its name, then choose it from the list.',
          run: async s => s.pick(s.page.locator('#LabOrganizationId-text'), 'Chesa',
            s.page.locator('#LabOrganizationId-list [role=option]', { hasText: 'Chesapeake Reference Laboratory' })),
        },
      ],
    },
    {
      title: 'Save and read the outcome',
      steps: [
        {
          say: 'Check the entries against the paper report, then choose Save result.',
          run: async s => {
            await s.click(s.page.getByRole('button', { name: 'Save result' }));
            await s.see(s.page.locator('#result-saved'));
          },
        },
        {
          say: 'MERIT confirms the result, with the value converted to its standard unit and the population it counts toward.',
          run: async s => {
            const alert = s.page.locator('#result-saved .usa-alert');
            await s.expectText(alert, /Result saved/);
            await s.zoom(alert, 1.5);
            await s.see(alert, { describe: true });
          },
        },
        {
          say: 'Below it, MERIT lists each level the result was compared with, and whether a case was opened or updated.',
          run: async s => {
            const box = s.page.locator('#result-saved .usa-summary-box');
            await s.expectText(box, /Thresholds and case outcome/);
            await s.highlight(box);
          },
          hold: 1200,
        },
        {
          say: 'From here, enter another result from the same report, or go back to his record.',
          run: async s => s.highlight(s.page.locator('#result-saved > p').last()),
        },
      ],
    },
  ],
  recap: [
    'Searched by name and date of birth before entering anything',
    'Entered the result on the existing person record',
    'Read the standardized value, the levels it was compared with and the case outcome',
  ],
});
