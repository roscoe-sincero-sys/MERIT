import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';

@Component({
  selector: 'merit-pager',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<nav [attr.aria-label]="label() + ' pages'">
    <button class="usa-button usa-button--outline usa-button--small" type="button" [disabled]="busy() || page() <= 1" (click)="change.emit(page() - 1)">Previous page</button>
    <span aria-live="polite">{{ total() === 0 ? 0 : (page() - 1) * size() + 1 }}–{{ end() }} of {{ total() }}; Page {{ page() }} of {{ pages() }}</span>
    <button class="usa-button usa-button--outline usa-button--small" type="button" [disabled]="busy() || page() >= pages()" (click)="change.emit(page() + 1)">Next page</button>
  </nav>`,
  styles: `nav { display: flex; align-items: center; gap: .5rem; flex-wrap: wrap; margin: .5rem 0; }`,
})
export class PagerComponent {
  readonly label = input('Records');
  readonly page = input(1);
  readonly size = input(50);
  readonly total = input(0);
  readonly busy = input(false);
  readonly change = output<number>();
  protected readonly pages = computed(() => Math.max(1, Math.ceil(this.total() / this.size())));
  protected readonly end = computed(() => Math.min(this.page() * this.size(), this.total()));
}
