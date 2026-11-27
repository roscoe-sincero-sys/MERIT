using Merit.Application.Data;

namespace Merit.Worker;

/// <summary>Scrubs searchText and soft-deletes expired logs.searchLog rows under the records schedule (#329).</summary>
public sealed partial class SearchLogPurger(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<SearchLogPurger> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                int days = Math.Clamp(configuration.GetValue("SearchLog:RetentionDays", 90), 30, 3650);
                int count = await scope.ServiceProvider.GetRequiredService<ISecurityMonitoringData>()
                    .PurgeSearchLogAsync(days, stoppingToken).ConfigureAwait(false);
                Purged(logger, count, days);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Failed(logger, ex.GetType().Name); }
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Search log retention: {Count} row(s) scrubbed after {Days} days")]
    private static partial void Purged(ILogger logger, int count, int days);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Search log retention failed ({ExceptionType})")]
    private static partial void Failed(ILogger logger, string exceptionType);
}
