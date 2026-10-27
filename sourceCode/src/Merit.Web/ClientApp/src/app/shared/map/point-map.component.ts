import { afterNextRender, ChangeDetectionStrategy, Component, computed, DestroyRef, ElementRef, inject, input, output, signal, viewChild } from '@angular/core';
import { SERIES_COLORS } from '../chart/series-chart.component';
import { MapAreas } from './map-areas.service';

export interface MapPoint {
  id: string;
  latitude: number;
  longitude: number;
  label: string;
  detail: string;
  /** Category key; its colour comes from its position in the categories input. */
  category: string;
  /** Drawn with a dark ring, e.g. a case in an open cluster. */
  ringed: boolean;
}

export interface MapCategory {
  key: string;
  label: string;
}

/** The fitted view never spans less than this (degrees of latitude), so a tight cluster still shows its county. */
const MIN_SPAN = 0.12;

/**
 * Presentational zoomable point map in SVG (no tiles, no third-party script, nothing leaves MERIT's origin): the
 * jurisdiction's county outlines and names for context (issue #48), then points projected from latitude and longitude,
 * coloured by category (at most three, the all-pairs-safe slots of the chart palette), a ring for flagged points.
 * Zoom with the buttons, the wheel or + and -; pan by dragging or with the arrow keys; every point takes keyboard focus
 * and shows its details. The map is drawn at its own pixel width, as tall as its shape needs up to maxHeight.
 */
@Component({
  selector: 'merit-point-map',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(keydown)': 'onKey($event)' },
  template: `
    <div class="controls" role="group" aria-label="Map zoom and pan">
      <button class="usa-button usa-button--outline usa-button--small" type="button" (click)="zoomBy(1.6)" aria-keyshortcuts="+">Zoom in</button>
      <button class="usa-button usa-button--outline usa-button--small" type="button" (click)="zoomBy(1 / 1.6)" aria-keyshortcuts="-">Zoom out</button>
      <button class="usa-button usa-button--unstyled" type="button" (click)="reset()">Fit all points</button>
      <ul class="legend" aria-label="Legend">
        @for (c of categories(); track c.key; let i = $index) { <li><span class="dot" [style.background]="colors[i]"></span>{{ c.label }}</li> }
        <li><span class="dot ring"></span>{{ ringLabel() }}</li>
        @if (areaPaths().length > 0) { <li><span class="swatch"></span>County</li> }
      </ul>
    </div>
    <div class="plot" #plot tabindex="0" role="application" aria-roledescription="map" [style.height.px]="H()"
         [attr.aria-label]="'Map of ' + points().length + ' points' + (areaPaths().length > 0 ? ' over county outlines' : '') + '. Arrow keys pan, plus and minus zoom, Tab moves between points.'"
         (wheel)="onWheel($event)" (pointerdown)="dragStart($event)" (pointermove)="dragMove($event)" (pointerup)="dragEnd()" (pointerleave)="dragEnd()">
      <svg [attr.viewBox]="'0 0 ' + W() + ' ' + H()" [attr.width]="W()" [attr.height]="H()">
        <g aria-hidden="true">
          @for (a of areaPaths(); track a.id) { <path class="area" [attr.d]="a.d" /> }
          @for (g of grid(); track g.key) {
            @if (g.vertical) {
              <line class="grid" [attr.x1]="g.pos" [attr.x2]="g.pos" y1="0" [attr.y2]="H()" />
              <text class="tick" [attr.x]="g.pos + 3" [attr.y]="H() - 4">{{ g.label }}</text>
            } @else {
              <line class="grid" x1="0" [attr.x2]="W()" [attr.y1]="g.pos" [attr.y2]="g.pos" />
              <text class="tick" x="3" [attr.y]="g.pos - 3">{{ g.label }}</text>
            }
          }
          @for (l of areaLabels(); track l.id) { <text class="area-label" [attr.x]="l.x" [attr.y]="l.y" text-anchor="middle">{{ l.name }}</text> }
        </g>
        @for (p of projected(); track p.point.id) {
          @if (p.point.ringed) { <circle class="ring" [attr.cx]="p.x" [attr.cy]="p.y" r="9" /> }
          <circle class="point" [attr.cx]="p.x" [attr.cy]="p.y" r="6" [attr.fill]="color(p.point.category)" tabindex="0" role="button"
                  [attr.aria-label]="p.point.label + '. ' + p.point.detail" (focus)="focused.set(p.point.id)" (blur)="focused.set(null)"
                  (mouseenter)="focused.set(p.point.id)" (mouseleave)="focused.set(null)" (click)="pick.emit(p.point.id)"
                  (keydown.enter)="pick.emit(p.point.id)" />
        }
      </svg>
      @if (tooltip(); as t) {
        <div class="tooltip" role="status" [style.left.px]="t.x" [style.top.px]="t.y">
          <strong>{{ t.point.label }}</strong><div>{{ t.point.detail }}</div>
        </div>
      }
    </div>
  `,
  styles: `
    .controls { display: flex; flex-wrap: wrap; align-items: center; gap: .5rem 1rem; margin-bottom: .5rem; }
    .legend { display: flex; flex-wrap: wrap; gap: .25rem 1rem; list-style: none; padding: 0; margin: 0 0 0 auto; font-size: .9rem; }
    .dot { display: inline-block; width: .8rem; height: .8rem; border-radius: 50%; margin-right: .35rem; vertical-align: -1px; }
    .dot.ring { border: 2px solid #1b1b1b; background: transparent; }
    .swatch { display: inline-block; width: 1rem; height: .7rem; margin-right: .35rem; vertical-align: -1px; background: #f1f3f6; border: 1px solid #8d9297; }
    .plot { position: relative; overflow: hidden; border: 1px solid #dfe1e2; background: #fcfcfb; touch-action: none; cursor: grab; }
    .plot:focus { outline: 3px solid #2491ff; }
    svg { display: block; }
    .area { fill: #f1f3f6; stroke: #8d9297; stroke-width: 1; stroke-linejoin: round; }
    .area-label { font-size: 11px; fill: #565c65; paint-order: stroke; stroke: #f1f3f6; stroke-width: 3px; }
    .grid { stroke: #ececec; }
    .tick { font-size: 10px; fill: #71767a; }
    .point { stroke: #fcfcfb; stroke-width: 2; cursor: pointer; }
    .point:focus { outline: none; stroke: #1b1b1b; stroke-width: 3; }
    .ring { fill: none; stroke: #1b1b1b; stroke-width: 2; }
    .tooltip { position: absolute; transform: translate(-50%, calc(-100% - 14px)); pointer-events: none; background: #fff; color: #1b1b1b;
               border: 1px solid #a9aeb1; border-radius: 4px; padding: .35rem .5rem; font-size: .85rem; max-width: 20rem; z-index: 1;
               box-shadow: 0 2px 6px rgba(0,0,0,.15); }
  `,
})
export class PointMapComponent {
  readonly points = input.required<MapPoint[]>();
  readonly categories = input.required<MapCategory[]>();
  readonly ringLabel = input('Flagged');
  /** The tallest the map may be, in pixels; a page that is mostly about something else keeps it small. */
  readonly maxHeight = input(560);
  readonly pick = output<string>();

  private readonly areas = inject(MapAreas).areas;
  private readonly plot = viewChild.required<ElementRef<HTMLElement>>('plot');

  /** Drawing size in pixels: the plot's own width, and a height from the 720 by 440 shape, capped at maxHeight. */
  protected readonly W = signal(720);
  protected readonly H = computed(() => Math.min(Math.round((this.W() * 440) / 720), this.maxHeight()));
  protected readonly colors = SERIES_COLORS.slice(0, 3);
  protected readonly focused = signal<string | null>(null);
  /** Zoom factor and centre offset (in projected units) relative to the fitted view. */
  private readonly zoom = signal(1);
  private readonly center = signal<{ x: number; y: number } | null>(null);
  private drag: { x: number; y: number; cx: number; cy: number } | null = null;

  constructor() {
    inject(MapAreas).load();
    const destroyRef = inject(DestroyRef);
    afterNextRender(() => {
      const el = this.plot().nativeElement;
      const measure = () => this.W.set(Math.max(Math.round(el.clientWidth), 200));
      measure();
      const observer = new ResizeObserver(measure);
      observer.observe(el);
      destroyRef.onDestroy(() => observer.disconnect());
    });
  }

  private readonly cosLat = computed(() => {
    const pts = this.points();
    const labelled = this.areas().filter(a => a.label);
    const mean = pts.length ? pts.reduce((s, p) => s + p.latitude, 0) / pts.length
      : labelled.length ? labelled.reduce((s, a) => s + a.label!.latitude, 0) / labelled.length : 39;
    return Math.cos((mean * Math.PI) / 180);
  });

  /**
   * Projected bounds that "fit" shows: every point, padded, and never less than MIN_SPAN across, so the county around a
   * tight cluster is in view. With no points, the whole jurisdiction.
   */
  private readonly bounds = computed(() => {
    let pts = this.points().map(p => this.project(p.latitude, p.longitude));
    if (pts.length === 0) {
      pts = this.areas().flatMap(a => a.rings.flat().map(([lon, lat]) => this.project(lat, lon)));
    }
    if (pts.length === 0) return { minX: -77.2, maxX: -76.3, minY: -39.6, maxY: -38.9 };
    let minX = Math.min(...pts.map(p => p.x)), maxX = Math.max(...pts.map(p => p.x));
    let minY = Math.min(...pts.map(p => p.y)), maxY = Math.max(...pts.map(p => p.y));
    const pad = Math.max(maxX - minX, maxY - minY, 0.02) * 0.15;
    minX -= pad; maxX += pad; minY -= pad; maxY += pad;
    const grow = (lo: number, hi: number): [number, number] => {
      const mid = (lo + hi) / 2;
      const half = Math.max(hi - lo, MIN_SPAN) / 2;
      return [mid - half, mid + half];
    };
    [minX, maxX] = grow(minX, maxX);
    [minY, maxY] = grow(minY, maxY);
    return { minX, maxX, minY, maxY };
  });

  /** Units per pixel at zoom 1, keeping the aspect ratio. */
  private readonly baseScale = computed(() => {
    const b = this.bounds();
    return Math.max((b.maxX - b.minX) / this.W(), (b.maxY - b.minY) / this.H());
  });

  private readonly view = computed(() => {
    const b = this.bounds();
    const c = this.center() ?? { x: (b.minX + b.maxX) / 2, y: (b.minY + b.maxY) / 2 };
    const scale = this.baseScale() / this.zoom();
    return { cx: c.x, cy: c.y, scale };
  });

  /** County outlines as screen paths, with their on-screen extent for placing names. */
  private readonly screenAreas = computed(() => this.areas().map(a => {
    let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
    const d = a.rings.map(ring => 'M' + ring.map(([lon, lat]) => {
      const s = this.toScreen(...this.xy(lat, lon));
      minX = Math.min(minX, s.x); maxX = Math.max(maxX, s.x); minY = Math.min(minY, s.y); maxY = Math.max(maxY, s.y);
      return `${s.x.toFixed(1)},${s.y.toFixed(1)}`;
    }).join('L') + 'Z').join('');
    return { area: a, d, minX, maxX, minY, maxY };
  }));

  /** Only the outlines that reach the visible plot. */
  protected readonly areaPaths = computed(() => this.screenAreas()
    .filter(s => s.maxX >= 0 && s.minX <= this.W() && s.maxY >= 0 && s.minY <= this.H())
    .map(s => ({ id: s.area.id, d: s.d })));

  /** A county's name where it fits inside the county as drawn and inside the plot; "County" is dropped to save room. */
  protected readonly areaLabels = computed(() => this.screenAreas().flatMap(s => {
    if (!s.area.label) return [];
    const name = s.area.name.replace(/ County$/, '');
    const p = this.toScreen(...this.xy(s.area.label.latitude, s.area.label.longitude));
    const fits = s.maxX - s.minX > name.length * 6.5 + 8 && s.maxY - s.minY > 18;
    const inside = p.x > 20 && p.x < this.W() - 20 && p.y > 14 && p.y < this.H() - 14;
    return fits && inside ? [{ id: s.area.id, name, x: p.x, y: p.y + 4 }] : [];
  }));

  /** Points at the same spot (people at one address) fan out on a small circle so each stays visible and focusable. */
  protected readonly projected = computed(() => {
    const screen = this.points().map(point => {
      const p = this.project(point.latitude, point.longitude);
      return { point, ...this.toScreen(p.x, p.y) };
    });
    const groups = new Map<string, typeof screen>();
    for (const s of screen) {
      const key = `${Math.round(s.x / 6)}:${Math.round(s.y / 6)}`;
      groups.set(key, [...(groups.get(key) ?? []), s]);
    }
    return [...groups.values()].flatMap(g => g.length === 1 ? g : g.map((s, i) => ({
      ...s,
      x: s.x + 14 * Math.cos((2 * Math.PI * i) / g.length),
      y: s.y + 14 * Math.sin((2 * Math.PI * i) / g.length),
    })));
  });

  protected readonly tooltip = computed(() => this.projected().find(p => p.point.id === this.focused()) ?? null);

  /** Degree lines at a step that keeps about five on screen. */
  protected readonly grid = computed(() => {
    const v = this.view();
    const spanLon = (this.W() * v.scale) / this.cosLat();
    const step = [0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2].find(s => spanLon / s <= 6) ?? 5;
    const lines: { key: string; vertical: boolean; pos: number; label: string }[] = [];
    const lonMin = (v.cx - (this.W() / 2) * v.scale) / this.cosLat(), lonMax = (v.cx + (this.W() / 2) * v.scale) / this.cosLat();
    for (let lon = Math.ceil(lonMin / step) * step; lon <= lonMax; lon += step) {
      lines.push({ key: 'v' + lon.toFixed(3), vertical: true, pos: this.toScreen(lon * this.cosLat(), 0).x, label: Math.abs(lon).toFixed(2) + '°W' });
    }
    const latMin = -(v.cy + (this.H() / 2) * v.scale), latMax = -(v.cy - (this.H() / 2) * v.scale);
    for (let lat = Math.ceil(latMin / step) * step; lat <= latMax; lat += step) {
      lines.push({ key: 'h' + lat.toFixed(3), vertical: false, pos: this.toScreen(0, -lat).y, label: lat.toFixed(2) + '°N' });
    }
    return lines;
  });

  protected color(category: string): string {
    const i = this.categories().findIndex(c => c.key === category);
    return this.colors[Math.max(0, i) % this.colors.length];
  }

  protected zoomBy(factor: number): void {
    this.zoom.update(z => Math.min(Math.max(z * factor, 0.5), 400));
  }

  protected reset(): void {
    this.zoom.set(1);
    this.center.set(null);
  }

  protected onWheel(event: WheelEvent): void {
    event.preventDefault();
    this.zoomBy(event.deltaY < 0 ? 1.25 : 0.8);
  }

  protected dragStart(event: PointerEvent): void {
    if ((event.target as Element).classList.contains('point')) return;
    const v = this.view();
    this.drag = { x: event.clientX, y: event.clientY, cx: v.cx, cy: v.cy };
    (event.currentTarget as HTMLElement).setPointerCapture?.(event.pointerId);
  }

  protected dragMove(event: PointerEvent): void {
    if (!this.drag) return;
    const s = this.view().scale;
    this.center.set({ x: this.drag.cx - (event.clientX - this.drag.x) * s, y: this.drag.cy - (event.clientY - this.drag.y) * s });
  }

  protected dragEnd(): void {
    this.drag = null;
  }

  protected onKey(event: KeyboardEvent): void {
    const v = this.view();
    const stepPx = 60 * v.scale;
    const move = (dx: number, dy: number) => this.center.set({ x: v.cx + dx, y: v.cy + dy });
    switch (event.key) {
      case '+': case '=': this.zoomBy(1.6); break;
      case '-': case '_': this.zoomBy(1 / 1.6); break;
      case 'ArrowLeft': move(-stepPx, 0); break;
      case 'ArrowRight': move(stepPx, 0); break;
      case 'ArrowUp': move(0, -stepPx); break;
      case 'ArrowDown': move(0, stepPx); break;
      default: return;
    }
    event.preventDefault();
  }

  private project(lat: number, lon: number): { x: number; y: number } {
    return { x: lon * this.cosLat(), y: -lat };
  }

  private xy(lat: number, lon: number): [number, number] {
    const p = this.project(lat, lon);
    return [p.x, p.y];
  }

  private toScreen(x: number, y: number): { x: number; y: number } {
    const v = this.view();
    return { x: this.W() / 2 + (x - v.cx) / v.scale, y: this.H() / 2 + (y - v.cy) / v.scale };
  }
}
