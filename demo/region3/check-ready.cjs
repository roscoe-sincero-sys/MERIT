const { chromium, request } = require('../../sourceCode/tests/e2e/node_modules/playwright');
const jsQR = require('../../sourceCode/tests/e2e/node_modules/jsqr');
const { PNG } = require('../../sourceCode/tests/e2e/node_modules/pngjs');
(async () => {
  const browser = await chromium.launch({ channel: 'msedge' });
  try {
    for (const user of ['region3demo','marylanddemo','countydemo','platformdemo']) {
      const context = await browser.newContext({ ignoreHTTPSErrors: true });
      try {
        const page = await context.newPage();
        await page.goto('https://localhost:7243/account/signin');
        await page.locator('#UserName').fill(user);
        await page.locator('#Password').fill('MeritDemo!2026');
        await page.getByRole('button', {name:'Continue',exact:true}).click();
        await page.waitForURL(/\/account\/enroll$/);
        const png = PNG.sync.read(await page.locator('.merit-qr svg').screenshot());
        const decoded = jsQR(new Uint8ClampedArray(png.data),png.width,png.height);
        if (!decoded) throw new Error(`${user}: displayed QR is unreadable`);
        const key = (await page.locator('code').first().innerText()).replace(/\s/g,'');
        if (new URL(decoded.data).searchParams.get('secret') !== key) throw new Error('QR/manual key mismatch');
        console.log(`${user}: password accepted; enrollment page and QR ready. MFA left unenrolled.`);
      } finally { await context.close(); }
    }
    const api = await request.newContext();
    try {
      const response = await api.get('http://localhost:5280/health');
      if (!response.ok()) throw new Error(`Worker health: ${response.status()}`);
      console.log(`Worker health: ${response.status()}`);
    } finally { await api.dispose(); }
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode=1; });
