import { expect, expectAccessible, test } from './helpers';

test('account controls keep the same desktop slot and mobile header height on People', async ({ asUser }) => {
  const page=await asUser('supervisor');
  for(const width of [1280,1040,390]) {
    await page.setViewportSize({width,height:900});
    const geometry: {x:number;y:number;height:number}[]=[];
    for(const path of ['/','/search','/cases','/intake','/app/queues']) {
      await page.goto(path);
      const header=await page.locator('.merit-header').boundingBox();
      if(width>1024) {
        const account=await page.locator('#merit-user').boundingBox();
        geometry.push({x:account!.x,y:account!.y,height:header!.height});
      } else {
        geometry.push({x:0,y:0,height:header!.height});
        await page.getByRole('button',{name:'Menu',exact:true}).click();
        await expect(page.locator('#merit-user')).toBeVisible();
        await expect(page.locator('#merit-user button',{hasText:'Sign out'})).toBeVisible();
      }
      expect(await page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth)).toBe(true);
    }
    for(const value of geometry.slice(1)) {
      expect(Math.abs(value.x-geometry[0].x)).toBeLessThanOrEqual(1);
      expect(Math.abs(value.y-geometry[0].y)).toBeLessThanOrEqual(1);
      expect(Math.abs(value.height-geometry[0].height)).toBeLessThanOrEqual(1);
    }
    await page.locator('.merit-header__who').evaluate(el=>{el.textContent='AlexandriaWithAnExtendedUnbrokenDisplayNameForReflow';});
    await page.locator('.merit-profile-switch__name').evaluate(el=>{el.textContent='RegionalCaseManagementAndIntakeProfileForReflow';});
    expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
  }
  await page.goto('/search');
  await expect(page.locator('#header-search')).toHaveCount(0);
  await page.getByRole('link',{name:'Search people on this page'}).click();
  await expect(page.locator('#search-q')).toBeFocused();
});

test('Cases searches while typing and preserves a filtered URL without replacing the form', async ({ asUser })=>{
  const page=await asUser('supervisor'); await page.goto('/cases');
  const requests:string[]=[];
  page.on('request',request=>{if(new URL(request.url()).pathname==='/cases') requests.push(request.url());});
  await page.locator('#q').fill('NoSuchCase208233');
  await expect.poll(()=>requests.length).toBeGreaterThan(0);
  await expect(page).toHaveURL(/q=NoSuchCase208233/);
  await expect(page.locator('#q')).toHaveValue('NoSuchCase208233');
  await expect(page.locator('#q')).toBeFocused();
  await expect(page.locator('#case-list')).not.toContainText('PB-');
  await page.locator('#q').fill('');
  await expect(page.locator('#case-list a[href^="/cases/"]').first()).toBeVisible();
  await page.locator('#case-list a[href^="/cases/"]').first().click();
  await expect(page.getByRole('navigation',{name:'Breadcrumb',exact:true}).getByRole('link',{name:'Return to filtered cases'})).toBeVisible();
});

test('manual entry starts with a blank collection date and a readable patient review at phone width', async ({asUser})=>{
  const page=await asUser('supervisor');
  const list=await (await page.request.get('/api/v1/results?pageSize=20')).json();
  const patient=list.items.find((r:{personId:string|null})=>r.personId).personId;
  await page.setViewportSize({width:390,height:844});
  await page.goto(`/people/${patient}/results/new`);
  await expect(page.locator('#CollectedDate')).toHaveValue('');
  await expect(page.getByRole('region',{name:'Patient identity'})).toContainText('Date of birth:');
  await expect(page.getByRole('region',{name:'Patient identity'})).toContainText('Before saving');
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
  await expectAccessible(page,'Manual result identity and date review');
  await page.locator('#ValueText').fill('4.2');
  await page.locator('#CollectedDate').fill('2026-09-01');
  await expect(page.locator('[data-result-review="ValueText"]')).toHaveText('4.2');
  await expect(page.locator('[data-result-review="CollectedDate"]')).toHaveText('2026-09-01');
});

test('htmx case closure uses the shared modal, Escape restores focus and acceptance writes once',async({asUser})=>{
  const page=await asUser('supervisor'); await page.goto('/cases');
  await page.locator('tbody a[href^="/cases/"]').first().click();
  await page.locator('#case-close-details summary').click();
  await page.locator('#closureReason').selectOption('other');
  await page.locator('label[for=closureReviewConfirmed]').click();
  const button=page.locator('#case-close-form button[type=submit]');
  let writes=0;
  await page.route('**/cases/*/close',async route=>{writes++;await route.fulfill({status:200,body:'<div id="case-body">Test response</div>',contentType:'text/html'});});
  await button.click();
  const dialog=page.getByRole('dialog',{name:'Confirm action',exact:true});
  await expect(dialog).toBeVisible();
  await expectAccessible(page,'Case closure confirmation');
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();await expect(button).toBeFocused();expect(writes).toBe(0);
  await button.click();await dialog.getByRole('button',{name:'Continue',exact:true}).click();
  await expect.poll(()=>writes).toBe(1);
});
