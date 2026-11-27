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

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISecurityMonitoringData.RunSecurityFindingScanAsync))
            {
                return Task.FromResult(Rows);
            }

            throw new NotImplementedException(targetMethod?.Name);
        }

        public static ISecurityMonitoringData Create(IReadOnlyList<NewSecurityFindingRow> rows)
        {
            var proxy = DispatchProxy.Create<ISecurityMonitoringData, StubSecurityMonitoringProxy>();
            ((StubSecurityMonitoringProxy)(object)proxy).Rows = rows;
            return proxy;
        }
    }

    private static (SecurityFindingScanner scanner, CapturingLogger logger, IReadOnlyList<NewSecurityFindingRow> findings)
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
        var services = new ServiceCollection();
        services.AddSingleton(StubSecurityMonitoringProxy.Create(findings));
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
        return (scanner, logger, findings);
    }

    [Fact]
    public async Task WebhookTimeout_RetriesAllAttempts_LogsTerminalFailure_AndKeepsScannerAlive()
    {
        int attempts = 0;
        var (scanner, logger, _) = CreateScanner((_, _) =>
        {
            attempts++;
            // Simulate HttpClient.Timeout firing while stoppingToken is NOT cancelled.
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 10 seconds elapsing.");
        });

        await scanner.RunScanCycleAsync(CancellationToken.None);

        Assert.Equal(3, attempts);
        Assert.Contains(logger.Messages, m => m.Contains("Security alert webhook delivery failed (TaskCanceledException)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ServiceStopCancellation_PropagatesImmediately_WithoutRetrying()
    {
        int attempts = 0;
        using var cts = new CancellationTokenSource();
        var (scanner, _, _) = CreateScanner((_, ct) =>
        {
            attempts++;
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanner.RunScanCycleAsync(cts.Token));
        Assert.Equal(1, attempts);
    }
}
