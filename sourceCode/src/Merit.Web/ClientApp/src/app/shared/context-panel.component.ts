import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, inject, input, OnInit, signal, viewChild } from '@angular/core';

/** Reclaims working width on the two pages that already have contextual navigation (#137). */
@Component({
  selector: 'merit-context-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="layout" [class.collapsed]="!expanded()" (keydown.escape)="dismiss($event)">
      <div class="panel">
        @if (enabled) {
          <button #toggleButton type="button" class="usa-button usa-button--outline toggle"
                  [attr.aria-controls]="panelId()" [attr.aria-expanded]="expanded()" (click)="toggle()">
            {{ expanded() ? 'Hide' : 'Show' }} {{ label() }}
          </button>
        }
        <div #navigation [id]="panelId()" [hidden]="!expanded()" (click)="choose($event)">
          <ng-content select="[panelNavigation]" />
        </div>
      </div>
      <div class="content"><ng-content /></div>
    </div>
  `,
  styles: `
    :host, .content, .panel { display: block; min-width: 0; }
    .layout { display: grid; grid-template-columns: minmax(12rem, 18rem) minmax(0, 1fr); gap: 1.5rem; }
    .layout.collapsed { grid-template-columns: minmax(0, 1fr); gap: .5rem; }
    .toggle { margin: 0 0 .5rem; }
    @media (max-width: 64em) { .layout { grid-template-columns: minmax(0, 1fr); } }
  `,
})
export class ContextPanelComponent implements OnInit {
  readonly panelId = input.required<string>();
  readonly label = input.required<string>();
  protected readonly enabled = document.querySelector('header')?.getAttribute('data-enhanced-navigation') === 'true';
  protected readonly expanded = signal(true);
  private readonly navigation = viewChild.required<ElementRef<HTMLElement>>('navigation');
  private readonly toggleButton = viewChild<ElementRef<HTMLButtonElement>>('toggleButton');
  private readonly destroy = inject(DestroyRef);
  private readonly narrow = window.matchMedia('(max-width: 64em)');
  private desktopExpanded = true;

  ngOnInit(): void {
    if (!this.enabled) return;
    try { this.desktopExpanded = localStorage.getItem(this.storageKey()) !== 'false'; } catch { /* Storage may be disabled. */ }
    this.expanded.set(!this.narrow.matches && this.desktopExpanded);
    const resize = () => this.setExpanded(!this.narrow.matches && this.desktopExpanded);
    this.narrow.addEventListener('change', resize);
    this.destroy.onDestroy(() => this.narrow.removeEventListener('change', resize));
  }

  protected toggle(): void {
    const open = !this.expanded();
    this.setExpanded(open);
    // Narrow-screen disclosure choices must not overwrite a user's desktop preference.
    if (!this.narrow.matches) {
      this.desktopExpanded = open;
      try { localStorage.setItem(this.storageKey(), String(open)); } catch { /* Disclosure still works without persistence. */ }
    }
  }

  protected dismiss(event: Event): void {
    if (!this.enabled || !this.expanded()) return;
    if (!this.navigation().nativeElement.contains(event.target as Node) && event.target !== this.toggleButton()?.nativeElement) return;
    event.preventDefault();
    this.setExpanded(false);
    this.toggleButton()?.nativeElement.focus();
  }

  protected choose(event: Event): void {
    if (this.enabled && this.narrow.matches && (event.target as HTMLElement).closest('a')) this.setExpanded(false);
  }

  private setExpanded(open: boolean): void {
    if (!open && this.navigation().nativeElement.contains(document.activeElement)) this.toggleButton()?.nativeElement.focus();
    this.expanded.set(open);
  }

  private storageKey(): string { return `merit.context-panel.${this.panelId()}`; }
}
