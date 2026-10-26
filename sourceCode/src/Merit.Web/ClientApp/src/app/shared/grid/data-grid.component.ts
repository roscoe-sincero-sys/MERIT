import { CdkTableModule } from '@angular/cdk/table';
import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';

export interface GridColumn<T> {
  key: string;
  label: string;
  sortable?: boolean;
  hidden?: boolean;
  cell: (row: T) => string;
  link?: (row: T) => string | null;
  badge?: (row: T) => string | null;
}

export interface GridSort {
  key: string;
  descending: boolean;
}

/**
 * The shared data grid: server-side paging and sorting (the page emits; the store asks the API), bulk selection, a
 * column chooser and keyboard navigation (arrow keys move between rows, Space selects). Presentational: data comes in
 * through inputs and actions go out through outputs; it injects nothing and calls no HTTP. Built on the CDK table and
 * styled with USWDS table classes.
 */
@Component({
  selector: 'merit-data-grid',
  imports: [CdkTableModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="grid-toolbar">
      <span class="grid-count" aria-live="polite">{{ total() }} {{ total() === 1 ? 'row' : 'rows' }}@if (selectable() && selected().size > 0) {, {{ selected().size }} selected}</span>
      <details class="grid-columns">
        <summary>Columns</summary>
        @for (c of columns(); track c.key) {
          <div class="usa-checkbox">
            <input class="usa-checkbox__input" type="checkbox" [id]="gridId() + '-col-' + c.key" [checked]="!isHidden(c)" (change)="toggleColumn(c.key)" />
            <label class="usa-checkbox__label" [for]="gridId() + '-col-' + c.key">{{ c.label }}</label>
          </div>
        }
      </details>
    </div>
    <div class="grid-wrap">
      <table cdk-table [dataSource]="rows()" [trackBy]="trackRow" class="usa-table usa-table--borderless usa-table--compact" [attr.aria-label]="caption()" [attr.aria-busy]="loading()">
        @if (selectable()) {
          <ng-container cdkColumnDef="__select">
            <th cdk-header-cell *cdkHeaderCellDef scope="col" class="grid-select">
              <div class="usa-checkbox">
                <input class="usa-checkbox__input" type="checkbox" [id]="gridId() + '-all'" [checked]="allSelected()" (change)="toggleAll()" />
                <label class="usa-checkbox__label" [for]="gridId() + '-all'"><span class="usa-sr-only">Select all on this page</span></label>
              </div>
            </th>
            <td cdk-cell *cdkCellDef="let row; let i = index" class="grid-select">
              <div class="usa-checkbox">
                <input class="usa-checkbox__input" type="checkbox" [id]="gridId() + '-sel-' + i" [checked]="selected().has(rowKey()(row))" (change)="toggle(row)" />
                <label class="usa-checkbox__label" [for]="gridId() + '-sel-' + i"><span class="usa-sr-only">Select row {{ i + 1 }}</span></label>
              </div>
            </td>
          </ng-container>
        }
        @for (c of visibleColumns(); track c.key) {
          <ng-container [cdkColumnDef]="c.key">
            <th cdk-header-cell *cdkHeaderCellDef scope="col" [attr.aria-sort]="ariaSort(c)">
              @if (c.sortable) {
                <button type="button" class="usa-button usa-button--unstyled sort-button" (click)="sortBy(c.key)">
                  {{ c.label }} <span aria-hidden="true">{{ sortMark(c) }}</span>
                </button>
              } @else {
                {{ c.label }}
              }
            </th>
            <td cdk-cell *cdkCellDef="let row">
              @if (c.link && c.link(row); as href) {
                <a class="usa-link" [href]="href">{{ c.cell(row) }}</a>
              } @else {
                {{ c.cell(row) }}
              }
              @if (c.badge && c.badge(row); as b) { <span class="grid-badge">{{ b }}</span> }
            </td>
          </ng-container>
        }
        <tr cdk-header-row *cdkHeaderRowDef="displayed()"></tr>
        <tr cdk-row *cdkRowDef="let row; columns: displayed(); let i = index" tabindex="0" [attr.data-index]="i"
            [class.grid-row--selected]="selectable() && selected().has(rowKey()(row))" (keydown)="onRowKey($event, row, i)"></tr>
      </table>
      @if (rows().length === 0 && !loading()) {
        <p class="grid-empty">{{ emptyText() }}</p>
      }
    </div>
    <nav class="grid-pager" [attr.aria-label]="caption() + ' pages'">
      <button type="button" class="usa-button usa-button--outline usa-button--small" [disabled]="page() <= 1" (click)="pageChange.emit(page() - 1)">Previous</button>
      <span>Page {{ page() }} of {{ pageCount() }}</span>
      <button type="button" class="usa-button usa-button--outline usa-button--small" [disabled]="page() >= pageCount()" (click)="pageChange.emit(page() + 1)">Next</button>
    </nav>
  `,
  styles: `
    .grid-toolbar { display: flex; justify-content: space-between; align-items: center; gap: 1rem; flex-wrap: wrap; }
    .grid-columns summary { cursor: pointer; }
    .grid-wrap { overflow-x: auto; }
    .sort-button { color: inherit; font-weight: 700; text-decoration: none; }
    tr[cdk-row]:focus { outline: 3px solid #2491ff; outline-offset: -3px; }
    .grid-row--selected td { background: #d9e8f6; }
    /* USWDS gives checkbox labels a form margin and a 2rem text indent; in a cell the box alone is centred on the row. */
    th[cdk-header-cell], td[cdk-cell] { vertical-align: middle; }
    .grid-select { width: 2.75rem; padding-top: 0; padding-bottom: 0; }
    .grid-select .usa-checkbox { background: transparent; }
    .grid-select .usa-checkbox__label { margin-top: 0; padding-left: 0; min-height: 2rem; width: 2rem; display: flex; align-items: center; }
    .grid-select .usa-checkbox__label::before { margin-left: 0; }
    .grid-badge { display: inline-block; margin-left: .25rem; padding: 0 .4rem; border-radius: 1rem; background: #b50909; color: #fff; font-size: .75rem; font-weight: 700; }
    .grid-empty { color: #565c65; font-style: italic; }
    .grid-pager { display: flex; align-items: center; gap: 1rem; margin-top: .5rem; }
  `,
})
export class DataGridComponent<T> {
  readonly gridId = input('grid');
  readonly caption = input('Results');
  readonly columns = input.required<GridColumn<T>[]>();
  readonly rows = input.required<T[]>();
  readonly total = input(0);
  readonly page = input(1);
  readonly pageSize = input(50);
  readonly sort = input<GridSort | null>(null);
  readonly loading = input(false);
  readonly selectable = input(false);
  readonly selected = input<ReadonlySet<string>>(new Set<string>());
  readonly rowKey = input.required<(row: T) => string>();
  readonly emptyText = input('Nothing to show.');

  readonly sortChange = output<GridSort>();
  readonly pageChange = output<number>();
  readonly selectionChange = output<ReadonlySet<string>>();

  private readonly hiddenKeys = signal<ReadonlySet<string>>(new Set<string>());

  readonly visibleColumns = computed(() => this.columns().filter(c => !this.isHidden(c)));
  readonly displayed = computed(() => [...(this.selectable() ? ['__select'] : []), ...this.visibleColumns().map(c => c.key)]);
  readonly pageCount = computed(() => Math.max(1, Math.ceil(this.total() / this.pageSize())));
  readonly allSelected = computed(() => this.rows().length > 0 && this.rows().every(r => this.selected().has(this.rowKey()(r))));

  readonly trackRow = (_: number, row: T): string => this.rowKey()(row);

  isHidden(c: GridColumn<T>): boolean {
    return this.hiddenKeys().has(c.key) || (c.hidden === true && !this.hiddenKeys().has('!' + c.key));
  }

  toggleColumn(key: string): void {
    const next = new Set(this.hiddenKeys());
    const col = this.columns().find(c => c.key === key);
    if (col?.hidden) {
      next.has('!' + key) ? next.delete('!' + key) : next.add('!' + key);
    } else {
      next.has(key) ? next.delete(key) : next.add(key);
    }
    this.hiddenKeys.set(next);
  }

  sortBy(key: string): void {
    const s = this.sort();
    this.sortChange.emit({ key, descending: s?.key === key ? !s.descending : false });
  }

  sortMark(c: GridColumn<T>): string {
    const s = this.sort();
    return s?.key === c.key ? (s.descending ? '▼' : '▲') : '';
  }

  ariaSort(c: GridColumn<T>): string | null {
    const s = this.sort();
    return c.sortable ? (s?.key === c.key ? (s.descending ? 'descending' : 'ascending') : 'none') : null;
  }

  toggle(row: T): void {
    const key = this.rowKey()(row);
    const next = new Set(this.selected());
    next.has(key) ? next.delete(key) : next.add(key);
    this.selectionChange.emit(next);
  }

  toggleAll(): void {
    const next = new Set(this.selected());
    const keys = this.rows().map(r => this.rowKey()(r));
    if (this.allSelected()) {
      keys.forEach(k => next.delete(k));
    } else {
      keys.forEach(k => next.add(k));
    }
    this.selectionChange.emit(next);
  }

  /** Grid keyboard navigation: arrows move between rows, Home/End jump, Space toggles selection. */
  onRowKey(event: KeyboardEvent, row: T, index: number): void {
    const tr = event.currentTarget as HTMLElement;
    const body = tr.parentElement;
    const move = (i: number): void => (body?.querySelector<HTMLElement>(`tr[data-index="${i}"]`))?.focus();
    switch (event.key) {
      case 'ArrowDown': event.preventDefault(); move(Math.min(index + 1, this.rows().length - 1)); break;
      case 'ArrowUp': event.preventDefault(); move(Math.max(index - 1, 0)); break;
      case 'Home': event.preventDefault(); move(0); break;
      case 'End': event.preventDefault(); move(this.rows().length - 1); break;
      case ' ':
        if (this.selectable() && event.target === tr) { event.preventDefault(); this.toggle(row); }
        break;
    }
  }
}
