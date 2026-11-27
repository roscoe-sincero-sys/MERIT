using System.Net.Http.Json;
using Merit.Application.Data;

namespace Merit.Worker;

/// <summary>
/// Periodically runs behavioral anomaly detection across active tenants, persists new findings in
/// <c>logs.securityFinding</c>, logs structured alerts, and optionally posts high-severity alerts to a webhook (#327).
/// </summary>
public sealed partial class SecurityFindingScanner : BackgroundService
{
    private static readonly HttpClient DefaultWebhookClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly TimeSpan[] DefaultRetryDelays = [TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];

    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SecurityFindingScanner> _logger;
    private readonly HttpClient _webhookClient;
    private readonly TimeSpan[] _retryDelays;

    public SecurityFindingScanner(
        IServiceScopeFactory scopes,
        IConfiguration configuration,
        ILogger<SecurityFindingScanner> logger)
        : this(scopes, configuration, logger, DefaultWebhookClient, DefaultRetryDelays)
    {
    }

    internal SecurityFindingScanner(
        IServiceScopeFactory scopes,
        IConfiguration configuration,
        ILogger<SecurityFindingScanner> logger,
        HttpClient webhookClient,
        TimeSpan[] retryDelays)
    {
        _scopes = scopes;
        _configuration = configuration;
        _logger = logger;
        _webhookClient = webhookClient;
        _retryDelays = retryDelays;
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
                string? webhookUrl = _configuration["SecurityAlerts:WebhookUrl"];
                if (!string.IsNullOrWhiteSpace(webhookUrl))
                {
                    var high = inserted.Where(f => f.Severity is "high" or "critical").ToList();
                    if (high.Count > 0)
                    {
                        await NotifyWebhookAsync(webhookUrl, high, stoppingToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            ScanFailed(_logger, ex.GetType().Name);
        }
    }

    internal async Task NotifyWebhookAsync(string webhookUrl, IReadOnlyList<NewSecurityFindingRow> findings, CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                WebhookSkipped(_logger);
                return;
            }

            object payload = new
            {
                source = "MERIT",
                eventType = "securityFinding.detected",
                count = findings.Count,
                findings = findings.Select(f => new
                {
                    f.SecurityFindingId,
                    f.RootTenantId,
                    f.RuleCode,
                    f.Severity,
                    f.Title,
                    f.DetectedUtc,
                }),
            };

            // #347: Bounded retry (3 attempts: immediate, +1s, +3s) for transient 5xx/429/network/timeout errors.
            for (int attempt = 0; attempt < _retryDelays.Length; attempt++)
            {
                if (_retryDelays[attempt] > TimeSpan.Zero)
                {
                    await Task.Delay(_retryDelays[attempt], ct).ConfigureAwait(false);
                }

                try
                {
                    using var resp = await _webhookClient.PostAsJsonAsync(uri, payload, ct).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        return;
                    }

                    int code = (int)resp.StatusCode;
                    bool transient = code == 429 || code >= 500;
                    if (!transient || attempt == _retryDelays.Length - 1)
                    {
                        WebhookFailed(_logger, code);
                        return;
                    }
                }
                catch (Exception ex) when ((ex is not OperationCanceledException || !ct.IsCancellationRequested) && attempt < _retryDelays.Length - 1)
                {
                    // Retry transient transport/timeout errors up to _retryDelays.Length attempts.
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            WebhookException(_logger, ex.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Security anomaly scan persisted {Count} new finding(s) over the last {Hours}h")]
    private static partial void FindingsDetected(ILogger logger, int count, int hours);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Security anomaly scan failed ({ExceptionType})")]
    private static partial void ScanFailed(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Security alert webhook skipped: URL must be absolute HTTPS")]
    private static partial void WebhookSkipped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Security alert webhook returned HTTP {StatusCode}")]
    private static partial void WebhookFailed(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Security alert webhook delivery failed ({ExceptionType})")]
    private static partial void WebhookException(ILogger logger, string exceptionType);
}
