using Merit.Application.Data;

namespace Merit.Worker;

/// <summary>Retains logs.auditEventRecord manifests for the configured window, then scrubs and soft-deletes (#333).</summary>
public sealed partial class AuditEventRecordPurger(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<AuditEventRecordPurger> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                int days = Math.Clamp(configuration.GetValue("AuditManifest:RetentionDays", 365), 30, 3650);
                int count = await scope.ServiceProvider.GetRequiredService<IWorkData>().PurgeAuditEventRecordsAsync(days, stoppingToken).ConfigureAwait(false);
                Purged(logger, count, days);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Failed(logger, ex.GetType().Name); }
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Audit manifest retention: {Count} record(s) scrubbed after {Days} days")]
    private static partial void Purged(ILogger logger, int count, int days);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audit manifest retention failed ({ExceptionType})")]
    private static partial void Failed(ILogger logger, string exceptionType);
}
