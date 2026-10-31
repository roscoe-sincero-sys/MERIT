import { DestroyRef, inject, Injectable } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, ParamMap, Router } from '@angular/router';

/** Applied list state lives in the URL, including history entries for browser Back. No draft contents. */
@Injectable()
export class ViewQuery {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroy = inject(DestroyRef);
  private read?: (query: ParamMap) => void;

  watch(read: (query: ParamMap) => void): void {
    this.read = read;
    this.route.queryParamMap.pipe(takeUntilDestroyed(this.destroy)).subscribe(read);
  }

  set(values: Record<string, unknown>, replaceUrl = false): void {
    const options = { relativeTo: this.route, queryParams: values, queryParamsHandling: 'merge' as const };
    if (this.router.serializeUrl(this.router.createUrlTree([], options)) === this.router.url) {
      // Applying the same filters is still a refresh, even though the router correctly skips duplicate URLs.
      this.read?.(this.route.snapshot.queryParamMap);
    } else void this.router.navigate([], { ...options, replaceUrl });
  }

  detail(path: string): string {
    return path + '?returnTo=' + encodeURIComponent(window.location.pathname + window.location.search);
  }
}

export function pageNumber(value: string | null): number {
  const n = Number(value);
  return Number.isSafeInteger(n) && n > 0 && n <= 1_000_000 ? n : 1;
}

export function choice(value: string | null, allowed: string[], fallback: string): string {
  return value !== null && allowed.includes(value) ? value : fallback;
}
