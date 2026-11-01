import { ChangeDetectionStrategy, Component, computed, inject, Injectable, OnInit, signal } from '@angular/core';
import { ViewQuery } from '../../shared/view-query';
import { ParamMap } from '@angular/router';
import { ApiClient, problemMessage } from '../../api/api-client';
import { AnalyteOptionDto, ReportCell, ReportDto, ReportSummaryDto } from '../../api/contracts';
import { ChartSeries, SeriesChartComponent } from '../../shared/chart/series-chart.component';
import { Display } from '../../shared/display.service';
import { LatestRequest } from '../../shared/latest-request';
import { ContextPanelComponent } from '../../shared/context-panel.component';

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
  imports: [ContextPanelComponent, SeriesChartComponent],
  providers: [ReportsStore, ViewQuery],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h1 tabindex="-1">Reports</h1>
    @if (store.error(); as e) { <div class="usa-alert usa-alert--error usa-alert--slim" role="alert"><div class="usa-alert__body"><p class="usa-alert__text">{{ e }}</p></div></div> }
    <merit-context-panel panelId="report-navigation" label="report library">
      <nav panelNavigation aria-label="Report library">
        <ul class="usa-sidenav">
          @for (r of store.library(); track r.code) {
            <li class="usa-sidenav__item">
              <a [href]="'/app/reports?code=' + r.code" [class.usa-current]="r.code === store.code()" [attr.aria-current]="r.code === store.code() ? 'true' : null" (click)="choose($event, r.code)">{{ r.title }}</a>
            </li>
          }
        </ul>
      </nav>
      <section aria-labelledby="report-title">
        @if (store.report(); as r) {
          <h2 id="report-title">{{ r.title }}</h2>
          <p class="merit-meta">{{ r.description }}</p>
          <form class="filters" (submit)="apply($event)" (input)="editFilters($event)" (change)="editFilters($event)">
            <div><label class="usa-label" for="r-from">From</label><input class="usa-input" id="r-from" name="from" [disabled]="store.loading()" type="date" [value]="r.fromDate" /></div>
            <div><label class="usa-label" for="r-to">To</label><input class="usa-input" id="r-to" name="to" [disabled]="store.loading()" type="date" [value]="r.toDate" /></div>
            @if (r.usesAnalyte) {
              <div><label class="usa-label" for="r-analyte">Analyte</label>
                <select class="usa-select" id="r-analyte" name="analyteId" [disabled]="store.loading()">
                  <option value="">All</option>
                  @for (a of store.analytes(); track a.analyteId) { <option [value]="a.analyteId" [selected]="a.analyteId === r.analyteId">{{ a.displayName }}</option> }
                </select></div>
            }
            <div><button class="usa-button" type="submit" [disabled]="store.loading()">Apply</button></div>
            @if (r.canExport) {
              <div class="exports">
                <a class="usa-button usa-button--outline" [attr.href]="exportReady() ? exportUrl('csv') : null" [attr.aria-disabled]="!exportReady()" download>Export CSV</a>
                <a class="usa-button usa-button--outline" [attr.href]="exportReady() ? exportUrl('xlsx') : null" [attr.aria-disabled]="!exportReady()" download>Export Excel</a>
              </div>
            }
          </form>
          @if (!exportReady() && r.canExport) { <p role="status">Apply filter changes and wait for the report before exporting.</p> }
          @if (chart().ambiguous) { <p role="status">Some chart values are omitted because multiple rows share a category. See the table for individual values; percentages, medians and averages cannot be summed.</p> }

          @if (r.rows.length === 0) {
            <p class="merit-empty">No data in this period.</p>
          } @else {
            <div class="measure">
              <label class="usa-label" for="r-measure">Chart</label>
              <select class="usa-select" id="r-measure" [value]="measureKey()" (change)="chooseMeasure($any($event.target).value)">
                @for (m of r.chart.measures; track m) { <option [value]="m" [selected]="m === measureKey()">{{ label(r, m) }}</option> }
              </select>
            </div>
            <merit-series-chart [kind]="r.chart.kind" [categories]="chart().categories" [series]="chart().series"
                                [colorKeys]="chart().colorKeys" [valueLabel]="label(r, measureKey())" [percent]="isPercent(r, measureKey())" />
            <div class="scroll" tabindex="0" role="region" aria-labelledby="table-caption">
              <table class="usa-table usa-table--borderless usa-table--compact usa-table--striped">
                <caption id="table-caption">{{ r.title }}, {{ display.date(r.fromDate) }} to {{ display.date(r.toDate) }}</caption>
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
    </merit-context-panel>
  `,
  styles: `
    .filters { display: flex; flex-wrap: wrap; gap: .5rem 1rem; align-items: end; margin-bottom: 1rem; }
    .filters .usa-input, .filters .usa-select, .measure .usa-select { max-width: 14rem; }
    .exports { display: flex; gap: .5rem; margin-left: auto; }
    .measure { margin-bottom: .5rem; }
    .scroll { overflow-x: auto; }
    .num { text-align: right; font-variant-numeric: tabular-nums; }
  `,
})
export class ReportsPage implements OnInit {
  protected readonly store = inject(ReportsStore);
  private readonly api = inject(ApiClient);
  protected readonly display = inject(Display);
  private readonly view = inject(ViewQuery);
  private readonly libraryLoad = new LatestRequest();
  private readonly reportLoad = new LatestRequest();
  private reportQuery: string | null = null;
  private readonly draft = signal({ from: '', to: '', analyteId: '' });
  protected readonly exportReady = computed(() => {
    const r = this.store.report(); const f = this.draft();
    return !!r && !this.store.loading() && !this.store.error() && f.from === r.fromDate && f.to === r.toDate && f.analyteId === (r.analyteId ?? '');
  });
  protected editFilters(event: Event): void {
    const form = (event.target as HTMLElement).closest('form');
    if (!form) return;
    const data = new FormData(form);
    this.draft.set({ from: String(data.get('from') ?? ''), to: String(data.get('to') ?? ''), analyteId: String(data.get('analyteId') ?? '') });
  }

  protected readonly measureKey = computed(() => {
    const r = this.store.report();
    const m = this.store.measure();
    return r && m && r.chart.measures.includes(m) ? m : (r?.chart.measures[0] ?? '');
  });

  /** Categories along x, one series per group value (or one series), summed per cell; colours keyed to every group value. */
  protected readonly chart = computed(() => {
    const r = this.store.report();
    if (!r) return { categories: [] as string[], series: [] as ChartSeries[], colorKeys: [] as string[], ambiguous: false };
    const xi = r.columns.findIndex(c => c.key === r.chart.x);
    const gi = r.chart.group ? r.columns.findIndex(c => c.key === r.chart.group) : -1;
    const mi = r.columns.findIndex(c => c.key === this.measureKey());
    const categories = [...new Set(r.rows.map(row => String(row[xi] ?? '')))];
    if (r.columns[xi]?.kind === 'month') categories.sort();
    const groups = gi < 0 ? [this.measureKey()] : [...new Set(r.rows.map(row => String(row[gi] ?? '')))].sort();
    let ambiguous = false;
    const series = groups.map(g => ({
      key: g,
      label: gi < 0 ? this.label(r, g) : this.display.ifCode(g),
      values: categories.map(cat => {
        const cells = r.rows.filter(row => String(row[xi] ?? '') === cat && (gi < 0 || String(row[gi] ?? '') === g))
          .map(row => row[mi]).filter((v): v is number => typeof v === 'number');
        // A category with rows but none for this group is a zero count (no tests that month), not missing data.
        if (cells.length > 1 && !this.isCount(r, this.measureKey())) { ambiguous = true; return null; }
        return cells.length ? cells.reduce((a, b) => a + b, 0) : (gi >= 0 && this.isCount(r, this.measureKey()) ? 0 : null);
      }),
    }));
    return { categories: categories.map(c => this.cell(c, r.columns[xi]?.kind ?? 'text')), series, colorKeys: groups, ambiguous };
  });

  ngOnInit(): void {
    this.view.watch((query, refresh) => this.readView(query, refresh));
    this.api.analytes().subscribe({ next: a => this.store.analytes.set(a), error: () => this.store.analytes.set([]) });
    this.libraryLoad.run(this.api.reports(), {
      next: lib => {
        this.store.library.set(lib);
        this.view.refresh();
      },
      error: e => this.store.error.set(problemMessage(e)),
    });
  }

  private readView(query: ParamMap, refresh: boolean): void {
    this.store.filters.set({ from: query.get('from') ?? '', to: query.get('to') ?? '', analyteId: query.get('analyteId') ?? '' });
    this.store.measure.set(query.get('measure'));
    const library = this.store.library();
    const report = library.find(r => r.code === query.get('code')) ?? library[0];
    if (!report) return;
    const key = JSON.stringify([report.code, this.store.filters()]);
    if (!refresh && key === this.reportQuery) return;
    this.reportQuery = key;
    this.run(report.code);
  }

  protected choose(event: Event, code: string): void {
    event.preventDefault();
    this.store.measure.set(null);
    this.view.set({ code, measure: null });
  }

  protected apply(event: Event): void {
    event.preventDefault();
    if (this.store.loading()) return;
    const f = new FormData(event.target as HTMLFormElement);
    this.store.filters.set({ from: String(f.get('from') ?? ''), to: String(f.get('to') ?? ''), analyteId: String(f.get('analyteId') ?? '') });
    const code = this.store.code();
    if (code) this.view.set({ code, ...this.store.filters() });
  }

  protected chooseMeasure(measure: string): void { this.view.set({ measure }); }

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
    if (kind === 'month') return this.display.month(String(v));
    // A stored code (a population, a classification) shows its words; any other text as it is (issue #37).
    return kind === 'text' ? this.display.ifCode(String(v)) : String(v);
  }

  private run(code: string): void {
    if (this.store.report()?.code !== code) {
      this.store.report.set(null);
    }
    this.store.code.set(code);
    this.store.loading.set(true);
    this.store.error.set(null);
    const f = this.store.filters();
    this.reportLoad.run(this.api.report(code, { from: f.from, to: f.to, analyteId: f.analyteId }), {
      next: r => {
        if (this.store.code() !== code || r.code !== code) return;
        this.store.report.set(r);
        this.draft.set({ from: r.fromDate, to: r.toDate, analyteId: r.analyteId ?? '' });
        this.store.loading.set(false);
      },
      error: e => {
        if (this.store.code() !== code) return;
        this.store.error.set(problemMessage(e));
        this.store.loading.set(false);
      },
    });
  }
}
