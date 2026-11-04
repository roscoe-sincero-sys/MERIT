import type { Browser } from '@playwright/test';
import { VIDEO } from './config.ts';
import { FEATURES, ROLES, type Workflow } from './workflow.ts';

/**
 * The title and recap cards, drawn as HTML by the same browser and saved as 1920 x 1080 PNGs. White on the MERIT header
 * blue (#1a4480) is about 10:1; the accent text on it is above 4.5:1.
 */
const STYLE = `
  * { box-sizing: border-box; margin: 0; }
  body { width: ${VIDEO.width}px; height: ${VIDEO.height}px; background: #1a4480; color: #fff;
    font-family: "Segoe UI", "Source Sans Pro", system-ui, sans-serif; padding: 150px 170px; display: flex; flex-direction: column; }
  .eyebrow { font-size: 30px; letter-spacing: .14em; text-transform: uppercase; color: #d9e8f6; font-weight: 600; }
  h1 { font-size: 84px; line-height: 1.08; margin-top: 30px; font-weight: 700; max-width: 1500px; }
  .tags { margin-top: 44px; display: flex; gap: 18px; }
  .tag { font-size: 30px; padding: 8px 22px; border: 2px solid #d9e8f6; border-radius: 999px; }
  .summary { font-size: 40px; line-height: 1.35; margin-top: 50px; max-width: 1450px; color: #f0f0f0; }
  ul { list-style: none; padding: 0; margin-top: 46px; }
  li { font-size: 44px; line-height: 1.3; margin: 0 0 30px; padding-left: 70px; position: relative; max-width: 1500px; }
  li::before { content: ""; position: absolute; left: 0; top: 10px; width: 38px; height: 20px;
    border-left: 7px solid #73b3e7; border-bottom: 7px solid #73b3e7; transform: rotate(-45deg); }
  .foot { margin-top: auto; font-size: 28px; color: #d9e8f6; display: flex; justify-content: space-between; }`;

function page(body: string): string {
  return `<!doctype html><html lang="en"><head><meta charset="utf-8"><style>${STYLE}</style></head><body>${body}</body></html>`;
}

const esc = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;');

export function titleHtml(w: Workflow): string {
  return page(`<div class="eyebrow">MERIT training</div><h1>${esc(w.title)}</h1>
    <div class="tags"><span class="tag">${esc(ROLES[w.role])}</span><span class="tag">${esc(FEATURES[w.feature])}</span></div>
    <p class="summary">${esc(w.summary)}</p>
    <div class="foot"><span>Synthetic demonstration data only</span><span>Captions and transcript available</span></div>`);
}

export function recapHtml(w: Workflow): string {
  return page(`<div class="eyebrow">Recap · ${esc(w.title)}</div><h1>You have</h1>
    <ul>${w.recap.map(r => `<li>${esc(r)}</li>`).join('')}</ul>
    <div class="foot"><span>MERIT training · ${esc(ROLES[w.role])}</span><span>Synthetic demonstration data only</span></div>`);
}

export async function renderCard(browser: Browser, html: string, file: string): Promise<void> {
  const context = await browser.newContext({ viewport: { width: VIDEO.width, height: VIDEO.height }, deviceScaleFactor: 1 });
  const page = await context.newPage();
  await page.setContent(html, { waitUntil: 'load' });
  await page.screenshot({ path: file });
  await context.close();
}
