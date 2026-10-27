import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import {
  AnalyteOptionDto, AssignableUserDto, BulkActionResponse, BulkTaskActionRequest, CaseMapPointDto, ClusterDto, ClusterListItemDto,
  ClusterMemberRequest, ClusterUpdateRequest, HeldActionRequest, HeldGroupDto, HeldRowDto, LabelsDto, MatchDecisionRequest, MatchDecisionResponse,
  MatchQueueItemDto, MatchReviewDto, MeDto, PagedResult, PersonTimelineDto, QueueCountDto, ReportDto, ReportSummaryDto,
  ResultListItemDto, WorkTaskDto,
} from './contracts';

type Query = Record<string, string | number | boolean | null | undefined>;

/** The only code in the client that calls HttpClient. Same origin, session cookie, no tokens in browser storage. */
@Injectable({ providedIn: 'root' })
export class ApiClient {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/v1';

  labels(): Observable<LabelsDto> {
    return this.http.get<LabelsDto>(`${this.base}/reference/labels`);
  }

  me(): Observable<MeDto> {
    return this.http.get<MeDto>(`${this.base}/me`);
  }

  queues(): Observable<QueueCountDto[]> {
    return this.http.get<QueueCountDto[]>(`${this.base}/queues`);
  }

  tasks(q: Query): Observable<PagedResult<WorkTaskDto>> {
    return this.http.get<PagedResult<WorkTaskDto>>(`${this.base}/tasks`, { params: params(q) });
  }

  bulkTasks(request: BulkTaskActionRequest): Observable<BulkActionResponse> {
    return this.http.post<BulkActionResponse>(`${this.base}/tasks/bulk`, request);
  }

  assignableUsers(): Observable<AssignableUserDto[]> {
    return this.http.get<AssignableUserDto[]>(`${this.base}/users/assignable`);
  }

  heldGroups(): Observable<HeldGroupDto[]> {
    return this.http.get<HeldGroupDto[]>(`${this.base}/held/groups`);
  }

  heldRows(q: Query): Observable<PagedResult<HeldRowDto>> {
    return this.http.get<PagedResult<HeldRowDto>>(`${this.base}/held/rows`, { params: params(q) });
  }

  correctHeld(id: string, corrections: Record<string, string | null>): Observable<void> {
    return this.http.put<void>(`${this.base}/held/rows/${id}/correction`, { corrections });
  }

  releaseHeld(request: HeldActionRequest): Observable<BulkActionResponse> {
    return this.http.post<BulkActionResponse>(`${this.base}/held/release`, request);
  }

  rejectHeld(request: HeldActionRequest): Observable<BulkActionResponse> {
    return this.http.post<BulkActionResponse>(`${this.base}/held/reject`, request);
  }

  analytes(): Observable<AnalyteOptionDto[]> {
    return this.http.get<AnalyteOptionDto[]>(`${this.base}/analytes`);
  }

  results(q: Query): Observable<PagedResult<ResultListItemDto>> {
    return this.http.get<PagedResult<ResultListItemDto>>(`${this.base}/results`, { params: params(q) });
  }

  timeline(personId: string): Observable<PersonTimelineDto> {
    return this.http.get<PersonTimelineDto>(`${this.base}/people/${personId}/timeline`);
  }

  matchQueue(q: Query): Observable<PagedResult<MatchQueueItemDto>> {
    return this.http.get<PagedResult<MatchQueueItemDto>>(`${this.base}/match-review`, { params: params(q) });
  }

  matchReview(stagingRowId: string): Observable<MatchReviewDto> {
    return this.http.get<MatchReviewDto>(`${this.base}/match-review/${stagingRowId}`);
  }

  matchDecision(stagingRowId: string, request: MatchDecisionRequest): Observable<MatchDecisionResponse> {
    return this.http.post<MatchDecisionResponse>(`${this.base}/match-review/${stagingRowId}/decision`, request);
  }

  clusters(q: Query): Observable<PagedResult<ClusterListItemDto>> {
    return this.http.get<PagedResult<ClusterListItemDto>>(`${this.base}/clusters`, { params: params(q) });
  }

  cluster(clusterId: string): Observable<ClusterDto> {
    return this.http.get<ClusterDto>(`${this.base}/clusters/${clusterId}`);
  }

  updateCluster(clusterId: string, request: ClusterUpdateRequest): Observable<ClusterDto> {
    return this.http.put<ClusterDto>(`${this.base}/clusters/${clusterId}`, request);
  }

  setClusterMember(clusterId: string, request: ClusterMemberRequest): Observable<ClusterDto> {
    return this.http.post<ClusterDto>(`${this.base}/clusters/${clusterId}/members`, request);
  }

  caseMap(q: Query): Observable<CaseMapPointDto[]> {
    return this.http.get<CaseMapPointDto[]>(`${this.base}/cases/map`, { params: params(q) });
  }

  reports(): Observable<ReportSummaryDto[]> {
    return this.http.get<ReportSummaryDto[]>(`${this.base}/reports`);
  }

  report(code: string, q: Query): Observable<ReportDto> {
    return this.http.get<ReportDto>(`${this.base}/reports/${code}`, { params: params(q) });
  }

  /** A same-origin link for a report export; the browser downloads it with the session cookie. */
  reportExportUrl(code: string, q: Query): string {
    return `${this.base}/reports/${code}/export?${params(q).toString()}`;
  }
}

function params(q: Query): HttpParams {
  let p = new HttpParams();
  for (const [k, v] of Object.entries(q)) {
    if (v !== null && v !== undefined && v !== '') {
      p = p.set(k, String(v));
    }
  }
  return p;
}

/** A plain-language message for a failed request (RFC 9457 problem details from the server, or a lost connection). */
export function problemMessage(error: unknown): string {
  const e = error as { status?: number; error?: { detail?: string; title?: string } };
  if (!e || e.status === 0) {
    return 'The connection to MERIT was lost. Check your network and try again.';
  }
  if (e.status === 401) {
    window.location.href = '/account/signin?ReturnUrl=' + encodeURIComponent(window.location.pathname + window.location.search);
    return 'Your session ended. Signing in again...';
  }
  return e.error?.detail ?? e.error?.title ?? 'The request could not be completed.';
}
