import { expect, test } from './helpers';

// Layout checks at the narrow desktop width the user guides are captured at.

test('intake: a long file name wraps inside its batch card', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.setViewportSize({ width: 1040, height: 900 });
  await page.goto('/intake');
  const heading = page.locator('#batches .usa-card__heading').first();
  await expect(heading).toBeVisible();
  // A name with no spaces, longer than any card is wide (issue #16).
  await heading.evaluate(h => { h.textContent = 'chesapeake_reference_laboratory_match_review_sample_2026_10.csv'; });
  const overflowing = await page.locator('#batches *').evaluateAll(els =>
    els.filter(e => e.scrollWidth > e.clientWidth + 1).map(e => `${e.tagName}.${e.className}`));
  expect(overflowing).toEqual([]);
});

test('reporters: the filter box is a plain input, not a USWDS search box', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.setViewportSize({ width: 1040, height: 900 });
  await page.goto('/reporters');
  const box = page.getByLabel('Name, CLIA number or NPI');
  const kind = page.locator('#kind');
  // Issue #15: the USWDS search-box styles made it 32 px tall, floated left, with no vertical padding, beside a 40 px list.
  const [b, k] = [await box.boundingBox(), await kind.boundingBox()];
  expect(Math.abs(b!.height - k!.height)).toBeLessThanOrEqual(1);
  const style = await box.evaluate(e => { const s = getComputedStyle(e); return { float: s.float, paddingTop: parseFloat(s.paddingTop) }; });
  expect(style.float).toBe('none');
  expect(style.paddingTop).toBeGreaterThan(0);
});

test('reporters: the add form uses the width and shows only the chosen kind\'s fields', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.setViewportSize({ width: 1040, height: 900 });
  await page.goto('/reporters');
  // Issue #18: .usa-form capped the grid at 320 px, one column.
  const form = page.locator('form.merit-reporter-form');
  expect((await form.boundingBox())!.width).toBeGreaterThan(600);
  expect((await form.evaluate(f => getComputedStyle(f).gridTemplateColumns)).split(' ').length).toBeGreaterThan(1);
  await expect(page.locator('#Clia')).toBeVisible();
  await expect(page.locator('#LastName')).toBeHidden();
  await page.locator('#Kind').selectOption('provider');
  await expect(page.locator('#Clia')).toBeHidden();
  await expect(page.locator('#LastName')).toBeVisible();
});

test('extracts: a Download button keeps its padding and fits its label', async ({ asUser }) => {
  const page = await asUser('supervisor');
  await page.setViewportSize({ width: 1040, height: 900 });
  await page.goto('/extracts');
  // Issue #17: as the card body's last child, USWDS removed the button's bottom padding and its second line sat on the border.
  const buttons = await page.locator('#extract-runs a.usa-button').evaluateAll(as => as.map(a => ({
    padding: parseFloat(getComputedStyle(a).paddingBottom), overflows: a.scrollHeight > a.clientHeight + 1 })));
  expect(buttons.length).toBeGreaterThan(0);
  for (const b of buttons) {
    expect(b.padding).toBeGreaterThan(0);
    expect(b.overflows).toBe(false);
  }
});
