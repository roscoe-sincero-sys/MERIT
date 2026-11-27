namespace Merit.Application.Data;

/// <summary>
/// Data access for behavioral anomaly detection (#327), structured log export (#328), search log retention &amp;
/// reporting (#329), unified per-user behavior summary &amp; operational logs (#330), and row-change history (#332).
/// </summary>
public interface ISecurityMonitoringData
{
    // #329 searchLog
    Task<int> PurgeSearchLogAsync(int retentionDays, CancellationToken ct = default);
    Task<IReadOnlyList<SearchLogSummaryRow>> GetSearchLogSummaryAsync(int days, Guid? appUserId = null, CancellationToken ct = default);
    Task<IReadOnlyList<SearchLogEventRow>> ListSearchLogEventsAsync(DateTime? fromUtc, DateTime? toUtc, Guid? appUserId, bool includeSearchText, int top = 200, CancellationToken ct = default);

    // #332 row-change history
    Task<IReadOnlyList<AuditEventChangeRow>> GetAuditEventChangesAsync(long auditEventId, CancellationToken ct = default);
    Task<int> PurgeAuditEventChangesAsync(int retentionDays, CancellationToken ct = default);

    // #327 behavioral anomaly detection & persistent findings
    Task<IReadOnlyList<BehaviorAnomalyRow>> GetBehaviorAnomaliesAsync(BehaviorAnomalyQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<NewSecurityFindingRow>> RunSecurityFindingScanAsync(int hours = 24, CancellationToken ct = default);
    Task<IReadOnlyList<SecurityFindingRow>> ListSecurityFindingsAsync(int days = 30, string? status = null, string? severity = null, Guid? appUserId = null, int top = 200, CancellationToken ct = default);
    Task UpdateSecurityFindingStatusAsync(Guid securityFindingId, string status, CancellationToken ct = default);

    // #328 cursor-paginated canonical exports
    Task<IReadOnlyList<AuditExportRow>> ExportAuditEventsAsync(DateTime fromUtc, DateTime toUtc, DateTime? afterEventUtc, long? afterAuditEventId, int batchSize = 2000, CancellationToken ct = default);
    Task<IReadOnlyList<SignInExportRow>> ExportSignInEventsAsync(DateTime fromUtc, DateTime toUtc, DateTime? afterEventUtc, Guid? afterSignInEventId, int batchSize = 2000, CancellationToken ct = default);
    Task<IReadOnlyList<ScreenVisitExportRow>> ExportScreenVisitsAsync(DateTime fromUtc, DateTime toUtc, DateTime? afterEventUtc, Guid? afterScreenVisitId, int batchSize = 2000, CancellationToken ct = default);
    Task<IReadOnlyList<SearchLogExportRow>> ExportSearchLogsAsync(DateTime fromUtc, DateTime toUtc, DateTime? afterSearchedUtc, Guid? afterSearchLogId, int batchSize = 2000, CancellationToken ct = default);

    // #330 user behavior summary & operational logs
    Task<UserBehaviorSummary?> GetUserBehaviorSummaryAsync(Guid appUserId, int days = 30, CancellationToken ct = default);
    Task<OperationalLogsReport> GetOperationalLogsAsync(int hours = 24, int top = 100, CancellationToken ct = default);
    Task<OperationalIntegrityCheck> CheckOperationalIntegrityAsync(int minutes = 15, CancellationToken ct = default);
}

public sealed record BehaviorAnomalyQuery(
    int Hours = 24,
    int ViewVelocityThreshold = 50,
    int ExportSpikeThreshold = 5,
    int ExportRowsThreshold = 500,
    int ProbingThreshold = 10,
    int SearchVelocityThreshold = 40,
    int PrivBurstThreshold = 5,
    int MfaAbuseThreshold = 5,
    int DistributedFailThreshold = 10);

public sealed class BehaviorAnomalyRow
{
    public string RuleCode { get; init; } = string.Empty;
    public string Severity { get; init; } = "medium";
    public Guid? AppUserId { get; init; }
    public string? UserName { get; init; }
    public string? DisplayName { get; init; }
    public string? ClientAddress { get; init; }
    public Guid? SessionReference { get; init; }
    public int EventCount { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? DetailJson { get; init; }
    public DateTime FirstUtc { get; init; }
    public DateTime LastUtc { get; init; }
}

public sealed class SecurityFindingRow
{
    public Guid SecurityFindingId { get; init; }
    public Guid RootTenantId { get; init; }
    public Guid? OwningTenantId { get; init; }
    public Guid? AppUserId { get; init; }
    public string? UserName { get; init; }
    public string? DisplayName { get; init; }
    public string? ClientAddress { get; init; }
    public Guid? SessionReference { get; init; }
    public string RuleCode { get; init; } = string.Empty;
    public string Severity { get; init; } = "medium";
    public string Title { get; init; } = string.Empty;
    public string? DetailJson { get; init; }
    public DateTime WindowStartUtc { get; init; }
    public DateTime WindowEndUtc { get; init; }
    public DateTime DetectedUtc { get; init; }
    public string Status { get; init; } = "open";
    public Guid? AcknowledgedByUserId { get; init; }
    public string? AcknowledgedByName { get; init; }
    public DateTime? AcknowledgedUtc { get; init; }
}

public sealed class NewSecurityFindingRow
{
    public Guid SecurityFindingId { get; init; }
    public Guid RootTenantId { get; init; }
    public Guid? AppUserId { get; init; }
    public string RuleCode { get; init; } = string.Empty;
    public string Severity { get; init; } = "medium";
    public string Title { get; init; } = string.Empty;
    public string? DetailJson { get; init; }
    public DateTime DetectedUtc { get; init; }
}

public sealed class SearchLogSummaryRow
{
    public Guid AppUserId { get; init; }
    public string? UserName { get; init; }
    public string? DisplayName { get; init; }
    public int TotalSearches { get; init; }
    public int ZeroResultSearches { get; init; }
    public int OutsideScopeHits { get; init; }
    public int DistinctSessions { get; init; }
    public DateTime FirstUtc { get; init; }
    public DateTime LastUtc { get; init; }
}

public sealed class SearchLogEventRow
{
    public Guid SearchLogId { get; init; }
    public DateTime SearchedUtc { get; init; }
    public Guid AppUserId { get; init; }
    public string? UserName { get; init; }
    public string? DisplayName { get; init; }
    public Guid RootTenantId { get; init; }
    public Guid? ActingTenantId { get; init; }
    public Guid? SessionReference { get; init; }
    public string? ClientAddress { get; init; }
    public int ResultCount { get; init; }
    public bool? ExistsElsewhere { get; init; }
    public string? SearchSource { get; init; }
    public string? SearchText { get; init; }
}

public sealed class AuditEventChangeRow
{
    public Guid AuditEventChangeId { get; init; }
    public long AuditEventId { get; init; }
    public Guid RootTenantId { get; init; }
    public Guid? OwningTenantId { get; init; }
    public string EntityType { get; init; } = string.Empty;
    public Guid EntityId { get; init; }
    public DateTime ChangedUtc { get; init; }
    public string? BeforeJson { get; init; }
    public string? AfterJson { get; init; }
}

public sealed class AuditExportRow
{
    public long AuditEventId { get; init; }
    public DateTime EventUtc { get; init; }
    public string EventAction { get; init; } = string.Empty;
    public string EntityType { get; init; } = string.Empty;
    public Guid? EntityId { get; init; }
    public int? RecordCount { get; init; }
    public string? ViewKind { get; init; }
    public Guid? AppUserId { get; init; }
    public string? UserName { get; init; }
    public string? DisplayName { get; init; }
    public Guid RootTenantId { get; init; }
    public Guid? OwningTenantId { get; init; }
    public Guid? ActingTenantId { get; init; }
    public Guid? ViewAsUserId { get; init; }
    public Guid? SessionReference { get; init; }
    public string? TraceId { get; init; }
    public string? ClientAddress { get; init; }
    public string? FilterJson { get; init; }
}

public sealed class SignInExportRow
{
    public Guid SignInEventId { get; init; }
    public DateTime EventUtc { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string? Step { get; init; }
    public string? Method { get; init; }
    public string? Reason { get; init; }
    public Guid? AppUserId { get; init; }
    public string? UserName { get; init; }
    public string? DisplayName { get; init; }
    public Guid? ActorUserId { get; init; }
    public Guid? RootTenantId { get; init; }
    public string? ClientAddress { get; init; }
    public string? UserAgent { get; init; }
    public Guid? SessionReference { get; init; }
    public Guid? AttemptId { get; init; }
    public string? TraceId { get; init; }
    public int? FailedSignInCount { get; init; }
    public DateTime? LockoutEndUtc { get; init; }
}

public sealed class ScreenVisitExportRow
{
    public Guid ScreenVisitId { get; init; }
    public DateTime EventUtc { get; init; }
    public Guid RootTenantId { get; init; }
    public Guid? ActingTenantId { get; init; }
    public Guid? AppUserId { get; init; }
    public string? UserName { get; init; }
    public string? DisplayName { get; init; }
    public Guid? SessionReference { get; init; }
    public Guid? ProfileId { get; init; }
    public Guid? ViewAsUserId { get; init; }
    public string? RoleCodes { get; init; }
    public string ScreenCode { get; init; } = string.Empty;
    public string NavigationType { get; init; } = string.Empty;
    public Guid? TabId { get; init; }
    public Guid? PreviousVisitId { get; init; }
    public int? DurationMs { get; init; }
}

public sealed class SearchLogExportRow
{
    public Guid SearchLogId { get; init; }
    public DateTime SearchedUtc { get; init; }
    public Guid AppUserId { get; init; }
    public string? UserName { get; init; }
    public string? DisplayName { get; init; }
    public Guid RootTenantId { get; init; }
    public Guid? ActingTenantId { get; init; }
    public Guid? SessionReference { get; init; }
    public string? ClientAddress { get; init; }
    public int ResultCount { get; init; }
    public bool? ExistsElsewhere { get; init; }
    public string? SearchSource { get; init; }
}

public sealed class UserBehaviorOverviewRow
{
    public Guid AppUserId { get; init; }
    public string UserName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public bool MfaEnabled { get; init; }
    public DateTime? LastSignInUtc { get; init; }
    public int ActiveSessions { get; init; }
    public int SignInSuccesses { get; init; }
    public int SignInFailures { get; init; }
    public int Lockouts { get; init; }
    public int DistinctAddresses { get; init; }
    public int TotalSearches { get; init; }
    public int ZeroResultSearches { get; init; }
    public int OutsideScopeSearches { get; init; }
    public int UsageSessions { get; init; }
    public int ScreenVisits { get; init; }
}

public sealed class UserAuditActionRow
{
    public string EventAction { get; init; } = string.Empty;
    public string EntityType { get; init; } = string.Empty;
    public int EventCount { get; init; }
    public int TotalRecords { get; init; }
    public DateTime LastUtc { get; init; }
}

public sealed class UserSecurityFindingRow
{
    public Guid SecurityFindingId { get; init; }
    public string RuleCode { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTime DetectedUtc { get; init; }
}

public sealed class UserUsageSessionRow
{
    public Guid SessionReference { get; init; }
    public DateTime FirstUtc { get; init; }
    public DateTime LastUtc { get; init; }
    public int Visits { get; init; }
}

public sealed record UserBehaviorSummary(
    UserBehaviorOverviewRow Overview,
    IReadOnlyList<UserAuditActionRow> AuditActions,
    IReadOnlyList<UserSecurityFindingRow> Findings,
    IReadOnlyList<UserUsageSessionRow> UsageSessions);

public sealed class ExecutionLogFailureRow
{
    public long ExecutionLogId { get; init; }
    public string ProcedureName { get; init; } = string.Empty;
    public DateTime StartDateUtc { get; init; }
    public DateTime? EndDateUtc { get; init; }
    public int? ElapsedMilliseconds { get; init; }
    public bool ReCreatedAfterRollback { get; init; }
    public int? ErrorNumber { get; init; }
    public int? ErrorLine { get; init; }
    public string? ErrorProcedure { get; init; }
    public string? KeyParameters { get; init; }
    public string? CreatedBy { get; init; }
}

public sealed class DdlChangeRow
{
    public long ChangeId { get; init; }
    public DateTime EventTimeUtc { get; init; }
    public string? EventType { get; init; }
    public string? ObjectSchema { get; init; }
    public string? ObjectName { get; init; }
    public string? ObjectType { get; init; }
    public string? LoginName { get; init; }
    public string? AppUser { get; init; }
}

public sealed class DdlAuditVerifyRow
{
    public string CheckName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string? Detail { get; init; }
}

public sealed record OperationalLogsReport(
    IReadOnlyList<ExecutionLogFailureRow> ExecutionFailures,
    IReadOnlyList<DdlChangeRow> DdlChanges,
    IReadOnlyList<DdlAuditVerifyRow> IntegrityChecks);

public sealed class ExecutionFailureSpikeRow
{
    public int FailureCount { get; init; }
    public int AffectedProcedures { get; init; }
    public long? LatestExecutionLogId { get; init; }
}

public sealed record OperationalIntegrityCheck(
    ExecutionFailureSpikeRow ExecutionSummary,
    IReadOnlyList<DdlAuditVerifyRow> DdlProblems);
