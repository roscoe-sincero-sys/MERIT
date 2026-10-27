import { ChangeDetectionStrategy, Component, computed, inject, Injectable, input, OnInit, output, signal } from '@angular/core';
import { ApiClient, problemMessage } from '../../api/api-client';
import { PersonTimelineDto, TimelineEventDto } from '../../api/contracts';
import { Display } from '../../shared/display.service';

const DAY = 86_400_000;
const LANES: { kind: TimelineEventDto['kind']; label: string }[] = [
  { kind: 'result', label: 'Results' },
  { kind: 'place', label: 'Places' },
  { kind: 'job', label: 'Jobs' },
  { kind: 'case', label: 'Cases' },
  { kind: 'pregnancy', label: 'Pregnancy' },
];

interface DrawnEvent {
  e: TimelineEventDto;
  x: number;
  w: number;
  y: number;
  point: boolean;
}

/**
 * Presentational timeline: results, address changes, job changes and case events on one time axis. Receives the
 * events and the visible range; reports the event the user picks. No services, no HTTP.
 */
@Component({
  selector: 'merit-timeline-chart',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <svg class="timeline" [attr.viewBox]="'0 0 ' + width + ' ' + height" role="group" aria-label="Timeline of events">
      @for (lane of lanes; track lane.kind; let i = $index) {
        <text class="lane-label" x="4" [attr.y]="laneY(i) + 14">{{ lane.label }}</text>
        <line class="lane" [attr.x1]="left" [attr.x2]="width - 8" [attr.y1]="laneY(i) + 30" [attr.y2]="laneY(i) + 30" />
      }
      @for (t of ticks(); track t.x) {
        <line class="tick" [attr.x1]="t.x" [attr.x2]="t.x" y1="0" [attr.y2]="height - 16" />
        <text class="tick-label" [attr.x]="t.x + 2" [attr.y]="height - 4">{{ t.label }}</text>
      }
      @for (d of drawn(); track $index) {
        @if (d.point) {
          <circle class="point" [class.venous]="d.e.sampleType === 'venous'" [attr.cx]="d.x" [attr.cy]="d.y" r="6"
                  tabindex="0" role="button" [attr.aria-label]="d.e.date + ': ' + d.e.label" (click)="pick.emit(d.e)" (keydown.enter)="pick.emit(d.e)" />
        } @else {
          <rect class="bar" [class]="'bar bar--' + d.e.kind" [attr.x]="d.x" [attr.y]="d.y - 8" [attr.width]="d.w" height="16" rx="3"
                tabindex="0" role="button" [attr.aria-label]="d.e.date + (d.e.endDate ? ' to ' + d.e.endDate : ' onward') + ': ' + d.e.label"
                (click)="pick.emit(d.e)" (keydown.enter)="pick.emit(d.e)" />
        }
      }
    </svg>
  `,
  styles: `
    .timeline { width: 100%; height: auto; border: 1px solid #dfe1e2; }
    .lane { stroke: #dfe1e2; }
    .lane-label { font-size: 12px; font-weight: 700; fill: #1b1b1b; }
    .tick { stroke: #f0f0f0; }
    .tick-label { font-size: 10px; fill: #565c65; }
    .point { fill: #fff; stroke: #1a4480; stroke-width: 2; cursor: pointer; }
    .point.venous { fill: #1a4480; }
    .bar { fill: #73b3e7; cursor: pointer; }
    .bar--place { fill: #97d4ea; }
    .bar--job { fill: #ffbe2e; }
    .bar--case { fill: #d54309; }
    .bar--pregnancy { fill: #f2938c; }
    circle:focus, rect:focus { outline: 3px solid #2491ff; }
  `,
})
export class TimelineChartComponent {
  readonly events = input.required<TimelineEventDto[]>();
  readonly from = input.required<number>();
  readonly to = input.required<number>();
  readonly pick = output<TimelineEventDto>();

  protected readonly width = 960;
  protected readonly left = 90;
  protected readonly lanes = LANES;
  protected readonly height = LANES.length * 48 + 24;

  protected laneY(i: number): number {
    return i * 48 + 4;
  }

  private x(ms: number): number {
    const span = Math.max(this.to() - this.from(), DAY);
    return this.left + ((ms - this.from()) / span) * (this.width - this.left - 16);
  }

  protected readonly ticks = computed(() => {
    const start = new Date(this.from());
    const end = this.to();
    const months = (end - this.from()) / (30 * DAY);
    const step = months > 36 ? 12 : months > 12 ? 3 : 1;
    const out: { x: number; label: string }[] = [];
    const d = new Date(start.getFullYear(), start.getMonth(), 1);
    while (d.getTime() <= end) {
      if (d.getTime() >= this.from()) {
        out.push({ x: this.x(d.getTime()), label: d.toLocaleDateString('en-US', { month: 'short', year: '2-digit' }) });
      }
      d.setMonth(d.getMonth() + step);
    }
    return out;
  });

  protected readonly drawn = computed<DrawnEvent[]>(() => {
    const results = this.events().filter(e => e.kind === 'result' && e.value !== null);
    const maxValue = Math.max(1, ...results.map(e => e.value ?? 0));
    return this.events()
      .map(e => {
        const lane = LANES.findIndex(l => l.kind === e.kind);
        const start = Date.parse(e.date);
        const end = e.endDate ? Date.parse(e.endDate) : this.to();
        if (end < this.from() || start > this.to()) {
          return null;
        }
        if (e.kind === 'result') {
          const y = this.laneY(lane) + 40 - ((e.value ?? 0) / maxValue) * 34;
          return { e, x: this.x(start), w: 0, y, point: true };
        }
        const x1 = this.x(Math.max(start, this.from()));
        const x2 = this.x(Math.min(end, this.to()));
        return { e, x: x1, w: Math.max(4, x2 - x1), y: this.laneY(lane) + 30, point: false };
      })
      .filter((d): d is DrawnEvent => d !== null);
  });
}

@Injectable()
export class TimelineStore {
  readonly timeline = signal<PersonTimelineDto | null>(null);
  readonly selected = signal<TimelineEventDto | null>(null);
  readonly range = signal<{ from: number; to: number } | null>(null);
  readonly error = signal<string | null>(null);
}

/** Person timeline: zoom in and out, pan, and pick an event for its detail. Route component. */
@Component({
  selector: 'merit-timeline-page',
  imports: [TimelineChartComponent],
  providers: [TimelineStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (store.timeline(); as t) {
      <p><a class="usa-link" [href]="'/people/' + t.personId">Back to {{ t.personName }}</a></p>
      <h1 tabindex="-1">Timeline: {{ t.personName }}</h1>
      @if (store.range(); as r) {
        <div class="controls" role="group" aria-label="Zoom and pan">
          <button class="usa-button usa-button--outline usa-button--small" type="button" (click)="pan(-1)">Earlier</button>
          <button class="usa-button usa-button--outline usa-button--small" type="button" (click)="zoom(0.5)">Zoom in</button>
          <button class="usa-button usa-button--outline usa-button--small" type="button" (click)="zoom(2)">Zoom out</button>
          <button class="usa-button usa-button--outline usa-button--small" type="button" (click)="pan(1)">Later</button>
          <button class="usa-button usa-button--unstyled" type="button" (click)="reset()">Show everything</button>
          <span class="merit-meta">{{ dateText(r.from) }} to {{ dateText(r.to) }}</span>
        </div>
        <merit-timeline-chart [events]="t.events" [from]="r.from" [to]="r.to" (pick)="store.selected.set($event)" />
      }
      <p class="merit-meta">Filled circles are venous results; open circles capillary or unknown. Higher points are higher values.</p>
      @if (store.selected(); as s) {
        <div class="usa-summary-box" role="status">
          <div class="usa-summary-box__body">
            <h2 class="usa-summary-box__heading">{{ s.label }}</h2>
            <p>{{ display.date(s.date) }}@if (s.endDate) { to {{ display.date(s.endDate) }} } &middot; {{ s.kind }}@if (s.detail) { &middot; {{ s.detail }} }</p>
          </div>
        </div>
      }
      <h2>All events</h2>
      <ol class="usa-list">
        @for (e of t.events; track $index) { <li>{{ display.date(e.date) }}@if (e.endDate) { &ndash; {{ display.date(e.endDate) }} }: {{ e.label }}</li> }
      </ol>
    } @else if (store.error(); as e) {
      <div class="usa-alert usa-alert--error usa-alert--slim" role="alert"><div class="usa-alert__body"><p class="usa-alert__text">{{ e }}</p></div></div>
    } @else {
      <p class="merit-indicator">Loading the timeline&hellip;</p>
    }
  `,
  styles: `.controls { display: flex; flex-wrap: wrap; align-items: center; gap: .5rem; margin-bottom: .5rem; }`,
})
export class TimelinePage implements OnInit {
  protected readonly store = inject(TimelineStore);
  private readonly api = inject(ApiClient);
  protected readonly display = inject(Display);
  readonly personId = input.required<string>();
  private full = { from: 0, to: 0 };

  ngOnInit(): void {
    this.api.timeline(this.personId()).subscribe({
      next: t => {
        const times = t.events.flatMap(e => [Date.parse(e.date), e.endDate ? Date.parse(e.endDate) : Date.now()]);
        const from = (times.length ? Math.min(...times) : Date.now() - 365 * DAY) - 30 * DAY;
        const to = Math.max(Date.now(), ...times) + 30 * DAY;
        this.full = { from, to };
        this.store.range.set(this.full);
        this.store.timeline.set(t);
      },
      error: e => this.store.error.set(problemMessage(e)),
    });
  }

  protected zoom(factor: number): void {
    const r = this.store.range();
    if (!r) return;
    const mid = (r.from + r.to) / 2;
    const half = Math.max(((r.to - r.from) * factor) / 2, 15 * DAY);
    this.store.range.set({ from: mid - half, to: mid + half });
  }

  protected pan(direction: number): void {
    const r = this.store.range();
    if (!r) return;
    const shift = ((r.to - r.from) / 3) * direction;
    this.store.range.set({ from: r.from + shift, to: r.to + shift });
  }

  protected reset(): void {
    this.store.range.set(this.full);
  }

  protected dateText(ms: number): string {
    return this.display.date(new Date(ms).toISOString());
  }
}
