import { ChangeDetectionStrategy, Component } from '@angular/core';
import { CanMatchFn } from '@angular/router';
import { inject } from '@angular/core';
import { Display } from './display.service';

/**
 * What a screen shows when the profile does not allow it: the same words as the server-rendered pages' "Not permitted"
 * page, rather than a request error over an empty list (issue #161). The address stays as it was.
 */
@Component({ selector: 'merit-not-permitted-page', changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h1 tabindex="-1">Not permitted</h1>
    <div class="usa-alert usa-alert--warning usa-alert--slim"><div class="usa-alert__body">
      <p class="usa-alert__text">Your profile does not allow this page.</p>
    </div></div>
    <p>If your work needs it, switch to a profile that includes it, or ask your MERIT administrator.</p>
    <p><a class="usa-link" href="/account/profiles">Switch profile</a> &middot; <a class="usa-link" href="/">Go to Home</a></p>
  `,
})
export class NotPermittedPage {}

/** A route the user may open only with one of these permissions; otherwise the next route for the path answers. */
export function requires(...permissions: string[]): CanMatchFn {
  return () => {
    const display = inject(Display);
    return permissions.some(p => display.can(p));
  };
}
