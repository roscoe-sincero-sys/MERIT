import { ChangeDetectionStrategy, Component, inject, Injectable, OnInit, signal } from '@angular/core';
import { ApiClient, problemMessage } from '../../api/api-client';
import { AnalyteOptionDto, PagedResult, ResultListItemDto } from '../../api/contracts';
import { Display } from '../../shared/display.service';
import { DataGridComponent, GridColumn, GridSort } from '../../shared/grid/data-grid.component';
import { LatestRequest } from '../../shared/latest-request';
import { ViewQuery, pageNumber, choice } from '../../shared/view-query';

@Injectable()
export class ResultsStore {
  readonly analytes = signal<AnalyteOptionDto[]>([]);
  readonly filters = signal({ analyteId: '', from: '', to: '', population: '', minValue: '', unmatched: false });
  readonly sort = signal<GridSort>({ key: 'collectedDate', descending: true });
  readonly page = signal(1);
  readonly result = signal<PagedResult<ResultListItemDto>>({ items: [], totalCount: 0, pageNumber: 1, pageSize: 50 });
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
}

/** The results line list: server-side filtering, sorting and paging over every result in the caller's scope. */
@Component({
  selector: 'merit-results-page',
  imports: [DataGridComponent],
  providers: [ResultsStore, ViewQuery],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h1 tabindex="-1">Results</h1>
    @if (store.error(); as e) { <div class="usa-alert usa-alert--error usa-alert--slim" role="alert"><div class="usa-alert__body"><p class="usa-alert__text">{{ e }}</p></div></div> }
    <form class="filters" (submit)="apply($event)">
      <div><label class="usa-label" for="f-analyte">Analyte</label>
        <select class="usa-select" id="f-analyte" name="analyteId">
          <option value="">All</option>
          @for (a of store.analytes(); track a.analyteId) { <option [value]="a.analyteId" [selected]="a.analyteId === store.filters().analyteId">{{ a.displayName }}</option> }
        </select></div>
      <div><label class="usa-label" for="f-from">Collected from</label><input class="usa-input" id="f-from" name="from" [value]="store.filters().from" type="date" /></div>
      <div><label class="usa-label" for="f-to">Collected to</label><input class="usa-input" id="f-to" name="to" [value]="store.filters().to" type="date" /></div>
      <div><label class="usa-label" for="f-pop">Population</label>
        <select class="usa-select" id="f-pop" name="population" [value]="store.filters().population"><option value="">All</option><option value="child">Child</option><option value="adult">Adult</option><option value="pregnant">Pregnant</option><option value="unknown">Unknown</option></select></div>
      <div><label class="usa-label" for="f-min">Value at least</label><input class="usa-input" id="f-min" name="minValue" [value]="store.filters().minValue" inputmode="decimal" /></div>
      <div class="usa-checkbox"><input class="usa-checkbox__input" id="f-unmatched" name="unmatched" type="checkbox" [checked]="store.filters().unmatched" /><label class="usa-checkbox__label" for="f-unmatched">Unmatched only</label></div>
      <div><button class="usa-button" type="submit">Apply</button></div>
    </form>
    <merit-data-grid gridId="results" caption="Lab results" [columns]="columns" [rows]="store.result().items" [total]="store.result().totalCount"
                     [page]="store.page()" [pageSize]="50" [sort]="store.sort()" [loading]="store.loading()" [rowKey]="key"
                     emptyText="No results match these filters." (sortChange)="onSort($event)" (pageChange)="onPage($event)" />
  `,
  styles: `
    .filters { display: flex; flex-wrap: wrap; gap: .5rem 1rem; align-items: end; margin-bottom: 1rem; }
    .filters .usa-input, .filters .usa-select { max-width: 12rem; }
  `,
})
export class ResultsPage implements OnInit {
  protected readonly store = inject(ResultsStore);
  private readonly api = inject(ApiClient);
  private readonly display = inject(Display);
  private readonly resultsLoad = new LatestRequest();
  private readonly view = inject(ViewQuery);
  protected readonly key = (r: ResultListItemDto): string => r.labResultId;

  protected readonly columns: GridColumn<ResultListItemDto>[] = [
    { key: 'collectedDate', label: 'Collected', sortable: true, nowrap: true, cell: r => this.display.date(r.collectedDate), link: r => this.view.detail(`/results/${r.labResultId}`) },
    { key: 'lastName', label: 'Person', sortable: true, cell: r => r.personName ?? '(unmatched)', link: r => (r.personId ? this.view.detail(`/people/${r.personId}`) : null) },
    { key: 'analyteCode', label: 'Analyte', sortable: true, cell: r => `${this.display.of(r.analyteCode)}, ${this.display.of(r.specimen).toLowerCase()}` },
    { key: 'valueCanonical', label: 'Value', sortable: true, cell: r => `${r.operator === '=' ? '' : r.operator}${r.valueCanonical ?? ''} ${r.unitCanonical}`, badge: r => (r.isIndeterminate ? 'Indeterminate' : null) },
    { key: 'sampleType', label: 'Sample', cell: r => this.display.of(r.sampleType) },
    { key: 'population', label: 'Population', cell: r => this.display.of(r.population) },
    { key: 'status', label: 'Status', cell: r => (r.resultRowStatus === 'valid' ? this.display.of(r.resultStatus)
      : `${this.display.of(r.resultStatus)} (${this.display.of(r.resultRowStatus).toLowerCase()})`) },
    { key: 'labName', label: 'Lab', cell: r => r.labName ?? '' },
    { key: 'receivedUtc', label: 'Received', sortable: true, hidden: true, nowrap: true, cell: r => this.display.dateOf(r.receivedUtc) },
    { key: 'testMethod', label: 'Method', hidden: true, cell: r => this.display.of(r.testMethod) },
  ];

  ngOnInit(): void {
    this.api.analytes().subscribe({ next: a => this.store.analytes.set(a), error: e => this.store.error.set(problemMessage(e)) });
    this.view.watch(q => {
      this.store.filters.set({ analyteId: q.get('analyteId') ?? '', from: q.get('from') ?? '', to: q.get('to') ?? '',
        population: choice(q.get('population'), ['child', 'adult', 'pregnant', 'unknown'], ''), minValue: q.get('minValue') ?? '', unmatched: q.get('unmatched') === 'true' });
      this.store.sort.set({ key: choice(q.get('sort'), ['collectedDate', 'lastName', 'analyteCode', 'valueCanonical', 'receivedUtc'], 'collectedDate'), descending: q.get('desc') !== 'false' });
      this.store.page.set(pageNumber(q.get('page')));
      this.load();
    });
  }

  protected apply(event: Event): void {
    event.preventDefault();
    const f = new FormData(event.target as HTMLFormElement);
    this.store.filters.set({
      analyteId: String(f.get('analyteId') ?? ''), from: String(f.get('from') ?? ''), to: String(f.get('to') ?? ''),
      population: String(f.get('population') ?? ''), minValue: String(f.get('minValue') ?? ''), unmatched: f.get('unmatched') !== null,
    });
    this.store.page.set(1);
    this.saveView();
  }

  protected onSort(s: GridSort): void {
    this.store.sort.set(s);
    this.saveView();
  }

  protected onPage(p: number): void {
    this.store.page.set(p);
    this.saveView();
  }

  private saveView(): void {
    this.view.set({ ...this.store.filters(), sort: this.store.sort().key, desc: this.store.sort().descending, page: this.store.page() });
  }

  private load(): void {
    const f = this.store.filters();
    this.store.error.set(null);
    this.store.loading.set(true);
    this.resultsLoad.run(this.api.results({
      analyteId: f.analyteId, from: f.from, to: f.to, population: f.population, minValue: f.minValue, unmatched: f.unmatched,
      sort: this.store.sort().key, desc: this.store.sort().descending, page: this.store.page(), pageSize: 50,
    }), {
      next: r => { this.store.result.set(r); this.store.loading.set(false); },
      error: e => { this.store.error.set(problemMessage(e)); this.store.loading.set(false); },
    });
  }
}
