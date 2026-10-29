import { ChangeDetectionStrategy, Component, computed, inject, Injectable, input, OnInit, signal } from '@angular/core';
import { ApiClient, problemMessage } from '../../api/api-client';
import { ClusterDto, ClusterListItemDto, ClusterStatus } from '../../api/contracts';
import { MapPoint, PointMapComponent } from '../../shared/map/point-map.component';
import { CLASSIFICATIONS } from '../map/case-map.page';
import { Display } from '../../shared/display.service';
import { LatestRequest } from '../../shared/latest-request';

const TYPE_LABELS: Record<string, string> = {
  location: 'Shared location', building: 'Shared building', childCareSite: 'Child-care site', worksite: 'Shared worksite',
  provider: 'Ordering provider', product: 'Consumer product', distance: 'Cases near each other',
};
const STATUS_LABELS: Record<ClusterStatus, string> = { open: 'Open', underReview: 'Under review', closed: 'Closed' };

@Injectable()
export class ClustersStore {
  readonly status = signal('');
  readonly clusters = signal<ClusterListItemDto[]>([]);
  readonly error = signal<string | null>(null);
}

/** Cluster review list: groups flagged by detection, open first. Nothing was merged; staff review each. */
@Component({
  selector: 'merit-clusters-page',
  providers: [ClustersStore],
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
    @if (store.clusters().length === 0) { <p class="merit-empty">No clusters.</p> } @else {
      <div class="merit-table-wrap" tabindex="0" role="region" aria-label="Clusters; scroll sideways for more columns">
      <table class="usa-table usa-table--borderless">
        <caption class="usa-sr-only">Clusters</caption>
        <thead><tr><th scope="col">Cluster</th><th scope="col">Kind</th><th scope="col">Status</th><th scope="col">Cases</th><th scope="col">Window</th><th scope="col">Owner</th></tr></thead>
        <tbody>
          @for (c of store.clusters(); track c.clusterId) {
            <tr>
              <th scope="row"><a class="usa-link" [href]="'/app/clusters/' + c.clusterId">{{ c.title }}</a></th>
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

  ngOnInit(): void {
    this.load();
  }

  protected setStatus(status: string): void {
    this.store.status.set(status);
    this.load();
  }

  protected typeLabel(t: string): string {
    return TYPE_LABELS[t] ?? t;
  }

  protected statusLabel(s: ClusterStatus): string {
    return STATUS_LABELS[s];
  }

  private load(): void {
    this.store.error.set(null);
    this.clustersLoad.run(this.api.clusters({ status: this.store.status(), page: 1, pageSize: 200 }), {
      next: p => this.store.clusters.set(p.items),
      error: e => this.store.error.set(problemMessage(e)),
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
  imports: [PointMapComponent],
  providers: [ClusterStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p><a class="usa-link" href="/app/clusters">All clusters</a></p>
    @if (store.error(); as e) { <div class="usa-alert usa-alert--error usa-alert--slim" role="alert"><div class="usa-alert__body"><p class="usa-alert__text">{{ e }}</p></div></div> }
    @if (store.message(); as m) { <div class="usa-alert usa-alert--success usa-alert--slim" role="status"><div class="usa-alert__body"><p class="usa-alert__text">{{ m }}</p></div></div> }
    @if (store.cluster(); as c) {
      <h1 tabindex="-1">{{ c.title }}</h1>
      <p class="merit-meta">{{ typeLabel(c.clusterType) }} &middot; window {{ display.date(c.windowStart) }} to {{ display.date(c.windowEnd) }} &middot; owned by {{ c.owningTenantName }}</p>

      <form class="usa-form usa-form--large status" (submit)="save($event)">
        <label class="usa-label" for="cl-status">Status</label>
        <select class="usa-select" id="cl-status" name="status">
          @for (s of statuses; track s) { <option [value]="s" [selected]="s === c.status">{{ statusLabel(s) }}</option> }
        </select>
        <label class="usa-label" for="cl-notes">Notes</label>
        <textarea class="usa-textarea" id="cl-notes" name="notes" maxlength="2000">{{ c.notes ?? '' }}</textarea>
        <button class="usa-button" type="submit" [disabled]="store.busy()">Save</button>
      </form>

      <h2>Cases</h2>
      @if (points().length > 0) {
        <!-- A locator, not the page's subject: kept short so the case list stays in view (issue #48). -->
        <merit-point-map [points]="points()" [categories]="classifications" ringLabel="Left the cluster" [maxHeight]="320" />
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
                @if (c.status !== 'closed') {
                  <button class="usa-button usa-button--unstyled" type="button" [disabled]="store.busy()" (click)="member(m.caseNumber, !!m.leftDate)">
                    {{ m.leftDate ? 'Rejoin' : 'Remove from cluster' }}<span class="usa-sr-only"> {{ m.caseNumber }}</span></button>
                }
              </td>
            </tr>
          }
        </tbody>
      </table>
      </div>
      @if (c.status !== 'closed') {
        <form class="add" (submit)="add($event)">
          <label class="usa-label" for="cl-case">Add a case by number</label>
          <input class="usa-input" id="cl-case" name="caseNumber" placeholder="PB-2026-000123" maxlength="20" required />
          <button class="usa-button usa-button--outline" type="submit" [disabled]="store.busy()">Add case</button>
        </form>
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
  readonly clusterId = input.required<string>();
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

  protected save(event: Event): void {
    event.preventDefault();
    const c = this.store.cluster();
    if (!c) return;
    const f = new FormData(event.target as HTMLFormElement);
    this.run(this.api.updateCluster(c.clusterId, { status: String(f.get('status')) as ClusterStatus, notes: String(f.get('notes') ?? '') || null,
      rowVersion: c.rowVersion }), 'Cluster saved.');
  }

  protected member(caseNumber: string, isMember: boolean): void {
    const c = this.store.cluster();
    if (c) this.run(this.api.setClusterMember(c.clusterId, { caseNumber, isMember }), isMember ? `${caseNumber} rejoined.` : `${caseNumber} left the cluster.`);
  }

  protected add(event: Event): void {
    event.preventDefault();
    const form = event.target as HTMLFormElement;
    const caseNumber = String(new FormData(form).get('caseNumber') ?? '').trim();
    const c = this.store.cluster();
    if (c && caseNumber && !this.store.busy()) {
      this.run(this.api.setClusterMember(c.clusterId, { caseNumber, isMember: true }), `${caseNumber} added.`);
      form.reset();
    }
  }

  private run(call: ReturnType<ApiClient['cluster']>, message: string): void {
    if (this.store.busy()) return;
    this.clusterLoad.cancel();
    this.store.busy.set(true);
    this.store.error.set(null);
    call.subscribe({
      next: c => { this.store.cluster.set(c); this.store.message.set(message); this.store.busy.set(false); },
      error: e => { this.store.error.set(problemMessage(e)); this.store.busy.set(false); },
    });
  }
}
