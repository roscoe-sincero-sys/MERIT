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
