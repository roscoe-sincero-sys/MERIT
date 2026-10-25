import { angularReady, expectAccessible, test } from './helpers';

// Every key screen of both front ends at tablet width, checked with axe for WCAG 2.1 AA.
const htmxPages = ['/', '/cases', '/investigations', '/intake', '/extracts', '/config/thresholds', '/search?q=Rivera', '/account/profiles',
  '/account/profiles/new', '/reporters', '/reporters/import'];
const angularPages = ['/app/queues', '/app/held', '/app/results', '/app/match-review', '/app/map', '/app/clusters', '/app/reports'];

for (const path of htmxPages) {
  test(`htmx ${path} meets WCAG 2.1 AA`, async ({ asUser }) => {
    const page = await asUser('supervisor');
    await page.goto(path);
    await page.waitForLoadState('networkidle');
    await expectAccessible(page, path);
  });
}

test('htmx /admin/tenants meets WCAG 2.1 AA', async ({ asUser }) => {
  const page = await asUser('admin');
  await page.goto('/admin/tenants');
  await page.waitForLoadState('networkidle');
  await expectAccessible(page, '/admin/tenants');
});

for (const path of angularPages) {
  test(`Angular ${path} meets WCAG 2.1 AA`, async ({ asUser }) => {
    const page = await asUser('supervisor');
    await page.goto(path);
    await angularReady(page);
    await expectAccessible(page, path);
  });
}

test('person page, its timeline and a case page meet WCAG 2.1 AA', async ({ asUser }) => {
  const page = await asUser('supervisor');
  const results = await (await page.request.get('/api/v1/results?pageSize=20')).json() as { items: { personId: string | null }[] };
  const personId = results.items.find(r => r.personId)!.personId!;
  await page.goto(`/people/${personId}`);
  await page.waitForLoadState('networkidle');
  await expectAccessible(page, 'person page');
  await page.goto(`/app/people/${personId}/timeline`);
  await angularReady(page);
  await expectAccessible(page, 'timeline');
  await page.goto('/cases');
  await page.locator('main a[href^="/cases/"]').first().focus();
  await page.keyboard.press('Enter');
  await page.waitForLoadState('networkidle');
  await expectAccessible(page, 'case page');
});
