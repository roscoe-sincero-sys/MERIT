import { Locator, Page } from '@playwright/test';
import { angularReady, answerConfirmation, expect, expectAccessible, test } from './helpers';

const id=(n:number)=>`00000000-0000-0000-0000-${String(n).padStart(12,'0')}`;
async function openPatient(page:Page):Promise<void> {
  await page.goto('/search?q=Rivera');
  await page.getByRole('link',{name:/Rivera, Mia/}).first().click();
}
async function readableText(svg:Locator):Promise<void> {
  const sizes=await svg.locator('text').evaluateAll(elements=>elements.map(el=>{
    const text=el as SVGTextElement, matrix=text.getScreenCTM();
    return parseFloat(getComputedStyle(el).fontSize)*Math.hypot(matrix?.c??0,matrix?.d??0);
  }));
  expect(sizes.length).toBeGreaterThan(0);
  expect(Math.min(...sizes)).toBeGreaterThanOrEqual(11.9);
}

test('patient charts keep readable labels and records keep their actions and status on phones',async({asUser})=>{
  const page=await asUser('supervisor'); await openPatient(page);
  for(const width of [320,390,1280]) {
    await page.setViewportSize({width,height:900});
    const charts=page.locator('svg[data-chart-width]'); await expect(charts.first()).toBeVisible();
    await expect.poll(async()=>charts.first().evaluate(el=>Math.abs(el.clientWidth-(el as SVGSVGElement).viewBox.baseVal.width))).toBeLessThan(1);
    for(const chart of await charts.all()) await readableText(chart);
    if(width<640) {
      for(const section of ['results','places','relationships']) {
        const table=page.locator(`#section-${section} [data-phone-cards]`);
        if(!await table.count()) continue;
        const row=table.locator('tbody tr').first(); await expect(row).toHaveCSS('display','block');
        expect(await table.evaluate(el=>el.scrollWidth<=el.clientWidth+1)).toBe(true);
        for(const cell of await row.locator('td').all()) await expect(cell).toBeVisible();
      }
      await expect(page.locator('#section-results tbody tr').first()).toContainText('Laboratory result status:');
      expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true);
    } else await expect(page.locator('#section-results tbody tr').first()).toHaveCSS('display','table-row');
  }
  await page.setViewportSize({width:390,height:844}); await expectAccessible(page,'Patient phone cards and responsive charts');
});

test('phone Cases starts with results, announces filters and preserves a filtered return',async({asUser})=>{
  const page=await asUser('supervisor'); await page.setViewportSize({width:390,height:844}); await page.goto('/cases');
  const details=page.locator('[data-responsive-filters]'); const summary=details.locator('summary');
  await expect(details).not.toHaveAttribute('open');
  const first=page.locator('#case-list tbody tr').first(); await expect(first).toBeVisible();
  expect((await first.boundingBox())!.y).toBeLessThan(844);
  await summary.focus();await page.keyboard.press('Space');await expect(details).toHaveAttribute('open','');
  await page.locator('#classification').selectOption('confirmed');
  await page.getByRole('button',{name:'Apply filters',exact:true}).click();
  await expect(page).toHaveURL(/classification=confirmed/);
  await expect(page.locator('[data-filter-count]')).toHaveText('1');
  await summary.focus();await page.keyboard.press('Space');await expect(details).not.toHaveAttribute('open');await expect(summary).toBeFocused();
  await page.locator('#case-list a[href^="/cases/"]').first().click();
  await page.getByRole('link',{name:'Return to filtered cases',exact:true}).click();
  await expect(page.locator('#classification')).toHaveValue('confirmed');
  await summary.click();await page.getByRole('link',{name:'Clear filters',exact:true}).click();
  await expect(page.locator('#classification')).toHaveValue('');
});

test('End place requires a deliberate date and confirmation can cancel without writing',async({asUser})=>{
  const page=await asUser('supervisor');await page.setViewportSize({width:390,height:844});await openPatient(page);
  const form=page.locator('#section-places form[hx-post$="/end"]').first();
  await expect(form).toHaveCount(1);await expect(form).toBeHidden();
  await form.locator('..').locator('summary').click();await expect(form.locator('input[name=endDate]')).toHaveValue('');
  await form.locator('input[name=endDate]').fill('2026-11-01');
  let writes=0;
  await page.route('**/people/*/places/*/end',async route=>{writes++;await route.fulfill({contentType:'text/html',body:'<section id="section-places">Recorded test response</section>'});});
  const cancel=answerConfirmation(page,false,['2026-11-01','place history']);
  await form.getByRole('button',{name:'End place',exact:true}).click();await cancel;expect(writes).toBe(0);
  const accept=answerConfirmation(page,true,['2026-11-01']);
  await form.getByRole('button',{name:'End place',exact:true}).click();await accept;await expect.poll(()=>writes).toBe(1);
});

test('Held records exposes mobile cards and the exact Match review action from its inspector',async({asUser})=>{
  const page=await asUser('supervisor'); const row=id(231);
  await page.route('**/api/v1/held/**',route=>route.fulfill({json:new URL(route.request().url()).pathname.endsWith('/overview')
    ? {groups:[{reasonCode:'uncertainMatch',rowCount:1,urgentCount:0,oldestHeldUtc:'2026-01-01T00:00:00Z'}],totalCount:1}
    : {items:[{stagingRowId:row,intakeBatchId:id(1),rowNumber:1,fileName:'review.csv',reported:{value:'7.2',unit:'ug/dL',lastName:'Sample'},corrected:{},reasonCodes:['uncertainMatch'],reasons:'Person match needed',heldSinceUtc:'2026-01-01T00:00:00Z'}],totalCount:1,pageNumber:1,pageSize:50}}));
  await page.setViewportSize({width:390,height:844});await page.goto('/app/held');await angularReady(page);
  await expect(page.locator('tr[cdk-row]').first()).toHaveCSS('display','block');
  await page.getByRole('button',{name:/Inspect \/ edit/}).first().click();
  const link=page.getByRole('link',{name:'Review the match for row 1',exact:true});await expect(link).toBeVisible();
  await expect(link).toHaveAttribute('href',`/app/match-review?row=${row}`);
  await expect(page.getByRole('button',{name:'Save and release this row',exact:true})).toBeDisabled();
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true);
  expect((await page.getByRole('button',{name:'All held rows',exact:true}).boundingBox())!.height).toBeGreaterThanOrEqual(44);
  await link.click();await expect(page).toHaveURL(new RegExp(`row=${row}`));
});

test('map targets are 24px, nearby markers have a chooser, and keyboard focus still roves',async({asUser})=>{
  const page=await asUser('supervisor');
  await page.route('**/api/v1/cases/map**',route=>route.fulfill({json:[1,2,3].map(n=>({exposureCaseId:id(n),personId:id(n+10),caseNumber:`PB-2026-00000${n}`,personName:`Example ${n}`,
    classification:'confirmed',latitude:39,longitude:-76,openedDate:'2026-01-01',analyteCode:'PB',population:'child',addressLabel:'Example address',inOpenCluster:false}))}));
  await page.setViewportSize({width:320,height:844});await page.goto('/app/map');await angularReady(page);
  const points=page.locator('merit-point-map .point');await expect(points).toHaveCount(3);
  for(const point of await points.all()) { const box=(await point.boundingBox())!;expect(box.width).toBeGreaterThanOrEqual(23.9);expect(box.height).toBeGreaterThanOrEqual(23.9); }
  await expect(page.locator('merit-point-map .point[tabindex="0"]')).toHaveCount(1);
  await points.first().click();const chooser=page.getByRole('region',{name:'Choose a case at this location'});await expect(chooser).toBeVisible();
  await chooser.getByRole('button',{name:/PB-2026-000002/}).click();await expect(page.locator('.usa-summary-box')).toContainText('PB-2026-000002');
  await points.first().focus();await page.keyboard.press('End');
  const active=page.locator('merit-point-map .point[tabindex="0"]');await expect(active).toBeFocused();
  const rightmost=await points.evaluateAll(elements=>Math.max(...elements.map(e=>e.getBoundingClientRect().x)));
  expect(Math.abs((await active.boundingBox())!.x-rightmost)).toBeLessThan(1);
  await page.keyboard.press('Home');await expect(active).toBeFocused();
  const leftmost=await points.evaluateAll(elements=>Math.min(...elements.map(e=>e.getBoundingClientRect().x)));
  expect(Math.abs((await active.boundingBox())!.x-leftmost)).toBeLessThan(1);
  const selectedId=await active.getAttribute('data-point-id');
  await page.keyboard.press('Space');await expect(page.locator('.usa-summary-box')).toContainText(`PB-2026-00000${selectedId!.slice(-1)}`);
  await readableText(page.locator('merit-point-map .plot > svg'));
  expect((await page.getByRole('button',{name:'Fit all points',exact:true}).boundingBox())!.height).toBeGreaterThanOrEqual(44);
});

test('a dense timeline has one plot tab stop, chronological arrows and screen-sized targets',async({asUser})=>{
  const page=await asUser('supervisor');
  await page.route('**/api/v1/people/*/timeline',route=>route.fulfill({json:{personId:id(1),personName:'Example patient',events:Array.from({length:40},(_,n)=>({kind:'result',date:`2026-01-${String(1+Math.floor(n/2)).padStart(2,'0')}`,label:`Result ${n}`,value:n,unit:'ug/dL',sampleType:'venous'}))}}));
  await page.setViewportSize({width:320,height:844});await page.goto(`/app/people/${id(1)}/timeline`);await angularReady(page);
  const points=page.locator('.event-marker');await expect(points).toHaveCount(40);await expect(page.locator('.event-marker[tabindex="0"]')).toHaveCount(1);
  await points.first().focus();await page.keyboard.press('ArrowRight');await expect(points.nth(1)).toBeFocused();
  await page.keyboard.press('End');await expect(points.last()).toBeFocused();await page.keyboard.press('Space');await expect(page.getByRole('status')).toContainText('Result 39');
  await page.keyboard.press('Home');await expect(points.first()).toBeFocused();await page.keyboard.press('Tab');
  expect(await page.evaluate(()=>!!document.activeElement?.closest('merit-timeline-chart'))).toBe(false);
  for(const point of await points.all()) { const box=(await point.boundingBox())!;expect(box.width).toBeGreaterThanOrEqual(23.9);expect(box.height).toBeGreaterThanOrEqual(23.9); }
  await readableText(page.locator('svg.timeline'));
});

test('Reports labels stay readable on phones without end labels or page overflow',async({asUser})=>{
  const page=await asUser('supervisor');await page.goto('/app/reports');await angularReady(page);
  for(const width of [320,390,1280]) {
    await page.setViewportSize({width,height:900});const chart=page.locator('merit-series-chart svg');await expect(chart).toBeVisible();
    await expect.poll(async()=>chart.evaluate(el=>Math.abs(el.clientWidth-(el as SVGSVGElement).viewBox.baseVal.width))).toBeLessThan(1);
    await readableText(chart);
    if(width<600) await expect(chart.locator('.direct')).toHaveCount(0);
    expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true);
  }
  await expect(page.getByRole('link',{name:'Show the numbers',exact:true})).toHaveAttribute('href','#report-numbers');
});

test('phone audit uses 25-event pages and keeps record identity with each event',async({asUser})=>{
  const page=await asUser('supervisor');await page.setViewportSize({width:390,height:844});await page.goto('/admin/audit');
  await expect(page).toHaveURL(/pageSize=25/);const rows=page.locator('[data-phone-cards] tbody tr');
  await expect(rows.first()).toHaveCSS('display','block');expect(await rows.count()).toBeLessThanOrEqual(25);
  await expect(rows.first()).toContainText('When:');await expect(rows.first()).toContainText('Record:');
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true);
  await page.getByRole('link',{name:'Next 25 events',exact:true}).click();await expect(page).toHaveURL(/page=2/);await expect(page).toHaveURL(/pageSize=25/);
});

test('authenticator entry opens a numeric keypad while recovery entry remains text',async({browser})=>{
  const context=await browser.newContext({ignoreHTTPSErrors:true});const page=await context.newPage();
  try {
    await page.goto('/account/signin');await page.locator('#UserName').fill('supervisor');await page.locator('#Password').fill('MeritDemo!2026');
    await page.getByRole('button',{name:'Continue',exact:true}).click();await expect(page).toHaveURL(/\/account\/mfa$/);
    await expect(page.locator('#Code')).toHaveAttribute('inputmode','numeric');
    await page.getByRole('link',{name:"Can't access your authenticator?",exact:true}).click();
    await expect(page.locator('#Code')).toHaveAttribute('inputmode','text');
  } finally {await context.close();}
});

test('combobox suggestions fit the visual viewport after the mobile keyboard reduces it',async({asUser})=>{
  const page=await asUser('supervisor');await page.setViewportSize({width:390,height:844});
  await page.addInitScript(()=>{
    const viewport=Object.assign(new EventTarget(),{offsetTop:0,offsetLeft:0,height:400,width:390});
    Object.defineProperty(window,'visualViewport',{configurable:true,value:viewport});
  });
  await openPatient(page);await page.getByRole('link',{name:'Enter a result',exact:true}).first().click();
  const input=page.locator('#LabOrganizationId-text');await input.fill('Chesapeake');
  const list=page.locator('#LabOrganizationId-list');await expect(list).toBeVisible();
  await expect.poll(async()=>{const box=await list.boundingBox();return !!box&&box.y>=0&&box.y+box.height<=400;}).toBe(true);
  await input.press('ArrowDown');await input.press('Enter');await expect(page.locator('#LabOrganizationId')).not.toHaveValue('');await expect(list).toBeHidden();
});
