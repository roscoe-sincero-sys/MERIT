using System.Reflection;
using Merit.Application.Data;
using Merit.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Merit.Tests;

/// <summary>
/// Unit tests for <see cref="AuditLedgerVerifier"/> write-once digest archival and prior-digest verification (#334).
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

    public class StubLedgerMonitoringProxy : DispatchProxy
    {
        public Func<string?, AuditLedgerVerificationRow> OnVerify { get; set; } = _ => new AuditLedgerVerificationRow();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISecurityMonitoringData.VerifyAuditLedgerAsync))
            {
                return Task.FromResult(OnVerify((string?)args?[0]));
            }

            throw new NotImplementedException(targetMethod?.Name);
        }

        public static ISecurityMonitoringData Create(Func<string?, AuditLedgerVerificationRow> onVerify)
        {
            var proxy = DispatchProxy.Create<ISecurityMonitoringData, StubLedgerMonitoringProxy>();
            ((StubLedgerMonitoringProxy)(object)proxy).OnVerify = onVerify;
            return proxy;
        }
    }

    [Fact]
    public async Task WritesDigestOnce_VerifiesPriorDigestOnNextCycle_AndAlertsOnFailure()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "merit-ledger-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var calls = new List<string?>();
            bool failNext = false;
            var services = new ServiceCollection();
            services.AddSingleton(StubLedgerMonitoringProxy.Create(digestArg =>
            {
                calls.Add(digestArg);
                if (failNext)
                {
                    return new AuditLedgerVerificationRow { Verified = false, Detail = "Hash mismatch" };
                }

                return new AuditLedgerVerificationRow
                {
                    Verified = true,
                    LastVerifiedBlockId = 7,
                    DigestJson = """{"database_name":"MERIT_IT","block_id":7,"hash":"0xABCD"}""",
                    VerifiedUtc = DateTime.UtcNow,
                    Detail = "Ledger verified up to block 7.",
                };
            }));
            var provider = services.BuildServiceProvider();
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LedgerVerification:DigestDirectory"] = tempDir,
                })
                .Build();
            var logger = new CapturingLogger();
            var verifier = new AuditLedgerVerifier(provider.GetRequiredService<IServiceScopeFactory>(), config, logger);

            // Cycle 1: no prior digest; generates fresh digest and archives it write-once.
            await verifier.RunVerificationCycleAsync(CancellationToken.None);
            Assert.Single(calls);
            Assert.Null(calls[0]);
            var files = Directory.GetFiles(tempDir, "merit-ledger-digest-*.json");
            Assert.Single(files);

            // Cycle 2: verifies prior archived digest first, then generates and archives next digest.
            await verifier.RunVerificationCycleAsync(CancellationToken.None);
            Assert.Equal(3, calls.Count);
            Assert.Contains("0xABCD", calls[1] ?? "", StringComparison.Ordinal);
            Assert.Null(calls[2]);

            // Cycle 3: verification failure logs Error alert.
            failNext = true;
            await verifier.RunVerificationCycleAsync(CancellationToken.None);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("AUDIT LEDGER VERIFICATION FAILED", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
