import { sql } from './sql.ts';

/**
 * Training-data resets for workflow prepare() steps. Records a video creates are marked (results by an accession number
 * starting TRN-), and these put the training database back by soft-deleting them and what they caused: the case a
 * result opened, its activity, investigations and tasks. Nothing is hard-deleted, as everywhere in MERIT. Case numbers
 * come from a sequence, so a rebuilt video can show the next number.
 */
const BY = "N'merit-training'";

export function resetTrainingResults(prefix = 'TRN-'): void {
  const results = `SELECT labResultId FROM dbo.labResult WHERE accessionNumber LIKE N'${prefix.replace(/'/g, "''")}%'`;
  const cases = `SELECT exposureCaseId FROM dbo.exposureCase WHERE qualifyingLabResultId IN (${results}) OR highestLabResultId IN (${results})`;
  const gone = (table: string, where: string) =>
    `UPDATE ${table} SET isDeleted = 1, deletedBy = ${BY}, deletedDateUtc = SYSUTCDATETIME() WHERE isDeleted = 0 AND (${where});`;
  sql([
    'SET XACT_ABORT ON; BEGIN TRANSACTION;',
    gone('dbo.workTask', `labResultId IN (${results}) OR exposureCaseId IN (${cases})`),
    gone('dbo.caseActivity', `exposureCaseId IN (${cases})`),
    gone('dbo.investigationCase', `exposureCaseId IN (${cases})`),
    gone('dbo.caseCluster', `exposureCaseId IN (${cases})`),
    gone('dbo.exposureCase', `exposureCaseId IN (${cases})`),
    gone('dbo.labResult', `labResultId IN (${results})`),
    'COMMIT;',
  ].join('\n'));
}
