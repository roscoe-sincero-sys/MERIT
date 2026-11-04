import { defineConfig } from '@playwright/test';

/**
 * Tests for the training-video tooling (npm test):
 *   unit.spec.ts       captions, narration timing helpers and the workflow catalog; no app needed
 *   privacy.spec.ts    the on-screen scanner and the training-database guard
 *   workflows.spec.ts  every workflow, run fast against the app on the training database without recording: a renamed
 *                      button or a changed screen fails at the step that no longer matches
 *   outputs.spec.ts    every built video: format, captions, transcript, sensitive strings, and whether it is current
 * The installed Microsoft Edge is used (MERIT_TRAINING_CHANNEL), so no browser download is needed.
 */
export default defineConfig({
  testDir: './tests',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 180_000,
  reporter: [['list']],
  use: { channel: process.env.MERIT_TRAINING_CHANNEL ?? 'msedge' },
});
