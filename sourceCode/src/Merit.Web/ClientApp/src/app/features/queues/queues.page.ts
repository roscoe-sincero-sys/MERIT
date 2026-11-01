import { ChangeDetectionStrategy, Component, computed, inject, OnInit, signal } from '@angular/core';
import { ViewQuery, pageNumber, choice } from '../../shared/view-query';
import { ApiClient, problemMessage } from '../../api/api-client';
import { BulkTaskActionRequest, WorkTaskDto } from '../../api/contracts';
import { DataGridComponent, GridColumn, GridSort } from '../../shared/grid/data-grid.component';
import { QueuesStore } from './queues.store';
import { Display } from '../../shared/display.service';
import { LatestRequest } from '../../shared/latest-request';
import { ContextPanelComponent } from '../../shared/context-panel.component';

/** Work queues: the shared grid over tasks of every queue, with bulk assign, complete and dismiss. Route component. */
@Component({
  selector: 'merit-queues-page',
  imports: [DataGridComponent, ContextPanelComponent],
  providers: [QueuesStore, ViewQuery],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h1 tabindex="-1">Work queues</h1>
    @if (store.error(); as e) {
      <div class="usa-alert usa-alert--error usa-alert--slim" role="alert"><div class="usa-alert__body"><p class="usa-alert__text">{{ e }}</p></div></div>
    }
    @if (store.message(); as m) {
      <div class="usa-alert usa-alert--success usa-alert--slim" role="status"><div class="usa-alert__body"><p class="usa-alert__text">{{ m }}</p></div></div>
    }
    <merit-context-panel panelId="queue-navigation" label="queues">
      <nav panelNavigation aria-label="Queues">
        <ul class="usa-sidenav">
          <li class="usa-sidenav__item"><a href="" [class.usa-current]="store.queue() === null" [attr.aria-current]="store.queue() === null ? 'true' : null" (click)="chooseQueue($event, null)">All queues</a></li>
          @for (q of store.visibleQueues(); track q.queueCode) {
            <li class="usa-sidenav__item">
              <a href="" [class.usa-current]="store.queue() === q.queueCode" [attr.aria-current]="store.queue() === q.queueCode ? 'true' : null" (click)="chooseQueue($event, q.queueCode)">
                {{ q.queueName }} ({{ q.openCount }})@if (q.urgentCount > 0) { <strong> · {{ q.urgentCount }} urgent</strong> }
              </a>
            </li>
          }
        </ul>
      </nav>
      <section [attr.aria-label]="store.currentQueueName()">
        <h2>{{ store.currentQueueName() }}</h2>
        <p class="merit-meta">Queue counts show open tasks.</p>
        <div class="filters">
          <label class="usa-label" for="status">Status</label>
          <select class="usa-select" id="status" [value]="store.status()" [disabled]="busy()" (change)="setStatus($event)">
            <option value="open">Open</option><option value="done">Done</option><option value="dismissed">Dismissed</option>
          </select>
          <div class="usa-checkbox"><input class="usa-checkbox__input" id="mine" type="checkbox" [checked]="store.mine()" [disabled]="busy()" (change)="toggleMine()" /><label class="usa-checkbox__label" for="mine">Assigned to me</label></div>
          <div class="usa-checkbox"><input class="usa-checkbox__input" id="urgent" type="checkbox" [checked]="store.urgentOnly()" [disabled]="busy()" (change)="toggleUrgent()" /><label class="usa-checkbox__label" for="urgent">Urgent only</label></div>
        </div>
        <div class="usa-checkbox"><input class="usa-checkbox__input" id="overdue" type="checkbox" [checked]="store.overdueOnly()" [disabled]="busy()" (change)="toggleOverdue()" /><label class="usa-checkbox__label" for="overdue">Overdue only</label></div>
        @if (store.selected().size > 0) { <p role="status">{{ store.selected().size }} selected across pages. <button class="usa-button usa-button--unstyled" type="button" [disabled]="busy()" (click)="clearSelection()">Clear selection</button></p> }
        @if (display.can('task.edit')) {
        <div class="bulk" role="group" aria-label="Bulk actions on selected tasks">
          @if (store.status() === 'open') {
          <button class="usa-button usa-button--outline" type="button" [disabled]="store.selected().size === 0 || store.loading() || busy()" (click)="bulk('assignToMe')">Assign to me</button>
          <label class="usa-sr-only" for="assignee">Assign to</label>
          <!-- No one is chosen until the user chooses, so Assign never goes to whoever is first in the list (issue #41). -->
          <select class="usa-select assignee" id="assignee" [value]="assignee()" [disabled]="busy()" [title]="assigneeName()" (change)="assignee.set($any($event.target).value)">
            <option value="">Choose a person&hellip;</option>
            @for (u of store.users(); track u.appUserId) { <option [value]="u.appUserId">{{ u.displayName }}</option> }
          </select>
          <button class="usa-button usa-button--outline" type="button" [disabled]="store.selected().size === 0 || !assignee() || store.loading() || busy()" (click)="bulk('assign', assignee())">Assign</button>
          <button class="usa-button" type="button" [disabled]="store.selected().size === 0 || store.loading() || busy()" (click)="bulk('complete')">Complete</button>
          <label class="usa-label" for="dismiss-reason">Reason for dismissal</label><input class="usa-input" id="dismiss-reason" maxlength="1000" [value]="dismissReason()" [disabled]="busy()" (input)="dismissReason.set($any($event.target).value)" />
          <button class="usa-button usa-button--secondary" type="button" [disabled]="store.selected().size === 0 || store.loading() || busy()" (click)="bulk('dismiss')">Dismiss</button>
          } @else {
            <button class="usa-button" type="button" [disabled]="store.selected().size === 0 || store.loading() || busy()" (click)="bulk('reopen')">Reopen selected</button>
          }
          @if (busy()) { <span role="status">Updating selected tasks&hellip;</span> }
        </div>
        }
        <merit-data-grid gridId="tasks" caption="Tasks" [columns]="columns" [rows]="store.result().items" [total]="store.result().totalCount"
                         [page]="store.page()" [pageSize]="store.pageSize()" [sort]="store.sort()" [loading]="store.loading() || busy()"
                         [selectable]="display.can('task.edit')" [rowSelectable]="canSelect" [selectionDisabled]="store.loading() || busy()" [selected]="store.selected()" [rowKey]="key" emptyText="No tasks in this view."
                         (sortChange)="onSort($event)" (pageChange)="onPage($event)" (selectionChange)="onSelect($event)" />
      </section>
    </merit-context-panel>
  `,
  styles: `
    .filters, .bulk { display: flex; flex-wrap: wrap; align-items: end; gap: .5rem 1rem; margin-bottom: .75rem; }
    .filters .usa-select, .bulk .usa-select { max-width: 14rem; margin-top: 0; }
    .bulk .usa-select.assignee { max-width: 22rem; }
  `,
})
export class QueuesPage implements OnInit {
  protected readonly store = inject(QueuesStore);
  private readonly api = inject(ApiClient);
  protected readonly display = inject(Display);
  private readonly view = inject(ViewQuery);
  private readonly queuesLoad = new LatestRequest();
  private readonly tasksLoad = new LatestRequest();
  private pendingSelectionReset = false;
  protected readonly busy = signal(false);
  protected readonly dismissReason = signal('');
  protected readonly canSelect = (t: WorkTaskDto): boolean => t.canEdit;

  protected readonly key = (t: WorkTaskDto): string => t.workTaskId;

  protected readonly columns: GridColumn<WorkTaskDto>[] = [
    { key: 'title', label: 'Task', sortable: true, cell: t => t.title, badge: t => (t.isUrgent ? 'Urgent' : null) },
    { key: 'queueCode', label: 'Queue', sortable: true, cell: t => this.queueName(t.queueCode) },
    { key: 'person', label: 'Person', cell: t => t.personName ?? '', link: t => (t.personId ? this.view.detail(`/people/${t.personId}`) : null) },
    { key: 'case', label: 'Case', nowrap: true, cell: t => t.caseNumber ?? '', link: t => (t.exposureCaseId ? this.view.detail(`/cases/${t.exposureCaseId}`) : null) },
    { key: 'dueDate', label: 'Due', sortable: true, nowrap: true, cell: t => this.display.date(t.dueDate), badge: t => t.isOverdue ? 'Overdue' : null },
    { key: 'assigned', label: 'Assigned', cell: t => t.assignedToName ?? '' },
    { key: 'createdDateUtc', label: 'Created', sortable: true, nowrap: true, cell: t => this.display.dateOf(t.createdUtc) },
    { key: 'detail', label: 'Detail', cell: t => t.detail ?? '' },
  ];

  ngOnInit(): void {
    this.view.watch(q => {
      const oldScope = JSON.stringify([this.store.queue(), this.store.status(), this.store.mine(), this.store.urgentOnly(), this.store.overdueOnly()]);
      this.store.queue.set(q.get('queue'));
      this.store.status.set(choice(q.get('status'), ['open', 'done', 'dismissed'], 'open'));
      this.store.mine.set(q.get('mine') === 'true');
      this.store.urgentOnly.set(q.get('urgent') === 'true');
      this.store.overdueOnly.set(q.get('overdue') === 'true');
      this.store.page.set(pageNumber(q.get('page')));
      this.store.sort.set({ key: choice(q.get('sort'), ['dueDate', 'createdDateUtc', 'title', 'queueCode'], 'dueDate'), descending: q.get('desc') === 'true' });
      const scopeChanged = oldScope !== JSON.stringify([this.store.queue(), this.store.status(), this.store.mine(), this.store.urgentOnly(), this.store.overdueOnly()]);
      this.load(scopeChanged);
    });
    this.loadQueues();
    this.api.assignableUsers().subscribe({ next: u => this.store.users.set(u), error: () => this.store.users.set([]) });
  }

  protected chooseQueue(event: Event, code: string | null): void {
    event.preventDefault();
    if (this.busy()) return;
    this.pendingSelectionReset = true;
    this.store.queue.set(code);
    this.store.page.set(1);
    this.saveView();
  }

  protected setStatus(event: Event): void {
    if (this.busy()) return;
    this.pendingSelectionReset = true;
    this.store.status.set((event.target as HTMLSelectElement).value);
    this.store.page.set(1);
    this.saveView();
  }

  protected toggleMine(): void {
    if (this.busy()) return;
    this.pendingSelectionReset = true;
    this.store.mine.update(v => !v);
    this.store.page.set(1);
    this.saveView();
  }

  protected clearSelection(): void { if (!this.busy()) this.store.selected.set(new Set<string>()); }
  protected toggleOverdue(): void {
    if (this.busy()) return;
    this.pendingSelectionReset = true;
    this.store.overdueOnly.update(v => !v); this.store.page.set(1); this.saveView();
  }

  protected toggleUrgent(): void {
    if (this.busy()) return;
    this.pendingSelectionReset = true;
    this.store.urgentOnly.update(v => !v);
    this.store.page.set(1);
    this.saveView();
  }

  protected onSort(sort: GridSort): void {
    if (this.busy()) return;
    this.store.sort.set(sort);
    this.saveView();
  }

  protected onPage(page: number): void {
    if (this.busy()) return;
    this.store.page.set(page);
    this.saveView();
  }

  protected onSelect(selected: ReadonlySet<string>): void {
    if (this.pendingSelectionReset || this.store.loading() || this.busy()) return;
    this.store.selected.set(selected);
  }

  /** The person chosen in the assignee list, or empty: nobody is chosen until the user chooses. */
  protected readonly assignee = signal('');
  protected readonly assigneeName = computed(() => this.store.users().find(u => u.appUserId === this.assignee())?.displayName ?? '');

  protected bulk(action: BulkTaskActionRequest['action'], assignTo?: string): void {
    if (this.busy() || this.store.loading() || this.pendingSelectionReset || !this.display.can('task.edit') || this.store.selected().size === 0) return;
    if (action === 'dismiss' && !this.dismissReason().trim()) { this.store.error.set('Enter a reason for dismissal.'); return; }
    const ids = [...this.store.selected()];
    if ((action === 'complete' || action === 'dismiss') && !window.confirm(
      `${action === 'complete' ? 'Complete' : 'Dismiss'} ${ids.length} selected task(s), including selections on other pages? You can reopen them from Done or Dismissed.`)) return;
    this.tasksLoad.cancel();
    this.queuesLoad.cancel();
    this.busy.set(true);
    const to = this.assigneeName();
    this.store.error.set(null);
    this.api.bulkTasks({ taskIds: ids, action, assignToUserId: assignTo ?? null, note: action === 'dismiss' ? this.dismissReason().trim() : null }).subscribe({
      next: r => {
        // Say what happened and, for an assignment, to whom.
        const tasks = r.updated === 1 ? '1 task' : `${r.updated} tasks`;
        this.store.message.set(action === 'assign' ? `${tasks} assigned to ${to}.` : action === 'assignToMe' ? `${tasks} assigned to you.`
          : action === 'complete' ? `${tasks} completed.` : action === 'reopen' ? `${tasks} reopened.` : `${tasks} dismissed.`);
        this.busy.set(false);
        this.assignee.set('');
        this.dismissReason.set('');
        this.store.selected.set(new Set<string>());
        this.loadQueues();
        this.load();
      },
      error: e => { this.busy.set(false); this.store.error.set(problemMessage(e)); this.loadQueues(true); },
    });
  }

  private saveView(): void {
    if (this.pendingSelectionReset) this.store.selected.set(new Set<string>());
    this.view.set(this.store.query());
  }

  private queueName(code: string): string {
    return this.store.queues().find(q => q.queueCode === code)?.queueName ?? code;
  }

  private loadQueues(preserveError = false): void {
    this.queuesLoad.run(this.api.queues(), {
      next: q => this.store.queues.set(q),
      error: e => { if (!preserveError) this.store.error.set(problemMessage(e)); },
    });
  }

  private load(resetSelection = false): void {
    if (resetSelection || this.pendingSelectionReset) {
      this.pendingSelectionReset = true;
      this.store.selected.set(new Set<string>());
      this.store.result.set({ items: [], totalCount: 0, pageNumber: 1, pageSize: this.store.pageSize() });
    }
    this.store.error.set(null);
    this.store.loading.set(true);
    this.tasksLoad.run(this.api.tasks(this.store.query()), {
      next: r => {
        if (this.pendingSelectionReset) {
          this.store.selected.set(new Set<string>());
          this.pendingSelectionReset = false;
        }
        this.store.result.set(r);
        if (this.store.page() !== r.pageNumber) { this.store.page.set(r.pageNumber); this.view.replaceLoaded({ page: r.pageNumber }); }
        this.store.loading.set(false);
      },
      error: e => { this.store.error.set(problemMessage(e)); this.store.loading.set(false); },
    });
  }
}
