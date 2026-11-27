using Merit.Application.Data;

namespace Merit.Worker;

/// <summary>
/// Dedicated background service that drains open, undelivered <c>ledgerVerificationFailed</c> alerts from the SQL
/// outbox in bounded, atomically leased batches (#354). Runs on its own schedule independent of both
/// <see cref="AuditLedgerVerifier"/> (daily verification) and <see cref="SecurityFindingScanner"/> (behavior scan),
/// so neither a webhook outage backlog nor a behavior-scan error can delay ledger verification or alert recovery.
/// </summary>
public sealed class LedgerAlertOutboxWorker(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ILogger<LedgerAlertOutboxWorker> logger,
    ISecurityAlertNotifier notifier) : BackgroundService
{
    private const int DefaultBatchSize = 10;
    private const int DefaultLeaseSeconds = 300;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            int fallbackMinutes = configuration.GetValue("SecurityAlerts:ScanIntervalMinutes", 15);
            int intervalMinutes = Math.Clamp(configuration.GetValue("SecurityAlerts:OutboxPollIntervalMinutes", fallbackMinutes), 1, 1440);
            await RunDrainCycleAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task<int> RunDrainCycleAsync(CancellationToken ct)
    {
        int batchSize = Math.Clamp(configuration.GetValue("SecurityAlerts:OutboxBatchSize", DefaultBatchSize), 1, 100);
        int leaseSeconds = Math.Clamp(configuration.GetValue("SecurityAlerts:OutboxLeaseSeconds", DefaultLeaseSeconds), 60, 3600);
        using var scope = scopes.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<ISecurityMonitoringData>();
        return await SecurityAlertWebhookNotifier.DrainPendingLedgerAlertsAsync(data, notifier, logger, batchSize, leaseSeconds, ct).ConfigureAwait(false);
    }
}
