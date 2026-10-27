// Takes the screenshots in docs/images/guide for the user guides. Each image is cropped to whole elements (no table row
// cut in half) and captured at 2x for print, from the running demo, signed in with the e2e sessions (supervisor,
// admin) and signed out for the sign-in page. Widths: 1280 for the header and wide tables, 1040 for ordinary pages,
// 680 for single-column forms; size-figures.py prints each image at the matching scale.
//
// Needs: the web app on the demo database (MERIT_URL, default https://localhost:7243) and fresh sessions in
// sourceCode/tests/e2e/.auth (run any e2e test first). Usage: node docs/guides/tools/capture-screenshots.cjs [names...]
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const REPO = path.resolve(__dirname, '../../..');
const { chromium } = require(path.join(REPO, 'sourceCode/tests/e2e/node_modules/@playwright/test'));
const fs = require('node:fs');
const AUTH = path.join(REPO, 'sourceCode/tests/e2e/.auth');
const OUT = path.join(REPO, 'docs/images/guide');
const SAMPLE = path.join(REPO, 'sourceCode/samples/registry_npi_clia_sample.csv');
const BASE = process.env.MERIT_URL ?? 'https://localhost:7243';
const DPR = 2;
const only = process.argv.slice(2);
const want = name => only.length === 0 || only.includes(name);
const log = [];
const ROW = 1;   // bottom padding when a crop ends on a table row: just past its border, never into the next row

async function context(browser, user, width, height = 900) {
  return browser.newContext({ baseURL: BASE, ignoreHTTPSErrors: true, viewport: { width, height }, deviceScaleFactor: DPR,
    ...(user ? { storageState: `${AUTH}/${user}.json` } : {}) });
}

async function box(page, selector) {
  const loc = page.locator(selector).first();
  await loc.waitFor({ state: 'attached', timeout: 10000 });
  return loc.boundingBox();
}

/** Crops from the top of `top` to the bottom of the lowest `bottom` element, across the `across` element's width. */
async function shot(page, name, { top = 'main h1', bottom, across = 'main', padTop = 16, padBottom = 16, padX = 0, bottomEdge = 'bottom' }) {
  if (!want(name)) return;
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.waitForTimeout(300);
  const t = await box(page, top);
  const bottoms = [];
  for (const sel of [].concat(bottom)) {
    const b = await box(page, sel);
    bottoms.push(bottomEdge === 'top' ? b.y : b.y + b.height);
  }
  const a = await box(page, across);
  const y = Math.max(0, t.y - padTop);
  const clip = { x: Math.max(0, a.x - padX), y, width: a.width + 2 * padX, height: Math.max(...bottoms) + padBottom - y };
  await page.screenshot({ path: path.join(OUT, `${name}.jpg`), type: 'jpeg', quality: 88, clip, fullPage: true });
  log.push(`${name}: ${Math.round(clip.width)}x${Math.round(clip.height)} css px`);
}

async function signedIn(page, user) {
  if (page.url().includes('/account/')) throw new Error(`The ${user} session has expired: run an e2e test to refresh .auth.`);
}

(async () => {
  fs.mkdirSync(OUT, { recursive: true });
  const browser = await chromium.launch({ channel: process.env.MERIT_BROWSER_CHANNEL ?? 'msedge' });

  // ------------------------------------------------------------------ signed out
  let ctx = await context(browser, null, 1040, 800);
  let p = await ctx.newPage();
  await p.goto('/account/signin'); await p.waitForLoadState('networkidle');
  await shot(p, '00-signin', { top: 'header.merit-header', padTop: 0, bottom: '.merit-signin', padBottom: 20, across: 'body' });
  await ctx.close();

  // ------------------------------------------------------------------ supervisor, wide: the header on one line, wide tables
  ctx = await context(browser, 'supervisor', 1280);
  p = await ctx.newPage();
  await p.goto('/'); await p.waitForLoadState('networkidle'); await signedIn(p, 'supervisor');
  await shot(p, '01-home', { top: 'header.merit-header', padTop: 0, bottom: 'section[aria-labelledby="urgent-heading"]', across: 'body' });

  await p.goto('/intake'); await p.waitForLoadState('networkidle');
  await shot(p, '02-intake', { bottom: 'section[aria-labelledby="upload-heading"]', padBottom: 8 });
  await shot(p, '02b-batch-card', { top: '#batches .usa-card__container >> nth=0', padTop: 2, bottom: '#batches .usa-card__container >> nth=0',
    padBottom: 2, across: '#batches .usa-card__container >> nth=0', padX: 2 });

  if (want('03-held') || want('03b-held-correction')) {
    await p.goto('/app/held'); await p.waitForTimeout(1500);
    await p.locator('ul.groups li >> nth=1 >> button').click(); await p.waitForTimeout(900);
    const firstBox = p.locator('table tbody input[type=checkbox]').first();
    await firstBox.focus(); await p.keyboard.press('Space'); await p.waitForTimeout(700);
    await shot(p, '03-held', { bottom: 'table tbody tr >> nth=2', padBottom: ROW });
    await shot(p, '03b-held-correction', { top: '#row-detail', bottom: 'section[aria-labelledby="row-detail"] tbody tr >> nth=5',
      across: 'section[aria-labelledby="row-detail"]', padX: 8, padBottom: ROW });
  }

  const results = await (await p.request.get('/api/v1/results?pageSize=50')).json();
  const ana = results.items.find(r => (r.personName ?? '').includes('Rivera, Ana')) ?? results.items.find(r => r.personId);
  const personId = ana.personId;

  if (want('11-case')) {
    await p.goto(`/people/${personId}`);
    const caseHref = await p.locator('main a[href^="/cases/"]').first().getAttribute('href');
    await p.goto(caseHref); await p.waitForLoadState('networkidle');
    await shot(p, '11-case', { bottom: 'section[aria-labelledby="case-summary"]', padBottom: 12 });
  }

  await p.goto('/app/queues'); await p.waitForTimeout(1800);
  await shot(p, '13-queues', { bottom: 'table tbody tr >> nth=2', padBottom: ROW });

  await p.goto('/app/results'); await p.waitForTimeout(1800);
  await shot(p, '18-results', { bottom: 'table tbody tr >> nth=5', padBottom: ROW });
  await ctx.close();

  // ------------------------------------------------------------------ supervisor, standard width
  ctx = await context(browser, 'supervisor', 1040);
  p = await ctx.newPage();

  if (want('04-match-review')) {
    await p.goto('/app/match-review'); await p.waitForTimeout(1500); await signedIn(p, 'supervisor');
    await p.locator('nav[aria-label="Records to review"] a, nav[aria-label="Records to review"] button').filter({ hasText: 'Rivera, Luis' }).first().click();
    await p.waitForTimeout(1500);
    await shot(p, '04-match-review', { bottom: '[aria-label="Decision"]' });
  }

  await p.goto('/reporters'); await p.waitForLoadState('networkidle');
  await shot(p, '05-reporters', { bottom: 'main table tbody tr >> nth=3', padBottom: ROW });
  // The add form is a grid of columns (one kind's fields at a time), not a single-column form.
  await shot(p, '05b-reporter-form', { top: '#add-h', bottom: 'section[aria-labelledby="add-h"]', padBottom: 8 });

  if (want('06-reporter-check')) {
    await p.goto('/reporters/import');
    await p.getByLabel(/CSV file/).setInputFiles(SAMPLE);
    await p.getByRole('button', { name: 'Check the file' }).click(); await p.waitForLoadState('networkidle');
    await shot(p, '06-reporter-check', { top: '#check-h', bottom: 'section[aria-labelledby="check-h"]', padBottom: 8 });
  }

  await p.goto(`/people/${personId}`); await p.waitForTimeout(1200);
  await shot(p, '08-person', { top: 'main > :first-child', padTop: 8, bottom: 'section[aria-labelledby="places-heading"] tbody tr >> nth=0', padBottom: ROW });

  await p.goto('/investigations'); await p.waitForLoadState('networkidle');
  await shot(p, '12-investigations', { bottom: 'main table' });

  await p.goto('/search?q=Rivera'); await p.waitForLoadState('networkidle');
  await shot(p, '19-search', { bottom: 'main a:has-text("Add a person")' });

  await p.goto('/app/clusters'); await p.waitForTimeout(1500);
  await shot(p, '14-clusters', { bottom: 'main table' });

  await p.goto('/app/map'); await p.waitForTimeout(2500);
  await shot(p, '15-map', { bottom: 'h2:has-text("Cases on this map")', bottomEdge: 'top', padBottom: -4 });

  await p.goto('/app/reports'); await p.waitForTimeout(2500);
  await shot(p, '16-reports', { bottom: '[aria-labelledby="table-caption"]', bottomEdge: 'top', padBottom: -4 });

  await p.goto('/extracts'); await p.waitForLoadState('networkidle');
  await shot(p, '17-extracts', { bottom: 'main button:has-text("Request")' });
  // One ready run: Download, then Mark submitted once the file is sent.
  await shot(p, '17b-extract-runs', { top: '#extract-runs .usa-card__container >> nth=0', padTop: 2,
    bottom: '#extract-runs .usa-card__container >> nth=0', padBottom: 2, across: '#extract-runs .usa-card__container >> nth=0', padX: 2 });
  await ctx.close();

  // ------------------------------------------------------------------ supervisor, form width
  ctx = await context(browser, 'supervisor', 680);
  p = await ctx.newPage();
  await p.goto('/people/new'); await p.waitForLoadState('networkidle'); await signedIn(p, 'supervisor');
  await shot(p, '07-person-new', { top: '#dialog-title', bottom: 'main form button[type=submit]' });
  await p.goto(`/people/${personId}/results/new`); await p.waitForLoadState('networkidle');
  await shot(p, '09-result-new', { bottom: 'main button:has-text("Save result")' });
  await ctx.close();

  // ------------------------------------------------------------------ administrator
  ctx = await context(browser, 'admin', 1040);
  p = await ctx.newPage();
  await p.goto('/admin/tenants'); await p.waitForLoadState('networkidle'); await signedIn(p, 'admin');
  await shot(p, '20-tenants', { bottom: ['details.merit-details', 'main ul.merit-tree'] });
  await ctx.close();
  ctx = await context(browser, 'admin', 1280);
  p = await ctx.newPage();
  await p.goto('/admin/users'); await p.waitForLoadState('networkidle'); await signedIn(p, 'admin');
  await shot(p, '21-users', { bottom: 'main table tbody tr >> nth=2', padBottom: ROW });
  await ctx.close();

  // ------------------------------------------------------------------ Archify diagrams: the drawn content only
  ctx = await browser.newContext({ viewport: { width: 1440, height: 1000 }, deviceScaleFactor: 2 });
  p = await ctx.newPage();
  for (const [file, name] of [['daily-intake_workflow.html', 'diagram-daily-intake'], ['npi-clia-import_workflow.html', 'diagram-npi-clia-import'],
                              ['cbls-extract_lifecycle.html', 'diagram-cbls-extract']]) {
    if (!want(name)) continue;
    await p.goto(pathToFileURL(path.join(REPO, 'docs', file)).href); await p.waitForTimeout(3000);
    // Still: stop the trace animation so the capture shows every element at rest.
    const still = p.getByRole('button', { name: /^Still$/ });
    if (await still.count()) { await still.first().click(); await p.waitForTimeout(500); }
    const clip = await p.evaluate(() => {
      const svg = [...document.querySelectorAll('svg')].sort((a, b) => {
        const ra = a.getBoundingClientRect(), rb = b.getBoundingClientRect();
        return rb.width * rb.height - ra.width * ra.height;
      })[0];
      const sr = svg.getBoundingClientRect();
      let x1 = Infinity, y1 = Infinity, x2 = -Infinity, y2 = -Infinity;
      for (const el of svg.querySelectorAll('rect, path, text, line, circle, polygon, polyline')) {
        const r = el.getBoundingClientRect();
        if (r.width === 0 && r.height === 0) continue;
        if (r.width > sr.width * 0.95 || r.height > sr.height * 0.95) continue;   // background and grid
        x1 = Math.min(x1, r.left); y1 = Math.min(y1, r.top); x2 = Math.max(x2, r.right); y2 = Math.max(y2, r.bottom);
      }
      const pad = 14;
      return { x: Math.max(sr.left, x1 - pad), y: Math.max(sr.top, y1 - pad) + window.scrollY,
               width: Math.min(sr.right, x2 + pad) - Math.max(sr.left, x1 - pad), height: Math.min(sr.bottom, y2 + pad) - Math.max(sr.top, y1 - pad) };
    });
    await p.screenshot({ path: path.join(OUT, `${name}.png`), clip, fullPage: true });
    log.push(`${name}: ${Math.round(clip.width)}x${Math.round(clip.height)} css px`);
  }
  await browser.close();
  console.log(log.join('\n'));
})().catch(e => { console.error(e); process.exit(1); });
