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
    Task NotifyAsync(IReadOnlyList<NewSecurityFindingRow> findings, CancellationToken ct);
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

    public async Task NotifyAsync(IReadOnlyList<NewSecurityFindingRow> findings, CancellationToken ct)
    {
        if (findings.Count == 0)
        {
            return;
        }

        string? webhookUrl = _configuration["SecurityAlerts:WebhookUrl"];
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return;
        }

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Security alert webhook skipped: URL must be absolute HTTPS")]
    private static partial void WebhookSkipped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Security alert webhook returned HTTP {StatusCode}")]
    private static partial void WebhookFailed(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Security alert webhook delivery failed ({ExceptionType})")]
    private static partial void WebhookException(ILogger logger, string exceptionType);
}
