import { DATABASE, SERVER, run } from './config.ts';

/** Runs a query against the training database as the deployment identity (Windows authentication). */
export function sql(query: string): string {
  return run('sqlcmd', ['-S', SERVER, '-E', '-C', '-I', '-d', DATABASE, '-b', '-h', '-1', '-W', '-Q', `SET NOCOUNT ON; ${query}`]);
}

/** The rows of a one-column query, trimmed. */
export function sqlValues(query: string): string[] {
  return sql(query).split(/\r?\n/).map(l => l.trim()).filter(Boolean);
}

/** Quotes a value as an N'' literal. */
export function lit(value: string): string {
  return `N'${value.replace(/'/g, "''")}'`;
}
