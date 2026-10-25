import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';

export interface ChartSeries {
  /** Identity of the series; its colour comes from its position in colorKeys, never from its rank in the data. */
  key: string;
  label: string;
  values: (number | null)[];
}

/**
 * Categorical palette, fixed order, validated for colour-vision deficiency (dataviz validator, light surface): blue,
 * orange, aqua, yellow, magenta, green. Three slots are below 3:1 contrast, so every chart ships with a legend, direct
 * labels and a table view on the same screen. Series past six fold into the table only.
 */
export const SERIES_COLORS = ['#2a78d6', '#eb6834', '#1baf7a', '#eda100', '#e87ba4', '#008300'];

interface Hover {
  x: number;
  y: number;
  category: string;
  rows: { label: string; value: string; color: string }[];
}

/**
 * Presentational line or grouped-bar chart in SVG: one y axis, recessive grid, 2px lines with 8px markers, bars with
 * 4px rounded data ends and 2px gaps, a crosshair tooltip on lines and a per-bar tooltip on bars, keyboard focus on
 * every mark. Inputs only; no services.
 */
@Component({
  selector: 'merit-series-chart',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <figure class="chart">
      @if (shown().length > 1) {
        <ul class="legend" aria-label="Legend">
          @for (s of shown(); track s.key) {
            <li><span class="swatch" [class.line]="kind() === 'line'" [style.background]="color(s.key)"></span>{{ s.label }}</li>
          }
        </ul>
      }
      <div class="plot" (mouseleave)="hover.set(null)">
        <svg [attr.viewBox]="'0 0 ' + W + ' ' + H" role="img" [attr.aria-label]="ariaLabel()">
          @for (t of yTicks(); track t.value) {
            <line class="grid" [attr.x1]="L" [attr.x2]="W - R" [attr.y1]="t.y" [attr.y2]="t.y" />
            <text class="tick" [attr.x]="L - 6" [attr.y]="t.y + 4" text-anchor="end">{{ t.label }}</text>
          }
          <line class="axis" [attr.x1]="L" [attr.x2]="W - R" [attr.y1]="H - B" [attr.y2]="H - B" />
          @for (c of categories(); track $index; let i = $index) {
            @if (showCategoryLabel(i)) {
              <text class="tick" [attr.x]="cx(i)" [attr.y]="H - B + 16" text-anchor="middle">{{ short(c) }}</text>
            }
          }

          @if (kind() === 'line') {
            @if (hover(); as h) { <line class="crosshair" [attr.x1]="h.x" [attr.x2]="h.x" [attr.y1]="T" [attr.y2]="H - B" /> }
            @for (s of shown(); track s.key) {
              <path class="series-line" [attr.d]="linePath(s)" [attr.stroke]="color(s.key)" />
              @for (v of s.values; track $index; let i = $index) {
                @if (v !== null) {
                  <circle class="marker" [attr.cx]="cx(i)" [attr.cy]="y(v)" r="4" [attr.fill]="color(s.key)" />
                }
              }
            }
            @if (shown().length <= 4) {
              @for (d of directLabels(); track d.key) { <text class="direct" [attr.x]="d.x" [attr.y]="d.y">{{ d.label }}</text> }
            }
            @for (c of categories(); track $index; let i = $index) {
              <rect class="hit" role="img" [attr.x]="cx(i) - step() / 2" [attr.y]="T" [attr.width]="step()" [attr.height]="H - B - T"
                    tabindex="0" [attr.aria-label]="describe(i)" (mouseenter)="hoverAt(i)" (focus)="hoverAt(i)" (blur)="hover.set(null)" />
            }
          } @else {
            @for (c of categories(); track $index; let i = $index) {
              @for (s of shown(); track s.key; let j = $index) {
                @if (s.values[i] !== null) {
                  <path class="bar" role="img" [attr.d]="barPath(i, j, s.values[i]!)" [attr.fill]="color(s.key)" tabindex="0"
                        [attr.aria-label]="c + ', ' + s.label + ': ' + format(s.values[i])"
                        (mouseenter)="hoverBar(i, s)" (focus)="hoverBar(i, s)" (blur)="hover.set(null)" />
                }
              }
            }
          }
        </svg>
        @if (hover(); as h) {
          <div class="tooltip" role="status" [style.left.%]="(h.x / W) * 100" [style.top.%]="(h.y / H) * 100">
            <strong>{{ h.category }}</strong>
            @for (r of h.rows; track r.label) {
              <div><span class="swatch" [style.background]="r.color"></span>{{ r.label }}: {{ r.value }}</div>
            }
          </div>
        }
      </div>
      @if (hidden() > 0) { <figcaption class="merit-meta">{{ hidden() }} more series are in the table below.</figcaption> }
    </figure>
  `,
  styles: `
    .chart { margin: 0 0 1rem; background: #fcfcfb; border: 1px solid #dfe1e2; padding: .75rem; }
    .legend { display: flex; flex-wrap: wrap; gap: .25rem 1rem; list-style: none; padding: 0; margin: 0 0 .5rem; font-size: .9rem; color: #1b1b1b; }
    .swatch { display: inline-block; width: .8rem; height: .8rem; border-radius: 2px; margin-right: .35rem; vertical-align: -1px; }
    .swatch.line { height: .2rem; vertical-align: 3px; }
    .plot { position: relative; }
    svg { width: 100%; height: auto; display: block; overflow: visible; }
    .grid { stroke: #e6e6e6; stroke-width: 1; }
    .axis { stroke: #a9aeb1; stroke-width: 1; }
    .tick { font-size: 11px; fill: #565c65; }
    .direct { font-size: 11px; fill: #1b1b1b; }
    .series-line { fill: none; stroke-width: 2; stroke-linejoin: round; stroke-linecap: round; }
    .marker { stroke: #fcfcfb; stroke-width: 2; }
    .crosshair { stroke: #71767a; stroke-width: 1; stroke-dasharray: 3 3; }
    .hit { fill: transparent; cursor: crosshair; }
    .hit:focus, .bar:focus { outline: 3px solid #2491ff; }
    .bar { cursor: pointer; }
    .tooltip { position: absolute; transform: translate(-50%, calc(-100% - 12px)); pointer-events: none; background: #fff; color: #1b1b1b;
               border: 1px solid #a9aeb1; border-radius: 4px; padding: .35rem .5rem; font-size: .85rem; white-space: nowrap;
               box-shadow: 0 2px 6px rgba(0,0,0,.15); z-index: 1; }
  `,
})
export class SeriesChartComponent {
  readonly kind = input<'line' | 'bar'>('line');
  readonly categories = input.required<string[]>();
  readonly series = input.required<ChartSeries[]>();
  /** Every series key that can appear, in a stable order, so a filter never repaints the survivors. */
  readonly colorKeys = input<string[]>([]);
  readonly valueLabel = input('Value');
  readonly percent = input(false);

  protected readonly W = 720;
  protected readonly H = 300;
  protected readonly L = 48;
  protected readonly R = 90;
  protected readonly T = 12;
  protected readonly B = 28;
  protected readonly hover = signal<Hover | null>(null);

  protected readonly shown = computed(() => this.series().slice(0, SERIES_COLORS.length));
  protected readonly hidden = computed(() => Math.max(0, this.series().length - SERIES_COLORS.length));

  private readonly max = computed(() => {
    const values = this.shown().flatMap(s => s.values).filter((v): v is number => v !== null);
    const m = Math.max(0, ...values);
    return this.percent() ? Math.max(100, m) : niceCeiling(m);
  });

  protected readonly step = computed(() => (this.W - this.L - this.R) / Math.max(this.categories().length, 1));

  protected readonly yTicks = computed(() => {
    const max = this.max();
    const integers = this.shown().every(s => s.values.every(v => v === null || Number.isInteger(v)));
    const fractions = integers && max <= 4 ? Array.from({ length: max + 1 }, (_, i) => i / max) : [0, 0.25, 0.5, 0.75, 1];
    return fractions.map(f => ({ value: f * max, y: this.y(f * max), label: this.format(f * max) }));
  });

  /** Series names at the end of each line, nudged apart so two lines ending together do not overprint. */
  protected readonly directLabels = computed(() => {
    const labels = this.shown().map(s => {
      const i = this.lastIndex(s);
      return i === null ? null : { key: s.key, label: s.label, x: this.cx(i) + 8, y: this.y(s.values[i]!) + 4 };
    }).filter((l): l is { key: string; label: string; x: number; y: number } => l !== null).sort((a, b) => a.y - b.y);
    for (let i = 1; i < labels.length; i++) {
      if (labels[i].y - labels[i - 1].y < 13) labels[i].y = labels[i - 1].y + 13;
    }
    return labels;
  });

  protected readonly ariaLabel = computed(() =>
    `${this.kind() === 'line' ? 'Line' : 'Bar'} chart of ${this.valueLabel()} for ${this.shown().map(s => s.label).join(', ')}. The table below has every value.`);

  protected color(key: string): string {
    const keys = this.colorKeys().length ? this.colorKeys() : this.series().map(s => s.key);
    const i = keys.indexOf(key);
    return SERIES_COLORS[(i < 0 ? 0 : i) % SERIES_COLORS.length];
  }

  protected cx(i: number): number {
    return this.L + this.step() * (i + 0.5);
  }

  protected y(v: number): number {
    return this.H - this.B - (v / (this.max() || 1)) * (this.H - this.B - this.T);
  }

  protected linePath(s: ChartSeries): string {
    let d = '';
    let pen = false;
    s.values.forEach((v, i) => {
      if (v === null) { pen = false; return; }
      d += `${pen ? 'L' : 'M'}${this.cx(i).toFixed(1)},${this.y(v).toFixed(1)}`;
      pen = true;
    });
    return d;
  }

  protected lastIndex(s: ChartSeries): number | null {
    for (let i = s.values.length - 1; i >= 0; i--) if (s.values[i] !== null) return i;
    return null;
  }

  /** Bars in a group, 2px apart, with a 4px rounded data end and a square baseline. */
  protected barPath(i: number, j: number, v: number): string {
    const n = this.shown().length;
    const group = this.step() * 0.8;
    const gap = 2;
    const w = Math.max(2, (group - gap * (n - 1)) / n);
    const x = this.cx(i) - group / 2 + j * (w + gap);
    const top = this.y(v);
    const base = this.H - this.B;
    const r = Math.min(4, w / 2, Math.max(0, base - top));
    return `M${x},${base}V${top + r}Q${x},${top} ${x + r},${top}H${x + w - r}Q${x + w},${top} ${x + w},${top + r}V${base}Z`;
  }

  protected showCategoryLabel(i: number): boolean {
    const n = this.categories().length;
    const every = Math.ceil(n / 12);
    return i % every === 0 || i === n - 1;
  }

  protected short(c: string): string {
    return c.length > 14 ? c.slice(0, 13) + '…' : c;
  }

  protected hoverAt(i: number): void {
    this.hover.set({
      x: this.cx(i), y: this.T + 10, category: this.categories()[i],
      rows: this.shown().map(s => ({ label: s.label, value: this.format(s.values[i]), color: this.color(s.key) })),
    });
  }

  protected hoverBar(i: number, s: ChartSeries): void {
    const v = s.values[i];
    this.hover.set({ x: this.cx(i), y: v === null ? this.T : this.y(v), category: this.categories()[i],
      rows: [{ label: s.label, value: this.format(v), color: this.color(s.key) }] });
  }

  protected describe(i: number): string {
    return this.categories()[i] + ': ' + this.shown().map(s => `${s.label} ${this.format(s.values[i])}`).join(', ');
  }

  protected format(v: number | null): string {
    if (v === null) return 'no data';
    const text = Number.isInteger(v) ? v.toLocaleString('en-US') : v.toLocaleString('en-US', { maximumFractionDigits: 1 });
    return this.percent() ? text + '%' : text;
  }
}

function niceCeiling(m: number): number {
  if (m <= 0) return 1;
  const p = Math.pow(10, Math.floor(Math.log10(m)));
  for (const f of [1, 2, 2.5, 5, 10]) {
    if (m <= f * p) return f * p;
  }
  return 10 * p;
}
