import { execFileSync } from 'node:child_process';

/**
 * The database the MERIT instance under test uses, for the setup and the specs that reach it directly. One default for
 * all of them (issue #175): a spec that defaulted to another database signed in to an account the app could not see.
 *   MERIT_E2E_SERVER    SQL Server instance (default localhost\PORTAL)
 *   MERIT_E2E_DATABASE  database the instance uses (default MERIT)
 */
export const E2E_SERVER = process.env.MERIT_E2E_SERVER ?? 'localhost\\PORTAL';
export const E2E_DATABASE = process.env.MERIT_E2E_DATABASE ?? 'MERIT';

/**
 * Specs that create their own accounts or synthetic records run only against the integration database, never the
 * everyday demo; elsewhere they skip with this reason.
 */
export const ISOLATED_DATA_ONLY = E2E_DATABASE === 'MERIT_IT' ? null : 'Creates isolated records, so it runs only when MERIT_E2E_DATABASE is MERIT_IT.';

/** Runs a statement as the deployment identity (Windows authentication). */
export function sql(query: string): Buffer {
  return execFileSync('sqlcmd', ['-S', E2E_SERVER, '-E', '-C', '-I', '-d', E2E_DATABASE, '-b', '-Q', query]);
}
