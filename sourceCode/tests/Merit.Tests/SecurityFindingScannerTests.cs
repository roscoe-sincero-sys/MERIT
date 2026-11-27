using System.Net;
using System.Reflection;
using Merit.Application.Data;
using Merit.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Merit.Tests;

/// <summary>
/// Regression tests for <see cref="SecurityFindingScanner"/> webhook timeout retry vs. service-stop cancellation (#347).
/// </summary>
public sealed class SecurityFindingScannerTests
{
    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }

    private sealed class CapturingLogger : ILogger<SecurityFindingScanner>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    public class StubSecurityMonitoringProxy : DispatchProxy
    {
        public IReadOnlyList<NewSecurityFindingRow> Rows { get; set; } = [];
        public Func<IReadOnlyList<NewSecurityFindingRow>> OnGetPendingLedgerAlerts { get; set; } = () => [];
        public Action<Guid> OnMarkDelivered { get; set; } = _ => { };

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISecurityMonitoringData.RunSecurityFindingScanAsync))
            {
                return Task.FromResult(Rows);
            }
            if (targetMethod?.Name == nameof(ISecurityMonitoringData.GetPendingLedgerAlertsAsync))
            {
                return Task.FromResult(OnGetPendingLedgerAlerts());
            }
            if (targetMethod?.Name == nameof(ISecurityMonitoringData.MarkSecurityFindingWebhookDeliveredAsync))
            {
                OnMarkDelivered((Guid)args![0]!);
                return Task.CompletedTask;
            }

            throw new NotImplementedException(targetMethod?.Name);
        }

        public static ISecurityMonitoringData Create(
            IReadOnlyList<NewSecurityFindingRow> rows,
            Func<IReadOnlyList<NewSecurityFindingRow>>? onGetPendingLedgerAlerts = null,
            Action<Guid>? onMarkDelivered = null)
        {
            var proxy = DispatchProxy.Create<ISecurityMonitoringData, StubSecurityMonitoringProxy>();
            var impl = (StubSecurityMonitoringProxy)(object)proxy;
            impl.Rows = rows;
            if (onGetPendingLedgerAlerts is not null) impl.OnGetPendingLedgerAlerts = onGetPendingLedgerAlerts;
            if (onMarkDelivered is not null) impl.OnMarkDelivered = onMarkDelivered;
            return proxy;
        }
    }

    private static (SecurityFindingScanner scanner, CapturingLogger logger, IReadOnlyList<NewSecurityFindingRow> findings, List<Guid> deliveredIds)
        CreateScanner(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
            IReadOnlyList<NewSecurityFindingRow>? pendingLedgerAlerts = null)
    {
        var findings = new[]
        {
            new NewSecurityFindingRow
            {
                SecurityFindingId = Guid.NewGuid(),
                RootTenantId = Guid.NewGuid(),
                RuleCode = "excessivePersonReads",
                Severity = "high",
                Title = "Spike",
                DetectedUtc = DateTime.UtcNow,
            },
        };
        var deliveredIds = new List<Guid>();
        var pending = pendingLedgerAlerts ?? [];
        var services = new ServiceCollection();
        services.AddSingleton(StubSecurityMonitoringProxy.Create(
            findings,
            () => pending.Where(p => !deliveredIds.Contains(p.SecurityFindingId)).ToList(),
            id => deliveredIds.Add(id)));
        var provider = services.BuildServiceProvider();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SecurityAlerts:WebhookUrl"] = "https://alerts.example.test/merit",
            })
            .Build();
        var logger = new CapturingLogger();
        var httpClient = new HttpClient(new StubHttpMessageHandler(handler));
        var scanner = new SecurityFindingScanner(
            provider.GetRequiredService<IServiceScopeFactory>(),
            config,
            logger,
            httpClient,
            [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);
        return (scanner, logger, findings, deliveredIds);
    }

    [Fact]
    public async Task WebhookTimeout_RetriesAllAttempts_LogsTerminalFailure_AndKeepsScannerAlive()
    {
        int attempts = 0;
        var (scanner, logger, _, deliveredIds) = CreateScanner((_, _) =>
        {
            attempts++;
            // Simulate HttpClient.Timeout firing while stoppingToken is NOT cancelled.
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 10 seconds elapsing.");
        });

        await scanner.RunScanCycleAsync(CancellationToken.None);

        Assert.Equal(3, attempts);
        Assert.Empty(deliveredIds);
        Assert.Contains(logger.Messages, m => m.Contains("Security alert webhook delivery failed (TaskCanceledException)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ServiceStopCancellation_PropagatesImmediately_WithoutRetrying()
    {
        int attempts = 0;
        using var cts = new CancellationTokenSource();
        var (scanner, _, _, _) = CreateScanner((_, ct) =>
        {
            attempts++;
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanner.RunScanCycleAsync(cts.Token));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ScanCycle_MarksDeliveredScannerFindings_AndDrainsPendingLedgerAlertsFromFailedCycle()
    {
        var pendingLedger = new NewSecurityFindingRow
        {
            SecurityFindingId = Guid.NewGuid(),
            RootTenantId = Guid.NewGuid(),
            RuleCode = "ledgerVerificationFailed",
            Severity = "critical",
            Title = "Audit ledger verification failed",
            DetectedUtc = DateTime.UtcNow.AddMinutes(-10),
        };

        var postedBodies = new List<string>();
        var (scanner, _, findings, deliveredIds) = CreateScanner(
            async (req, ct) =>
            {
                postedBodies.Add(await req.Content!.ReadAsStringAsync(ct));
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            pendingLedgerAlerts: [pendingLedger]);

        // Scan cycle delivers both the new high scanner finding and the pending ledger alert left over from a failed cycle.
        await scanner.RunScanCycleAsync(CancellationToken.None);

        Assert.Equal(2, postedBodies.Count);
        Assert.Contains(findings[0].SecurityFindingId, deliveredIds);
        Assert.Contains(pendingLedger.SecurityFindingId, deliveredIds);
        Assert.Contains(postedBodies, b => b.Contains("ledgerVerificationFailed", StringComparison.Ordinal));

        // Next scan cycle: pending ledger alert was already marked delivered -> only the scanner finding is posted.
        await scanner.RunScanCycleAsync(CancellationToken.None);
        Assert.Equal(3, postedBodies.Count);
    }
}
