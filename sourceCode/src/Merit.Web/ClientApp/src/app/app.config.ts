import { ApplicationConfig, inject, provideAppInitializer, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { APP_BASE_HREF } from '@angular/common';
import { provideHttpClient, withFetch, withXsrfConfiguration } from '@angular/common/http';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { routes } from './app.routes';
import { Display } from './shared/display.service';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZonelessChangeDetection(),
    // The Razor host page has no <base> element (its links are site-absolute), so the router is told its root here.
    { provide: APP_BASE_HREF, useValue: '/app/' },
    provideRouter(routes, withComponentInputBinding()),
    // The antiforgery token travels in X-XSRF-TOKEN, read from the XSRF-TOKEN cookie the server sets - the same header
    // htmx sends from merit.js, so both front ends share one antiforgery scheme.
    provideHttpClient(withFetch(), withXsrfConfiguration({ cookieName: 'XSRF-TOKEN', headerName: 'X-XSRF-TOKEN' })),
    // The words for stored codes, before any page renders a cell.
    provideAppInitializer(() => inject(Display).load()),
  ],
};
