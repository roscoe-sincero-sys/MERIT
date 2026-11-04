import { mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { DATABASE, PATHS, SERVER, run } from './config.ts';

/**
 * Runs a query against the training database as the deployment identity (Windows authentication). The query goes
 * through a file (-i), because Windows command-line quoting mangles double quotes, which JSON arguments contain.
 */
export function sql(query: string): string {
  mkdirSync(PATHS.work, { recursive: true });
  const file = join(PATHS.work, `query-${process.pid}-${Date.now()}.sql`);
  writeFileSync(file, `SET NOCOUNT ON;\n${query}\n`, 'utf8');
  try {
    return run('sqlcmd', ['-S', SERVER, '-E', '-C', '-I', '-d', DATABASE, '-b', '-h', '-1', '-W', '-f', '65001', '-i', file]);
  } finally {
    rmSync(file, { force: true });
  }
}

/** The rows of a one-column query, trimmed. */
export function sqlValues(query: string): string[] {
  return sql(query).split(/\r?\n/).map(l => l.trim()).filter(Boolean);
}

/** Quotes a value as an N'' literal. */
export function lit(value: string): string {
  return `N'${value.replace(/'/g, "''")}'`;
}
