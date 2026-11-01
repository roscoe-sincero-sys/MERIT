import { ChangeDetectionStrategy, Component, computed, DestroyRef, inject, Injectable, input, OnInit, signal } from '@angular/core';
import { ApiClient, problemMessage } from '../../api/api-client';
import { ClusterDto, ClusterListItemDto, ClusterStatus } from '../../api/contracts';
import { MapPoint, PointMapComponent } from '../../shared/map/point-map.component';
import { CLASSIFICATIONS } from '../map/case-map.page';
import { Display } from '../../shared/display.service';
import { LatestRequest } from '../../shared/latest-request';
import { PagerComponent } from '../../shared/pager.component';
import { ViewQuery, pageNumber, choice } from '../../shared/view-query';
import { RouterLink } from '@angular/router';

const TYPE_LABELS: Record<string, string> = {
  location: 'Shared location', building: 'Shared building', childCareSite: 'Child-care site', worksite: 'Shared worksite',
  provider: 'Ordering provider', product: 'Consumer product', distance: 'Cases near each other',
};
const STATUS_LABELS: Record<ClusterStatus, string> = { open: 'Open', underReview: 'Under review', closed: 'Closed' };

@Injectable()
export class ClustersStore {
  readonly page = signal(1);
  readonly pageSize = signal(200);
  readonly total = signal(0);
  readonly loading = signal(false);
  readonly status = signal('');
  readonly clusters = signal<ClusterListItemDto[]>([]);
  readonly error = signal<string | null>(null);
}

/** Cluster review list: groups flagged by detection, open first. Nothing was merged; staff review each. */
@Component({
  selector: 'merit-clusters-page',
  imports: [PagerComponent, RouterLink],
  providers: [ClustersStore, ViewQuery],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h1 tabindex="-1">Clusters</h1>
    <p class="merit-meta">Groups of cases that share a building, child-care site or worksite within the detection window. Cases join or leave a cluster; nothing is merged.</p>
    @if (store.error(); as e) { <div class="usa-alert usa-alert--error usa-alert--slim" role="alert"><div class="usa-alert__body"><p class="usa-alert__text">{{ e }}</p></div></div> }
    <div class="filters">
      <label class="usa-label" for="c-status">Status</label>
      <select class="usa-select" id="c-status" [value]="store.status()" (change)="setStatus($any($event.target).value)">
        <option value="">All</option><option value="open">Open</option><option value="underReview">Under review</option><option value="closed">Closed</option>
      </select>
    </div>
    <merit-pager label="Clusters" [page]="store.page()" [size]="store.pageSize()" [total]="store.total()" [busy]="store.loading()" (change)="goPage($event)" />
    @if (store.clusters().length === 0 && !store.loading()) { <p class="merit-empty">No clusters.</p> } @else {
      <div class="merit-table-wrap" tabindex="0" role="region" aria-label="Clusters; scroll sideways for more columns">
      <table class="usa-table usa-table--borderless">
        <caption class="usa-sr-only">Clusters</caption>
        <thead><tr><th scope="col">Cluster</th><th scope="col">Kind</th><th scope="col">Status</th><th scope="col">Cases</th><th scope="col">Window</th><th scope="col">Owner</th></tr></thead>
        <tbody>
          @for (c of store.clusters(); track c.clusterId) {
            <tr>
              <th scope="row"><a class="usa-link" [routerLink]="['/clusters', c.clusterId]" [queryParams]="{ returnTo: '/clusters' + listQuery() }">{{ c.title }}</a></th>
              <td>{{ typeLabel(c.clusterType) }}</td>
              <td>{{ statusLabel(c.status) }}</td>
              <td>{{ c.memberCount }}</td>
              <td><span class="merit-nowrap">{{ display.date(c.windowStart) }}</span> to <span class="merit-nowrap">{{ display.date(c.windowEnd) }}</span></td>
              <td>{{ c.owningTenantName }}</td>
            </tr>
          }
        </tbody>
      </table>
      </div>
    }
  `,
  styles: `.filters { display: flex; gap: 1rem; align-items: end; margin-bottom: 1rem; } .filters .usa-select { max-width: 12rem; margin-top: 0; }`,
})
export class ClustersPage implements OnInit {
  protected readonly store = inject(ClustersStore);
  private readonly api = inject(ApiClient);
  protected readonly display = inject(Display);
  private readonly clustersLoad = new LatestRequest();
  protected listQuery(): string { return window.location.search; }
  private readonly view = inject(ViewQuery);

  ngOnInit(): void {
    this.view.watch(q => {
      this.store.status.set(choice(q.get('status'), ['open', 'underReview', 'closed'], ''));
      this.store.page.set(pageNumber(q.get('page')));
      this.load();
    });
  }

  protected setStatus(status: string): void {
    this.store.status.set(status);
    this.store.page.set(1);
    this.view.set({ status: status || null, page: 1 });
  }

  protected typeLabel(t: string): string {
    return TYPE_LABELS[t] ?? t;
  }

  protected statusLabel(s: ClusterStatus): string {
    return STATUS_LABELS[s];
  }

  protected goPage(page: number): void { if (!this.store.loading()) { this.store.page.set(page); this.view.set({ page }); } }

  private load(): void {
    this.store.loading.set(true);
    this.store.error.set(null);
    this.clustersLoad.run(this.api.clusters({ status: this.store.status(), page: this.store.page(), pageSize: this.store.pageSize() }), {
      next: p => { this.store.clusters.set(p.items); this.store.total.set(p.totalCount); if (this.store.page() !== p.pageNumber) this.view.replaceLoaded({ page: p.pageNumber }); this.store.page.set(p.pageNumber); this.store.pageSize.set(p.pageSize); this.store.loading.set(false); },
      error: e => { this.store.error.set(problemMessage(e)); this.store.loading.set(false); },
    });
  }
}

@Injectable()
export class ClusterStore {
  readonly cluster = signal<ClusterDto | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);
  readonly error = signal<string | null>(null);
}

/** One cluster: its cases on a map and in a table, status and notes, and cases joining or leaving. */
@Component({
  selector: 'merit-cluster-page',
  imports: [PointMapComponent, RouterLink],
  providers: [ClusterStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p><a class="usa-link" [routerLink]="returnPath()" [queryParams]="returnQuery()">Return to clusters</a></p>
    @if (store.error(); as e) { <div class="usa-alert usa-alert--error usa-alert--slim" role="alert"><div class="usa-alert__body"><p class="usa-alert__text">{{ e }}</p></div></div> }
    @if (store.message(); as m) { <div class="usa-alert usa-alert--success usa-alert--slim" role="status"><div class="usa-alert__body"><p class="usa-alert__text">{{ m }}</p></div></div> }
    @if (store.cluster(); as c) {
      <h1 tabindex="-1">{{ c.title }}</h1>
      <p class="merit-meta">{{ typeLabel(c.clusterType) }} &middot; window {{ display.date(c.windowStart) }} to {{ display.date(c.windowEnd) }} &middot; owned by {{ c.owningTenantName }}</p>

      @if (c.canEdit) {
      <form class="usa-form usa-form--large status" (submit)="save($event)">
        <label class="usa-label" for="cl-status">Status</label>
        <select class="usa-select" id="cl-status" name="status">
          @for (s of statuses; track s) { <option [value]="s" [selected]="s === c.status">{{ statusLabel(s) }}</option> }
        </select>
        <label class="usa-label" for="cl-notes">Notes</label>
        <textarea class="usa-textarea" id="cl-notes" name="notes" maxlength="2000">{{ c.notes ?? '' }}</textarea>
        <button class="usa-button" type="submit" [disabled]="store.busy()">Save</button>
      </form>
      } @else {
        <p>Status: {{ statusLabel(c.status) }}</p><h2>Notes</h2><p>{{ c.notes || 'No notes recorded.' }}</p>
        <p class="merit-meta">You have read-only access to this cluster.</p>
      }

      <h2>Cases</h2>
      @if (points().length > 0) {
        <!-- A locator, not the page's subject: kept short so the case list stays in view (issue #48). -->
        <merit-point-map [points]="points()" [categories]="classifications" ringLabel="Left the cluster" [maxHeight]="320" (pick)="openCase($event)" />
      }
      <div class="merit-table-wrap" tabindex="0" role="region" aria-label="Cases in the cluster; scroll sideways for more columns">
      <table class="usa-table usa-table--borderless">
        <caption class="usa-sr-only">Cases in the cluster</caption>
        <thead><tr><th scope="col">Case</th><th scope="col">Person</th><th scope="col">Classification</th><th scope="col">Highest</th><th scope="col">Address</th><th scope="col">Joined</th><th scope="col"><span class="usa-sr-only">Action</span></th></tr></thead>
        <tbody>
          @for (m of c.members; track m.exposureCaseId) {
            <tr [class.left]="m.leftDate">
              <th scope="row"><a class="usa-link" [href]="'/cases/' + m.exposureCaseId">{{ m.caseNumber }}</a></th>
              <td><a class="usa-link" [href]="'/people/' + m.personId">{{ m.personName }}</a></td>
              <td>{{ display.of(m.analyteCode) }} &middot; {{ display.of(m.classification) }} ({{ display.of(m.population).toLowerCase() }})</td>
              <td>{{ m.highestValue ?? '—' }} {{ m.highestUnit }}</td>
              <td>{{ m.addressLabel ?? '—' }}</td>
              <td class="nowrap">{{ display.date(m.joinedDate) }}@if (m.leftDate) { (left {{ display.date(m.leftDate) }})}</td>
              <td>
                @if (c.canEdit && c.status !== 'closed') {
                  <button class="usa-button usa-button--unstyled" type="button" [disabled]="store.busy()" (click)="member(m.caseNumber, !!m.leftDate)">
                    {{ m.leftDate ? 'Rejoin' : 'Remove from cluster' }}<span class="usa-sr-only"> {{ m.caseNumber }}</span></button>
                }
              </td>
            </tr>
          }
        </tbody>
      </table>
      </div>
      @if (c.canEdit && c.status !== 'closed') {
        <form class="add" (submit)="add($event)">
          <label class="usa-label" for="cl-case">Add a case by last name or number</label>
          <input class="usa-input" id="cl-case" name="caseNumber" placeholder="PB-2026-000123 or last name" maxlength="60" required [value]="caseQuery()" [disabled]="store.busy()" (input)="findCases($any($event.target).value)" aria-describedby="cl-case-hint" />
          <button class="usa-button usa-button--outline" type="submit" [disabled]="store.busy()">Add case</button>
        </form>
        <p class="usa-hint" id="cl-case-hint">Choose a search result, or enter an exact case number.</p>
        @if (caseOptions().length) {
          <ul class="usa-list" aria-label="Matching cases">
            @for (option of caseOptions(); track option.caseNumber) {
              <li><button class="usa-button usa-button--unstyled" type="button" [disabled]="store.busy()" (click)="chooseCase(option.caseNumber)">{{ option.caseNumber }}: {{ option.personName }}</button></li>
            }
          </ul>
        }
        <p role="status">{{ searchMessage() }}</p>
      }

      @if (c.tasks.length > 0) {
        <h2>Open tasks</h2>
        <ul class="usa-list">
          <!-- Each task opens its work queue, where it is assigned and done (issue #48). -->
          @for (t of c.tasks; track t.workTaskId) {
            <li><a class="usa-link" [href]="'/app/queues?queue=' + t.queueCode">{{ t.title }}</a>@if (t.isUrgent) { <strong>(urgent)</strong>}@if (t.dueDate) {, due <span class="merit-nowrap">{{ display.date(t.dueDate) }}</span>}@if (t.assignedToName) {, {{ t.assignedToName }}}</li>
          }
        </ul>
      }
    } @else if (!store.error()) {
      <p class="merit-indicator">Loading the cluster&hellip;</p>
    }
  `,
  styles: `
    .status { max-width: 36rem; }
    .left td, .left th { color: #71767a; }
    .add { display: flex; flex-wrap: wrap; gap: .5rem 1rem; align-items: end; }
    .add .usa-input { max-width: 14rem; }
  `,
})
export class ClusterPage implements OnInit {
  protected readonly store = inject(ClusterStore);
  private readonly api = inject(ApiClient);
  protected readonly display = inject(Display);
  private readonly clusterLoad = new LatestRequest();
  protected readonly caseQuery = signal('');
  protected readonly caseOptions = signal<{ caseNumber: string; personName: string }[]>([]);
  protected readonly searchMessage = signal('');
  private readonly caseSearch = new LatestRequest();
  private searchTimer: ReturnType<typeof setTimeout> | undefined;
  constructor() { inject(DestroyRef).onDestroy(() => { clearTimeout(this.searchTimer); this.caseSearch.cancel(); }); }

  protected findCases(value: string): void {
    this.caseQuery.set(value);
    clearTimeout(this.searchTimer);
    this.caseSearch.cancel();
    this.caseOptions.set([]);
    this.searchMessage.set('');
    if (value.trim().length < 2 || !this.store.cluster()?.canEdit) return;
    this.searchTimer = setTimeout(() => this.caseSearch.run(this.api.caseOptions(value.trim()), {
      next: options => { this.caseOptions.set(options); this.searchMessage.set(options.length ? `${options.length} matching cases. Choose a result below.` : 'No matching cases. Check the last name or case number.'); },
      error: e => this.searchMessage.set(problemMessage(e)),
    }), 300);
  }

  protected chooseCase(caseNumber: string): void {
    clearTimeout(this.searchTimer);
    this.caseSearch.cancel();
    this.caseQuery.set(caseNumber);
    this.caseOptions.set([]);
    this.searchMessage.set(`Selected ${caseNumber}. Choose Add case to confirm.`);
    document.getElementById('cl-case')?.focus();
  }

  readonly clusterId = input.required<string>();
  readonly returnTo = input<string>('');
  protected returnPath(): string { return '/clusters'; }
  protected returnQuery(): Record<string, string> {
    const target = this.returnTo() ?? '';
    if (target.split('?')[0] !== '/clusters') return {};
    const params = new URLSearchParams(target.split('?')[1] ?? '');
    return Object.fromEntries([...params].filter(([key]) => ['status', 'page'].includes(key)));
  }
  protected readonly statuses: ClusterStatus[] = ['open', 'underReview', 'closed'];
  protected readonly classifications = CLASSIFICATIONS;

  protected readonly points = computed<MapPoint[]>(() => (this.store.cluster()?.members ?? [])
    .filter(m => m.latitude !== null && m.longitude !== null)
    .map(m => ({ id: m.exposureCaseId, latitude: m.latitude!, longitude: m.longitude!, category: m.classification, ringed: !!m.leftDate,
      label: `${m.caseNumber}: ${m.personName}`, detail: `${this.display.of(m.classification)}, opened ${this.display.date(m.openedDate)}. ${m.addressLabel ?? ''}` })));

  ngOnInit(): void {
    this.clusterLoad.run(this.api.cluster(this.clusterId()), {
      next: c => this.store.cluster.set(c),
      error: e => this.store.error.set(problemMessage(e)),
    });
  }

  protected typeLabel(t: string): string {
    return TYPE_LABELS[t] ?? t;
  }

  protected statusLabel(s: ClusterStatus): string {
    return STATUS_LABELS[s];
  }

  protected openCase(id: string): void { window.location.assign('/cases/' + encodeURIComponent(id)); }

  protected save(event: Event): void {
    event.preventDefault();
    const c = this.store.cluster();
    if (!c || !c.canEdit) return;
    const f = new FormData(event.target as HTMLFormElement);
    this.run(this.api.updateCluster(c.clusterId, { status: String(f.get('status')) as ClusterStatus, notes: String(f.get('notes') ?? '') || null,
      rowVersion: c.rowVersion }), 'Cluster saved.');
  }

  protected member(caseNumber: string, isMember: boolean): void {
    const c = this.store.cluster();
    if (!isMember && c?.canEdit && !this.store.busy() && !window.confirm(`Remove case ${caseNumber} from cluster ${c.title}?`)) return;
    if (c?.canEdit) this.run(this.api.setClusterMember(c.clusterId, { caseNumber, isMember }), isMember ? `${caseNumber} rejoined.` : `${caseNumber} left the cluster.`);
  }

  protected add(event: Event): void {
    event.preventDefault();
    const form = event.target as HTMLFormElement;
    const caseNumber = String(new FormData(form).get('caseNumber') ?? '').trim();
    const c = this.store.cluster();
    if (c?.canEdit && caseNumber && !this.store.busy()) {
      this.run(this.api.setClusterMember(c.clusterId, { caseNumber, isMember: true }), `${caseNumber} added.`, () => {
        form.reset(); this.caseQuery.set(''); this.caseOptions.set([]); this.searchMessage.set('');
        this.caseSearch.cancel(); clearTimeout(this.searchTimer);
      });
    }
  }

  private run(call: ReturnType<ApiClient['cluster']>, message: string, onSuccess?: () => void): void {
    if (this.store.busy()) return;
    this.clusterLoad.cancel();
    this.store.busy.set(true);
    this.store.error.set(null);
    call.subscribe({
      next: c => { this.store.cluster.set(c); this.store.message.set(message); this.store.busy.set(false); onSuccess?.(); },
      error: e => { this.store.error.set(problemMessage(e)); this.store.busy.set(false); },
    });
  }
}
