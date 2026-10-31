import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({ selector: 'merit-not-found-page', changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<h1 tabindex="-1">Page not found</h1><p>This address is not part of MERIT. Check the address or use the navigation menu.</p><p><a class="usa-link" href="/">Go to Home</a></p>`,
})
export class NotFoundPage {}
