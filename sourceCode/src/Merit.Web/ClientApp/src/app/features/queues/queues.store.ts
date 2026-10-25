import { computed, Injectable, signal } from '@angular/core';
import { AssignableUserDto, PagedResult, QueueCountDto, WorkTaskDto } from '../../api/contracts';
import { GridSort } from '../../shared/grid/data-grid.component';

/** Work queue state, held in signals. No HTTP and no DOM here; the route component fills it. Gone when the tab closes. */
@Injectable()
export class QueuesStore {
  readonly queues = signal<QueueCountDto[]>([]);
  readonly users = signal<AssignableUserDto[]>([]);
  readonly queue = signal<string | null>(null);
  readonly status = signal('open');
  readonly mine = signal(false);
  readonly urgentOnly = signal(false);
  readonly sort = signal<GridSort>({ key: 'dueDate', descending: false });
  readonly page = signal(1);
  readonly pageSize = signal(50);
  readonly result = signal<PagedResult<WorkTaskDto>>({ items: [], totalCount: 0, pageNumber: 1, pageSize: 50 });
  readonly selected = signal<ReadonlySet<string>>(new Set<string>());
  readonly loading = signal(false);
  readonly message = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  readonly visibleQueues = computed(() => this.queues().filter(q => q.queueCode !== 'heldRecords' && q.openCount > 0));
  readonly currentQueueName = computed(() => this.queues().find(q => q.queueCode === this.queue())?.queueName ?? 'All queues');

  query(): Record<string, string | number | boolean | null> {
    return {
      queue: this.queue(),
      status: this.status(),
      mine: this.mine(),
      urgent: this.urgentOnly(),
      sort: this.sort().key,
      desc: this.sort().descending,
      page: this.page(),
      pageSize: this.pageSize(),
    };
  }
}
