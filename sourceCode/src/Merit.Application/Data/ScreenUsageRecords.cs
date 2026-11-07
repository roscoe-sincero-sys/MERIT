namespace Merit.Application.Data;

// Navigation metadata only: no resource IDs, raw URLs, queries, titles, addresses or form values.
public sealed record ScreenVisitInput(Guid VisitId, string ScreenCode, Guid? TabId = null, Guid? PreviousVisitId = null,
                                      string NavigationType = "server");

public interface IScreenUsageData
{
    Task RecordAsync(ScreenVisitInput visit, CancellationToken ct = default);
    Task DurationAsync(Guid visitId, int durationMs, CancellationToken ct = default);
    Task ContinueSessionAsync(Guid newSessionScopeId, CancellationToken ct = default);
    Task<ScreenUsageReport> ReportAsync(int days, CancellationToken ct = default);
    Task<ScreenSessionReport> SessionAsync(Guid sessionReference, int days, int page, CancellationToken ct = default);
    Task<int> PurgeAsync(int retentionDays, CancellationToken ct = default);
}

public sealed class ScreenCountRow
{
    public string ScreenCode { get; init; } = "";
    public string? RoleCode { get; init; }
    public int Visits { get; init; }
    public int Sessions { get; init; }
    public int TimedVisits { get; init; }
    public double? AverageVisibleMs { get; init; }
}

public sealed class ScreenPathRow
{
    public string Kind { get; init; } = "";
    public string PathCodes { get; init; } = "";
    public int Occurrences { get; init; }
    public int Sessions { get; init; }
}

public sealed class ScreenFlowRow
{
    public int Completions { get; init; }
    public double? AverageElapsedSeconds { get; init; }
}

public sealed class ScreenSessionRow
{
    public Guid SessionReference { get; init; }
    public Guid AppUserId { get; init; }
    public string DisplayName { get; init; } = "";
    public DateTime FirstUtc { get; init; }
    public DateTime LastUtc { get; init; }
    public int Visits { get; init; }
    public int TotalCount { get; init; }
}

public sealed class ScreenVisitRow
{
    public Guid ScreenVisitId { get; init; }
    public DateTime EventUtc { get; init; }
    public string ScreenCode { get; init; } = "";
    public string NavigationType { get; init; } = "";
    public Guid? TabId { get; init; }
    public Guid? PreviousVisitId { get; init; }
    public string? RoleCodes { get; init; }
    public Guid? ActingTenantId { get; init; }
    public Guid? ProfileId { get; init; }
    public Guid? ViewAsUserId { get; init; }
    public int? DurationMs { get; init; }
    public int TotalCount { get; init; }
}

public sealed class ScreenSessionEventRow
{
    public DateTime EventUtc { get; init; }
    public string EventType { get; init; } = "";
    public string? Reason { get; init; }
}

public sealed record ScreenUsageReport(IReadOnlyList<ScreenCountRow> Screens, IReadOnlyList<ScreenPathRow> Paths,
                                      ScreenFlowRow Flow, IReadOnlyList<ScreenSessionRow> Sessions);
public sealed record ScreenSessionReport(IReadOnlyList<ScreenVisitRow> Visits, IReadOnlyList<ScreenSessionEventRow> Events);
