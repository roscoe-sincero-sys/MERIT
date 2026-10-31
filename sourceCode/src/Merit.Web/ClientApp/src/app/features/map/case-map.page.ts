import { ChangeDetectionStrategy, Component, computed, inject, Injectable, OnInit, signal } from '@angular/core';
import { ApiClient, problemMessage } from '../../api/api-client';
import { AnalyteOptionDto, CaseMapPointDto } from '../../api/contracts';
import { MapCategory, MapPoint, PointMapComponent } from '../../shared/map/point-map.component';
import { Display } from '../../shared/display.service';
import { LatestRequest } from '../../shared/latest-request';
import { ViewQuery } from '../../shared/view-query';

export const CLASSIFICATIONS: MapCategory[] = [
  { key: 'confirmed', label: 'Confirmed' },
  { key: 'suspect', label: 'Suspect' },
  { key: 'notACase', label: 'Not a case' },
];

@Injectable()
export class CaseMapStore {
  readonly analytes = signal<AnalyteOptionDto[]>([]);
  readonly cases = signal<CaseMapPointDto[]>([]);
  readonly filters = signal({ analyteId: '', from: '', to: '', openOnly: true });
  readonly selected = signal<string | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  readonly located = computed(() => this.cases().filter(c => c.latitude !== null && c.longitude !== null));
  readonly unlocated = computed(() => this.cases().filter(c => c.latitude === null || c.longitude === null));
}

/** Case map: each case at the primary residence on its open date, coloured by classification, ringed when in an open cluster. */
@Component({
  selector: 'merit-case-map-page',
  imports: [PointMapComponent],
  providers: [CaseMapStore, ViewQuery],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h1 tabindex="-1">Case map</h1>
    @if (store.error(); as e) { <div class="usa-alert usa-alert--error usa-alert--slim" role="alert"><div class="usa-alert__body"><p class="usa-alert__text">{{ e }}</p></div></div> }
    <form class="filters" (submit)="apply($event)">
      <div><label class="usa-label" for="m-analyte">Analyte</label>
        <select class="usa-select" id="m-analyte" name="analyteId">
          <option value="">All</option>
          @for (a of store.analytes(); track a.analyteId) { <option [value]="a.analyteId" [selected]="a.analyteId === store.filters().analyteId">{{ a.displayName }}</option> }
        </select></div>
      <div><label class="usa-label" for="m-from">Opened from</label><input class="usa-input" id="m-from" name="from" type="date" [value]="store.filters().from" /></div>
      <div><label class="usa-label" for="m-to">Opened to</label><input class="usa-input" id="m-to" name="to" type="date" [value]="store.filters().to" /></div>
      <div class="usa-checkbox"><input class="usa-checkbox__input" id="m-open" name="openOnly" type="checkbox" [checked]="store.filters().openOnly" /><label class="usa-checkbox__label" for="m-open">Open cases only</label></div>
      <div><button class="usa-button" type="submit">Apply</button></div>
    </form>
    <p class="merit-meta">{{ store.located().length }} case(s) on the map; {{ store.unlocated().length }} without coordinates (listed below).
      Addresses are geocoded by the jurisdiction's geocoder or placed by staff. County outlines give context; there is no street map.</p>
    <merit-point-map [points]="points()" [categories]="categories" ringLabel="In an open cluster" (pick)="store.selected.set($event)" />

    @if (selectedCase(); as c) {
      <div class="usa-summary-box" role="status">
        <div class="usa-summary-box__body">
          <h2 class="usa-summary-box__heading"><a class="usa-link" [href]="view.detail('/cases/' + c.exposureCaseId)">{{ c.caseNumber }}</a>: {{ c.personName }}</h2>
          <p>{{ display.of(c.analyteCode) }}, {{ display.of(c.population).toLowerCase() }}, {{ display.of(c.classification).toLowerCase() }}, opened {{ display.date(c.openedDate) }}@if (c.highestValue !== null) {, highest {{ c.highestValue }} {{ c.highestUnit }}}.
            {{ c.addressLabel }}@if (c.inOpenCluster) { &middot; in an open cluster}</p>
        </div>
      </div>
    }

    <h2>Cases on this map</h2>
    <div class="scroll" tabindex="0" role="region" aria-label="Cases on this map, as a table">
      <table class="usa-table usa-table--borderless usa-table--compact">
        <thead><tr><th scope="col">Case</th><th scope="col">Person</th><th scope="col">Classification</th><th scope="col">Opened</th><th scope="col">Address</th><th scope="col">Located</th></tr></thead>
        <tbody>
          @for (c of store.cases(); track c.exposureCaseId) {
            <tr>
              <td><a class="usa-link" [href]="view.detail('/cases/' + c.exposureCaseId)">{{ c.caseNumber }}</a></td>
              <td><a class="usa-link" [href]="view.detail('/people/' + c.personId)">{{ c.personName }}</a></td>
              <td>{{ display.of(c.classification) }}@if (c.inOpenCluster) { (cluster)}</td>
              <td class="nowrap">{{ display.date(c.openedDate) }}</td>
              <td>{{ c.addressLabel ?? 'No primary residence' }}</td>
              <td>{{ c.latitude === null ? 'No coordinates' : (c.geocodeQuality ?? 'yes') }}</td>
            </tr>
          }
        </tbody>
      </table>
    </div>
  `,
  styles: `
    .filters { display: flex; flex-wrap: wrap; gap: .5rem 1rem; align-items: end; margin-bottom: 1rem; }
    .filters .usa-input, .filters .usa-select { max-width: 12rem; }
    .scroll { overflow-x: auto; }
  `,
})
export class CaseMapPage implements OnInit {
  protected readonly store = inject(CaseMapStore);
  private readonly api = inject(ApiClient);
  protected readonly display = inject(Display);
  private readonly mapLoad = new LatestRequest();
  protected readonly view = inject(ViewQuery);
  protected readonly categories = CLASSIFICATIONS;

  protected readonly points = computed<MapPoint[]>(() => this.store.located().map(c => ({
    id: c.exposureCaseId, latitude: c.latitude!, longitude: c.longitude!, category: c.classification, ringed: c.inOpenCluster,
    label: `${c.caseNumber}: ${c.personName}`,
    detail: `${this.display.of(c.classification)}, ${this.display.of(c.population).toLowerCase()}, opened ${this.display.date(c.openedDate)}${c.addressLabel ? '. ' + c.addressLabel : ''}`,
  })));

  protected readonly selectedCase = computed(() => this.store.cases().find(c => c.exposureCaseId === this.store.selected()) ?? null);

  ngOnInit(): void {
    this.api.analytes().subscribe({ next: a => this.store.analytes.set(a), error: () => this.store.analytes.set([]) });
    this.view.watch(q => {
      this.store.filters.set({ analyteId: q.get('analyteId') ?? '', from: q.get('from') ?? '', to: q.get('to') ?? '', openOnly: q.get('openOnly') !== 'false' });
      this.load();
    });
  }

  protected apply(event: Event): void {
    event.preventDefault();
    const f = new FormData(event.target as HTMLFormElement);
    this.store.filters.set({ analyteId: String(f.get('analyteId') ?? ''), from: String(f.get('from') ?? ''), to: String(f.get('to') ?? ''),
      openOnly: f.get('openOnly') !== null });
    this.view.set(this.store.filters());
  }

  private load(): void {
    const f = this.store.filters();
    this.store.error.set(null);
    this.store.loading.set(true);
    this.mapLoad.run(this.api.caseMap({ analyteId: f.analyteId, from: f.from, to: f.to, openOnly: f.openOnly }), {
      next: c => { this.store.cases.set(c); this.store.loading.set(false); },
      error: e => { this.store.error.set(problemMessage(e)); this.store.loading.set(false); },
    });
  }
}
