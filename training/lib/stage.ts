import type { Locator, Page } from '@playwright/test';
import type { OverlayApi, Rect } from './overlay.ts';

/**
 * What a workflow step can do. In 'record' mode every action is paced like a person: the cursor travels on an eased,
 * slightly curved path, presses ripple, typing has a rhythm, the page scrolls smoothly, and native drop-down lists are
 * drawn by the overlay (headless browsers do not paint them). In 'dry' mode the same actions run directly, so a test
 * proves every selector and expectation in seconds without recording.
 * Each action also checks what it needs (the element is visible, the option exists), so a changed screen fails at the
 * step that no longer matches.
 */
export type Mode = 'record' | 'dry';

export interface StepNotes {
  /** Text of the elements the step highlighted: the transcript's description of what is on screen. */
  onScreen: string[];
  /** The actions, in words: "Chooses Lead for Analyte". */
  actions: string[];
  /** Both, in the order they happened, for the transcript. */
  log: { kind: 'action' | 'screen'; text: string }[];
  highlights: { at: number; rect: Rect }[];
  zooms: { at: number; rect: Rect; scale: number }[];
}

export class Stage {
  readonly page: Page;
  readonly mode: Mode;
  private cursor = { x: 960, y: 540 };
  private zoomed = false;
  private turn = 0;
  notes: StepNotes = { onScreen: [], actions: [], log: [], highlights: [], zooms: [] };

  constructor(page: Page, mode: Mode) {
    this.page = page;
    this.mode = mode;
    const v = page.viewportSize();
    if (v) this.cursor = { x: Math.round(v.width * 0.6), y: Math.round(v.height * 0.55) };
  }

  get recording(): boolean {
    return this.mode === 'record';
  }

  async pause(ms: number): Promise<void> {
    if (this.recording && ms > 0) await this.page.waitForTimeout(ms);
  }

  /** Places the cursor without moving it, after a full page load. */
  async placeCursor(): Promise<void> {
    if (!this.recording) return;
    await this.page.mouse.move(this.cursor.x, this.cursor.y);
  }

  async goto(path: string): Promise<void> {
    await this.page.goto(path);
    await this.settle();
    await this.placeCursor();
  }

  /** Waits for the page and any htmx request to finish. */
  async settle(): Promise<void> {
    await this.page.waitForLoadState('networkidle');
    await this.page.waitForFunction(() => !document.querySelector('.htmx-request'), undefined, { timeout: 10_000 }).catch(() => undefined);
  }

  async see(target: Locator, options: { describe?: boolean; timeout?: number } = {}): Promise<void> {
    await target.waitFor({ state: 'visible', timeout: options.timeout ?? 10_000 });
    if (options.describe) this.screen(await describe(target));
  }

  /** Fails the step unless the target's text matches. */
  async expectText(target: Locator, pattern: RegExp, timeout = 10_000): Promise<void> {
    await target.waitFor({ state: 'visible', timeout });
    const deadline = Date.now() + timeout;
    let text = '';
    while (Date.now() < deadline) {
      text = (await target.innerText()).replace(/\s+/g, ' ').trim();
      if (pattern.test(text)) return;
      await this.page.waitForTimeout(100);
    }
    throw new Error(`Expected ${pattern} in "${text.slice(0, 160)}"`);
  }

  async scrollTo(target: Locator, position: 'center' | 'top' = 'center'): Promise<void> {
    await target.waitFor({ state: 'visible' });
    if (!this.recording) { await target.scrollIntoViewIfNeeded(); return; }
    const box = await target.boundingBox();
    const view = this.page.viewportSize()!;
    if (!box) return;
    const margin = 90;
    if (box.y >= margin && box.y + box.height <= view.height - margin) return;
    const want = position === 'top' ? box.y - 120 : box.y + box.height / 2 - view.height * 0.45;
    // Wheel steps over about 0.8 s, as a person scrolls.
    const steps = Math.max(8, Math.min(40, Math.round(Math.abs(want) / 30)));
    for (let i = 1; i <= steps; i++) {
      const delta = want * (ease(i / steps) - ease((i - 1) / steps));
      await this.page.mouse.wheel(0, delta);
      await this.page.waitForTimeout(18);
    }
    await this.page.waitForTimeout(250);
  }

  async highlight(target: Locator | null, options: { describe?: boolean } = {}): Promise<void> {
    if (!target) { await this.call('highlight', null); return; }
    await this.scrollTo(target);
    const box = await target.boundingBox();
    if (!box) throw new Error('The element to highlight is not visible.');
    this.notes.highlights.push({ at: Date.now(), rect: box });
    if (options.describe ?? true) this.screen(await describe(target));
    await this.call('highlight', box);
    await this.pause(450);
  }

  /** Zooms towards the target (about 1.6x, less for a large element) until the step ends. */
  async zoom(target: Locator, scale?: number): Promise<void> {
    await this.scrollTo(target);
    const box = await target.boundingBox();
    const view = this.page.viewportSize()!;
    if (!box) throw new Error('The element to zoom to is not visible.');
    const fit = Math.min((view.width * 0.8) / box.width, (view.height * 0.7) / box.height);
    const s = Math.max(1.15, Math.min(scale ?? 1.6, fit));
    this.notes.zooms.push({ at: Date.now(), rect: box, scale: s });
    if (!this.recording) return;
    await this.call('highlight', null);
    await this.call('zoom', box, s, 800);
    this.zoomed = true;
    await this.page.waitForTimeout(900);
  }

  async unzoom(): Promise<void> {
    if (!this.zoomed) return;
    await this.call('zoom', null, 1, 700);
    this.zoomed = false;
    await this.page.waitForTimeout(750);
  }

  async click(target: Locator): Promise<void> {
    await this.scrollTo(target);
    this.act(`Selects “${await nameOf(target)}”`);
    if (!this.recording) { await target.click(); await this.settle(); return; }
    const box = await this.aim(target);
    await this.pause(140);
    await target.click({ position: { x: box.px, y: box.py }, delay: 70 });
    await this.settle();
    await this.placeCursor();
  }

  /** Types into a field as a person does. Dates are given as yyyy-mm-dd and typed as the date field expects. */
  async type(target: Locator, text: string, options: { clear?: boolean } = {}): Promise<void> {
    await this.scrollTo(target);
    const secret = (await target.getAttribute('type')) === 'password' || (await target.getAttribute('autocomplete')) === 'one-time-code';
    this.act(secret ? `Types into ${await labelOf(target)} (hidden)` : `Types “${text}” in ${await labelOf(target)}`);
    if (!this.recording) { await target.fill(text); return; }
    const kind = await target.getAttribute('type');
    const date = kind === 'date' ? /^(\d{4})-(\d{2})-(\d{2})$/.exec(text) : null;
    // A date field is typed segment by segment from the month, over whatever it held; other fields are cleared first.
    const box = await this.aim(target, date ? 18 : undefined);
    await target.click({ position: { x: box.px, y: box.py }, delay: 60 });
    if (!date && (options.clear ?? true)) {
      await this.page.keyboard.press('ControlOrMeta+A');
      await this.page.keyboard.press('Backspace');
    }
    const keys = date ? `${date[2]}${date[3]}${date[1]}` : text;
    for (const ch of keys) {
      await this.page.keyboard.type(ch);
      await this.page.waitForTimeout(55 + ((ch.charCodeAt(0) * 37) % 60));
    }
    const value = await target.inputValue();
    if (value !== text) throw new Error(`Typed "${text}" but the field holds "${value}".`);
    await this.pause(200);
  }

  /** Chooses an option of a native select by its visible label. */
  async select(target: Locator, label: string): Promise<void> {
    await this.scrollTo(target);
    const labels = await target.locator('option').allInnerTexts();
    const index = labels.findIndex(l => l.trim() === label);
    if (index < 0) throw new Error(`No option "${label}" (options: ${labels.join(', ')})`);
    this.act(`Chooses “${label}” for ${await labelOf(target)}`);
    if (this.recording) {
      await this.aim(target);
      await this.pause(120);
      await target.focus();
      await this.call('ripple', this.cursor.x, this.cursor.y);
      const rect = (await target.boundingBox())!;
      const rows = (await this.call('showOptions', rect, labels.map(l => l.trim()))) as Rect[];
      await this.pause(350);
      const row = rows[index];
      await this.moveTo(row.x + Math.min(row.width / 2, 60), row.y + row.height / 2);
      await this.call('chooseOption', index);
      await this.pause(220);
      await this.call('ripple', this.cursor.x, this.cursor.y);
      await this.call('hideOptions');
    }
    // By position: labels can carry indentation (non-breaking spaces) that an exact label match would miss.
    await target.selectOption({ index });
    await this.settle();
    await this.pause(250);
  }

  /** Types into a lookup (combobox) and chooses the matching suggestion. */
  async pick(target: Locator, text: string, option: Locator): Promise<void> {
    await this.type(target, text);
    await option.waitFor({ state: 'visible', timeout: 10_000 });
    await this.pause(400);
    await this.click(option);
  }

  private act(text: string): void {
    this.notes.actions.push(text);
    this.notes.log.push({ kind: 'action', text });
  }

  private screen(text: string): void {
    this.notes.onScreen.push(text);
    this.notes.log.push({ kind: 'screen', text });
  }

  /** Ends a step: the highlight goes and any zoom returns. */
  async endStep(): Promise<StepNotes> {
    await this.call('highlight', null);
    await this.unzoom();
    const notes = this.notes;
    this.notes = { onScreen: [], actions: [], log: [], highlights: [], zooms: [] };
    return notes;
  }

  /** Moves the cursor to the element and returns the point within it that was aimed at. */
  private async aim(target: Locator, at?: number): Promise<{ px: number; py: number }> {
    const box = await target.boundingBox();
    if (!box) throw new Error('The element is not visible.');
    const tag = await target.evaluate(el => el.tagName.toLowerCase());
    // Fields are aimed at near their start, where a person clicks to type; everything else at its centre.
    const px = at ?? (tag === 'input' || tag === 'textarea' || tag === 'select' ? Math.min(box.width * 0.3, 110) : box.width / 2);
    const py = box.height / 2;
    await this.moveTo(box.x + px, box.y + py);
    return { px, py };
  }

  /** Eased, gently curved travel at about 60 moves a second; the time grows with distance. */
  async moveTo(x: number, y: number): Promise<void> {
    if (!this.recording) { this.cursor = { x, y }; return; }
    const from = this.cursor;
    const dist = Math.hypot(x - from.x, y - from.y);
    if (dist < 2) return;
    const ms = Math.min(1000, Math.max(320, 260 + dist * 0.45));
    const steps = Math.round(ms / 16);
    const bend = (this.turn++ % 2 ? 1 : -1) * Math.min(60, dist * 0.08);
    const nx = -(y - from.y) / dist, ny = (x - from.x) / dist;
    const cx = (from.x + x) / 2 + nx * bend, cy = (from.y + y) / 2 + ny * bend;
    for (let i = 1; i <= steps; i++) {
      const t = ease(i / steps);
      const px = (1 - t) * (1 - t) * from.x + 2 * (1 - t) * t * cx + t * t * x;
      const py = (1 - t) * (1 - t) * from.y + 2 * (1 - t) * t * cy + t * t * y;
      await this.page.mouse.move(px, py);
      await this.page.waitForTimeout(8);
    }
    this.cursor = { x, y };
  }

  /** Calls the overlay drawn in the page (overlay.ts); nothing is drawn in dry runs. */
  private async call(method: keyof OverlayApi, ...args: unknown[]): Promise<unknown> {
    if (!this.recording) return undefined;
    return this.page.evaluate(([m, a]) => {
      const o = window.__meritTraining as unknown as Record<string, (...x: unknown[]) => unknown> | undefined;
      return o ? o[m](...a) : undefined;
    }, [method, args] as const);
  }
}

function ease(t: number): number {
  return t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2;
}

/** The accessible name of a control, near enough for a transcript. */
async function nameOf(target: Locator): Promise<string> {
  const name = await target.evaluate(el => el.getAttribute('aria-label') || (el as HTMLElement).innerText || el.getAttribute('title') || '');
  return name.replace(/\s+/g, ' ').trim().slice(0, 80);
}

/** The label of a form field. */
async function labelOf(target: Locator): Promise<string> {
  const label = await target.evaluate(el => {
    const own = (el as HTMLInputElement).labels?.[0];
    const firstLine = (t: string | null | undefined) => (t ?? '').split('\n')[0].replace(/\*/g, '').trim();
    if (own) return firstLine(own.innerText);
    const by = el.getAttribute('aria-labelledby');
    if (by) return firstLine(document.getElementById(by)?.innerText);
    return el.getAttribute('aria-label') ?? el.getAttribute('name') ?? 'the field';
  });
  return label || 'the field';
}

async function describe(target: Locator): Promise<string> {
  const text = await target.evaluate(el => {
    const label = el.getAttribute('aria-label');
    const value = (el as HTMLInputElement).value;
    const body = (el as HTMLElement).innerText?.trim();
    return body || (value ? `${label ?? ''} ${value}` : label ?? '');
  });
  return text.replace(/\s*\n\s*/g, ' · ').replace(/\s+/g, ' ').trim().slice(0, 400);
}
