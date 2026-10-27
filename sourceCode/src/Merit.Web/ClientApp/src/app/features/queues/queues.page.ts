import { ChangeDetectionStrategy, Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { Router } from '@angular/router';
import { ApiClient, problemMessage } from '../../api/api-client';
import { BulkTaskActionRequest, WorkTaskDto } from '../../api/contracts';
import { DataGridComponent, GridColumn, GridSort } from '../../shared/grid/data-grid.component';
import { QueuesStore } from './queues.store';
import { Display } from '../../shared/display.service';

/** Work queues: the shared grid over tasks of every queue, with bulk assign, complete and dismiss. Route component. */
@Component({
  selector: 'merit-queues-page',
  imports: [DataGridComponent],
  providers: [QueuesStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h1 tabindex="-1">Work queues</h1>
    @if (store.error(); as e) {
      <div class="usa-alert usa-alert--error usa-alert--slim" role="alert"><div class="usa-alert__body"><p class="usa-alert__text">{{ e }}</p></div></div>
    }
    @if (store.message(); as m) {
      <div class="usa-alert usa-alert--success usa-alert--slim" role="status"><div class="usa-alert__body"><p class="usa-alert__text">{{ m }}</p></div></div>
    }
    <div class="layout">
      <nav aria-label="Queues">
        <ul class="usa-sidenav">
          <li class="usa-sidenav__item"><a href="" [class.usa-current]="store.queue() === null" (click)="chooseQueue($event, null)">All queues</a></li>
          @for (q of store.visibleQueues(); track q.queueCode) {
            <li class="usa-sidenav__item">
              <a href="" [class.usa-current]="store.queue() === q.queueCode" (click)="chooseQueue($event, q.queueCode)">
                {{ q.queueName }} ({{ q.openCount }})@if (q.urgentCount > 0) { <strong> · {{ q.urgentCount }} urgent</strong> }
              </a>
            </li>
          }
        </ul>
      </nav>
      <section [attr.aria-label]="store.currentQueueName()">
        <h2>{{ store.currentQueueName() }}</h2>
        <div class="filters">
          <label class="usa-label" for="status">Status</label>
          <select class="usa-select" id="status" [value]="store.status()" (change)="setStatus($event)">
            <option value="open">Open</option><option value="done">Done</option><option value="dismissed">Dismissed</option>
          </select>
          <div class="usa-checkbox"><input class="usa-checkbox__input" id="mine" type="checkbox" [checked]="store.mine()" (change)="toggleMine()" /><label class="usa-checkbox__label" for="mine">Assigned to me</label></div>
          <div class="usa-checkbox"><input class="usa-checkbox__input" id="urgent" type="checkbox" [checked]="store.urgentOnly()" (change)="toggleUrgent()" /><label class="usa-checkbox__label" for="urgent">Urgent only</label></div>
        </div>
        <div class="bulk" role="group" aria-label="Bulk actions on selected tasks">
          <button class="usa-button usa-button--outline" type="button" [disabled]="store.selected().size === 0" (click)="bulk('assignToMe')">Assign to me</button>
          <label class="usa-sr-only" for="assignee">Assign to</label>
          <!-- No one is chosen until the user chooses, so Assign never goes to whoever is first in the list (issue #41). -->
          <select class="usa-select assignee" id="assignee" [value]="assignee()" [title]="assigneeName()" (change)="assignee.set($any($event.target).value)">
            <option value="">Choose a person&hellip;</option>
            @for (u of store.users(); track u.appUserId) { <option [value]="u.appUserId">{{ u.displayName }}</option> }
          </select>
          <button class="usa-button usa-button--outline" type="button" [disabled]="store.selected().size === 0 || !assignee()" (click)="bulk('assign', assignee())">Assign</button>
          <button class="usa-button" type="button" [disabled]="store.selected().size === 0" (click)="bulk('complete')">Complete</button>
          <button class="usa-button usa-button--secondary" type="button" [disabled]="store.selected().size === 0" (click)="bulk('dismiss')">Dismiss</button>
        </div>
        <merit-data-grid gridId="tasks" caption="Tasks" [columns]="columns" [rows]="store.result().items" [total]="store.result().totalCount"
                         [page]="store.page()" [pageSize]="store.pageSize()" [sort]="store.sort()" [loading]="store.loading()"
                         [selectable]="true" [selected]="store.selected()" [rowKey]="key" emptyText="No tasks in this view."
                         (sortChange)="onSort($event)" (pageChange)="onPage($event)" (selectionChange)="store.selected.set($event)" />
      </section>
    </div>
  `,
  styles: `
    .layout { display: grid; grid-template-columns: minmax(12rem, 18rem) 1fr; gap: 1.5rem; }
    .filters, .bulk { display: flex; flex-wrap: wrap; align-items: end; gap: .5rem 1rem; margin-bottom: .75rem; }
    .filters .usa-select, .bulk .usa-select { max-width: 14rem; margin-top: 0; }
    .bulk .usa-select.assignee { max-width: 22rem; }
    @media (max-width: 64em) { .layout { grid-template-columns: 1fr; } }
  `,
})
export class QueuesPage implements OnInit {
  protected readonly store = inject(QueuesStore);
  private readonly api = inject(ApiClient);
  protected readonly display = inject(Display);
  private readonly router = inject(Router);

  /** Query-string inputs, so a dashboard tile can link straight to a queue. */
  readonly queueParam = input<string | undefined>(undefined, { alias: 'queue' });
  readonly mineParam = input<string | undefined>(undefined, { alias: 'mine' });

  protected readonly key = (t: WorkTaskDto): string => t.workTaskId;

  protected readonly columns: GridColumn<WorkTaskDto>[] = [
    { key: 'title', label: 'Task', sortable: true, cell: t => t.title, badge: t => (t.isUrgent ? 'Urgent' : null) },
    { key: 'queueCode', label: 'Queue', sortable: true, cell: t => this.queueName(t.queueCode) },
    { key: 'person', label: 'Person', cell: t => t.personName ?? '', link: t => (t.personId ? `/people/${t.personId}` : null) },
    { key: 'case', label: 'Case', nowrap: true, cell: t => t.caseNumber ?? '', link: t => (t.exposureCaseId ? `/cases/${t.exposureCaseId}` : null) },
    { key: 'dueDate', label: 'Due', sortable: true, nowrap: true, cell: t => this.display.date(t.dueDate) },
    { key: 'assigned', label: 'Assigned', cell: t => t.assignedToName ?? '' },
    { key: 'createdDateUtc', label: 'Created', sortable: true, nowrap: true, cell: t => this.display.dateOf(t.createdUtc) },
    { key: 'detail', label: 'Detail', hidden: true, cell: t => t.detail ?? '' },
  ];

  ngOnInit(): void {
    this.store.queue.set(this.queueParam() ?? null);
    this.store.mine.set(this.mineParam() === 'true');
    this.api.queues().subscribe({ next: q => this.store.queues.set(q), error: e => this.store.error.set(problemMessage(e)) });
    this.api.assignableUsers().subscribe({ next: u => this.store.users.set(u), error: () => this.store.users.set([]) });
    this.load();
  }

  protected chooseQueue(event: Event, code: string | null): void {
    event.preventDefault();
    this.store.queue.set(code);
    this.store.page.set(1);
    this.store.selected.set(new Set<string>());
    void this.router.navigate([], { queryParams: { queue: code }, replaceUrl: true });
    this.load();
  }

  protected setStatus(event: Event): void {
    this.store.status.set((event.target as HTMLSelectElement).value);
    this.store.page.set(1);
    this.load();
  }

  protected toggleMine(): void {
    this.store.mine.update(v => !v);
    this.store.page.set(1);
    this.load();
  }

  protected toggleUrgent(): void {
    this.store.urgentOnly.update(v => !v);
    this.store.page.set(1);
    this.load();
  }

  protected onSort(sort: GridSort): void {
    this.store.sort.set(sort);
    this.load();
  }

  protected onPage(page: number): void {
    this.store.page.set(page);
    this.load();
  }

  /** The person chosen in the assignee list, or empty: nobody is chosen until the user chooses. */
  protected readonly assignee = signal('');
  protected readonly assigneeName = computed(() => this.store.users().find(u => u.appUserId === this.assignee())?.displayName ?? '');

  protected bulk(action: BulkTaskActionRequest['action'], assignTo?: string): void {
    const ids = [...this.store.selected()];
    const to = this.assigneeName();
    this.store.error.set(null);
    this.api.bulkTasks({ taskIds: ids, action, assignToUserId: assignTo ?? null, note: null }).subscribe({
      next: r => {
        // Say what happened and, for an assignment, to whom.
        const tasks = r.updated === 1 ? '1 task' : `${r.updated} tasks`;
        this.store.message.set(action === 'assign' ? `${tasks} assigned to ${to}.` : action === 'assignToMe' ? `${tasks} assigned to you.`
          : action === 'complete' ? `${tasks} completed.` : `${tasks} dismissed.`);
        this.assignee.set('');
        this.store.selected.set(new Set<string>());
        this.api.queues().subscribe(q => this.store.queues.set(q));
        this.load();
      },
      error: e => this.store.error.set(problemMessage(e)),
    });
  }

  private queueName(code: string): string {
    return this.store.queues().find(q => q.queueCode === code)?.queueName ?? code;
  }

  private load(): void {
    this.store.loading.set(true);
    this.api.tasks(this.store.query()).subscribe({
      next: r => { this.store.result.set(r); this.store.loading.set(false); },
      error: e => { this.store.error.set(problemMessage(e)); this.store.loading.set(false); },
    });
  }
}
