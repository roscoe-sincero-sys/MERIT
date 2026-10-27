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

  async load(): Promise<void> {
    try {
      const labels = await firstValueFrom(this.api.labels());
      this.codes = labels.codes;
      this.fields = labels.fields;
      this.order = labels.fieldOrder;
    } catch {
      // Signed out or offline: codes fall back to the camel-case split below.
    }
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

  /** File field keys in the order a person checks them: known fields first. */
  fieldOrder(keys: string[]): string[] {
    const rank = (k: string) => { const i = this.order.indexOf(k); return i < 0 ? Number.MAX_SAFE_INTEGER : i; };
    return [...keys].sort((a, b) => rank(a) - rank(b));
  }
}

/** "icpMs" becomes "Icp ms": only for a code with no name. */
function split(code: string): string {
  return code.charAt(0).toUpperCase() + code.slice(1).replace(/[A-Z]/g, c => ' ' + c.toLowerCase());
}
