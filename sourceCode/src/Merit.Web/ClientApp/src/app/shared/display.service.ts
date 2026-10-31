import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../api/api-client';

/**
 * How stored values read on screen, the same as on the server-rendered pages: codes as words ("icpMs" is "ICP-MS",
 * "uncertainMatch" is "Uncertain person match") and laboratory file fields by their labels (issues #37 and #38). Loaded
 * once when the client starts, so grid cells can use it synchronously.
 */
@Injectable({ providedIn: 'root' })
export class Display {
  private readonly api = inject(ApiClient);
  private codes: Record<string, string> = {};
  private fields: Record<string, string> = {};
  private order: string[] = [];
  private dayFormat = dayFormatIn('America/New_York');

  async load(): Promise<void> {
    try {
      const [labels, me] = await Promise.all([firstValueFrom(this.api.labels()), firstValueFrom(this.api.me())]);
      this.codes = labels.codes;
      this.fields = labels.fields;
      this.order = labels.fieldOrder;
      this.dayFormat = dayFormatIn(me.timeZone);
    } catch {
      // Signed out or offline: codes fall back to the camel-case split below, dates to Eastern time.
    }
  }

  /** A date-only value ("2026-10-27") as "Oct 27, 2026", the format the server-rendered pages use (issue #39). */
  date(value: string | null | undefined): string {
    if (!value) return '';
    const [y, m, d] = value.substring(0, 10).split('-').map(Number);
    return DATE_ONLY.format(new Date(Date.UTC(y, m - 1, d)));
  }

  /** A stored UTC instant as its date in the jurisdiction's time zone (issue #36). */
  dateOf(instant: string | null | undefined): string {
    if (!instant) return '';
    return this.dayFormat.format(new Date(/[zZ]|[+-]\d\d:\d\d$/.test(instant) ? instant : instant + 'Z'));
  }

  /** A report month ("2026-07") as "Jul 2026". */
  month(value: string): string {
    const [y, m] = value.split('-').map(Number);
    return MONTH.format(new Date(Date.UTC(y, m - 1, 1)));
  }

  /** The words for a stored code. */
  of(code: string | null | undefined): string {
    if (!code) return '';
    return this.codes[code] ?? split(code);
  }

  /** The words for a value only when it is a known code; any other text (a name, a number) is shown as it is. */
  ifCode(value: string): string {
    return this.codes[value] ?? value;
  }

  /** A laboratory file field's label. */
  field(key: string): string {
    return this.fields[key] ?? split(key);
  }

  stagingFields(): string[] { return [...this.order]; }

  /** File field keys in the order a person checks them: known fields first. */
  fieldOrder(keys: string[]): string[] {
    const rank = (k: string) => { const i = this.order.indexOf(k); return i < 0 ? Number.MAX_SAFE_INTEGER : i; };
    return [...keys].sort((a, b) => rank(a) - rank(b));
  }
}

const DATE_ONLY = new Intl.DateTimeFormat('en-US', { month: 'short', day: 'numeric', year: 'numeric', timeZone: 'UTC' });
const MONTH = new Intl.DateTimeFormat('en-US', { month: 'short', year: 'numeric', timeZone: 'UTC' });

function dayFormatIn(timeZone: string): Intl.DateTimeFormat {
  try {
    return new Intl.DateTimeFormat('en-US', { month: 'short', day: 'numeric', year: 'numeric', timeZone });
  } catch {
    return new Intl.DateTimeFormat('en-US', { month: 'short', day: 'numeric', year: 'numeric', timeZone: 'America/New_York' });
  }
}

/** "icpMs" becomes "Icp ms": only for a code with no name. */
function split(code: string): string {
  return code.charAt(0).toUpperCase() + code.slice(1).replace(/[A-Z]/g, c => ' ' + c.toLowerCase());
}
