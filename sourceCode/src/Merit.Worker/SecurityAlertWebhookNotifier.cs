using System.Net.Http.Json;
using Merit.Application.Data;

namespace Merit.Worker;

/// <summary>
/// Outbound HTTPS webhook notifier for high/critical security findings (<c>SecurityAlerts:WebhookUrl</c>, #327, #347, #354).
/// Shared by <see cref="SecurityFindingScanner"/> and <see cref="AuditLedgerVerifier"/> so every critical alert uses
/// the same payload schema and bounded 3-attempt retry policy.
/// </summary>
public interface ISecurityAlertNotifier
{
    /// <summary>Posts findings to the configured HTTPS webhook. Returns <c>true</c> only on an HTTP 2xx response.</summary>
    Task<bool> NotifyAsync(IReadOnlyList<NewSecurityFindingRow> findings, CancellationToken ct);
}

public sealed partial class SecurityAlertWebhookNotifier : ISecurityAlertNotifier
{
    private static readonly HttpClient DefaultWebhookClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly TimeSpan[] DefaultRetryDelays = [TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];

    private readonly IConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly HttpClient _webhookClient;
    private readonly TimeSpan[] _retryDelays;

    public SecurityAlertWebhookNotifier(IConfiguration configuration, ILogger<SecurityAlertWebhookNotifier> logger)
        : this(configuration, (ILogger)logger, DefaultWebhookClient, DefaultRetryDelays)
    {
    }

    internal SecurityAlertWebhookNotifier(
        IConfiguration configuration,
        ILogger logger,
        HttpClient webhookClient,
        TimeSpan[] retryDelays)
    {
        _configuration = configuration;
        _logger = logger;
        _webhookClient = webhookClient;
        _retryDelays = retryDelays;
    }

    public async Task<bool> NotifyAsync(IReadOnlyList<NewSecurityFindingRow> findings, CancellationToken ct)
    {
        if (findings.Count == 0)
        {
            return false;
        }

        string? webhookUrl = _configuration["SecurityAlerts:WebhookUrl"];
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return false;
        }

        try
        {
            if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                WebhookSkipped(_logger);
                return false;
            }

            string deliveryKey = findings.Count == 1
                ? findings[0].SecurityFindingId.ToString("D")
                : string.Join(",", findings.Select(f => f.SecurityFindingId.ToString("D")));

            object payload = new
            {
                source = "MERIT",
                eventType = "securityFinding.detected",
                deliveryKey,
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
                    using var req = new HttpRequestMessage(HttpMethod.Post, uri)
                    {
                        Content = JsonContent.Create(payload),
                    };
                    req.Headers.TryAddWithoutValidation("X-MERIT-Delivery-Key", deliveryKey);
                    using var resp = await _webhookClient.SendAsync(req, ct).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        return true;
                    }

                    int code = (int)resp.StatusCode;
                    bool transient = code == 429 || code >= 500;
                    if (!transient || attempt == _retryDelays.Length - 1)
                    {
                        WebhookFailed(_logger, code);
                        return false;
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

        return false;
    }

    /// <summary>
    /// Atomically claims a bounded batch of open, undelivered <c>ledgerVerificationFailed</c> findings via
    /// <c>logs.uspClaimPendingLedgerAlerts</c> (UPDLOCK/READPAST/ROWLOCK lease), posts each to the webhook, and marks
    /// delivery with its claim token only after HTTP 2xx (#354). Safe under concurrent drainers and worker replicas.
    /// </summary>
    internal static async Task<int> DrainPendingLedgerAlertsAsync(
        ISecurityMonitoringData data,
        ISecurityAlertNotifier? notifier,
        ILogger logger,
        int batchSize = 10,
        int leaseSeconds = 300,
        CancellationToken ct = default)
    {
        if (notifier is null)
        {
            return 0;
        }

        int deliveredCount = 0;
        try
        {
            var claimed = await data.ClaimPendingLedgerAlertsAsync(batchSize, leaseSeconds, ct).ConfigureAwait(false);
            foreach (var finding in claimed)
            {
                if (await notifier.NotifyAsync([finding], ct).ConfigureAwait(false))
                {
                    await data.MarkSecurityFindingWebhookDeliveredAsync(finding.SecurityFindingId, finding.ClaimToken, CancellationToken.None).ConfigureAwait(false);
                    deliveredCount++;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LedgerAlertOutboxError(logger, ex.GetType().Name);
        }

        return deliveredCount;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Security alert webhook skipped: URL must be absolute HTTPS")]
    private static partial void WebhookSkipped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Security alert webhook returned HTTP {StatusCode}")]
    private static partial void WebhookFailed(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Security alert webhook delivery failed ({ExceptionType})")]
    private static partial void WebhookException(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audit ledger alert outbox drain failed ({ExceptionType}); pending alerts will retry on the next cycle.")]
    private static partial void LedgerAlertOutboxError(ILogger logger, string exceptionType);
}
