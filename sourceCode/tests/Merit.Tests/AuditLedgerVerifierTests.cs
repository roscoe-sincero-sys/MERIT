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

            throw new NotImplementedException(targetMethod?.Name);
        }

        public static ISecurityMonitoringData Create(
            Func<LedgerVerificationStateRow> onGetState,
            Func<VerifyCall, AuditLedgerVerificationRow> onVerify,
            Func<OutcomeRecord, LedgerVerificationOutcomeRow> onRecord)
        {
            var proxy = DispatchProxy.Create<ISecurityMonitoringData, StubLedgerMonitoringProxy>();
            var impl = (StubLedgerMonitoringProxy)(object)proxy;
            impl.OnGetState = onGetState;
            impl.OnVerify = onVerify;
            impl.OnRecord = onRecord;
            return proxy;
        }
    }

    private static (AuditLedgerVerifier verifier, CapturingLogger logger, List<VerifyCall> verifyCalls, List<OutcomeRecord> outcomes)
        CreateVerifier(string? digestDir, Func<VerifyCall, AuditLedgerVerificationRow> onVerify,
                       bool hasAutomaticStorage = false, int? initialGoodCount = null,
                       string? webhookUrl = null, HttpClient? webhookClient = null)
    {
        var verifyCalls = new List<VerifyCall>();
        var outcomes = new List<OutcomeRecord>();
        int? durableLastGoodCount = initialGoodCount;
        bool findingCreatedToday = false;
        var services = new ServiceCollection();
        services.AddSingleton(StubLedgerMonitoringProxy.Create(
            () => new LedgerVerificationStateRow { HasAutomaticDigestStorage = hasAutomaticStorage, LastGoodDigestFileCount = durableLastGoodCount },
            call =>
            {
                verifyCalls.Add(call);
                var r = onVerify(call);
                if (!r.Verified && !findingCreatedToday)
                {
                    findingCreatedToday = true;
                    return new AuditLedgerVerificationRow
                    {
                        Verified = false,
                        LastVerifiedBlockId = r.LastVerifiedBlockId,
                        DigestJson = r.DigestJson,
                        VerifiedUtc = r.VerifiedUtc == default ? DateTime.UtcNow : r.VerifiedUtc,
                        Detail = r.Detail,
                        HasAutomaticDigestStorage = r.HasAutomaticDigestStorage,
                        FindingCreated = true,
                        SecurityFindingId = Guid.NewGuid(),
                        RootTenantId = Guid.NewGuid(),
                    };
                }
                return r;
            },
            rec =>
            {
                outcomes.Add(rec);
                if (rec.AnchorSource == "digestDirectory" && rec.Verified && rec.FileCount is not null)
                {
                    durableLastGoodCount = Math.Max(durableLastGoodCount ?? 0, rec.FileCount.Value);
                }
                bool created = false;
                Guid? fid = null;
                Guid? rid = null;
                if (!rec.Verified && !findingCreatedToday)
                {
                    findingCreatedToday = true;
                    created = true;
                    fid = Guid.NewGuid();
                    rid = Guid.NewGuid();
                }
                return new LedgerVerificationOutcomeRow
                {
                    LedgerVerificationRunId = Guid.NewGuid(),
                    FindingCreated = created,
                    SecurityFindingId = fid,
                    RootTenantId = rid,
                    DetectedUtc = DateTime.UtcNow,
                };
            }));
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
        var client = new HttpClient(new StubHttpMessageHandler(async (req, ct) =>
        {
            httpPosts++;
            lastBody = await req.Content!.ReadAsStringAsync(ct);
            // First attempt returns 503 (transient), second attempt succeeds -> proves shared retry policy.
            return new HttpResponseMessage(httpPosts == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        }));

        string missingDir = Path.Combine(Path.GetTempPath(), "merit-ledger-webhook-" + Guid.NewGuid().ToString("N"));
        var (verifier, _, _, _) = CreateVerifier(
            missingDir,
            _ => new AuditLedgerVerificationRow { Verified = false, Detail = "Hash mismatch" },
            webhookUrl: "https://alerts.example.test/merit",
            webhookClient: client);

        // First failure of the day: posts critical finding (1 transient 503 + 1 retry 200 = 2 HTTP calls).
        await verifier.RunVerificationCycleAsync(CancellationToken.None);
        Assert.Equal(2, httpPosts);
        Assert.NotNull(lastBody);
        Assert.Contains("securityFinding.detected", lastBody, StringComparison.Ordinal);
        Assert.Contains("ledgerVerificationFailed", lastBody, StringComparison.Ordinal);
        Assert.Contains("critical", lastBody, StringComparison.Ordinal);

        // Repeat failure on the same UTC day: SQL daily fingerprint deduplicates -> ZERO additional webhook posts.
        await verifier.RunVerificationCycleAsync(CancellationToken.None);
        Assert.Equal(2, httpPosts);
    }
}
