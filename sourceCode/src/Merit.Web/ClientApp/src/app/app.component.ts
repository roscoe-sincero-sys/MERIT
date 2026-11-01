import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterOutlet } from '@angular/router';

/** The shared header and navigation come from the Razor host view; Angular draws only the main region. */
@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<router-outlet />`,
})
export class AppComponent {
  private readonly router = inject(Router);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly destroy = inject(DestroyRef);
  private observer: MutationObserver | null = null;
  private path = '';

  constructor() {
    this.destroy.onDestroy(() => this.observer?.disconnect());
    this.router.events.pipe(takeUntilDestroyed(this.destroy)).subscribe(event => {
      if (!(event instanceof NavigationEnd)) return;
      const path = '/app' + event.urlAfterRedirects.split('?')[0].split('#')[0];
      document.querySelectorAll<HTMLAnchorElement>('.merit-nav a').forEach(link => {
        const target = new URL(link.href).pathname;
        link.setAttribute('aria-current', path === target || (target !== '/' && path.startsWith(target + '/')) ? 'page' : 'false');
      });
      if (path === this.path) return;
      document.dispatchEvent(new Event('merit:navigation'));
      this.path = path;
      this.observer?.disconnect();
      const focus = () => {
        const heading = this.host.nativeElement.querySelector<HTMLElement>('h1');
        if (!heading) return;
        this.observer?.disconnect();
        heading.tabIndex = -1;
        heading.focus();
        const live = document.getElementById('live-region');
        if (live) live.textContent = heading.textContent?.trim() + ' - MERIT';
      };
      this.observer = new MutationObserver(focus);
      this.observer.observe(this.host.nativeElement, { childList: true, subtree: true });
      requestAnimationFrame(focus);
    });
  }
}
