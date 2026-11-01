// Actual persisted browser zoom in an isolated Chromium profile; not CSS zoom or viewport emulation.
// Run from tests/e2e after Playwright global setup: node tools/navigation-zoom.cjs
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('@playwright/test');
const channel = process.env.MERIT_E2E_CHANNEL || 'msedge';
const baseURL = process.env.MERIT_E2E_URL || 'https://localhost:7243';
const output = path.resolve('test-results/navigation-zoom', channel);
fs.mkdirSync(output, { recursive: true });

(async () => {
  const results = [];
  for (const zoom of [1, 2, 4]) {
    // Chromium's zoom preference is logarithmic (1.2 per level). Never use a person's browser profile.
    const profile = fs.mkdtempSync(path.join(output, `profile-${zoom}-`));
    fs.mkdirSync(path.join(profile, 'Default'));
    // "x" is Chromium's empty/default storage-partition key; verify the effect below on every page.
    // https://chromium.googlesource.com/chromium/src/+/lkgr/chrome/browser/ui/zoom/chrome_zoom_level_prefs.cc
    fs.writeFileSync(path.join(profile, 'Default/Preferences'), JSON.stringify({ partition: { default_zoom_level: { x: Math.log(zoom) / Math.log(1.2) } } }));
    const context = await chromium.launchPersistentContext(profile, { channel, headless: true, viewport: null,
      ignoreHTTPSErrors: true, baseURL, args: ['--window-size=1280,900'] });
    try {
      const session = JSON.parse(fs.readFileSync('.auth/supervisor.json', 'utf8'));
      await context.addCookies(session.cookies);
      const page = context.pages()[0];
      for (const route of ['/', '/app/queues', '/app/reports', '/app/held', '/app/results', '/app/match-review', '/app/map']) {
        await page.goto(route);
        await page.locator('main h1').first().waitFor();
        await page.waitForLoadState('networkidle');
        assert(!page.url().includes('/account/'), 'Refresh demo sessions before running the zoom evaluation');
        assert.equal(await page.locator('header').getAttribute('data-enhanced-navigation'), 'true');
        const measured = await page.evaluate(() => ({ innerWidth, outerWidth, devicePixelRatio,
          scrollWidth: document.documentElement.scrollWidth, clientWidth: document.documentElement.clientWidth }));
        assert(Math.abs(measured.devicePixelRatio - zoom) < .01, `Requested ${zoom}x zoom was not applied: ${JSON.stringify(measured)}`);
        assert(measured.innerWidth <= 1280 / zoom + 1, 'Browser zoom must reduce the effective layout width');
        assert(measured.scrollWidth <= measured.clientWidth, `${route} has page-wide horizontal overflow at ${zoom}x`);
        if (route === '/' && zoom > 1) {
          const menu = page.getByRole('button', { name: 'Menu', exact: true });
          await menu.press('Enter');
          const group = page.locator('summary').filter({ hasText: /^Registry/ });
          await group.press('Space');
          await page.locator('#merit-nav a[href="/app/reports"]').focus();
          await page.keyboard.press('Escape'); await group.waitFor();
          assert(await group.evaluate(e => document.activeElement === e), 'Group Escape focus return');
          await page.keyboard.press('Escape');
          assert(await menu.evaluate(e => document.activeElement === e), 'Menu Escape focus return');
        }
        if (route === '/app/queues' || route === '/app/reports') {
          const button = page.locator('merit-context-panel button.toggle');
          assert.equal(await button.getAttribute('aria-expanded'), zoom === 1 ? 'true' : 'false');
          await button.press('Enter'); await button.press('Enter');
          assert(await button.evaluate(e => document.activeElement === e), 'Panel toggle focus');
        }
        results.push({ zoom, route, ...measured });
        if (route === '/') await page.screenshot({ path: path.join(output, `${zoom}x.png`), fullPage: true });
      }
    } finally { await context.close(); }
  }
  fs.writeFileSync(path.join(output, 'results.json'), JSON.stringify(results, null, 2));
  console.log(JSON.stringify({ channel, checks: results.length, results }, null, 2));
})().catch(error => { console.error(error); process.exitCode = 1; });
