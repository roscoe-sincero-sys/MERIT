import { execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { Page } from '@playwright/test';
import { expect, expectAccessible, test } from './helpers';

// axe excludes closed dialogs; inspect ID references in the entire DOM as well.
async function expectResolvedReferences(page: Page) {
  const failures = await page.evaluate(() => {
    const ids = [...document.querySelectorAll('[id]')].map(el => el.id);
    const errors = ids.filter((id, index) => ids.indexOf(id) !== index).map(id => `Duplicate id: ${id}`);
    const attributes = ['aria-labelledby', 'aria-describedby', 'aria-controls', 'aria-owns',
      'aria-activedescendant', 'aria-details', 'aria-errormessage', 'aria-flowto'];
    for (const element of document.querySelectorAll('*')) {
      for (const attribute of attributes) {
        for (const id of (element.getAttribute(attribute) ?? '').split(/\s+/).filter(Boolean)) {
          if (!document.getElementById(id)) errors.push(`${element.id || element.tagName}: ${attribute} references missing ${id}`);
        }
      }
    }
    return errors;
  });
  expect(failures).toEqual([]);
}

test('sign-in ARIA references and keyboard error links remain valid before and after validation', async ({ browser }) => {
  const context = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await context.newPage();
  try {
    await page.goto('/account/signin');
    await expectResolvedReferences(page);
    await expectAccessible(page, 'initial sign-in');
    await expect(page.locator('#UserName')).toHaveAccessibleName('User name');
    await expect(page.locator('#Password')).toHaveAccessibleName('Password');
    await page.getByRole('button', { name: 'Continue', exact: true }).click();
    const summary = page.locator('#account-signin-errors');
    await expect(summary).toBeFocused();
    await expectResolvedReferences(page);
    await expectAccessible(page, 'sign-in required-field errors');
    await expect(page.locator('#UserName')).toHaveAccessibleDescription('Enter your user name.');
    await expect(page.locator('#Password')).toHaveAccessibleDescription('Enter your password.');
    await summary.locator('a[href="#UserName"]').focus();
    await page.keyboard.press('Enter');
    await expect(page.locator('#UserName')).toBeFocused();
    await page.locator('#UserName').fill(`missing-${randomUUID()}`);
    await page.locator('#Password').fill('Incorrect password');
    await page.getByRole('button', { name: 'Continue', exact: true }).click();
    await expect(page.getByRole('alert')).toContainText('The user name or password is not right.');
    await expectResolvedReferences(page);
    await expectAccessible(page, 'invalid sign-in credentials');
  } finally { await context.close(); }
});

test('enrollment ARIA references and code hints remain valid through missing and invalid codes', async ({ browser }) => {
  const database = process.env.MERIT_E2E_DATABASE ?? 'MERIT';
  test.skip(database !== 'MERIT_IT', 'Creates an isolated account only in MERIT_IT.');
  const id = randomUUID();
  const sql = (query: string) => execFileSync('sqlcmd', ['-S', process.env.MERIT_E2E_SERVER ?? 'localhost\\PORTAL', '-E', '-C', '-I', '-d', database, '-b', '-Q', query]);
  sql(`INSERT auth.appUser(appUserId,rootTenantId,homeTenantId,userName,displayName,passwordHash,securityStamp)
    SELECT '${id}',rootTenantId,homeTenantId,N'aria-${id}',N'Enrollment accessibility test',passwordHash,NEWID()
    FROM auth.appUser WHERE userName='admin';`);
  const context = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await context.newPage();
  try {
    await page.goto('/account/signin');
    await page.locator('#UserName').fill(`aria-${id}`);
    await page.locator('#Password').fill('MeritDemo!2026');
    await page.getByRole('button', { name: 'Continue', exact: true }).click();
    await expect(page).toHaveURL(/\/account\/enroll$/);
    const code = page.locator('#Code');
    await expectResolvedReferences(page);
    await expectAccessible(page, 'initial enrollment');
    await expect(code).toHaveAccessibleName('Code from the app');
    await expect(code).toHaveAccessibleDescription(/Enter the current six-digit verification code/);
    await page.getByRole('button', { name: 'Turn on and sign in' }).click();
    const summary = page.locator('#account-enroll-errors');
    await expect(summary).toBeFocused();
    await expectResolvedReferences(page);
    await expectAccessible(page, 'enrollment missing-code errors');
    await expect(code).toHaveAttribute('aria-describedby', 'enroll-code-hint Code-error');
    await expect(code).toHaveAccessibleDescription(/Enter the six-digit code your authenticator app shows/);
    await summary.locator('a[href="#Code"]').focus();
    await page.keyboard.press('Enter');
    await expect(code).toBeFocused();
    await code.fill('123');
    await page.getByRole('button', { name: 'Turn on and sign in' }).click();
    await expect(page.getByRole('alert')).toContainText('That code did not match');
    await expectResolvedReferences(page);
    await expectAccessible(page, 'enrollment invalid-code error');
    await expect(code).toHaveAccessibleDescription(/Enter the current six-digit verification code/);
  } finally {
    await context.close();
    sql(`DELETE auth.appUser WHERE appUserId='${id}';`);
  }
});

test('shared dialog uses its loaded heading and clears that reference when dismissed', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.goto('/search?q=Rivera');
  await page.getByRole('link', { name: /Rivera, Mia/ }).first().click();
  const dialog = page.locator('#dialog');
  await expect(dialog).toHaveAttribute('aria-label', 'Details');
  await expect(dialog).not.toHaveAttribute('aria-labelledby');
  const opener = page.getByRole('button', { name: 'Edit identity', exact: true });
  for (const dismiss of ['Escape', 'Close']) {
    await opener.click();
    await expect(dialog).toBeVisible();
    await expect(dialog).toHaveAccessibleName('Edit identity');
    await expect(dialog).toHaveAttribute('aria-labelledby', 'dialog-title');
    if (dismiss === 'Escape') await page.keyboard.press('Escape');
    else await dialog.getByRole('button', { name: 'Close dialog' }).click();
    await expect(dialog).toBeHidden();
    await expect(page.locator('#dialog-title')).toHaveCount(0);
    await expect(dialog).not.toHaveAttribute('aria-labelledby');
    await expect(dialog).toHaveAttribute('aria-label', 'Details');
    await expect(opener).toBeFocused();
  }
});
