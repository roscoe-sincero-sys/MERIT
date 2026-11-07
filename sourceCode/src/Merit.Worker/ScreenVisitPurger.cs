using Merit.Application.Data;

namespace Merit.Worker;

/// <summary>Retainable usage stays outside the immutable ledger. No hard deletes.</summary>
public sealed partial class ScreenVisitPurger(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<ScreenVisitPurger> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                int days = Math.Clamp(configuration.GetValue("ScreenVisit:RetentionDays", 90), 30, 365);
                int count = await scope.ServiceProvider.GetRequiredService<IScreenUsageData>().PurgeAsync(days, stoppingToken).ConfigureAwait(false);
                Purged(logger, count, days);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Failed(logger, ex.GetType().Name); }
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Screen usage retention: {Count} visits anonymized after {Days} days")]
    private static partial void Purged(ILogger logger, int count, int days);
    [LoggerMessage(Level = LogLevel.Warning, Message = "Screen usage retention failed ({ExceptionType})")]
    private static partial void Failed(ILogger logger, string exceptionType);
}
