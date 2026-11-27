using System.Reflection;
using Merit.Application.Data;
using Merit.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Merit.Tests;

/// <summary>
/// Unit tests for <see cref="AuditLedgerVerifier"/> covering WORM directory verification, missing/malformed archive
/// failures, atomic publish cancellation, automatic-storage mode, and outcome recording (#334).
/// </summary>
public sealed class AuditLedgerVerifierTests
{
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
        public Func<VerifyCall, AuditLedgerVerificationRow> OnVerify { get; set; } = _ => new AuditLedgerVerificationRow();
        public Func<OutcomeRecord, int?> OnRecord { get; set; } = _ => null;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
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
            Func<VerifyCall, AuditLedgerVerificationRow> onVerify,
            Func<OutcomeRecord, int?>? onRecord = null)
        {
            var proxy = DispatchProxy.Create<ISecurityMonitoringData, StubLedgerMonitoringProxy>();
            var impl = (StubLedgerMonitoringProxy)(object)proxy;
            impl.OnVerify = onVerify;
            if (onRecord is not null) impl.OnRecord = onRecord;
            return proxy;
        }
    }

    private static (AuditLedgerVerifier verifier, CapturingLogger logger, List<VerifyCall> verifyCalls, List<OutcomeRecord> outcomes)
        CreateVerifier(string? digestDir, Func<VerifyCall, AuditLedgerVerificationRow> onVerify)
    {
        var verifyCalls = new List<VerifyCall>();
        var outcomes = new List<OutcomeRecord>();
        int? lastRecordedCount = null;
        var services = new ServiceCollection();
        services.AddSingleton(StubLedgerMonitoringProxy.Create(
            call => { verifyCalls.Add(call); return onVerify(call); },
            rec =>
            {
                outcomes.Add(rec);
                int? prev = lastRecordedCount;
                if (rec.AnchorSource == "digestDirectory" && rec.FileCount is not null) lastRecordedCount = rec.FileCount;
                return prev;
            }));
        var provider = services.BuildServiceProvider();
        var dict = new Dictionary<string, string?>();
        if (digestDir is not null) dict["LedgerVerification:DigestDirectory"] = digestDir;
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var logger = new CapturingLogger();
        var verifier = new AuditLedgerVerifier(provider.GetRequiredService<IServiceScopeFactory>(), config, logger);
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
            Assert.Contains(outcomes, o => o.AnchorSource == "digestDirectory" && o.Verified);

            // Cycle 2: verifies prior archived digest with requireDigest=true, then generates next digest.
            await verifier.RunVerificationCycleAsync(CancellationToken.None);
            Assert.Equal(3, calls.Count);
            Assert.True(calls[1].RequireDigest);
            Assert.Contains("0xABCD", calls[1].DigestJson ?? "", StringComparison.Ordinal);

            // Cycle 3: SQL verification failure logs Error and records failure outcome.
            failNext = true;
            await verifier.RunVerificationCycleAsync(CancellationToken.None);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("AUDIT LEDGER VERIFICATION FAILED", StringComparison.Ordinal));
            Assert.False(outcomes[^1].Verified);

            // Cycle 4: deleting an archived file fails visibly and records failure outcome.
            failNext = false;
            File.Delete(files[0]);
            await verifier.RunVerificationCycleAsync(CancellationToken.None);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("AUDIT LEDGER DIGEST ARCHIVE SHRANK", StringComparison.Ordinal));
            Assert.False(outcomes[^1].Verified);
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
    public async Task AutomaticStorageOnlyMode_CallsAutomaticVerificationAndRecordsAutomaticSource()
    {
        var (verifier, _, calls, outcomes) = CreateVerifier(null, call => new AuditLedgerVerificationRow
        {
            Verified = true,
            LastVerifiedBlockId = 12,
            DigestJson = call.UseAutomaticStorage ? null : "{}",
            VerifiedUtc = DateTime.UtcNow,
            Detail = call.UseAutomaticStorage ? "Automatic digest storage verified up to block 12." : "OK",
            HasAutomaticDigestStorage = true,
        });

        await verifier.RunVerificationCycleAsync(CancellationToken.None);

        Assert.Equal(2, calls.Count);
        Assert.False(calls[0].UseAutomaticStorage);
        Assert.True(calls[1].UseAutomaticStorage);
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
        });

        await verifier.RunVerificationCycleAsync(CancellationToken.None);

        Assert.Single(outcomes);
        Assert.Equal("unanchored", outcomes[0].AnchorSource);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("not anchored outside SQL Server", StringComparison.Ordinal));
    }
}
