namespace Merit.Contracts;

// API request and response types. These define the OpenAPI document (/openapi/v1.json) that the Angular client's
// TypeScript types are generated from; they reference nothing, so the API's shape is stated in one place.

/// <summary>One page of a server-side paged, sorted, filtered list.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int PageNumber, int PageSize);

/// <param name="Codes">Stored code to the words shown for it.</param>
/// <param name="Fields">Laboratory file field key to its label.</param>
/// <param name="FieldOrder">The order fields are shown in.</param>
public sealed record LabelsDto(IReadOnlyDictionary<string, string> Codes, IReadOnlyDictionary<string, string> Fields,
                               IReadOnlyList<string> FieldOrder);

/// <summary>The signed-in user, for the Angular header and permission-aware controls.</summary>
/// <param name="TimeZone">The jurisdiction's time zone as an IANA name, for showing dates and times in the browser.</param>
public sealed record MeDto(string UserName, string DisplayName, string? ActingTenantName, IReadOnlyList<string> Permissions,
                           string TimeZone = "America/New_York");

public sealed record QueueCountDto(string QueueCode, string QueueName, int OpenCount, int UrgentCount, int OverdueCount, int MineCount);

public sealed record WorkTaskDto(
    Guid WorkTaskId,
    string QueueCode,
    string Title,
    string? Detail,
    bool IsUrgent,
    DateOnly? DueDate,
    string TaskStatus,
    DateTime CreatedUtc,
    Guid? PersonId,
    string? PersonName,
    Guid? ExposureCaseId,
    string? CaseNumber,
    Guid? AssignedToUserId,
    string? AssignedToName);

public sealed record BulkTaskActionRequest(IReadOnlyList<Guid> TaskIds, string Action, Guid? AssignToUserId, string? Note);

public sealed record BulkActionResponse(int Updated);

public sealed record AssignableUserDto(Guid AppUserId, string DisplayName);

public sealed record HeldGroupDto(
    string ReasonCode,
    string? ReportedValue,
    Guid? SenderOrganizationId,
    string? SenderName,
    int RowCount,
    int UrgentCount,
    DateTime? OldestHeldUtc,
    int BatchCount,
    string? SampleMessage);

public sealed record HeldRowDto(
    Guid StagingRowId,
    Guid IntakeBatchId,
    string? FileName,
    string? SenderName,
    int RowNumber,
    IReadOnlyDictionary<string, string?> Reported,
    IReadOnlyDictionary<string, string?> Corrected,
    bool IsUrgent,
    DateTime? HeldSinceUtc,
    string? AssignedToName,
    string? Reasons,
    IReadOnlyList<string> ReasonCodes);

public sealed record HeldActionRequest(IReadOnlyList<Guid> StagingRowIds, string? Reason);

public sealed record HeldCorrectionRequest(IReadOnlyDictionary<string, string?> Corrections);

public sealed record ResultListItemDto(
    Guid LabResultId,
    Guid? PersonId,
    string? PersonName,
    DateOnly? Dob,
    string AnalyteCode,
    string Specimen,
    DateOnly CollectedDate,
    DateTime ReceivedUtc,
    string Operator,
    decimal? ValueCanonical,
    string UnitCanonical,
    string? SampleType,
    string? TestMethod,
    string Population,
    string ResultStatus,
    string ResultRowStatus,
    bool IsIndeterminate,
    string? LabName);

public sealed record AnalyteOptionDto(Guid AnalyteId, string Code, string DisplayName);

/// <summary>One event on a person's timeline: a result, an address change, a job change or a case event.</summary>
public sealed record TimelineEventDto(DateOnly Date, DateOnly? EndDate, string Kind, string Label, string? Detail, decimal? Value, string? Unit,
                                      string? SampleType, string? AnalyteCode);

public sealed record PersonTimelineDto(Guid PersonId, string PersonName, IReadOnlyList<TimelineEventDto> Events,
                                       IReadOnlyList<ThresholdLineDto> Thresholds);

public sealed record ThresholdLineDto(string AnalyteCode, string Label, decimal Value, string Unit);

// ---------------------------------------------------------------------------------------------- person matching review

public sealed record MatchQueueItemDto(
    Guid StagingRowId,
    string? FileName,
    string? SenderName,
    int RowNumber,
    string? PatientName,
    string? Dob,
    bool IsUrgent,
    DateTime? HeldSinceUtc,
    string? ReasonMessage,
    bool IsEscalated,
    string? EscalationNote);

public sealed record IncomingFieldDto(string Key, string Label, string? Value);

/// <summary>One field compared: agreement is agree, close, different or missing; the note says why it is close.</summary>
public sealed record MatchFieldDto(string Field, string Label, string? Incoming, string? Candidate, string Agreement, string? Note);

public sealed record CandidatePlaceDto(string Role, bool IsPrimary, DateOnly StartDate, DateOnly? EndDate, string Label);

public sealed record CandidateResultDto(DateOnly CollectedDate, string AnalyteCode, string Value, string? SampleType, string? LabName);

public sealed record MatchCandidateDto(
    Guid PersonId,
    int Rank,
    string PersonName,
    string Explanation,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<MatchFieldDto> Fields,
    string? Aliases,
    int ResultCount,
    DateOnly? LastCollectedDate,
    string? OpenCaseNumbers,
    IReadOnlyList<CandidatePlaceDto> Places,
    IReadOnlyList<CandidateResultDto> Results);

public sealed record MatchReviewDto(
    Guid StagingRowId,
    string? FileName,
    string? SenderName,
    int RowNumber,
    bool IsUrgent,
    bool IsEscalated,
    string? EscalationNote,
    string? ReasonMessage,
    IReadOnlyList<IncomingFieldDto> Incoming,
    IReadOnlyList<MatchCandidateDto> Candidates);

/// <summary>decision: same, different, newPerson or notSure.</summary>
public sealed record MatchDecisionRequest(string Decision, Guid? PersonId, string? Note);

public sealed record MatchDecisionResponse(bool Released, int RemainingCandidates, string Message);

// ---------------------------------------------------------------------------------------------- clusters and map

public sealed record ClusterListItemDto(
    Guid ClusterId,
    string ClusterType,
    string Title,
    string Status,
    DateOnly WindowStart,
    DateOnly WindowEnd,
    int MemberCount,
    DateTime CreatedUtc,
    string? OwningTenantName);

public sealed record ClusterMemberDto(
    Guid ExposureCaseId,
    string CaseNumber,
    Guid PersonId,
    string PersonName,
    string AnalyteCode,
    string Population,
    string Classification,
    string CaseStatus,
    DateOnly OpenedDate,
    decimal? HighestValue,
    string? HighestUnit,
    DateOnly JoinedDate,
    DateOnly? LeftDate,
    string? AddressLabel,
    decimal? Latitude,
    decimal? Longitude);

public sealed record ClusterTaskDto(Guid WorkTaskId, string QueueCode, string Title, string? Detail, DateOnly? DueDate, bool IsUrgent,
                                    string? AssignedToName);

/// <summary>A county's outline for the maps' context layer: a GeoJSON Polygon or MultiPolygon, and where to write its name.</summary>
public sealed record BoundaryDto(string CountyFips, string Name, System.Text.Json.JsonElement Geometry, decimal? LabelLatitude, decimal? LabelLongitude);

public sealed record ClusterDto(
    Guid ClusterId,
    string ClusterType,
    string Title,
    string Status,
    DateOnly WindowStart,
    DateOnly WindowEnd,
    string? Notes,
    string? OwningTenantName,
    string RowVersion,
    IReadOnlyList<ClusterMemberDto> Members,
    IReadOnlyList<ClusterTaskDto> Tasks);

public sealed record ClusterUpdateRequest(string Status, string? Notes, string RowVersion);

public sealed record ClusterMemberRequest(string CaseNumber, bool IsMember);

public sealed record CaseMapPointDto(
    Guid ExposureCaseId,
    string CaseNumber,
    Guid PersonId,
    string PersonName,
    string AnalyteCode,
    string Population,
    string Classification,
    string CaseStatus,
    DateOnly OpenedDate,
    decimal? HighestValue,
    string? HighestUnit,
    string? AddressLabel,
    string? CountyFips,
    decimal? Latitude,
    decimal? Longitude,
    string? GeocodeQuality,
    bool InOpenCluster);

// ---------------------------------------------------------------------------------------------- reports

public sealed record ReportSummaryDto(string Code, string Title, string Description, bool UsesAnalyte);

/// <summary>kind: text, number, percent or month.</summary>
public sealed record ReportColumnDto(string Key, string Label, string Kind);

/// <summary>kind: line or bar. x is a column key; group splits the series; measures are the columns a user can chart.</summary>
public sealed record ReportChartDto(string Kind, string X, string? Group, IReadOnlyList<string> Measures);

public sealed record ReportDto(
    string Code,
    string Title,
    string Description,
    DateOnly FromDate,
    DateOnly ToDate,
    Guid? AnalyteId,
    bool UsesAnalyte,
    bool CanExport,
    IReadOnlyList<ReportColumnDto> Columns,
    ReportChartDto Chart,
    IReadOnlyList<IReadOnlyList<object?>> Rows);
