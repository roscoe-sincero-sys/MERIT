import { CdkTableModule } from '@angular/cdk/table';
import { PagerComponent } from '../pager.component';
import { AfterViewInit, ChangeDetectionStrategy, Component, computed, Directive, ElementRef, inject, input, OnDestroy, output, signal } from '@angular/core';

export interface GridColumn<T> {
  key: string;
  label: string;
  sortable?: boolean;
  hidden?: boolean;
  cell: (row: T) => string;
  link?: (row: T) => string | null;
  action?: (row: T) => void;
  badge?: (row: T) => string | null;
  /** Identifiers and dates stay on one line; text columns take the wrapping (issue #39). */
  nowrap?: boolean;
  /** A second line under the cell, shown two lines deep with "Show more" when longer (issue #154). */
  subtext?: (row: T) => string | null;
  /** Left out of the phone card layout, where only the essentials fit (issue #154). */
  phoneHidden?: boolean;
}

/** Whether a clamped element hides some of its text, kept current as the column's width changes. */
@Directive({ selector: '[meritClamp]', exportAs: 'clamp' })
export class ClampDirective implements AfterViewInit, OnDestroy {
  private readonly el = inject<ElementRef<HTMLElement>>(ElementRef);
  private observer: ResizeObserver | null = null;
  readonly overflowing = signal(false);

  ngAfterViewInit(): void {
    const e = this.el.nativeElement;
    const check = () => this.overflowing.set(e.scrollHeight > e.clientHeight + 1);
    this.observer = new ResizeObserver(check);
    this.observer.observe(e);
    check();
  }

  ngOnDestroy(): void { this.observer?.disconnect(); }
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
  imports: [CdkTableModule, PagerComponent, ClampDirective],
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
    <a class="grid-skip" (click)="skipToPager($event)" [href]="'#' + gridId() + '-pagination'">Skip table to pagination</a>
    <div class="grid-wrap" [class.grid-cards]="cards()">
      <table cdk-table [dataSource]="rows()" [trackBy]="trackRow" class="usa-table usa-table--borderless usa-table--compact" [attr.aria-label]="caption()" [attr.aria-busy]="loading()">
        @if (selectable()) {
          <ng-container cdkColumnDef="__select">
            <th cdk-header-cell *cdkHeaderCellDef scope="col" class="grid-select">
              <div class="usa-checkbox">
                <input class="usa-checkbox__input" type="checkbox" [id]="gridId() + '-all'" [checked]="allSelected()" [disabled]="selectionDisabled()" (change)="toggleAll()" />
                <label class="usa-checkbox__label" [for]="gridId() + '-all'"><span class="usa-sr-only">Select all on this page</span></label>
              </div>
            </th>
            <td cdk-cell *cdkCellDef="let row; let i = index" class="grid-select">
              <div class="usa-checkbox">
                <input class="usa-checkbox__input" type="checkbox" [id]="gridId() + '-sel-' + i" [checked]="selected().has(rowKey()(row))" [disabled]="selectionDisabled() || !rowSelectable()(row)" (change)="toggle(row)" />
                <label class="usa-checkbox__label" [for]="gridId() + '-sel-' + i"><span class="usa-sr-only">Select row {{ i + 1 }}</span></label>
              </div>
            </td>
          </ng-container>
        }
        @for (c of visibleColumns(); track c.key) {
          <ng-container [cdkColumnDef]="c.key">
            <th cdk-header-cell *cdkHeaderCellDef scope="col" [attr.aria-sort]="ariaSort(c)" [class.phone-hidden]="c.phoneHidden" [class.grid-wide]="!!c.subtext">
              @if (c.sortable) {
                <button type="button" class="usa-button usa-button--unstyled sort-button" (click)="sortBy(c.key)">
                  {{ c.label }} <span aria-hidden="true">{{ sortMark(c) }}</span>
                </button>
              } @else {
                {{ c.label }}
              }
            </th>
            <td cdk-cell *cdkCellDef="let row" [class.nowrap]="c.nowrap" [class.phone-hidden]="c.phoneHidden" [class.grid-wide]="!!c.subtext" [class.grid-blank]="!c.cell(row) && !c.badge?.(row)" [attr.data-label]="c.label">
              @if (c.action) {
                <button type="button" class="usa-button usa-button--unstyled" [disabled]="loading()" (click)="c.action(row)">{{ c.cell(row) }}</button>
              } @else if (c.link && c.cell(row) && c.link(row); as href) {
                <!-- A link needs text: with no value to show (a person this user cannot see) the cell stays plain (issue #169). -->
                <a class="usa-link" [href]="href">{{ c.cell(row) }}</a>
              } @else {
                {{ c.cell(row) }}
              }
              @if (c.badge && c.badge(row); as b) { <span class="grid-badge">{{ b }}</span> }
              @if (c.subtext && c.subtext(row); as sub) {
                <span class="grid-subtext-wrap">
                  <span class="grid-subtext" meritClamp #clamp="clamp" [class.grid-subtext--clamped]="!expanded().has(rowKey()(row))">{{ sub }}</span>
                  @if (expanded().has(rowKey()(row)) || clamp.overflowing()) {
                    <!-- Clamped, the toggle sits over the end of the second line, so a long detail costs no extra line. -->
                    <button type="button" class="usa-button usa-button--unstyled grid-more" [class.grid-more--over]="!expanded().has(rowKey()(row))"
                            [attr.aria-expanded]="expanded().has(rowKey()(row))" (click)="toggleExpanded(row)">{{ expanded().has(rowKey()(row)) ? 'Show less' : 'Show more' }}</button>
                  }
                </span>
              }
            </td>
          </ng-container>
        }
        <tr cdk-header-row *cdkHeaderRowDef="displayed()"></tr>
        <tr cdk-row *cdkRowDef="let row; columns: displayed(); let i = index" [attr.tabindex]="activeRowKey() === rowKey()(row) ? 0 : -1" [attr.data-index]="i"
            (focusin)="focusedKey.set(rowKey()(row))"
            [class.grid-row--selected]="selectable() && selected().has(rowKey()(row))" (keydown)="onRowKey($event, row, i)"></tr>
      </table>
      @if (rows().length === 0 && !loading()) {
        <p class="grid-empty">{{ emptyText() }}</p>
      }
    </div>
    <div data-grid-pagination [id]="gridId() + '-pagination'" tabindex="-1">
    <merit-pager [label]="caption()" [page]="page()" [size]="pageSize()" [total]="total()" [busy]="loading()" (change)="pageChange.emit($event)" />
    </div>
  `,
  styles: `
    .grid-skip { display: block; position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); }
    .grid-skip:focus { position: static; width: auto; height: auto; clip-path: none; padding: .5rem; }
    .nowrap { white-space: nowrap; }
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
    .nowrap .grid-badge { display: table; margin: .15rem 0 0; }
    .grid-badge { display: inline-block; margin-left: .25rem; padding: 0 .4rem; border-radius: 1rem; background: #b50909; color: #fff; font-size: .75rem; font-weight: 700; }
    .grid-empty { color: #565c65; font-style: italic; }
    .grid-wide { min-width: 18rem; }
    .grid-subtext { display: block; margin-top: .15rem; color: #565c65; font-size: .93em; overflow-wrap: anywhere; }
    .grid-subtext--clamped { display: -webkit-box; -webkit-box-orient: vertical; -webkit-line-clamp: 2; line-clamp: 2; overflow: hidden; }
    .grid-subtext-wrap { display: block; position: relative; }
    .grid-more { font-size: .87em; margin-top: .1rem; }
    .grid-more--over { position: absolute; right: 0; bottom: 0; margin: 0; padding-left: 1.5rem; background: linear-gradient(to right, rgb(255 255 255 / 0), #fff 1.25rem); }
    .grid-row--selected .grid-more--over { background: linear-gradient(to right, rgb(217 232 246 / 0), #d9e8f6 1.25rem); }
    /* Phones: each row is a card of its essentials, labelled, with its checkbox in the corner (issue #154). */
    @media (max-width: 39.99em) {
      .grid-cards { overflow-x: visible; }
      .grid-cards table, .grid-cards tbody, .grid-cards tr[cdk-row], .grid-cards td[cdk-cell] { display: block; width: 100%; }
      .grid-cards thead { display: block; }
      .grid-cards tr[cdk-header-row] { display: flex; flex-wrap: wrap; align-items: center; gap: .25rem 1rem; }
      .grid-cards th[cdk-header-cell] { border: 0; padding: .25rem 0; }
      .grid-cards th:not(.grid-select):not(:has(button)) { display: none; }
      .grid-cards .phone-hidden { display: none !important; }
      .grid-cards .grid-wide { min-width: 0; }
      .grid-cards tr[cdk-row] { position: relative; border: 1px solid #dfe1e2; border-radius: .25rem; margin: 0 0 .5rem; padding: .5rem 3rem .5rem .75rem; }
      .grid-cards td[cdk-cell] { border: 0; padding: .1rem 0; white-space: normal; }
      .grid-cards td[cdk-cell][data-label]:not(:first-of-type)::before { content: attr(data-label) ": "; font-weight: 700; }
      .grid-cards td.grid-select { position: absolute; top: .4rem; right: .5rem; width: auto; padding: 0; }
      .grid-cards td.grid-select + td[cdk-cell][data-label]::before { content: none; }
      .grid-cards td.grid-blank { display: none; }
      .grid-cards .nowrap .grid-badge { display: inline-block; margin: 0 0 0 .25rem; }
    }
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
  readonly selectionDisabled = input(false);
  readonly rowSelectable = input<(row: T) => boolean>(() => true);
  readonly selected = input<ReadonlySet<string>>(new Set<string>());
  readonly rowKey = input.required<(row: T) => string>();
  readonly emptyText = input('Nothing to show.');
  /** Below 40em, show rows as cards rather than a table that needs sideways scrolling (issue #154). */
  readonly cards = input(false);

  readonly sortChange = output<GridSort>();
  readonly pageChange = output<number>();
  readonly selectionChange = output<ReadonlySet<string>>();

  protected readonly focusedKey = signal<string | null>(null);
  /** Rows whose second line is shown in full. */
  protected readonly expanded = signal<ReadonlySet<string>>(new Set<string>());
  protected readonly activeRowKey = computed(() => {
    const key = this.focusedKey();
    return this.rows().some(row => this.rowKey()(row) === key) ? key : this.rows()[0] ? this.rowKey()(this.rows()[0]) : null;
  });

  private readonly hiddenKeys = signal<ReadonlySet<string>>(new Set<string>());

  readonly visibleColumns = computed(() => this.columns().filter(c => !this.isHidden(c)));
  readonly displayed = computed(() => [...(this.selectable() ? ['__select'] : []), ...this.visibleColumns().map(c => c.key)]);
  readonly allSelected = computed(() => this.rows().some(r => this.rowSelectable()(r)) && this.rows().filter(r => this.rowSelectable()(r)).every(r => this.selected().has(this.rowKey()(r))));

  readonly trackRow = (_: number, row: T): string => this.rowKey()(row);

  skipToPager(event: Event): void {
    event.preventDefault();
    (event.currentTarget as HTMLElement).closest('merit-data-grid')?.querySelector<HTMLElement>('[data-grid-pagination]')?.focus();
  }

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

  toggleExpanded(row: T): void {
    const key = this.rowKey()(row);
    const next = new Set(this.expanded());
    next.has(key) ? next.delete(key) : next.add(key);
    this.expanded.set(next);
  }

  sortBy(key: string): void {
    const s = this.sort();
    this.sortChange.emit({ key, descending: s?.key === key ? !s.descending : false });
  }

  sortMark(c: GridColumn<T>): string {
    const s = this.sort();
    return s?.key === c.key ? (s.descending ? '▼' : '▲') : '\u21c5';
  }

  ariaSort(c: GridColumn<T>): string | null {
    const s = this.sort();
    return c.sortable ? (s?.key === c.key ? (s.descending ? 'descending' : 'ascending') : null) : null;
  }

  toggle(row: T): void {
    if (this.selectionDisabled() || !this.rowSelectable()(row)) return;
    const key = this.rowKey()(row);
    const next = new Set(this.selected());
    next.has(key) ? next.delete(key) : next.add(key);
    this.selectionChange.emit(next);
  }

  toggleAll(): void {
    if (this.selectionDisabled()) return;
    const next = new Set(this.selected());
    const keys = this.rows().filter(r => this.rowSelectable()(r)).map(r => this.rowKey()(r));
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
    if (event.target !== tr || this.loading() || event.ctrlKey || event.altKey || event.metaKey) return;
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
