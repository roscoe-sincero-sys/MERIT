using System.Text;
using System.Text.Json;
using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Security;
using Merit.Application.Services;
using Xunit;

namespace Merit.Tests;

public sealed class LoggingWave2UnitTests
{
    [Fact]
    public void LogPseudonymizerIsDeterministicAndMasksIpv4AndIpv6()
    {
        byte[] key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var p1 = new LogPseudonymizer(key);
        var p2 = new LogPseudonymizer(key);
        var userId = Guid.NewGuid();

        string? u1 = p1.PseudonymizeUser(userId);
        string? u2 = p2.PseudonymizeUser(userId);
        Assert.NotNull(u1);
        Assert.StartsWith("usr_", u1, StringComparison.Ordinal);
        Assert.Equal(u1, u2);
        Assert.DoesNotContain(userId.ToString("N"), u1, StringComparison.OrdinalIgnoreCase);

        string? ip4 = p1.PseudonymizeIp("203.0.113.42");
        Assert.NotNull(ip4);
        Assert.StartsWith("203.0.113.0/24|ip_", ip4, StringComparison.Ordinal);
        Assert.Equal(ip4, p2.PseudonymizeIp("203.0.113.42"));
        Assert.NotEqual(ip4, p1.PseudonymizeIp("203.0.113.43"));

        string? ip6 = p1.PseudonymizeIp("2001:db8:abcd:1234::1");
        Assert.NotNull(ip6);
        Assert.StartsWith("2001:0db8:abcd::/48|ip_", ip6, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StructuredLogExportStreamsPaginatedJsonlWithPseudonymizationAndAuditsExport()
    {
        var userId = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        var sessionRef = Guid.NewGuid();
        var t1 = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 11, 1, 10, 5, 0, DateTimeKind.Utc);

        var data = new FakeSecurityMonitoringData
        {
            AuditRows =
            [
                new AuditExportRow { AuditEventId = 1, EventUtc = t1, EventAction = "view", EntityType = "person", EntityId = Guid.NewGuid(), AppUserId = userId, UserName = "alice", DisplayName = "Alice", RootTenantId = rootId, SessionReference = sessionRef, TraceId = "tr-1", ClientAddress = "198.51.100.7", FilterJson = "{\"q\":\"secret\"}" },
                new AuditExportRow { AuditEventId = 2, EventUtc = t2, EventAction = "list", EntityType = "exposureCase", RecordCount = 12, AppUserId = userId, UserName = "alice", DisplayName = "Alice", RootTenantId = rootId, SessionReference = sessionRef, TraceId = "tr-2", ClientAddress = "198.51.100.7" },
            ],
            SearchRows =
            [
                new SearchLogExportRow { SearchLogId = Guid.NewGuid(), SearchedUtc = t2, AppUserId = userId, UserName = "alice", DisplayName = "Alice", RootTenantId = rootId, SessionReference = sessionRef, ClientAddress = "198.51.100.7", ResultCount = 0, ExistsElsewhere = true, SearchSource = "global" },
            ],
        };

        var session = new FakeSession(userId, rootId, Permissions.AuditView, Permissions.SecurityReview);
        var auditData = new RecordingWorkData();
        var audit = new AuditService(auditData, new NullRequestAuditContext());
        var svc = new StructuredLogExportService(data, audit, session);

        using var ms = new MemoryStream();
        int count = await svc.WriteJsonlAsync(ms, t1.AddMinutes(-1), t2.AddMinutes(1),
            new HashSet<string> { "audit", "search" }, pseudonymize: true, batchSize: 1);

        Assert.Equal(3, count);
        Assert.Single(auditData.RecordedAudits);
        Assert.Equal("export", auditData.RecordedAudits[0].Action);
        Assert.Equal("structuredLogExport", auditData.RecordedAudits[0].EntityType);
        Assert.Equal(3, auditData.RecordedAudits[0].RecordCount);

        string text = Encoding.UTF8.GetString(ms.ToArray());
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.DoesNotContain("alice", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("198.51.100.7", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);

        using var doc0 = JsonDocument.Parse(lines[0]);
        using var doc2 = JsonDocument.Parse(lines[2]);
        Assert.Equal("auditEvent", doc0.RootElement.GetProperty("stream").GetString());
        Assert.Equal("2026-11-01T10:00:00.000Z", doc0.RootElement.GetProperty("eventUtc").GetString());
        Assert.Equal(doc0.RootElement.GetProperty("appUserId").GetString(), doc2.RootElement.GetProperty("appUserId").GetString());
        Assert.StartsWith("198.51.100.0/24|ip_", doc0.RootElement.GetProperty("clientAddress").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StructuredLogExportRefusesUnauthorizedStream()
    {
        var session = new FakeSession(Guid.NewGuid(), Guid.NewGuid(), Permissions.AuditView);
        var svc = new StructuredLogExportService(new FakeSecurityMonitoringData(), new AuditService(new RecordingWorkData(), new NullRequestAuditContext()), session);
        using var ms = new MemoryStream();
        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            svc.WriteJsonlAsync(ms, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, new HashSet<string> { "usage" }, pseudonymize: true));
    }

    [Fact]
    public void OperationalExecutionLogFailureDtoExposesOnlyAllowlistedDiagnosticFields()
    {
        // #338: Raw ErrorMessage, DynamicSql, and ContextMessage must never be exposed on the operational report DTO.
        var props = typeof(ExecutionLogFailureRow).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("ErrorMessage", props);
        Assert.DoesNotContain("DynamicSql", props);
        Assert.DoesNotContain("ContextMessage", props);
        Assert.Contains("ProcedureName", props);
        Assert.Contains("ErrorNumber", props);
        Assert.Contains("ErrorLine", props);
        Assert.Contains("ErrorProcedure", props);
        Assert.Contains("KeyParameters", props);
    }

    private sealed class FakeSession(Guid userId, Guid rootId, params string[] perms) : ICurrentSession
    {
        private readonly HashSet<string> set = [.. perms];
        public bool IsAuthenticated => true;
        public Guid? UserId => userId;
        public string? UserName => "tester";
        public string? DisplayName => "Tester";
        public Guid? RootTenantId => rootId;
        public Guid? ActingTenantId => rootId;
        public string? ActingTenantName => "Root";
        public Guid? ScopeKey => Guid.NewGuid();
        public IReadOnlySet<string> Permissions => set;
    }

    private sealed class RecordingWorkData : IWorkData
    {
        public List<(string Action, string EntityType, int? RecordCount)> RecordedAudits { get; } = [];
        public Task RecordAuditAsync(string eventAction, string entityType, Guid? entityId, int? recordCount, string? clientAddress, CancellationToken ct = default,
                                     string? viewKind = null, string? filterJson = null, string? recordIdsJson = null, string? traceId = null)
        {
            RecordedAudits.Add((eventAction, entityType, recordCount));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<AuditEventRecordRow>> GetAuditEventRecordsAsync(long auditEventId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditEventRecordRow>>([]);
        public Task<int> PurgeAuditEventRecordsAsync(int retentionDays, CancellationToken ct = default) => Task.FromResult(0);
        public Task RecordSearchLogAsync(string searchText, int resultCount, string searchSource, string entityType, string? filterJson = null, string? recordIdsJson = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateTaskAsync(WorkTaskCreate task, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<QueueCountRow>> GetQueueCountsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DashboardData> GetDashboardAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkTaskRow>> ListTasksAsync(WorkTaskQuery query, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> UpdateTasksAsync(IReadOnlyList<Guid> taskIds, string action, Guid? assignToUserId, string? note, CancellationToken ct = default) => throw new NotImplementedException();
        public Task EnqueueJobAsync(Guid jobId, string jobType, string payloadJson, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ClaimedJobRow?> ClaimNextJobAsync(string workerName, int leaseSeconds = 300, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> RenewJobClaimAsync(Guid jobId, Guid claimToken, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CompleteJobAsync(Guid jobId, string? resultJson, Guid? claimToken = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> FailJobAsync(Guid jobId, string errorText, Guid? claimToken = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JobRow?> GetJobAsync(Guid jobId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<QueueDepthRow>> GetQueueDepthAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> DetectClustersAsync(int windowDays, int minCount, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AuditEventRow>> SearchAuditAsync(AuditQuery query, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AuditUserRow>> ListAuditUsersAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FakeSecurityMonitoringData : ISecurityMonitoringData
    {
        public List<AuditExportRow> AuditRows { get; init; } = [];
        public List<SearchLogExportRow> SearchRows { get; init; } = [];
        public Task<int> PurgeSearchLogAsync(int retentionDays, CancellationToken ct = default) => Task.FromResult(0);
        public Task<IReadOnlyList<SearchLogSummaryRow>> GetSearchLogSummaryAsync(int days, Guid? appUserId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SearchLogSummaryRow>>([]);
        public Task<IReadOnlyList<SearchLogEventRow>> ListSearchLogEventsAsync(DateTime? fromUtc, DateTime? toUtc, Guid? appUserId, bool includeSearchText, int top = 200, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SearchLogEventRow>>([]);
        public Task<IReadOnlyList<AuditEventChangeRow>> GetAuditEventChangesAsync(long auditEventId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditEventChangeRow>>([]);
        public Task<int> PurgeAuditEventChangesAsync(int retentionDays, CancellationToken ct = default) => Task.FromResult(0);
        public Task<IReadOnlyList<BehaviorAnomalyRow>> GetBehaviorAnomaliesAsync(BehaviorAnomalyQuery query, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BehaviorAnomalyRow>>([]);
        public Task<IReadOnlyList<NewSecurityFindingRow>> RunSecurityFindingScanAsync(int hours = 24, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<NewSecurityFindingRow>>([]);
        public Task<IReadOnlyList<SecurityFindingRow>> ListSecurityFindingsAsync(int days = 30, string? status = null, string? severity = null, Guid? appUserId = null, int top = 200, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SecurityFindingRow>>([]);
        public Task UpdateSecurityFindingStatusAsync(Guid securityFindingId, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AuditExportRow>> ExportAuditEventsAsync(DateTime fromUtc, DateTime toUtc, DateTime? afterEventUtc, long? afterAuditEventId, int batchSize = 2000, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AuditExportRow>>(AuditRows.Where(r => afterAuditEventId is null || r.AuditEventId > afterAuditEventId).Take(batchSize).ToList());
        public Task<IReadOnlyList<SignInExportRow>> ExportSignInEventsAsync(DateTime fromUtc, DateTime toUtc, DateTime? afterEventUtc, Guid? afterSignInEventId, int batchSize = 2000, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SignInExportRow>>([]);
        public Task<IReadOnlyList<ScreenVisitExportRow>> ExportScreenVisitsAsync(DateTime fromUtc, DateTime toUtc, DateTime? afterEventUtc, Guid? afterScreenVisitId, int batchSize = 2000, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ScreenVisitExportRow>>([]);
        public Task<IReadOnlyList<SearchLogExportRow>> ExportSearchLogsAsync(DateTime fromUtc, DateTime toUtc, DateTime? afterSearchedUtc, Guid? afterSearchLogId, int batchSize = 2000, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SearchLogExportRow>>(SearchRows.Where(r => afterSearchLogId is null || r.SearchLogId != afterSearchLogId).Take(batchSize).ToList());
        public Task<UserBehaviorSummary?> GetUserBehaviorSummaryAsync(Guid appUserId, int days = 30, CancellationToken ct = default) => Task.FromResult<UserBehaviorSummary?>(null);
        public Task<OperationalLogsReport> GetOperationalLogsAsync(int hours = 24, int top = 100, CancellationToken ct = default) => Task.FromResult(new OperationalLogsReport([], [], []));
        public Task<OperationalIntegrityCheck> CheckOperationalIntegrityAsync(int minutes = 15, CancellationToken ct = default) => Task.FromResult(new OperationalIntegrityCheck(new ExecutionFailureSpikeRow(), []));
    }
}
