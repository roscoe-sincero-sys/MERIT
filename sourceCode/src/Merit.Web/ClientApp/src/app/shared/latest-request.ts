import { DestroyRef, inject } from '@angular/core';
import { Observable, Subscription } from 'rxjs';

/**
 * Runs one read request at a time for a logical slot: starting a new read cancels any in-flight subscription (aborting the
 * underlying HttpClient request) and checks a monotonic request identity so stale or out-of-order emissions never overwrite
 * newer selections or filters. Cancels automatically when its owning component or service is destroyed.
 */
export class LatestRequest {
  private sub: Subscription | null = null;
  private seq = 0;

  constructor(destroyRef?: DestroyRef | null) {
    const ref = destroyRef === undefined ? inject(DestroyRef, { optional: true }) : destroyRef;
    ref?.onDestroy(() => this.cancel());
  }

  /** Cancels any active read in this slot and invalidates its request identity. */
  cancel(): void {
    this.seq++;
    const active = this.sub;
    this.sub = null;
    active?.unsubscribe();
  }

  /** Subscribes to `source`, cancelling any previous read in this slot and delivering only the latest request's outcome. */
  run<T>(source: Observable<T>, observer: { next: (value: T) => void; error?: (err: unknown) => void }): void {
    this.cancel();
    const id = this.seq;
    const sub = new Subscription();
    this.sub = sub;
    sub.add(source.subscribe({
      next: value => {
        if (id !== this.seq) return;
        observer.next(value);
      },
      error: err => {
        if (id !== this.seq) return;
        if (this.sub === sub) this.sub = null;
        observer.error?.(err);
      },
      complete: () => {
        if (this.sub === sub) this.sub = null;
      },
    }));
    if (sub.closed && this.sub === sub) {
      this.sub = null;
    }
  }

  get active(): boolean {
    return this.sub !== null && !this.sub.closed;
  }
}
