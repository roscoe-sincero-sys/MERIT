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
        public Func<IReadOnlyList<NewSecurityFindingRow>> OnRunScan { get; set; } = () => [];
        public Func<int, int, IReadOnlyList<NewSecurityFindingRow>> OnClaimPendingLedgerAlerts { get; set; } = (_, _) => [];
        public Action<Guid, Guid?> OnMarkDelivered { get; set; } = (_, _) => { };

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISecurityMonitoringData.RunSecurityFindingScanAsync))
            {
                return Task.FromResult(OnRunScan());
            }
            if (targetMethod?.Name == nameof(ISecurityMonitoringData.ClaimPendingLedgerAlertsAsync))
            {
                return Task.FromResult(OnClaimPendingLedgerAlerts((int)args![0]!, (int)args[1]!));
            }
            if (targetMethod?.Name == nameof(ISecurityMonitoringData.MarkSecurityFindingWebhookDeliveredAsync))
            {
                OnMarkDelivered((Guid)args![0]!, (Guid?)args[1]);
                return Task.CompletedTask;
            }

            throw new NotImplementedException(targetMethod?.Name);
        }

        public static ISecurityMonitoringData Create(
            Func<IReadOnlyList<NewSecurityFindingRow>> onRunScan,
            Func<int, int, IReadOnlyList<NewSecurityFindingRow>>? onClaimPendingLedgerAlerts = null,
            Action<Guid, Guid?>? onMarkDelivered = null)
        {
            var proxy = DispatchProxy.Create<ISecurityMonitoringData, StubSecurityMonitoringProxy>();
            var impl = (StubSecurityMonitoringProxy)(object)proxy;
            impl.OnRunScan = onRunScan;
            if (onClaimPendingLedgerAlerts is not null) impl.OnClaimPendingLedgerAlerts = onClaimPendingLedgerAlerts;
            if (onMarkDelivered is not null) impl.OnMarkDelivered = onMarkDelivered;
            return proxy;
        }
    }

    private sealed class CapturingOutboxLogger : ILogger<LedgerAlertOutboxWorker>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    private static (SecurityFindingScanner scanner, CapturingLogger logger, IReadOnlyList<NewSecurityFindingRow> findings, List<(Guid Id, Guid? ClaimToken)> delivered)
        CreateScanner(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
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
        var delivered = new List<(Guid Id, Guid? ClaimToken)>();
        var services = new ServiceCollection();
        services.AddSingleton(StubSecurityMonitoringProxy.Create(
            () => findings,
            onMarkDelivered: (id, token) => delivered.Add((id, token))));
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
        return (scanner, logger, findings, delivered);
    }

    [Fact]
    public async Task WebhookTimeout_RetriesAllAttempts_LogsTerminalFailure_AndKeepsScannerAlive()
    {
        int attempts = 0;
        var (scanner, logger, _, delivered) = CreateScanner((_, _) =>
        {
            attempts++;
            // Simulate HttpClient.Timeout firing while stoppingToken is NOT cancelled.
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 10 seconds elapsing.");
        });

        await scanner.RunScanCycleAsync(CancellationToken.None);

        Assert.Equal(3, attempts);
        Assert.Empty(delivered);
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
    public async Task ScanCycle_MarksDeliveredScannerFindings_AndOutboxWorkerDrainsIndependentlyOfScanErrors()
    {
        var delivered = new List<(Guid Id, Guid? ClaimToken)>();
        var claimToken = Guid.NewGuid();
        var pendingLedger = new NewSecurityFindingRow
        {
            SecurityFindingId = Guid.NewGuid(),
            RootTenantId = Guid.NewGuid(),
            RuleCode = "ledgerVerificationFailed",
            Severity = "critical",
            Title = "Audit ledger verification failed",
            DetectedUtc = DateTime.UtcNow.AddMinutes(-10),
            ClaimToken = claimToken,
        };

        // 1. Scanner marks high findings delivered with null claimToken after HTTP 200 (S27).
        var (scanner, _, findings, scannerDelivered) = CreateScanner((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        await scanner.RunScanCycleAsync(CancellationToken.None);
        Assert.Single(scannerDelivered);
        Assert.Equal(findings[0].SecurityFindingId, scannerDelivered[0].Id);
        Assert.Null(scannerDelivered[0].ClaimToken);

        // 2. Dedicated LedgerAlertOutboxWorker drains pending ledger alerts even if behavior scans are failing.
        bool claimedOnce = false;
        int requestedBatchSize = 0;
        var services = new ServiceCollection();
        services.AddSingleton(StubSecurityMonitoringProxy.Create(
            onRunScan: () => throw new InvalidOperationException("Behavior scan unavailable"),
            onClaimPendingLedgerAlerts: (batchSize, _) =>
            {
                requestedBatchSize = batchSize;
                if (claimedOnce) return [];
                claimedOnce = true;
                return [pendingLedger];
            },
            onMarkDelivered: (id, token) => delivered.Add((id, token))));
        var provider = services.BuildServiceProvider();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SecurityAlerts:WebhookUrl"] = "https://alerts.example.test/merit",
                ["SecurityAlerts:OutboxBatchSize"] = "7",
            })
            .Build();
        var outboxLogger = new CapturingOutboxLogger();
        var postedBodies = new List<string>();
        var notifier = new SecurityAlertWebhookNotifier(
            config,
            outboxLogger,
            new HttpClient(new StubHttpMessageHandler(async (req, ct) =>
            {
                postedBodies.Add(await req.Content!.ReadAsStringAsync(ct));
                return new HttpResponseMessage(HttpStatusCode.OK);
            })),
            [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);
        var outboxWorker = new LedgerAlertOutboxWorker(provider.GetRequiredService<IServiceScopeFactory>(), config, outboxLogger, notifier);

        int count = await outboxWorker.RunDrainCycleAsync(CancellationToken.None);
        Assert.Equal(1, count);
        Assert.Equal(7, requestedBatchSize);
        Assert.Single(postedBodies);
        Assert.Contains("ledgerVerificationFailed", postedBodies[0], StringComparison.Ordinal);
        Assert.Single(delivered);
        Assert.Equal(pendingLedger.SecurityFindingId, delivered[0].Id);
        Assert.Equal(claimToken, delivered[0].ClaimToken);
    }
}
