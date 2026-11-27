using Merit.Application.Data;

namespace Merit.Worker;

/// <summary>Scrubs beforeJson/afterJson and soft-deletes expired logs.auditEventChange snapshots (#332).</summary>
public sealed partial class AuditEventChangePurger(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<AuditEventChangePurger> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                int days = Math.Clamp(configuration.GetValue("AuditChangeHistory:RetentionDays", 365), 30, 3650);
                int count = await scope.ServiceProvider.GetRequiredService<ISecurityMonitoringData>()
                    .PurgeAuditEventChangesAsync(days, stoppingToken).ConfigureAwait(false);
                Purged(logger, count, days);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Failed(logger, ex.GetType().Name); }
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Audit change-history retention: {Count} row(s) scrubbed after {Days} days")]
    private static partial void Purged(ILogger logger, int count, int days);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audit change-history retention failed ({ExceptionType})")]
    private static partial void Failed(ILogger logger, string exceptionType);
}
