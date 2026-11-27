using Merit.Application.Data;

namespace Merit.Worker;

/// <summary>
/// Hard-deletes expired internal SQL execution rows from <c>logs.ExecutionLog</c> past
/// <c>ExecutionLog:RetentionDays</c> (default 90, clamped 30..3650) in 5,000-row batches (#334).
/// </summary>
public sealed partial class ExecutionLogPurger(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ILogger<ExecutionLogPurger> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(4), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                int days = Math.Clamp(configuration.GetValue("ExecutionLog:RetentionDays", 90), 30, 3650);
                int count = await scope.ServiceProvider.GetRequiredService<ISecurityMonitoringData>()
                    .PurgeExecutionLogAsync(days, stoppingToken).ConfigureAwait(false);
                Purged(logger, count, days);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Failed(logger, ex.GetType().Name);
            }

            await Task.Delay(TimeSpan.FromHours(24), stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "ExecutionLog retention: {Count} row(s) deleted after {Days} days")]
    private static partial void Purged(ILogger logger, int count, int days);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ExecutionLog retention failed ({ExceptionType})")]
    private static partial void Failed(ILogger logger, string exceptionType);
}
