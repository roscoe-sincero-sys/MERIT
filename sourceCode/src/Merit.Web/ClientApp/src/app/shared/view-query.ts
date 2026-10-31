import { DestroyRef, inject, Injectable } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, ParamMap, Router } from '@angular/router';

/** Applied list state lives in the URL, including history entries for browser Back. No draft contents. */
@Injectable()
export class ViewQuery {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroy = inject(DestroyRef);
  private read?: (query: ParamMap, refresh: boolean) => void;
  private loadedQuery: string | null = null;

  watch(read: (query: ParamMap, refresh: boolean) => void): void {
    this.read = read;
    this.route.queryParamMap.pipe(takeUntilDestroyed(this.destroy)).subscribe(query => {
      const loaded = this.loadedQuery;
      this.loadedQuery = null;
      if (loaded !== null && loaded === this.queryKey(query)) return;
      read(query, false);
    });
  }

  set(values: Record<string, unknown>, replaceUrl = false): void {
    this.navigate(values, replaceUrl, true);
  }

  /** Update the URL to describe data already returned by the server, without fetching those data again. */
  replaceLoaded(values: Record<string, unknown>): void {
    this.navigate(values, true, false);
  }

  refresh(): void { this.read?.(this.route.snapshot.queryParamMap, true); }

  private navigate(values: Record<string, unknown>, replaceUrl: boolean, read: boolean): void {
    const options = { relativeTo: this.route, queryParams: values, queryParamsHandling: 'merge' as const };
    const target = this.router.createUrlTree([], options);
    this.loadedQuery = null;
    if (this.router.serializeUrl(target) === this.router.url) {
      // Applying the same filters is still a refresh, even though the router correctly skips duplicate URLs.
      if (read) this.refresh();
    } else {
      const loaded = read ? null : this.queryKey(target.queryParamMap);
      this.loadedQuery = loaded;
      void this.router.navigateByUrl(target, { replaceUrl }).then(navigated => {
        if (!navigated && this.loadedQuery === loaded) this.loadedQuery = null;
      });
    }
  }

  private queryKey(query: ParamMap): string { return JSON.stringify(query.keys.sort().map(k => [k, query.getAll(k)])); }

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
