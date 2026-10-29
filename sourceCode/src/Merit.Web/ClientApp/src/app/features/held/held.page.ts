import { ChangeDetectionStrategy, Component, computed, inject, Injectable, input, OnInit, signal } from '@angular/core';
import { ApiClient, problemMessage } from '../../api/api-client';
import { HeldGroupDto, HeldRowDto, PagedResult } from '../../api/contracts';
import { Display } from '../../shared/display.service';
import { DataGridComponent, GridColumn } from '../../shared/grid/data-grid.component';
import { LatestRequest } from '../../shared/latest-request';

/** Held-records state: groups by cause, the rows of the chosen group, the selection. Signals only. */
@Injectable()
export class HeldStore {
  readonly groups = signal<HeldGroupDto[]>([]);
  readonly group = signal<HeldGroupDto | null>(null);
  readonly batch = signal<string | null>(null);
  readonly page = signal(1);
  readonly rows = signal<PagedResult<HeldRowDto>>({ items: [], totalCount: 0, pageNumber: 1, pageSize: 50 });
  readonly selected = signal<ReadonlySet<string>>(new Set<string>());
  readonly loading = signal(false);
  readonly message = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly focusRow = signal<HeldRowDto | null>(null);
  readonly totalHeld = computed(() => this.groups().reduce((n, g) => n + g.rowCount, 0));
  /** Selected rows that wait for a match decision: releasing reruns matching, which holds them again (issue #42). */
  readonly selectedForMatch = computed(() => this.rows().items.filter(r => this.selected().has(r.stagingRowId) && needsMatchReview(r)));
  readonly releasable = computed(() => [...this.selected()].filter(id => !this.selectedForMatch().some(r => r.stagingRowId === id)));
}

/** A row held for an uncertain person match is decided on Match review, not released. */
export function needsMatchReview(r: HeldRowDto): boolean {
  return r.reasonCodes.includes('uncertainMatch');
}

/**
 * Held records, built around fixing causes rather than rows: groups by reason, reported value and sender (urgent first),
 * bulk release (reruns validation and matching in the worker) and reject with a required reason. Corrections are made
 * beside the reported values, which never change.
 */
@Component({
  selector: 'merit-held-page',
  imports: [DataGridComponent],
  providers: [HeldStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h1 tabindex="-1">Held records</h1>
    <p class="merit-meta">{{ store.totalHeld() }} held row(s). Fix the cause once, then release every row it held.</p>
    @if (store.error(); as e) { <div class="usa-alert usa-alert--error usa-alert--slim" role="alert"><div class="usa-alert__body"><p class="usa-alert__text">{{ e }}</p></div></div> }
    @if (store.message(); as m) { <div class="usa-alert usa-alert--success usa-alert--slim" role="status"><div class="usa-alert__body"><p class="usa-alert__text">{{ m }}</p></div></div> }

    <h2>By cause</h2>
    @if (store.groups().length === 0) { <p class="merit-empty">Nothing is held.</p> }
    <ul class="groups">
      <li><button type="button" class="usa-button usa-button--unstyled" [attr.aria-pressed]="store.group() === null" (click)="choose(null)">All held rows</button></li>
      @for (g of store.groups(); track groupKey(g)) {
        <li>
          <button type="button" class="usa-button usa-button--unstyled" [attr.aria-pressed]="isChosen(g)" (click)="choose(g)">
            {{ label(g.reasonCode) }}@if (g.reportedValue) { : <code>{{ g.reportedValue }}</code> } &middot; {{ sender(g) }} &middot; {{ g.rowCount }} row(s)
          </button>
          @if (g.urgentCount > 0) { <span class="urgent">{{ g.urgentCount }} urgent</span> }
          <span class="merit-meta"> waiting since {{ display.dateOf(g.oldestHeldUtc) }}</span>
        </li>
      }
    </ul>

    <h2>Rows</h2>
    <p class="merit-meta" id="held-scope" aria-live="polite">
      @if (store.group(); as g) {
        Showing {{ label(g.reasonCode) }}@if (g.reportedValue) { : <code>{{ g.reportedValue }}</code> } from {{ sender(g) }} only.
      } @else {
        Showing every held row.
      }
    </p>
    <div class="bulk" role="group" aria-label="Actions on selected rows" aria-describedby="held-scope">
      <button class="usa-button" type="button" [disabled]="store.releasable().length === 0" (click)="release()"
              [attr.aria-describedby]="store.selectedForMatch().length > 0 ? 'release-match-note' : null">Release selected</button>
      <label class="usa-label" for="reject-reason">Reject reason</label>
      <input class="usa-input" id="reject-reason" #reason />
      <button class="usa-button usa-button--secondary" type="button" [disabled]="store.selected().size === 0" (click)="reject(reason.value)">Reject selected</button>
    </div>
    @if (store.selectedForMatch().length > 0) {
      <p class="merit-meta" id="release-match-note">
        @if (store.releasable().length === 0) {
          Decide the match first: releasing does not choose a person, so the row would be held again.
        } @else {
          {{ store.selectedForMatch().length }} of the selected rows wait for a match decision; Release leaves them held.
        }
        <a class="usa-link" href="/app/match-review">Open Match review</a>.
      </p>
    }
    <merit-data-grid gridId="held" caption="Held rows" [columns]="columns" [rows]="store.rows().items" [total]="store.rows().totalCount"
                     [page]="store.page()" [pageSize]="50" [loading]="store.loading()" [selectable]="true" [selected]="store.selected()"
                     [rowKey]="key" emptyText="No held rows in this group." (pageChange)="onPage($event)" (selectionChange)="onSelect($event)" />

    @if (store.focusRow(); as r) {
      <section class="merit-section" aria-labelledby="row-detail">
        <h2 id="row-detail">Row {{ r.rowNumber }} of {{ r.fileName }}</h2>
        <table class="usa-table usa-table--borderless usa-table--compact">
          <thead><tr><th scope="col">Field</th><th scope="col">As reported</th><th scope="col">Correction</th></tr></thead>
          <tbody>
            @for (k of fields(r); track k) {
              <tr><th scope="row">{{ display.field(k) }}</th><td>{{ r.reported[k] }}</td>
                <td><label class="usa-sr-only" [for]="'fix-' + k">Correct {{ display.field(k) }}</label><input class="usa-input" [id]="'fix-' + k" [value]="r.corrected[k] ?? ''" (change)="setFix(k, $event)" /></td></tr>
            }
          </tbody>
        </table>
        <button class="usa-button usa-button--outline" type="button" (click)="saveFix(r)">Save correction</button>
      </section>
    }
  `,
  styles: `
    .groups { list-style: none; padding: 0; }
    .groups li { margin: .35rem 0; }
    .urgent { margin-left: .5rem; padding: 0 .4rem; border-radius: 1rem; background: #b50909; color: #fff; font-size: .8rem; font-weight: 700; }
    .bulk { display: flex; flex-wrap: wrap; align-items: end; gap: .5rem 1rem; margin-bottom: .75rem; }
    .bulk .usa-input { max-width: 18rem; margin-top: 0; }
    .bulk .usa-label { margin-top: 0; }
  `,
})
export class HeldPage implements OnInit {
  protected readonly store = inject(HeldStore);
  private readonly api = inject(ApiClient);
  protected readonly display = inject(Display);
  private readonly groupsLoad = new LatestRequest();
  private readonly rowsLoad = new LatestRequest();
  readonly batchParam = input<string | undefined>(undefined, { alias: 'batch' });
  private fixes: Record<string, string | null> = {};

  protected readonly key = (r: HeldRowDto): string => r.stagingRowId;

  protected readonly columns: GridColumn<HeldRowDto>[] = [
    { key: 'reasons', label: 'Reasons', cell: r => r.reasons ?? '', badge: r => (r.isUrgent ? 'Urgent' : null) },
    { key: 'patient', label: 'Patient as reported', cell: r => `${r.reported['lastName'] ?? ''}, ${r.reported['firstName'] ?? ''} ${r.reported['dob'] ?? ''}` },
    { key: 'value', label: 'Result as reported', cell: r => `${r.reported['loinc'] ?? ''} ${r.reported['value'] ?? ''} ${r.reported['unit'] ?? ''}` },
    { key: 'file', label: 'File and row', cell: r => `${r.fileName ?? ''} #${r.rowNumber}` },
    { key: 'sender', label: 'Sender', cell: r => r.senderName ?? '' },
    { key: 'waiting', label: 'Held since', nowrap: true, cell: r => this.display.dateOf(r.heldSinceUtc) },
    // The step that clears an uncertain match: deciding it on Match review (issue #42).
    { key: 'next', label: 'Next step', nowrap: true, cell: r => (needsMatchReview(r) ? 'Review the match' : ''),
      link: r => (needsMatchReview(r) ? `/app/match-review?row=${r.stagingRowId}` : null) },
  ];

  ngOnInit(): void {
    this.store.batch.set(this.batchParam() ?? null);
    this.loadGroups();
    this.loadRows();
  }

  protected label(code: string): string {
    return this.display.of(code);
  }

  /** A group is its reason, reported value and sender together, as the server groups them. */
  protected groupKey(g: HeldGroupDto): string {
    return JSON.stringify([g.reasonCode, g.reportedValue, g.senderOrganizationId]);
  }

  /** Compared by key, so the chosen group stays marked when the list is reloaded after a release or reject. */
  protected isChosen(g: HeldGroupDto): boolean {
    const chosen = this.store.group();
    return chosen !== null && this.groupKey(chosen) === this.groupKey(g);
  }

  protected sender(g: HeldGroupDto): string {
    return g.senderName ?? 'an unknown sender';
  }

  protected choose(g: HeldGroupDto | null): void {
    this.store.group.set(g);
    this.store.page.set(1);
    this.store.selected.set(new Set<string>());
    this.store.focusRow.set(null);
    this.fixes = {};
    this.loadRows();
  }

  protected onPage(p: number): void {
    this.store.page.set(p);
    this.loadRows();
  }

  /** Selecting exactly one row opens its reported values beside a correction form. */
  protected onSelect(ids: ReadonlySet<string>): void {
    this.store.selected.set(ids);
    const only = ids.size === 1 ? [...ids][0] : null;
    this.store.focusRow.set(this.store.rows().items.find(r => r.stagingRowId === only) ?? null);
    this.fixes = {};
  }

  protected fields(r: HeldRowDto): string[] {
    return this.display.fieldOrder(Object.keys(r.reported));
  }

  protected setFix(key: string, event: Event): void {
    const v = (event.target as HTMLInputElement).value;
    this.fixes[key] = v === '' ? null : v;
  }

  /** Sends only the fields changed since the row was opened; the server merges them into what is stored (issue #10). */
  protected saveFix(r: HeldRowDto): void {
    this.api.correctHeld(r.stagingRowId, { ...this.fixes }).subscribe({
      next: () => { this.store.message.set('Correction saved beside the reported values. Release the row to load it.'); this.fixes = {}; this.loadRows(); },
      error: e => this.store.error.set(problemMessage(e)),
    });
  }

  protected release(): void {
    const waiting = this.store.selectedForMatch().length;
    this.api.releaseHeld({ stagingRowIds: this.store.releasable(), reason: null }).subscribe({
      next: r => this.done(`${r.updated} row(s) released; they rerun validation and matching in the background.`
        + (waiting > 0 ? ` ${waiting} left for Match review.` : '')),
      error: e => this.store.error.set(problemMessage(e)),
    });
  }

  protected reject(reason: string): void {
    if (!reason.trim()) {
      this.store.error.set('A reason is required to reject held records.');
      return;
    }
    this.api.rejectHeld({ stagingRowIds: [...this.store.selected()], reason }).subscribe({
      next: r => this.done(`${r.updated} row(s) rejected.`),
      error: e => this.store.error.set(problemMessage(e)),
    });
  }

  private done(message: string): void {
    this.store.error.set(null);
    this.store.message.set(message);
    this.store.selected.set(new Set<string>());
    this.store.focusRow.set(null);
    this.loadGroups();
    this.loadRows();
  }

  private loadGroups(): void {
    this.groupsLoad.run(this.api.heldGroups(), {
      next: g => this.store.groups.set(g),
      error: e => this.store.error.set(problemMessage(e)),
    });
  }

  private loadRows(): void {
    const g = this.store.group();
    this.store.error.set(null);
    this.store.loading.set(true);
    // A chosen group is asked for exactly: its sender too, and a missing value means "none", not "any" (issue #9).
    const scope = g ? { reason: g.reasonCode, value: g.reportedValue, sender: g.senderOrganizationId, exact: true } : {};
    this.rowsLoad.run(this.api.heldRows({ ...scope, batch: this.store.batch(), page: this.store.page(), pageSize: 50 }), {
      next: r => {
        this.store.rows.set(r);
        // Keep the open row in step with what is stored, so its correction boxes show the saved values.
        const open = this.store.focusRow();
        this.store.focusRow.set(open ? (r.items.find(x => x.stagingRowId === open.stagingRowId) ?? null) : null);
        this.store.loading.set(false);
      },
      error: e => { this.store.error.set(problemMessage(e)); this.store.loading.set(false); },
    });
  }
}
