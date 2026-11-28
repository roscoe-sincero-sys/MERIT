using System.Net;
using System.Reflection;
using Merit.Application.Data;
using Merit.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Merit.Tests;

/// <summary>
/// Unit tests for <see cref="AuditLedgerVerifier"/> covering WORM directory verification, durable baseline survival
/// across restarts and failed zero-count runs, missing/malformed archive failures, atomic publish cancellation,
/// automatic-storage mode via metadata, outcome recording, and critical webhook notification with daily dedup (#334, #354).
/// </summary>
public sealed class AuditLedgerVerifierTests
{
    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }

    private sealed class CapturingLogger : ILogger<AuditLedgerVerifier>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    public sealed record VerifyCall(string? DigestJson, bool UseAutomaticStorage, bool RequireDigest);
    public sealed record OutcomeRecord(string AnchorSource, bool Verified, long? BlockId, int? FileCount, string Detail);

    public class StubLedgerMonitoringProxy : DispatchProxy
    {
        public Func<LedgerVerificationStateRow> OnGetState { get; set; } = () => new LedgerVerificationStateRow();
        public Func<VerifyCall, AuditLedgerVerificationRow> OnVerify { get; set; } = _ => new AuditLedgerVerificationRow();
        public Func<OutcomeRecord, LedgerVerificationOutcomeRow> OnRecord { get; set; } = _ => new LedgerVerificationOutcomeRow { LedgerVerificationRunId = Guid.NewGuid() };
        public Func<int, int, IReadOnlyList<NewSecurityFindingRow>> OnClaimPendingAlerts { get; set; } = (_, _) => [];
        public Func<Guid, Guid?, bool> OnMarkDelivered { get; set; } = (_, _) => true;
        public Action<Guid, Guid, int, string?> OnRecordFailure { get; set; } = (_, _, _, _) => { };

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISecurityMonitoringData.GetLedgerVerificationStateAsync))
            {
                return Task.FromResult(OnGetState());
            }
            if (targetMethod?.Name == nameof(ISecurityMonitoringData.VerifyAuditLedgerAsync))
            {
                var call = new VerifyCall((string?)args?[0], (bool)(args?[1] ?? false), (bool)(args?[2] ?? false));
                return Task.FromResult(OnVerify(call));
            }
            if (targetMethod?.Name == nameof(ISecurityMonitoringData.RecordLedgerVerificationOutcomeAsync))
            {
                var rec = new OutcomeRecord((string)args![0]!, (bool)args[1]!, (long?)args[2], (int?)args[3], (string)args[4]!);
                return Task.FromResult(OnRecord(rec));
            }
            if (targetMethod?.Name == nameof(ISecurityMonitoringData.ClaimPendingLedgerAlertsAsync))
            {
                return Task.FromResult(OnClaimPendingAlerts((int)args![0]!, (int)args[1]!));
            }
            if (targetMethod?.Name == nameof(ISecurityMonitoringData.MarkSecurityFindingWebhookDeliveredAsync))
            {
                return Task.FromResult(OnMarkDelivered((Guid)args![0]!, (Guid?)args[1]));
            }
            if (targetMethod?.Name == nameof(ISecurityMonitoringData.RecordSecurityFindingWebhookFailureAsync))
            {
                OnRecordFailure((Guid)args![0]!, (Guid)args[1]!, (int)args[2]!, (string?)args[3]);
                return Task.CompletedTask;
            }

            throw new NotImplementedException(targetMethod?.Name);
        }

        public static ISecurityMonitoringData Create(
            Func<LedgerVerificationStateRow> onGetState,
            Func<VerifyCall, AuditLedgerVerificationRow> onVerify,
            Func<OutcomeRecord, LedgerVerificationOutcomeRow> onRecord,
            Func<int, int, IReadOnlyList<NewSecurityFindingRow>>? onClaimPendingAlerts = null,
            Func<Guid, Guid?, bool>? onMarkDelivered = null,
            Action<Guid, Guid, int, string?>? onRecordFailure = null)
        {
            var proxy = DispatchProxy.Create<ISecurityMonitoringData, StubLedgerMonitoringProxy>();
            var impl = (StubLedgerMonitoringProxy)(object)proxy;
            impl.OnGetState = onGetState;
            impl.OnVerify = onVerify;
            impl.OnRecord = onRecord;
            if (onClaimPendingAlerts is not null) impl.OnClaimPendingAlerts = onClaimPendingAlerts;
            if (onMarkDelivered is not null) impl.OnMarkDelivered = onMarkDelivered;
            if (onRecordFailure is not null) impl.OnRecordFailure = onRecordFailure;
            return proxy;
        }
    }

    private sealed class InMemoryLedgerStore
    {
        private readonly object _gate = new();
        public int? DurableLastGoodCount { get; set; }
        public bool FindingCreatedToday { get; set; }
        public List<NewSecurityFindingRow> PendingAlerts { get; } = [];
        public HashSet<Guid> DeliveredFindingIds { get; } = [];
        public Dictionary<Guid, int> AttemptCounts { get; } = [];
        private readonly Dictionary<Guid, (Guid? Token, DateTime LeaseUntilUtc)> _leases = [];

        public void ExpireAllLeases()
        {
            lock (_gate)
            {
                _leases.Clear();
            }
        }

        public IReadOnlyList<NewSecurityFindingRow> Claim(int batchSize, int leaseSeconds)
        {
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                var token = Guid.NewGuid();
                var leaseUntil = now.AddSeconds(leaseSeconds);
                var candidates = PendingAlerts
                    .Where(a => !DeliveredFindingIds.Contains(a.SecurityFindingId))
                    .Where(a => !_leases.TryGetValue(a.SecurityFindingId, out var lease) || lease.LeaseUntilUtc < now)
                    .OrderBy(a => AttemptCounts.GetValueOrDefault(a.SecurityFindingId, 0))
                    .ThenBy(a => a.DetectedUtc)
                    .Take(batchSize)
                    .ToList();

                var claimed = new List<NewSecurityFindingRow>();
                foreach (var alert in candidates)
                {
                    _leases[alert.SecurityFindingId] = (token, leaseUntil);
                    claimed.Add(new NewSecurityFindingRow
                    {
                        SecurityFindingId = alert.SecurityFindingId,
                        RootTenantId = alert.RootTenantId,
                        AppUserId = alert.AppUserId,
                        RuleCode = alert.RuleCode,
                        Severity = alert.Severity,
                        Title = alert.Title,
                        DetailJson = alert.DetailJson,
                        DetectedUtc = alert.DetectedUtc,
                        ClaimToken = token,
                        LeaseUntilUtc = leaseUntil,
                        AttemptCount = AttemptCounts.GetValueOrDefault(alert.SecurityFindingId, 0),
                    });
                }
                return claimed;
            }
        }

        public bool MarkDelivered(Guid id, Guid? claimToken)
        {
            lock (_gate)
            {
                if (DeliveredFindingIds.Contains(id)) return false;
                if (claimToken is not null && (!_leases.TryGetValue(id, out var lease) || lease.Token != claimToken.Value))
                {
                    return false;
                }
                DeliveredFindingIds.Add(id);
                _leases.Remove(id);
                return true;
            }
        }

        public void RecordFailure(Guid id, Guid claimToken, int backoffSeconds, string? lastError)
        {
            lock (_gate)
            {
                if (!_leases.TryGetValue(id, out var lease) || lease.Token != claimToken)
                {
                    return;
                }
                int attempts = AttemptCounts.GetValueOrDefault(id, 0);
                AttemptCounts[id] = attempts + 1;
                int effectiveBackoff = Math.Min(14400, backoffSeconds * (1 << Math.Min(attempts, 4)));
                _leases[id] = (null, DateTime.UtcNow.AddSeconds(effectiveBackoff));
            }
        }
    }

    private static (AuditLedgerVerifier verifier, CapturingLogger logger, List<VerifyCall> verifyCalls, List<OutcomeRecord> outcomes)
        CreateVerifier(string? digestDir, Func<VerifyCall, AuditLedgerVerificationRow> onVerify,
                       bool hasAutomaticStorage = false, int? initialGoodCount = null,
                       string? webhookUrl = null, HttpClient? webhookClient = null,
                       InMemoryLedgerStore? store = null)
    {
        var verifyCalls = new List<VerifyCall>();
        var outcomes = new List<OutcomeRecord>();
        store ??= new InMemoryLedgerStore { DurableLastGoodCount = initialGoodCount };
        var services = new ServiceCollection();
        services.AddSingleton(StubLedgerMonitoringProxy.Create(
            () => new LedgerVerificationStateRow { HasAutomaticDigestStorage = hasAutomaticStorage, LastGoodDigestFileCount = store.DurableLastGoodCount },
            call =>
            {
                verifyCalls.Add(call);
                return onVerify(call);
            },
            rec =>
            {
                outcomes.Add(rec);
                if (rec.AnchorSource == "digestDirectory" && rec.Verified && rec.FileCount is not null)
                {
                    store.DurableLastGoodCount = Math.Max(store.DurableLastGoodCount ?? 0, rec.FileCount.Value);
                }
                bool created = false;
                Guid? fid = null;
                Guid? rid = null;
                DateTime now = DateTime.UtcNow;
                if (!rec.Verified && !store.FindingCreatedToday)
                {
                    store.FindingCreatedToday = true;
                    created = true;
                    fid = Guid.NewGuid();
                    rid = Guid.NewGuid();
                    store.PendingAlerts.Add(new NewSecurityFindingRow
                    {
                        SecurityFindingId = fid.Value,
                        RootTenantId = rid.Value,
                        RuleCode = "ledgerVerificationFailed",
                        Severity = "critical",
                        Title = "Audit ledger verification failed",
                        DetailJson = rec.Detail,
                        DetectedUtc = now,
                    });
                }
                return new LedgerVerificationOutcomeRow
                {
                    LedgerVerificationRunId = Guid.NewGuid(),
                    FindingCreated = created,
                    SecurityFindingId = fid,
                    RootTenantId = rid,
                    DetectedUtc = now,
                };
            },
            (batchSize, leaseSeconds) => store.Claim(batchSize, leaseSeconds),
            (id, claimToken) => store.MarkDelivered(id, claimToken),
            (id, claimToken, backoff, err) => store.RecordFailure(id, claimToken, backoff, err)));
        var provider = services.BuildServiceProvider();
        var dict = new Dictionary<string, string?>();
        if (digestDir is not null) dict["LedgerVerification:DigestDirectory"] = digestDir;
        if (webhookUrl is not null) dict["SecurityAlerts:WebhookUrl"] = webhookUrl;
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var logger = new CapturingLogger();
        ISecurityAlertNotifier? notifier = webhookClient is null
            ? null
            : new SecurityAlertWebhookNotifier(config, logger, webhookClient, [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);
        var verifier = new AuditLedgerVerifier(provider.GetRequiredService<IServiceScopeFactory>(), config, logger, notifier);
        return (verifier, logger, verifyCalls, outcomes);
    }

    [Fact]
    public async Task WritesDigestAtomically_VerifiesPriorDigestWithRequireDigest_DetectsShrinkage_AndAlertsOnFailure()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "merit-ledger-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            bool failNext = false;
            var (verifier, logger, calls, outcomes) = CreateVerifier(tempDir, _ =>
                failNext
                    ? new AuditLedgerVerificationRow { Verified = false, Detail = "Hash mismatch" }
                    : new AuditLedgerVerificationRow
                    {
                        Verified = true,
                        LastVerifiedBlockId = 7,
                        DigestJson = """{"database_name":"MERIT_IT","block_id":7,"hash":"0xABCD"}""",
                        VerifiedUtc = DateTime.UtcNow,
                        Detail = "Ledger verified up to block 7.",
                    });

            // Cycle 1: pre-provisioned empty dir -> generates fresh digest and atomically publishes it.
            await verifier.RunVerificationCycleAsync(CancellationToken.None);
            Assert.Single(calls);
            Assert.False(calls[0].RequireDigest);
            var files = Directory.GetFiles(tempDir, "merit-ledger-digest-*.json");
            Assert.Single(files);
            Assert.Empty(Directory.GetFiles(tempDir, ".tmp-*.part"));
            Assert.Single(outcomes);
            Assert.True(outcomes[0].Verified);

            // Cycle 2: verifies prior archived digest with requireDigest=true, then generates next digest.
            await verifier.RunVerificationCycleAsync(CancellationToken.None);
            Assert.Equal(3, calls.Count);
            Assert.True(calls[1].RequireDigest);
            Assert.Contains("0xABCD", calls[1].DigestJson ?? "", StringComparison.Ordinal);

            // Cycle 3: SQL verification failure logs Error and records one failure outcome.
            failNext = true;
            await verifier.RunVerificationCycleAsync(CancellationToken.None);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("AUDIT LEDGER VERIFICATION FAILED", StringComparison.Ordinal));
            Assert.False(outcomes[^1].Verified);

            // Cycle 4: deleting an archived file fails visibly without publishing a replacement.
            failNext = false;
            int countBeforeDelete = Directory.GetFiles(tempDir, "merit-ledger-digest-*.json").Length;
            File.Delete(files[0]);
            await verifier.RunVerificationCycleAsync(CancellationToken.None);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("AUDIT LEDGER DIGEST ARCHIVE SHRANK", StringComparison.Ordinal));
            Assert.False(outcomes[^1].Verified);
            Assert.Equal(countBeforeDelete - 1, Directory.GetFiles(tempDir, "merit-ledger-digest-*.json").Length);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RestartAfterMissingArchiveAndEmptyRemount_RefusesToPublishReplacement_AndStaysFailed()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "merit-ledger-restart-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Simulate persisted durable last-known-good count = 2, missing directory on cycle 1,
            // then worker restart with an empty remounted directory on cycle 2.
            var (verifier1, _, calls1, outcomes1) = CreateVerifier(tempDir, _ => new AuditLedgerVerificationRow { Verified = true }, initialGoodCount: 2);
            await verifier1.RunVerificationCycleAsync(CancellationToken.None);
            Assert.Single(outcomes1);
            Assert.False(outcomes1[0].Verified);
            Assert.Empty(calls1);

            // Fresh verifier instance (simulates worker restart) with empty remounted directory:
            Directory.CreateDirectory(tempDir);
            var (verifier2, logger2, calls2, outcomes2) = CreateVerifier(tempDir, _ => new AuditLedgerVerificationRow
            {
                Verified = true,
                LastVerifiedBlockId = 9,
                DigestJson = """{"database_name":"MERIT_IT","block_id":9,"hash":"0x9999"}""",
                VerifiedUtc = DateTime.UtcNow,
                Detail = "OK",
            }, initialGoodCount: 2);

            await verifier2.RunVerificationCycleAsync(CancellationToken.None);

            // Must NOT generate or publish a replacement digest, and must record a terminal failure.
            Assert.Empty(calls2);
            Assert.Empty(Directory.GetFiles(tempDir, "merit-ledger-digest-*.json"));
            Assert.Single(outcomes2);
            Assert.False(outcomes2[0].Verified);
            Assert.Contains(logger2.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("AUDIT LEDGER DIGEST ARCHIVE SHRANK", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task MissingDigestDirectory_FailsVisibly_AndDoesNotCreateDirectory()
    {
        string missingDir = Path.Combine(Path.GetTempPath(), "merit-ledger-missing-" + Guid.NewGuid().ToString("N"));
        var (verifier, logger, calls, outcomes) = CreateVerifier(missingDir, _ => new AuditLedgerVerificationRow { Verified = true });

        await verifier.RunVerificationCycleAsync(CancellationToken.None);

        Assert.False(Directory.Exists(missingDir));
        Assert.Empty(calls);
        Assert.Single(outcomes);
        Assert.False(outcomes[0].Verified);
        Assert.Contains("missing or unmounted", outcomes[0].Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task EmptyOrMalformedPriorDigestFile_FailsAsArchiveError_WithoutCallingSql()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "merit-ledger-badfile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "merit-ledger-digest-20261127T000000000Z-block0-000000.json"), "");
            var (verifier, _, calls, outcomes) = CreateVerifier(tempDir, _ => new AuditLedgerVerificationRow { Verified = true });

            await verifier.RunVerificationCycleAsync(CancellationToken.None);

            Assert.Empty(calls);
            Assert.Single(outcomes);
            Assert.False(outcomes[0].Verified);
            Assert.Contains("empty or malformed", outcomes[0].Detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationDuringPublish_LeavesNoFinalOrTempFile()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "merit-ledger-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var fresh = new AuditLedgerVerificationRow
            {
                Verified = true,
                LastVerifiedBlockId = 1,
                DigestJson = """{"database_name":"MERIT_IT","block_id":1,"hash":"0x1234"}""",
                VerifiedUtc = DateTime.UtcNow,
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AuditLedgerVerifier.PublishDigestAtomicallyAsync(tempDir, fresh, cts.Token));

            Assert.Empty(Directory.GetFiles(tempDir, "merit-ledger-digest-*.json"));
            Assert.Empty(Directory.GetFiles(tempDir, ".tmp-*.part"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task AutomaticStorageOnlyMode_SelectsFromMetadataWithoutRedundantVerify()
    {
        var (verifier, _, calls, outcomes) = CreateVerifier(null, call => new AuditLedgerVerificationRow
        {
            Verified = true,
            LastVerifiedBlockId = 12,
            DigestJson = null,
            VerifiedUtc = DateTime.UtcNow,
            Detail = "Automatic digest storage verified up to block 12.",
            HasAutomaticDigestStorage = true,
        }, hasAutomaticStorage: true);

        await verifier.RunVerificationCycleAsync(CancellationToken.None);

        // Exactly ONE verify call directly in automatic-storage mode (selected from metadata).
        Assert.Single(calls);
        Assert.True(calls[0].UseAutomaticStorage);
        Assert.Single(outcomes);
        Assert.Equal("automaticStorage", outcomes[0].AnchorSource);
        Assert.True(outcomes[0].Verified);
    }

    [Fact]
    public async Task WarnsWhenNoExternalOrAutomaticDigestStorageConfigured()
    {
        var (verifier, logger, _, outcomes) = CreateVerifier(null, _ => new AuditLedgerVerificationRow
        {
            Verified = true,
            LastVerifiedBlockId = 1,
            DigestJson = "{}",
            VerifiedUtc = DateTime.UtcNow,
            Detail = "OK",
            HasAutomaticDigestStorage = false,
        }, hasAutomaticStorage: false);

        await verifier.RunVerificationCycleAsync(CancellationToken.None);

        Assert.Single(outcomes);
        Assert.Equal("unanchored", outcomes[0].AnchorSource);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("not anchored outside SQL Server", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedVerification_PostsCriticalWebhookOncePerDay_WithBoundedRetry()
    {
        int httpPosts = 0;
        string? lastBody = null;
        string? deliveryKeyHeader = null;
        var client = new HttpClient(new StubHttpMessageHandler(async (req, ct) =>
        {
            httpPosts++;
            lastBody = await req.Content!.ReadAsStringAsync(ct);
            deliveryKeyHeader = req.Headers.TryGetValues("X-MERIT-Delivery-Key", out var vals) ? vals.FirstOrDefault() : null;
            // First attempt returns 503 (transient), second attempt succeeds -> proves shared retry policy.
            return new HttpResponseMessage(httpPosts == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        }));

        string missingDir = Path.Combine(Path.GetTempPath(), "merit-ledger-webhook-" + Guid.NewGuid().ToString("N"));
        var store = new InMemoryLedgerStore();
        var (verifier, _, _, _) = CreateVerifier(
            missingDir,
            _ => new AuditLedgerVerificationRow { Verified = false, Detail = "Hash mismatch" },
            webhookUrl: "https://alerts.example.test/merit",
            webhookClient: client,
            store: store);

        // First failure of the day: posts critical finding (1 transient 503 + 1 retry 200 = 2 HTTP calls).
        await verifier.RunVerificationCycleAsync(CancellationToken.None);
        Assert.Equal(2, httpPosts);
        Assert.NotNull(lastBody);
        Assert.Contains("securityFinding.detected", lastBody, StringComparison.Ordinal);
        Assert.Contains("ledgerVerificationFailed", lastBody, StringComparison.Ordinal);
        Assert.Contains("critical", lastBody, StringComparison.Ordinal);
        Assert.Single(store.PendingAlerts);
        Assert.Equal(store.PendingAlerts[0].SecurityFindingId.ToString("D"), deliveryKeyHeader);
        Assert.Contains($"\"deliveryKey\":\"{deliveryKeyHeader}\"", lastBody, StringComparison.Ordinal);
        Assert.Contains(store.PendingAlerts[0].SecurityFindingId, store.DeliveredFindingIds);

        // Repeat failure on the same UTC day: finding already marked delivered -> ZERO additional webhook posts.
        await verifier.RunVerificationCycleAsync(CancellationToken.None);
        Assert.Equal(2, httpPosts);
    }

    [Fact]
    public async Task ExhaustedWebhookRetries_RemainPendingAcrossRestart_AndDeliverOnceReceiverRecovers()
    {
        int httpPosts = 0;
        bool receiverHealthy = false;
        var deliveryKeys = new List<string>();
        var client = new HttpClient(new StubHttpMessageHandler((req, _) =>
        {
            httpPosts++;
            if (req.Headers.TryGetValues("X-MERIT-Delivery-Key", out var vals))
            {
                deliveryKeys.Add(vals.First());
            }
            return Task.FromResult(new HttpResponseMessage(receiverHealthy ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable));
        }));

        string missingDir = Path.Combine(Path.GetTempPath(), "merit-ledger-outbox-" + Guid.NewGuid().ToString("N"));
        var store = new InMemoryLedgerStore();

        // Cycle 1: webhook receiver is completely down (3x 503). Finding stays pending (NOT marked delivered).
        var (verifier1, _, _, _) = CreateVerifier(
            missingDir,
            _ => new AuditLedgerVerificationRow { Verified = false, Detail = "Hash mismatch" },
            webhookUrl: "https://alerts.example.test/merit",
            webhookClient: client,
            store: store);

        await verifier1.RunVerificationCycleAsync(CancellationToken.None);
        Assert.Equal(3, httpPosts);
        Assert.Single(store.PendingAlerts);
        Assert.Empty(store.DeliveredFindingIds);

        // Simulate lease expiry after receiver recovers: next cycle re-claims and delivers with the SAME deliveryKey!
        store.ExpireAllLeases();
        receiverHealthy = true;
        var (verifier2, _, _, _) = CreateVerifier(
            missingDir,
            _ => new AuditLedgerVerificationRow { Verified = false, Detail = "Hash mismatch" },
            webhookUrl: "https://alerts.example.test/merit",
            webhookClient: client,
            store: store);

        await verifier2.RunVerificationCycleAsync(CancellationToken.None);
        Assert.Equal(4, httpPosts);
        Assert.Single(store.DeliveredFindingIds);
        Assert.Contains(store.PendingAlerts[0].SecurityFindingId, store.DeliveredFindingIds);
        Assert.All(deliveryKeys, k => Assert.Equal(store.PendingAlerts[0].SecurityFindingId.ToString("D"), k));

        // Subsequent same-day cycle: already delivered -> ZERO additional posts.
        await verifier2.RunVerificationCycleAsync(CancellationToken.None);
        Assert.Equal(4, httpPosts);
    }

    [Fact]
    public async Task ConcurrentDrainers_ClaimAtomically_AndPostEachFindingExactlyOnce()
    {
        var store = new InMemoryLedgerStore();
        var findingId = Guid.NewGuid();
        store.PendingAlerts.Add(new NewSecurityFindingRow
        {
            SecurityFindingId = findingId,
            RootTenantId = Guid.NewGuid(),
            RuleCode = "ledgerVerificationFailed",
            Severity = "critical",
            Title = "Audit ledger verification failed",
            DetectedUtc = DateTime.UtcNow,
        });

        int httpPosts = 0;
        var client = new HttpClient(new StubHttpMessageHandler(async (_, ct) =>
        {
            Interlocked.Increment(ref httpPosts);
            await Task.Delay(50, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SecurityAlerts:WebhookUrl"] = "https://alerts.example.test/merit" })
            .Build();
        var logger = new CapturingLogger();
        var notifier = new SecurityAlertWebhookNotifier(config, logger, client, [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);
        var data = StubLedgerMonitoringProxy.Create(
            () => new LedgerVerificationStateRow(),
            _ => new AuditLedgerVerificationRow { Verified = true },
            _ => new LedgerVerificationOutcomeRow { LedgerVerificationRunId = Guid.NewGuid() },
            (batchSize, leaseSeconds) => store.Claim(batchSize, leaseSeconds),
            (id, claimToken) => store.MarkDelivered(id, claimToken));

        // Two overlapping drainers race against the same pending finding -> atomic lease guarantees exactly 1 HTTP POST.
        await Task.WhenAll(
            SecurityAlertWebhookNotifier.DrainPendingLedgerAlertsAsync(data, notifier, logger, batchSize: 10, leaseSeconds: 300, ct: CancellationToken.None),
            SecurityAlertWebhookNotifier.DrainPendingLedgerAlertsAsync(data, notifier, logger, batchSize: 10, leaseSeconds: 300, ct: CancellationToken.None));

        Assert.Equal(1, httpPosts);
        Assert.Contains(findingId, store.DeliveredFindingIds);
    }

    [Fact]
    public async Task ShutdownCancellationAfterNegativeSqlResult_StillPersistsFailureOutcome()
    {
        using var cts = new CancellationTokenSource();
        var (verifier, _, _, outcomes) = CreateVerifier(
            null,
            _ =>
            {
                // Host begins shutting down immediately as SQL returns Verified = false.
                cts.Cancel();
                return new AuditLedgerVerificationRow { Verified = false, Detail = "Hash mismatch", OutcomeRecorded = false };
            });

        await verifier.RunVerificationCycleAsync(cts.Token);

        Assert.Single(outcomes);
        Assert.False(outcomes[0].Verified);
    }

    [Fact]
    public async Task ExpiredClaimBeforeSend_DoesNotStartNewHttpPost_AndLogsWarning()
    {
        int httpPosts = 0;
        var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            httpPosts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SecurityAlerts:WebhookUrl"] = "https://alerts.example.test/merit" })
            .Build();
        var logger = new CapturingLogger();
        var notifier = new SecurityAlertWebhookNotifier(config, logger, client, [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);
        bool claimed = false;
        var baseTime = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        var findingId = Guid.NewGuid();
        int clockReads = 0;
        var data = StubLedgerMonitoringProxy.Create(
            () => new LedgerVerificationStateRow(),
            _ => new AuditLedgerVerificationRow { Verified = true },
            _ => new LedgerVerificationOutcomeRow { LedgerVerificationRunId = Guid.NewGuid() },
            (_, _) =>
            {
                if (claimed) return [];
                claimed = true;
                return
                [
                    new NewSecurityFindingRow
                    {
                        SecurityFindingId = findingId,
                        RootTenantId = Guid.NewGuid(),
                        RuleCode = "ledgerVerificationFailed",
                        Severity = "critical",
                        Title = "Audit ledger verification failed",
                        DetectedUtc = baseTime.AddMinutes(-10),
                        ClaimToken = Guid.NewGuid(),
                        LeaseUntilUtc = baseTime.AddSeconds(300),
                    },
                ];
            });

        // Simulate a 301s stall between starting the claim (t0) and starting the HTTP send (t0 + 301s).
        int delivered = await SecurityAlertWebhookNotifier.DrainPendingLedgerAlertsAsync(
            data, notifier, logger, batchSize: 10, leaseSeconds: 300, ct: CancellationToken.None,
            utcNow: () => clockReads++ == 0 ? baseTime : baseTime.AddSeconds(301));

        Assert.Equal(0, delivered);
        Assert.Equal(0, httpPosts);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(findingId.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkerClockAheadOfSqlServer_StillDeliversPendingAlerts()
    {
        // #356 (S31): Worker host clock is 10 minutes ahead of SQL Server's SYSUTCDATETIME().
        var sqlNow = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        var workerNow = sqlNow.AddMinutes(10);
        var findingId = Guid.NewGuid();
        var claimToken = Guid.NewGuid();
        int httpPosts = 0;
        bool claimed = false;
        Guid? markedId = null;

        var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            httpPosts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SecurityAlerts:WebhookUrl"] = "https://alerts.example.test/merit" })
            .Build();
        var logger = new CapturingLogger();
        var notifier = new SecurityAlertWebhookNotifier(config, logger, client, [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);
        var data = StubLedgerMonitoringProxy.Create(
            () => new LedgerVerificationStateRow(),
            _ => new AuditLedgerVerificationRow { Verified = true },
            _ => new LedgerVerificationOutcomeRow { LedgerVerificationRunId = Guid.NewGuid() },
            (_, _) =>
            {
                if (claimed) return [];
                claimed = true;
                return
                [
                    new NewSecurityFindingRow
                    {
                        SecurityFindingId = findingId,
                        RootTenantId = Guid.NewGuid(),
                        RuleCode = "ledgerVerificationFailed",
                        Severity = "critical",
                        Title = "Audit ledger verification failed",
                        DetectedUtc = sqlNow.AddMinutes(-1),
                        ClaimToken = claimToken,
                        // SQL computed leaseUntilUtc = sqlNow + 300s (which is 5 minutes BEHIND workerNow!).
                        LeaseUntilUtc = sqlNow.AddSeconds(300),
                    },
                ];
            },
            (id, token) => { markedId = id; return token == claimToken; });

        int delivered = await SecurityAlertWebhookNotifier.DrainPendingLedgerAlertsAsync(
            data, notifier, logger, batchSize: 10, leaseSeconds: 300, ct: CancellationToken.None, utcNow: () => workerNow);

        Assert.Equal(1, delivered);
        Assert.Equal(1, httpPosts);
        Assert.Equal(findingId, markedId);
    }

    [Fact]
    public async Task UnsetWebhookUrl_ClaimsNothing_RecordsNoFailures_AndDeliversOnceConfigured()
    {
        // #356 (S30): With SecurityAlerts:WebhookUrl unset (or non-HTTPS), drain must not claim or record failures.
        var store = new InMemoryLedgerStore();
        var findingId = Guid.NewGuid();
        store.PendingAlerts.Add(new NewSecurityFindingRow
        {
            SecurityFindingId = findingId,
            RootTenantId = Guid.NewGuid(),
            RuleCode = "ledgerVerificationFailed",
            Severity = "critical",
            Title = "Audit ledger verification failed",
            DetectedUtc = DateTime.UtcNow,
        });

        int claimCalls = 0;
        int failureCalls = 0;
        int httpPosts = 0;
        var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            httpPosts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));

        var settings = new Dictionary<string, string?>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var logger = new CapturingLogger();
        var notifier = new SecurityAlertWebhookNotifier(config, logger, client, [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);
        var data = StubLedgerMonitoringProxy.Create(
            () => new LedgerVerificationStateRow(),
            _ => new AuditLedgerVerificationRow { Verified = true },
            _ => new LedgerVerificationOutcomeRow { LedgerVerificationRunId = Guid.NewGuid() },
            (batchSize, leaseSeconds) => { claimCalls++; return store.Claim(batchSize, leaseSeconds); },
            (id, claimToken) => store.MarkDelivered(id, claimToken),
            (id, claimToken, backoff, err) => { failureCalls++; store.RecordFailure(id, claimToken, backoff, err); });

        // 1. Unset URL -> claims nothing, records no failures, posts nothing.
        Assert.False(notifier.IsConfigured);
        int deliveredUnset = await SecurityAlertWebhookNotifier.DrainPendingLedgerAlertsAsync(data, notifier, logger);
        Assert.Equal(0, deliveredUnset);
        Assert.Equal(0, claimCalls);
        Assert.Equal(0, failureCalls);
        Assert.Equal(0, httpPosts);
        Assert.Empty(store.AttemptCounts);

        // 2. Non-HTTPS URL -> still claims nothing and records no failures.
        config["SecurityAlerts:WebhookUrl"] = "http://insecure.example.test/merit";
        Assert.False(notifier.IsConfigured);
        int deliveredHttp = await SecurityAlertWebhookNotifier.DrainPendingLedgerAlertsAsync(data, notifier, logger);
        Assert.Equal(0, deliveredHttp);
        Assert.Equal(0, claimCalls);
        Assert.Equal(0, failureCalls);

        // 3. Valid HTTPS URL configured -> next drain cycle claims and delivers immediately.
        config["SecurityAlerts:WebhookUrl"] = "https://alerts.example.test/merit";
        Assert.True(notifier.IsConfigured);
        int deliveredHttps = await SecurityAlertWebhookNotifier.DrainPendingLedgerAlertsAsync(data, notifier, logger);
        Assert.Equal(1, deliveredHttps);
        Assert.Equal(1, httpPosts);
        Assert.Equal(0, failureCalls);
        Assert.Contains(findingId, store.DeliveredFindingIds);
    }

    [Fact]
    public async Task RepeatedlyFailingOlderAlerts_DoNotStarveNewerCriticalFindings()
    {
        var store = new InMemoryLedgerStore();
        var baseTime = DateTime.UtcNow.AddDays(-12);
        var failingIds = new HashSet<Guid>();
        for (int i = 0; i < 10; i++)
        {
            var id = Guid.NewGuid();
            failingIds.Add(id);
            store.PendingAlerts.Add(new NewSecurityFindingRow
            {
                SecurityFindingId = id,
                RootTenantId = Guid.NewGuid(),
                RuleCode = "ledgerVerificationFailed",
                Severity = "critical",
                Title = $"Old failing alert {i}",
                DetectedUtc = baseTime.AddDays(i),
            });
        }
        var newerId = Guid.NewGuid();
        store.PendingAlerts.Add(new NewSecurityFindingRow
        {
            SecurityFindingId = newerId,
            RootTenantId = Guid.NewGuid(),
            RuleCode = "ledgerVerificationFailed",
            Severity = "critical",
            Title = "Newer critical alert",
            DetectedUtc = DateTime.UtcNow,
        });

        var client = new HttpClient(new StubHttpMessageHandler((req, _) =>
        {
            string key = req.Headers.GetValues("X-MERIT-Delivery-Key").First();
            bool isOldFailing = failingIds.Contains(Guid.Parse(key));
            return Task.FromResult(new HttpResponseMessage(isOldFailing ? HttpStatusCode.BadRequest : HttpStatusCode.OK));
        }));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SecurityAlerts:WebhookUrl"] = "https://alerts.example.test/merit" })
            .Build();
        var logger = new CapturingLogger();
        var notifier = new SecurityAlertWebhookNotifier(config, logger, client, [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);
        var data = StubLedgerMonitoringProxy.Create(
            () => new LedgerVerificationStateRow(),
            _ => new AuditLedgerVerificationRow { Verified = true },
            _ => new LedgerVerificationOutcomeRow { LedgerVerificationRunId = Guid.NewGuid() },
            (batchSize, leaseSeconds) => store.Claim(batchSize, leaseSeconds),
            (id, claimToken) => store.MarkDelivered(id, claimToken),
            (id, claimToken, backoff, err) => store.RecordFailure(id, claimToken, backoff, err));

        // Poll 1: claims the 10 older rows, all 10 fail (HTTP 400) and receive backoff + attemptCount=1.
        int delivered1 = await SecurityAlertWebhookNotifier.DrainPendingLedgerAlertsAsync(data, notifier, logger, batchSize: 10, leaseSeconds: 300);
        Assert.Equal(0, delivered1);
        Assert.Equal(10, store.AttemptCounts.Count);

        // Even if all leases expired before Poll 2, never-attempted row 11 (attemptCount=0) advances ahead of failed rows!
        store.ExpireAllLeases();
        int delivered2 = await SecurityAlertWebhookNotifier.DrainPendingLedgerAlertsAsync(data, notifier, logger, batchSize: 1, leaseSeconds: 300);
        Assert.Equal(1, delivered2);
        Assert.Contains(newerId, store.DeliveredFindingIds);
    }
}
