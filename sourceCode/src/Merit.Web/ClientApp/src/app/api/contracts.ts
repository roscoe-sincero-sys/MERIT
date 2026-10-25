// Contract types for /api/v1, mirroring Merit.Contracts (see /openapi/v1.json). They are the client's model and are
// never edited to suit a screen; a screen that needs a different shape maps in its store.

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  pageNumber: number;
  pageSize: number;
}

export interface MeDto {
  userName: string;
  displayName: string;
  actingTenantName: string | null;
  permissions: string[];
}

export interface QueueCountDto {
  queueCode: string;
  queueName: string;
  openCount: number;
  urgentCount: number;
  overdueCount: number;
  mineCount: number;
}

export interface WorkTaskDto {
  workTaskId: string;
  queueCode: string;
  title: string;
  detail: string | null;
  isUrgent: boolean;
  dueDate: string | null;
  taskStatus: string;
  createdUtc: string;
  personId: string | null;
  personName: string | null;
  exposureCaseId: string | null;
  caseNumber: string | null;
  assignedToUserId: string | null;
  assignedToName: string | null;
}

export interface BulkTaskActionRequest {
  taskIds: string[];
  action: 'assignToMe' | 'assign' | 'complete' | 'dismiss' | 'reopen';
  assignToUserId: string | null;
  note: string | null;
}

export interface BulkActionResponse {
  updated: number;
}

export interface AssignableUserDto {
  appUserId: string;
  displayName: string;
}

export interface HeldGroupDto {
  reasonCode: string;
  reportedValue: string | null;
  senderName: string | null;
  rowCount: number;
  urgentCount: number;
  oldestHeldUtc: string | null;
  batchCount: number;
  sampleMessage: string | null;
}

export interface HeldRowDto {
  stagingRowId: string;
  intakeBatchId: string;
  fileName: string | null;
  senderName: string | null;
  rowNumber: number;
  reported: Record<string, string | null>;
  corrected: Record<string, string | null>;
  isUrgent: boolean;
  heldSinceUtc: string | null;
  assignedToName: string | null;
  reasons: string | null;
}

export interface HeldActionRequest {
  stagingRowIds: string[];
  reason: string | null;
}

export interface ResultListItemDto {
  labResultId: string;
  personId: string | null;
  personName: string | null;
  dob: string | null;
  analyteCode: string;
  specimen: string;
  collectedDate: string;
  receivedUtc: string;
  operator: string;
  valueCanonical: number | null;
  unitCanonical: string;
  sampleType: string | null;
  testMethod: string | null;
  population: string;
  resultStatus: string;
  resultRowStatus: string;
  isIndeterminate: boolean;
  labName: string | null;
}

export interface AnalyteOptionDto {
  analyteId: string;
  code: string;
  displayName: string;
}

export interface TimelineEventDto {
  date: string;
  endDate: string | null;
  kind: 'result' | 'place' | 'job' | 'case' | 'pregnancy';
  label: string;
  detail: string | null;
  value: number | null;
  unit: string | null;
  sampleType: string | null;
  analyteCode: string | null;
}

export interface PersonTimelineDto {
  personId: string;
  personName: string;
  events: TimelineEventDto[];
}

// ---------------------------------------------------------------------------------------------- person matching review

export interface MatchQueueItemDto {
  stagingRowId: string;
  fileName: string | null;
  senderName: string | null;
  rowNumber: number;
  patientName: string | null;
  dob: string | null;
  isUrgent: boolean;
  heldSinceUtc: string | null;
  reasonMessage: string | null;
  isEscalated: boolean;
  escalationNote: string | null;
}

export interface IncomingFieldDto {
  key: string;
  label: string;
  value: string | null;
}

export type Agreement = 'agree' | 'close' | 'different' | 'missing';

export interface MatchFieldDto {
  field: string;
  label: string;
  incoming: string | null;
  candidate: string | null;
  agreement: Agreement;
  note: string | null;
}

export interface CandidatePlaceDto {
  role: string;
  isPrimary: boolean;
  startDate: string;
  endDate: string | null;
  label: string;
}

export interface CandidateResultDto {
  collectedDate: string;
  analyteCode: string;
  value: string;
  sampleType: string | null;
  labName: string | null;
}

export interface MatchCandidateDto {
  personId: string;
  rank: number;
  personName: string;
  explanation: string;
  warnings: string[];
  fields: MatchFieldDto[];
  aliases: string | null;
  resultCount: number;
  lastCollectedDate: string | null;
  openCaseNumbers: string | null;
  places: CandidatePlaceDto[];
  results: CandidateResultDto[];
}

export interface MatchReviewDto {
  stagingRowId: string;
  fileName: string | null;
  senderName: string | null;
  rowNumber: number;
  isUrgent: boolean;
  isEscalated: boolean;
  escalationNote: string | null;
  reasonMessage: string | null;
  incoming: IncomingFieldDto[];
  candidates: MatchCandidateDto[];
}

export type MatchDecision = 'same' | 'different' | 'newPerson' | 'notSure';

export interface MatchDecisionRequest {
  decision: MatchDecision;
  personId: string | null;
  note: string | null;
}

export interface MatchDecisionResponse {
  released: boolean;
  remainingCandidates: number;
  message: string;
}

// ---------------------------------------------------------------------------------------------- clusters and map

export type ClusterStatus = 'open' | 'underReview' | 'closed';

export interface ClusterListItemDto {
  clusterId: string;
  clusterType: string;
  title: string;
  status: ClusterStatus;
  windowStart: string;
  windowEnd: string;
  memberCount: number;
  createdUtc: string;
  owningTenantName: string | null;
}

export interface ClusterMemberDto {
  exposureCaseId: string;
  caseNumber: string;
  personId: string;
  personName: string;
  analyteCode: string;
  population: string;
  classification: string;
  caseStatus: string;
  openedDate: string;
  highestValue: number | null;
  highestUnit: string | null;
  joinedDate: string;
  leftDate: string | null;
  addressLabel: string | null;
  latitude: number | null;
  longitude: number | null;
}

export interface ClusterTaskDto {
  workTaskId: string;
  title: string;
  detail: string | null;
  dueDate: string | null;
  isUrgent: boolean;
  assignedToName: string | null;
}

export interface ClusterDto {
  clusterId: string;
  clusterType: string;
  title: string;
  status: ClusterStatus;
  windowStart: string;
  windowEnd: string;
  notes: string | null;
  owningTenantName: string | null;
  rowVersion: string;
  members: ClusterMemberDto[];
  tasks: ClusterTaskDto[];
}

export interface ClusterUpdateRequest {
  status: ClusterStatus;
  notes: string | null;
  rowVersion: string;
}

export interface ClusterMemberRequest {
  caseNumber: string;
  isMember: boolean;
}

export interface CaseMapPointDto {
  exposureCaseId: string;
  caseNumber: string;
  personId: string;
  personName: string;
  analyteCode: string;
  population: string;
  classification: string;
  caseStatus: string;
  openedDate: string;
  highestValue: number | null;
  highestUnit: string | null;
  addressLabel: string | null;
  countyFips: string | null;
  latitude: number | null;
  longitude: number | null;
  geocodeQuality: string | null;
  inOpenCluster: boolean;
}

// ---------------------------------------------------------------------------------------------- reports

export interface ReportSummaryDto {
  code: string;
  title: string;
  description: string;
  usesAnalyte: boolean;
}

export interface ReportColumnDto {
  key: string;
  label: string;
  kind: 'text' | 'number' | 'percent' | 'month';
}

export interface ReportChartDto {
  kind: 'line' | 'bar';
  x: string;
  group: string | null;
  measures: string[];
}

export type ReportCell = string | number | boolean | null;

export interface ReportDto {
  code: string;
  title: string;
  description: string;
  fromDate: string;
  toDate: string;
  analyteId: string | null;
  usesAnalyte: boolean;
  canExport: boolean;
  columns: ReportColumnDto[];
  chart: ReportChartDto;
  rows: ReportCell[][];
}
