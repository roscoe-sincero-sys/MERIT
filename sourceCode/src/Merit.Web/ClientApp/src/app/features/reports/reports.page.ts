import { ChangeDetectionStrategy, Component, computed, inject, Injectable, input, OnInit, signal } from '@angular/core';
import { Router } from '@angular/router';
import { ApiClient, problemMessage } from '../../api/api-client';
import { AnalyteOptionDto, ReportCell, ReportDto, ReportSummaryDto } from '../../api/contracts';
import { ChartSeries, SeriesChartComponent } from '../../shared/chart/series-chart.component';
import { Display } from '../../shared/display.service';

@Injectable()
export class ReportsStore {
  readonly library = signal<ReportSummaryDto[]>([]);
  readonly analytes = signal<AnalyteOptionDto[]>([]);
  readonly code = signal<string | null>(null);
  readonly filters = signal({ from: '', to: '', analyteId: '' });
  readonly report = signal<ReportDto | null>(null);
  readonly measure = signal<string | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
}

/**
 * The standard report library with filters in one row, a chart of one chosen measure (one axis; switching the measure
 * redraws rather than adding a second scale), the table view of every value, and CSV or Excel export for users with the
 * export permission. All aggregation happens on the server inside the caller's scope.
 */
@Component({
  selector: 'merit-reports-page',
  imports: [SeriesChartComponent],
  providers: [ReportsStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h1 tabindex="-1">Reports</h1>
    @if (store.error(); as e) { <div class="usa-alert usa-alert--error usa-alert--slim" role="alert"><div class="usa-alert__body"><p class="usa-alert__text">{{ e }}</p></div></div> }
    <div class="layout">
      <nav aria-label="Report library">
        <ul class="usa-sidenav">
          @for (r of store.library(); track r.code) {
            <li class="usa-sidenav__item">
              <a [href]="'/app/reports?code=' + r.code" [class.usa-current]="r.code === store.code()" (click)="choose($event, r.code)">{{ r.title }}</a>
            </li>
          }
        </ul>
      </nav>
      <section aria-labelledby="report-title">
        @if (store.report(); as r) {
          <h2 id="report-title">{{ r.title }}</h2>
          <p class="merit-meta">{{ r.description }}</p>
          <form class="filters" (submit)="apply($event)">
            <div><label class="usa-label" for="r-from">From</label><input class="usa-input" id="r-from" name="from" type="date" [value]="r.fromDate" /></div>
            <div><label class="usa-label" for="r-to">To</label><input class="usa-input" id="r-to" name="to" type="date" [value]="r.toDate" /></div>
            @if (r.usesAnalyte) {
              <div><label class="usa-label" for="r-analyte">Analyte</label>
                <select class="usa-select" id="r-analyte" name="analyteId">
                  <option value="">All</option>
                  @for (a of store.analytes(); track a.analyteId) { <option [value]="a.analyteId" [selected]="a.analyteId === r.analyteId">{{ a.displayName }}</option> }
                </select></div>
            }
            <div><button class="usa-button" type="submit">Apply</button></div>
            @if (r.canExport) {
              <div class="exports">
                <a class="usa-button usa-button--outline" [href]="exportUrl('csv')" download>Export CSV</a>
                <a class="usa-button usa-button--outline" [href]="exportUrl('xlsx')" download>Export Excel</a>
              </div>
            }
          </form>

          @if (r.rows.length === 0) {
            <p class="merit-empty">No data in this period.</p>
          } @else {
            <div class="measure">
              <label class="usa-label" for="r-measure">Chart</label>
              <select class="usa-select" id="r-measure" [value]="measureKey()" (change)="store.measure.set($any($event.target).value)">
                @for (m of r.chart.measures; track m) { <option [value]="m">{{ label(r, m) }}</option> }
              </select>
            </div>
            <merit-series-chart [kind]="r.chart.kind" [categories]="chart().categories" [series]="chart().series"
                                [colorKeys]="chart().colorKeys" [valueLabel]="label(r, measureKey())" [percent]="isPercent(r, measureKey())" />
            <div class="scroll" tabindex="0" role="region" aria-labelledby="table-caption">
              <table class="usa-table usa-table--borderless usa-table--compact usa-table--striped">
                <caption id="table-caption">{{ r.title }}, {{ r.fromDate }} to {{ r.toDate }}</caption>
                <thead><tr>@for (c of r.columns; track c.key) { <th scope="col" [class.num]="c.kind === 'number' || c.kind === 'percent'">{{ c.label }}</th> }</tr></thead>
                <tbody>
                  @for (row of r.rows; track $index) {
                    <tr>@for (c of r.columns; track c.key; let i = $index) { <td [class.num]="c.kind === 'number' || c.kind === 'percent'">{{ cell(row[i], c.kind) }}</td> }</tr>
                  }
                </tbody>
              </table>
            </div>
          }
        } @else if (store.loading()) {
          <p class="merit-indicator">Running the report&hellip;</p>
        } @else if (store.library().length === 0) {
          <p class="merit-empty">No reports are available to you.</p>
        }
      </section>
    </div>
  `,
  styles: `
    .layout { display: grid; grid-template-columns: minmax(12rem, 18rem) 1fr; gap: 1.5rem; }
    .filters { display: flex; flex-wrap: wrap; gap: .5rem 1rem; align-items: end; margin-bottom: 1rem; }
    .filters .usa-input, .filters .usa-select, .measure .usa-select { max-width: 14rem; }
    .exports { display: flex; gap: .5rem; margin-left: auto; }
    .measure { margin-bottom: .5rem; }
    .scroll { overflow-x: auto; }
    .num { text-align: right; font-variant-numeric: tabular-nums; }
    @media (max-width: 64em) { .layout { grid-template-columns: 1fr; } }
  `,
})
export class ReportsPage implements OnInit {
  protected readonly store = inject(ReportsStore);
  private readonly api = inject(ApiClient);
  private readonly display = inject(Display);
  private readonly router = inject(Router);
  readonly codeParam = input<string | undefined>(undefined, { alias: 'code' });

  protected readonly measureKey = computed(() => {
    const r = this.store.report();
    const m = this.store.measure();
    return r && m && r.chart.measures.includes(m) ? m : (r?.chart.measures[0] ?? '');
  });

  /** Categories along x, one series per group value (or one series), summed per cell; colours keyed to every group value. */
  protected readonly chart = computed(() => {
    const r = this.store.report();
    if (!r) return { categories: [] as string[], series: [] as ChartSeries[], colorKeys: [] as string[] };
    const xi = r.columns.findIndex(c => c.key === r.chart.x);
    const gi = r.chart.group ? r.columns.findIndex(c => c.key === r.chart.group) : -1;
    const mi = r.columns.findIndex(c => c.key === this.measureKey());
    const categories = [...new Set(r.rows.map(row => String(row[xi] ?? '')))];
    if (r.columns[xi]?.kind === 'month') categories.sort();
    const groups = gi < 0 ? [this.measureKey()] : [...new Set(r.rows.map(row => String(row[gi] ?? '')))].sort();
    const series = groups.map(g => ({
      key: g,
      label: gi < 0 ? this.label(r, g) : this.display.ifCode(g),
      values: categories.map(cat => {
        const cells = r.rows.filter(row => String(row[xi] ?? '') === cat && (gi < 0 || String(row[gi] ?? '') === g))
          .map(row => row[mi]).filter((v): v is number => typeof v === 'number');
        // A category with rows but none for this group is a zero count (no tests that month), not missing data.
        return cells.length ? cells.reduce((a, b) => a + b, 0) : (gi >= 0 && this.isCount(r, this.measureKey()) ? 0 : null);
      }),
    }));
    return { categories, series, colorKeys: groups };
  });

  ngOnInit(): void {
    this.api.analytes().subscribe({ next: a => this.store.analytes.set(a), error: () => this.store.analytes.set([]) });
    this.api.reports().subscribe({
      next: lib => {
        this.store.library.set(lib);
        const first = lib.find(r => r.code === this.codeParam()) ?? lib[0];
        if (first) this.run(first.code);
      },
      error: e => this.store.error.set(problemMessage(e)),
    });
  }

  protected choose(event: Event, code: string): void {
    event.preventDefault();
    this.store.measure.set(null);
    void this.router.navigate([], { queryParams: { code }, replaceUrl: true });
    this.run(code);
  }

  protected apply(event: Event): void {
    event.preventDefault();
    const f = new FormData(event.target as HTMLFormElement);
    this.store.filters.set({ from: String(f.get('from') ?? ''), to: String(f.get('to') ?? ''), analyteId: String(f.get('analyteId') ?? '') });
    const code = this.store.code();
    if (code) this.run(code);
  }

  protected exportUrl(format: 'csv' | 'xlsx'): string {
    const r = this.store.report();
    return r ? this.api.reportExportUrl(r.code, { from: r.fromDate, to: r.toDate, analyteId: r.analyteId, format }) : '#';
  }

  protected label(r: ReportDto, key: string): string {
    return r.columns.find(c => c.key === key)?.label ?? key;
  }

  private isCount(r: ReportDto, key: string): boolean {
    return r.columns.find(c => c.key === key)?.kind === 'number' && /Count$/.test(key);
  }

  protected isPercent(r: ReportDto, key: string): boolean {
    return r.columns.find(c => c.key === key)?.kind === 'percent';
  }

  protected cell(v: ReportCell, kind: string): string {
    if (v === null || v === undefined) return '—';
    if (typeof v === 'number') {
      const text = Number.isInteger(v) ? v.toLocaleString('en-US') : v.toLocaleString('en-US', { maximumFractionDigits: 1 });
      return kind === 'percent' ? text + '%' : text;
    }
    // A stored code (a population, a classification) shows its words; any other text as it is (issue #37).
    return kind === 'text' ? this.display.ifCode(String(v)) : String(v);
  }

  private run(code: string): void {
    this.store.code.set(code);
    this.store.loading.set(true);
    this.store.error.set(null);
    const f = this.store.filters();
    this.api.report(code, { from: f.from, to: f.to, analyteId: f.analyteId }).subscribe({
      next: r => { this.store.report.set(r); this.store.loading.set(false); },
      error: e => { this.store.error.set(problemMessage(e)); this.store.loading.set(false); },
    });
  }
}
