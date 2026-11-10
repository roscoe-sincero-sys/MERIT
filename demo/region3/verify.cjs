// Local smoke test: enrolls only the four new demo accounts. Reset their MFA after this check.
const { chromium } = require('../../sourceCode/tests/e2e/node_modules/playwright');
const { createHmac } = require('node:crypto');
const { writeFileSync } = require('node:fs');
function totp(secret) {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  let bits = '';
  for (const c of secret.replace(/[\s=]/g, '').toUpperCase()) bits += alphabet.indexOf(c).toString(2).padStart(5,'0');
  const key = Buffer.from(bits.match(/.{8}/g).map(b=>parseInt(b,2)));
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(Date.now()/30000)));
  const hash = createHmac('sha1',key).update(counter).digest();
  return ((hash.readUInt32BE(hash[19]&15)&0x7fffffff)%1000000).toString().padStart(6,'0');
}
(async()=>{
  const browser = await chromium.launch({channel:'msedge'});
  const results=[];
  try {
    for(const user of ['region3demo','marylanddemo','countydemo','platformdemo']) {
      const context=await browser.newContext({baseURL:'https://localhost:7243',ignoreHTTPSErrors:true});
      const page=await context.newPage();
      await page.goto('/account/signin');
      await page.locator('#UserName').fill(user);
      await page.locator('#Password').fill('MeritDemo!2026');
      await page.getByRole('button',{name:'Continue',exact:true}).click();
      await page.waitForURL(/\/account\/enroll/);
      const secret=(await page.locator('code').first().innerText()).trim();
      await page.locator('#Code').fill(totp(secret));
      await page.getByRole('button',{name:'Turn on and sign in',exact:true}).click();
      await page.getByText('Save your recovery codes').waitFor();
      await page.locator('#saved-codes').focus();
      await page.keyboard.press('Space');
      await page.getByRole('button',{name:'Continue to MERIT'}).click();
      await page.waitForURL(url=>!url.pathname.startsWith('/account'));
      const me=await context.request.get('/api/v1/me');
      if(me.status()!==200) throw new Error(`${user}: me=${me.status()}`);
      const paths=user==='platformdemo'?['/admin/users','/admin/tenants']:
        ['/','/search','/intake','/cases','/investigations','/app/queues','/app/held','/app/match-review',
         '/app/results','/app/clusters','/app/map','/app/reports','/extracts','/reporters','/admin/users',
         '/admin/tenants','/admin/audit','/config/settings','/config/thresholds','/config/analytes'];
      const statuses={};
      for(const path of paths) {
        const response=await context.request.get(path);
        statuses[path]=response.status();
        if(response.status()!==200) throw new Error(`${user} ${path}: ${response.status()}`);
      }
      if(user!=='platformdemo') {
        await page.goto('/intake');
        if(await page.locator('.merit-nav-group').count()===0) throw new Error('Enhanced navigation missing');
      }
      const boundaryChecks={};
      for(const [label,id] of Object.entries({Virginia:'a54de270-56e4-f62e-cb05-0771a62dc1c0',
          BaltimoreCounty:'1011b884-618d-c705-40c7-1b69c265ba8a',PrinceGeorgesCounty:'07d5ae62-3830-72a0-cb27-2b18c9454cc6'})) {
        const response=await context.request.get(`/people/${id}`,{maxRedirects:0});
        const expected=user==='platformdemo'?302:(label==='Virginia'?(user==='region3demo'?200:404):
          label==='PrinceGeorgesCounty'&&user==='countydemo'?404:200);
        if(response.status()!==expected) throw new Error(`${user} ${label}: expected ${expected}, got ${response.status()}`);
        boundaryChecks[label]=response.status();
      }
      const usersResponse=await context.request.get('/admin/users');
      const usersHtml=await usersResponse.text();
      for(const excluded of user==='countydemo'?['marylanddemo','region3demo','platformdemo']:
          user==='marylanddemo'?['region3demo','platformdemo']:user==='region3demo'?['platformdemo']:[]) {
        if(usersHtml.includes(`>${excluded}<`)) throw new Error(`${user} sees out-of-scope account ${excluded}`);
      }
      results.push({user,me:await me.json(),pages:statuses,boundaryChecks});
      console.log(`${user}: password, MFA, ${paths.length} pages, and patient access boundaries passed.`);
      await context.close();
    }
    writeFileSync(__dirname+'/browser-validation.json',JSON.stringify(results,null,2));
  } finally { await browser.close(); }
})().catch(e=>{console.error(e);process.exitCode=1});
