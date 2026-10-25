import { defineConfig, devices } from '@playwright/test';

/**
 * Runs against a MERIT instance that is already up (dotnet run, or the Web + Worker launch profile) with the demo data.
 *   MERIT_E2E_URL       base address (default https://localhost:7243)
 *   MERIT_E2E_SERVER    SQL Server instance for resetting demo MFA (default localhost\PORTAL)
 *   MERIT_E2E_DATABASE  database the instance uses (default MERIT)
 * The installed Microsoft Edge is used, so no browser download is needed; set MERIT_E2E_CHANNEL=chrome for Chrome.
 * Every test runs at tablet width (768 x 1024).
 */
export default defineConfig({
  testDir: './specs',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list'], ['html', { open: 'never' }]],
  globalSetup: './global-setup.ts',
  use: {
    baseURL: process.env.MERIT_E2E_URL ?? 'https://localhost:7243',
    ignoreHTTPSErrors: true,
    channel: process.env.MERIT_E2E_CHANNEL ?? 'msedge',
    viewport: { width: 768, height: 1024 },
    trace: 'retain-on-failure',
  },
  projects: [
    { name: 'tablet', use: { ...devices['Desktop Edge'], viewport: { width: 768, height: 1024 } } },
  ],
});
