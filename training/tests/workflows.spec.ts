import { expect, test } from '@playwright/test';
import { loadCatalog } from '../lib/catalog.ts';
import { BASE_URL } from '../lib/config.ts';
import { perform } from '../lib/director.ts';

/**
 * Every workflow, run without recording against the app on the training database. Each step's actions check what
 * they need, and the privacy scan runs after every step, so this fails at the step a changed screen broke.
 * Needs the app running (npm run app); MERIT_TRAINING_SKIP_APP=1 skips these when it is not.
 */
const catalog = await loadCatalog();

test.beforeAll(async ({ request }) => {
  test.skip(process.env.MERIT_TRAINING_SKIP_APP === '1', 'MERIT_TRAINING_SKIP_APP=1');
  const reply = await request.get(`${BASE_URL}/account/signin`, { ignoreHTTPSErrors: true }).catch(() => null);
  expect(reply?.ok(), `MERIT is not answering at ${BASE_URL}; start it on the training database with npm run app`).toBe(true);
});

for (const { workflow } of catalog) {
  test(`${workflow.role}/${workflow.feature}/${workflow.id} still works`, async ({ browser }) => {
    const run = await perform(workflow, { mode: 'dry', browser });
    expect(run.steps).toHaveLength(workflow.scenes.reduce((n, s) => n + s.steps.length, 0));
  });
}
