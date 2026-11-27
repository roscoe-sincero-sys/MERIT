using Merit.Application.Data;

namespace Merit.Worker;

/// <summary>
/// Periodically verifies DDL audit integrity (<c>logs.uspDdlAuditVerify</c>) and checks for
/// <c>logs.ExecutionLog</c> failure spikes, emitting structured warnings/errors (#330).
/// </summary>
public sealed partial class OperationalIntegrityMonitor(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ILogger<OperationalIntegrityMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            int intervalMinutes = Math.Clamp(configuration.GetValue("OperationalIntegrity:IntervalMinutes", 15), 5, 1440);
            int spikeThreshold = Math.Clamp(configuration.GetValue("OperationalIntegrity:FailureSpikeThreshold", 25), 1, 10000);
            try
            {
                using var scope = scopes.CreateScope();
                var result = await scope.ServiceProvider.GetRequiredService<ISecurityMonitoringData>()
                    .CheckOperationalIntegrityAsync(intervalMinutes, stoppingToken).ConfigureAwait(false);

                foreach (var problem in result.DdlProblems)
                {
                    DdlIntegrityProblem(logger, problem.CheckName, problem.Status, problem.Detail ?? string.Empty);
                }

                if (result.ExecutionSummary.FailureCount >= spikeThreshold)
                {
                    ExecutionFailureSpike(logger, result.ExecutionSummary.FailureCount,
                        result.ExecutionSummary.AffectedProcedures, intervalMinutes);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                CheckFailed(logger, ex.GetType().Name);
            }

            await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "DDL audit integrity alert: check={CheckName} status={Status} detail={Detail}")]
    private static partial void DdlIntegrityProblem(ILogger logger, string checkName, string status, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SQL ExecutionLog failure spike: {FailureCount} failure(s) across {AffectedProcedures} procedure(s) in the last {Minutes} minute(s)")]
    private static partial void ExecutionFailureSpike(ILogger logger, int failureCount, int affectedProcedures, int minutes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Operational integrity check failed ({ExceptionType})")]
    private static partial void CheckFailed(ILogger logger, string exceptionType);
}
