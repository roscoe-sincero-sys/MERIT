/**
 * The layer drawn over the application while a video is recorded, injected into every page with addInitScript. It is
 * pointer-events: none, so it never changes what the application receives. It draws:
 *   - a cursor that follows the real mouse events Playwright sends, with a ripple on each press;
 *   - a highlight ring around the element a step is about;
 *   - a zoom (a CSS transform of the page body, so text stays sharp) towards an element;
 *   - a stand-in for a native drop-down list, which headless browsers do not paint into the page;
 *   - masks: password and one-time-code fields, and anything marked data-training-mask, are always obscured.
 * The cursor position survives full page loads through sessionStorage.
 */
export interface Rect { x: number; y: number; width: number; height: number }

export interface OverlayApi {
  highlight(rect: Rect | null): void;
  zoom(rect: Rect | null, scale: number, ms: number): void;
  showOptions(rect: Rect, labels: string[]): Rect[];
  chooseOption(index: number): void;
  hideOptions(): void;
  place(x: number, y: number): void;
  ripple(x: number, y: number): void;
}

declare global {
  interface Window { __meritTraining?: OverlayApi }
}

export function overlayScript(): void {
  if (window.__meritTraining) return;
  const ACCENT = '#c2185b';
  const css = `
    #mt-layer { position: fixed; inset: 0; pointer-events: none; z-index: 2147483647; }
    #mt-cursor { position: absolute; left: 0; top: 0; width: 30px; height: 30px; transform: translate(-100px, -100px);
      filter: drop-shadow(0 2px 3px rgba(0,0,0,.45)); }
    .mt-ripple { position: absolute; width: 44px; height: 44px; margin: -22px 0 0 -22px; border-radius: 50%;
      border: 3px solid ${ACCENT}; animation: mt-ripple .55s ease-out forwards; }
    @keyframes mt-ripple { from { transform: scale(.3); opacity: 1 } to { transform: scale(1.4); opacity: 0 } }
    #mt-ring { position: absolute; border: 4px solid ${ACCENT}; border-radius: 8px; box-shadow: 0 0 0 4px #fff, 0 0 18px rgba(0,0,0,.35);
      opacity: 0; transition: opacity .25s ease, left .3s ease, top .3s ease, width .3s ease, height .3s ease; }
    #mt-options { position: absolute; background: #fff; border: 1px solid #565c65; box-shadow: 0 6px 18px rgba(0,0,0,.25);
      font: 16px "Source Sans Pro Web", "Source Sans Pro", system-ui, sans-serif; color: #1b1b1b; display: none; }
    #mt-options div { padding: 6px 10px; white-space: nowrap; }
    #mt-options div.mt-chosen { background: #005ea2; color: #fff; }
    input[type=password], input[autocomplete="one-time-code"], [data-training-mask] { -webkit-text-security: disc !important; }
    [data-training-mask]:not(input) { filter: blur(6px) !important; }
    body.mt-zoom { transition: transform var(--mt-ms, 700ms) cubic-bezier(.45,0,.2,1); }`;

  function install(): void {
    const style = document.createElement('style');
    style.textContent = css;
    document.documentElement.appendChild(style);
    const layer = document.createElement('div');
    layer.id = 'mt-layer';
    layer.setAttribute('aria-hidden', 'true');
    layer.innerHTML = `<div id="mt-ring"></div><div id="mt-options"></div>
      <svg id="mt-cursor" viewBox="0 0 24 24"><path d="M3 2 L3 19 L7.5 14.8 L10.6 21.5 L13.6 20.2 L10.5 13.6 L16.8 13.6 Z"
        fill="#fff" stroke="#1b1b1b" stroke-width="1.4" stroke-linejoin="round"/></svg>`;
    document.documentElement.appendChild(layer);
    const cursor = layer.querySelector<SVGElement>('#mt-cursor')!;
    const ring = layer.querySelector<HTMLDivElement>('#mt-ring')!;
    const options = layer.querySelector<HTMLDivElement>('#mt-options')!;

    const place = (x: number, y: number) => {
      cursor.style.transform = `translate(${x - 3}px, ${y - 2}px)`;
      try { sessionStorage.setItem('mt-cursor', `${x},${y}`); } catch { /* storage can be unavailable */ }
    };
    try {
      const saved = sessionStorage.getItem('mt-cursor');
      if (saved) { const [x, y] = saved.split(',').map(Number); place(x, y); }
    } catch { /* storage can be unavailable */ }
    window.addEventListener('mousemove', e => place(e.clientX, e.clientY), { capture: true, passive: true });
    const ripple = (x: number, y: number) => {
      const r = document.createElement('div');
      r.className = 'mt-ripple';
      r.style.left = `${x}px`;
      r.style.top = `${y}px`;
      layer.appendChild(r);
      setTimeout(() => r.remove(), 600);
    };
    window.addEventListener('mousedown', e => ripple(e.clientX, e.clientY), { capture: true, passive: true });

    window.__meritTraining = {
      place,
      ripple,
      highlight(rect) {
        if (!rect) { ring.style.opacity = '0'; return; }
        const pad = 8;
        Object.assign(ring.style, { left: `${rect.x - pad}px`, top: `${rect.y - pad}px`,
          width: `${rect.width + pad * 2}px`, height: `${rect.height + pad * 2}px`, opacity: '1' });
      },
      zoom(rect, scale, ms) {
        const body = document.body;
        body.style.setProperty('--mt-ms', `${ms}ms`);
        body.classList.add('mt-zoom');
        if (!rect || scale <= 1) { body.style.transform = ''; return; }
        // Scale about the element's centre and move that centre towards the middle of the window, but never so far
        // that the window shows past the edge of the page.
        const b = body.getBoundingClientRect();
        const vw = window.innerWidth, vh = window.innerHeight;
        const cx = rect.x + rect.width / 2, cy = rect.y + rect.height / 2;
        const clamp = (v: number, lo: number, hi: number) => Math.min(Math.max(v, Math.min(lo, hi)), Math.max(lo, hi));
        const tx = clamp(vw / 2 - cx, vw - cx - scale * (Math.max(b.right, vw) - cx), -cx - scale * (Math.min(b.left, 0) - cx));
        const ty = clamp(vh / 2 - cy, vh - cy - scale * (Math.max(b.bottom, vh) - cy), -cy - scale * (Math.min(b.top, 0) - cy));
        body.style.transformOrigin = `${cx - b.left}px ${cy - b.top}px`;
        body.style.transform = `translate(${tx}px, ${ty}px) scale(${scale})`;
      },
      showOptions(rect, labels) {
        options.innerHTML = labels.map(l => `<div>${l.replace(/&/g, '&amp;').replace(/</g, '&lt;')}</div>`).join('');
        Object.assign(options.style, { left: `${rect.x}px`, top: `${rect.y + rect.height}px`, minWidth: `${rect.width}px`, display: 'block' });
        return [...options.children].map(c => { const b = c.getBoundingClientRect(); return { x: b.x, y: b.y, width: b.width, height: b.height }; });
      },
      chooseOption(index) { [...options.children].forEach((c, n) => c.classList.toggle('mt-chosen', n === index)); },
      hideOptions() { options.style.display = 'none'; options.innerHTML = ''; },
    };
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', install, { once: true });
  else install();
}
