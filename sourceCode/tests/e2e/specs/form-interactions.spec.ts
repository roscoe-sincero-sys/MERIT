import { expect, expectAccessible, test } from './helpers';
import { Page } from '@playwright/test';

async function personPage(page: Page) {
  await page.goto('/search?q=Rivera');
  await page.getByRole('link', { name: /Rivera, Mia/ }).first().click();
}

test('unselected lookup drafts remain distinct after validation and blur closes the popup', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await personPage(page);
  await page.goto(new URL(page.url()).pathname + '/results/new');
  const lab = page.locator('#LabOrganizationId-text');
  const provider = page.locator('#OrderingProviderId-text');
  await lab.fill('Chesapeake');
  await expect(page.locator('#LabOrganizationId-list [data-value]').first()).toBeVisible();
  await lab.press('Tab');
  await expect(lab).toHaveAttribute('aria-expanded', 'false');
  await expect(page.locator('#LabOrganizationId-list')).toBeHidden();
  await expect(page.locator('#LabOrganizationId')).toHaveValue('');
  await provider.fill('Unselected provider draft');
  await page.locator('#result-form button[type=submit]').click();
  await expect(page.locator('#error-summary')).toBeVisible();
  await expect(lab).toHaveValue('Chesapeake');
  await expect(provider).toHaveValue('Unselected provider draft');
  await expect(page.locator('#LabOrganizationId')).toHaveValue('');
  await expect(page.locator('#LabOrganizationId-hint')).toContainText('Choose a match');
});

test('lookup selection survives validation and disabled empty options cannot be selected', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await personPage(page);
  await page.goto(new URL(page.url()).pathname + '/results/new');
  const lab = page.locator('#LabOrganizationId-text');
  await lab.fill('Chesapeake');
  await expect(page.locator('#LabOrganizationId-list [data-value]').first()).toBeVisible();
  await lab.press('ArrowDown');
  await lab.press('Enter');
  const label = await lab.inputValue();
  const id = await page.locator('#LabOrganizationId').inputValue();
  expect(id).not.toBe('');
  await expect(lab).not.toHaveAttribute('aria-activedescendant');
  await page.locator('#result-form button[type=submit]').click();
  await expect(page.locator('#error-summary')).toBeVisible();
  await expect(lab).toHaveValue(label);
  await expect(page.locator('#LabOrganizationId')).toHaveValue(id);
  await lab.fill('zzzz-no-such-laboratory');
  await expect(page.locator('#LabOrganizationId-list [role=option][aria-disabled=true]')).toBeVisible();
  await lab.press('ArrowDown');
  await expect(lab).not.toHaveAttribute('aria-activedescendant');
  await page.locator('#LabOrganizationId-list [role=option]').click({ force: true });
  await expect(page.locator('#LabOrganizationId')).toHaveValue('');
  await expect(page.locator('#result-entry')).not.toHaveAttribute('aria-busy', 'true');
  await expectAccessible(page, 'validation screen and lookup with no matches');
  await lab.press('Escape');
  await expect(page.locator('#LabOrganizationId-list')).toBeHidden();
});

for (const dismiss of ['blur', 'Escape']) {
test(`a lookup response arriving after ${dismiss} cannot reopen the popup`, async ({ asUser }) => {
  const page = await asUser('supervisor');
  let release!: () => void;
  const gate = new Promise<void>(resolve => { release = resolve; });
  await page.route('**/lookup/locations**', async route => {
    await gate;
    await route.fulfill({ contentType: 'text/html', body: '<li role="option" data-value="fake" data-label="Late address">Late address</li>' });
  });
  await personPage(page);
  await page.getByRole('button', { name: 'Add residence', exact: true }).click();
  const lookup = page.locator('#place-LocationId-text');
  const requested = page.waitForRequest('**/lookup/locations**');
  await lookup.fill('Late');
  await requested;
  if (dismiss === 'blur') await page.locator('#place-AddressLine1').click();
  else await lookup.press('Escape');
  const received = page.waitForResponse('**/lookup/locations**');
  release();
  await received;
  await page.waitForLoadState('networkidle');
  await expect(lookup).toHaveAttribute('aria-expanded', 'false');
  await expect(page.locator('#place-LocationId-list')).toBeHidden();
  await expect(page.locator('#place-LocationId')).toHaveValue('');
});
}

test('inline person forms have unique IDs and cancel restores the exact opener', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await personPage(page);
  for (const name of ['Add relationship', 'Add residence', 'Add job', 'Add source', 'Add episode']) {
    await page.getByRole('button', { name, exact: true }).click();
  }
  await expect(page.locator('#pregnancy-form-body')).toBeVisible();
  const duplicates = await page.locator('[id]').evaluateAll(elements => {
    const ids = elements.map(e => e.id);
    return ids.filter((id, index) => ids.indexOf(id) !== index);
  });
  expect(duplicates).toEqual([]);
  await expect(page.locator('#place-LocationId-text')).toBeVisible();
  await page.locator('#places-form').getByRole('button', { name: 'Cancel', exact: true }).focus();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('button', { name: 'Add residence', exact: true })).toBeFocused();
  await page.getByRole('button', { name: 'Add child care or school', exact: true }).click();
  await page.locator('#places-form').getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Add child care or school', exact: true })).toBeFocused();
});

test('annual extracts hide and disable quarter without losing its quarterly value', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/extracts');
  await page.locator('#quarter').selectOption('2');
  await page.locator('#extractType').selectOption('ABLES');
  await expect(page.locator('#quarter')).toBeHidden();
  await expect(page.locator('#quarter')).toBeDisabled();
  expect(await page.locator('#extractType').evaluate(el => new FormData((el as HTMLSelectElement).form!).has('quarter'))).toBe(false);
  await page.locator('#extractType').selectOption('CBLS');
  await expect(page.locator('#quarter')).toBeVisible();
  await expect(page.locator('#quarter')).toBeEnabled();
  await expect(page.locator('#quarter')).toHaveValue('2');
});

for (const removeOpener of [false, true]) {
  test(`dialog save restores focus after section replacement (opener removed: ${removeOpener})`, async ({ asUser }) => {
    const page = await asUser('supervisor');
    await personPage(page);
    const opener = page.getByRole('button', { name: 'Edit details', exact: true }).first();
    const url = await opener.getAttribute('hx-get');
    const response = await page.request.get(new URL(page.url()).pathname + '/places', { headers: { 'HX-Request': 'true', 'HX-Request-Type': 'partial' } });
    const section = await page.evaluate(({ html, remove }) => {
      const clone = new DOMParser().parseFromString(html, 'text/html').querySelector('#section-places')!;
      if (remove) clone.querySelectorAll('[hx-target="#dialog-body"]').forEach(button => button.remove());
      return clone.outerHTML;
    }, { html: await response.text(), remove: removeOpener });
    await opener.click();
    await expect(page.locator('#dialog')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(opener).toBeFocused();
    await opener.click();
    await expect(page.locator('#dialog')).toBeVisible();
    await page.route('**' + url, route => route.request().method() === 'POST'
      ? route.fulfill({ contentType: 'text/html', headers: { 'HX-Trigger': '{"merit:closeDialog":""}', 'Content-Security-Policy': response.headers()['content-security-policy'] }, body: section })
      : route.continue());
    await page.locator('#dialog button[type=submit]').click();
    await expect(page.locator('#dialog')).not.toBeVisible();
    await expect(removeOpener ? page.locator('#places-heading') : opener).toBeFocused();
  });
}
