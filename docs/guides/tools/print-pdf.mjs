// Prints a guide's HTML source to PDF with Microsoft Edge (headless, DevTools protocol), with a page-number footer.
// Usage: node docs/guides/tools/print-pdf.mjs <guide.html> <output.pdf> "<footer text>"
import { spawn } from 'node:child_process';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const [,, input, output, footer = 'MERIT user guide'] = process.argv;
if (!input || !output) {
  console.error('Usage: node print-pdf.mjs <guide.html> <output.pdf> "<footer text>"');
  process.exit(2);
}
const edgePath = process.env.EDGE_PATH ?? 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';
const profile = mkdtempSync(join(tmpdir(), 'merit-pdf-'));
const port = 9334;
const edge = spawn(edgePath, ['--headless=new', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, 'about:blank']);
const sleep = ms => new Promise(r => setTimeout(r, ms));
try {
  let targets;
  for (let i = 0; i < 40 && !targets; i++) {
    try { targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json(); } catch { await sleep(250); }
  }
  const ws = new WebSocket(targets.find(t => t.type === 'page').webSocketDebuggerUrl);
  await new Promise(r => { ws.onopen = r; });
  let id = 0;
  const pending = new Map();
  ws.onmessage = m => { const d = JSON.parse(m.data); if (d.id && pending.has(d.id)) { pending.get(d.id)(d.result ?? d.error); pending.delete(d.id); } };
  const call = (method, params = {}) => new Promise(r => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
  await call('Page.enable');
  await call('Page.navigate', { url: pathToFileURL(resolve(input)).href });
  await sleep(2500);
  const escaped = footer.replace(/&/g, '&amp;').replace(/</g, '&lt;');
  const pdf = await call('Page.printToPDF', {
    printBackground: true, preferCSSPageSize: true, displayHeaderFooter: true, headerTemplate: '<div></div>',
    footerTemplate: `<div style="font-size:8px;width:100%;text-align:center;color:#565c65;font-family:Segoe UI,Arial">${escaped} &middot; page <span class="pageNumber"></span> of <span class="totalPages"></span></div>`,
  });
  writeFileSync(output, Buffer.from(pdf.data, 'base64'));
  ws.close();
  console.log('wrote', output);
} finally {
  edge.kill();
  await sleep(500);
  rmSync(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
}
