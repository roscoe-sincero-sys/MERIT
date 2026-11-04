import { createHash } from 'node:crypto';
import { readdirSync, readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { pathToFileURL } from 'node:url';
import { PATHS, ROOT } from './config.ts';
import type { Workflow } from './workflow.ts';

/** Every workflow script under workflows/<role>/<feature>/<id>.workflow.ts, with its file and fingerprint. */
export interface Entry { workflow: Workflow; file: string; fingerprint: string }

function find(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap(d =>
    d.isDirectory() ? find(join(dir, d.name)) : d.name.endsWith('.workflow.ts') ? [join(dir, d.name)] : []);
}

/**
 * The fingerprint changes when the workflow script or the tooling that renders it changes; --changed rebuilds only
 * videos whose stored fingerprint differs. Application changes are caught by the dry-run tests instead.
 */
export function fingerprint(file: string): string {
  const hash = createHash('sha256').update(readFileSync(file));
  for (const lib of readdirSync(join(ROOT, 'lib')).sort()) hash.update(readFileSync(join(ROOT, 'lib', lib)));
  return hash.digest('hex').slice(0, 16);
}

export async function loadCatalog(): Promise<Entry[]> {
  const entries: Entry[] = [];
  for (const file of find(PATHS.workflows).sort()) {
    const workflow = (await import(pathToFileURL(file).href)).default as Workflow;
    if (!workflow?.id) throw new Error(`${relative(ROOT, file)} has no default export from defineWorkflow().`);
    if (entries.some(e => e.workflow.id === workflow.id)) throw new Error(`Two workflows use the id ${workflow.id}.`);
    entries.push({ workflow, file, fingerprint: fingerprint(file) });
  }
  return entries;
}

/** Where a workflow's files go: output/<role>/<feature>/<id>.* */
export function outputBase(workflow: Workflow): string {
  return join(PATHS.output, workflow.role, workflow.feature, workflow.id);
}
