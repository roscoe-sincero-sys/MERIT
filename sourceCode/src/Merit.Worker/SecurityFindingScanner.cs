using Merit.Application.Data;

namespace Merit.Worker;

/// <summary>
/// Periodically runs behavioral anomaly detection across active tenants, persists new findings in
/// <c>logs.securityFinding</c>, logs structured alerts, and optionally posts high-severity alerts to a webhook (#327).
/// </summary>
public sealed partial class SecurityFindingScanner : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SecurityFindingScanner> _logger;
    private readonly ISecurityAlertNotifier _notifier;

    public SecurityFindingScanner(
        IServiceScopeFactory scopes,
        IConfiguration configuration,
        ILogger<SecurityFindingScanner> logger,
        ISecurityAlertNotifier notifier)
    {
        _scopes = scopes;
        _configuration = configuration;
        _logger = logger;
        _notifier = notifier;
    }

    internal SecurityFindingScanner(
        IServiceScopeFactory scopes,
        IConfiguration configuration,
        ILogger<SecurityFindingScanner> logger,
        HttpClient webhookClient,
        TimeSpan[] retryDelays)
        : this(scopes, configuration, logger, new SecurityAlertWebhookNotifier(configuration, logger, webhookClient, retryDelays))
    {
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            int intervalMinutes = Math.Clamp(_configuration.GetValue("SecurityAlerts:ScanIntervalMinutes", 15), 5, 1440);
            await RunScanCycleAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task RunScanCycleAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            int windowHours = Math.Clamp(_configuration.GetValue("SecurityAlerts:WindowHours", 24), 1, 168);
            var inserted = await scope.ServiceProvider.GetRequiredService<ISecurityMonitoringData>()
                .RunSecurityFindingScanAsync(windowHours, stoppingToken).ConfigureAwait(false);
            if (inserted.Count > 0)
            {
                FindingsDetected(_logger, inserted.Count, windowHours);
                var high = inserted.Where(f => f.Severity is "high" or "critical").ToList();
                if (high.Count > 0)
                {
                    await _notifier.NotifyAsync(high, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            ScanFailed(_logger, ex.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Security anomaly scan persisted {Count} new finding(s) over the last {Hours}h")]
    private static partial void FindingsDetected(ILogger logger, int count, int hours);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Security anomaly scan failed ({ExceptionType})")]
    private static partial void ScanFailed(ILogger logger, string exceptionType);
}
