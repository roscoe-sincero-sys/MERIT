using Merit.Application.Data;
using Merit.Application.Jobs;
using Merit.Application.Security;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Merit.Worker;

public sealed class WorkerOptions
{
    public int PollSeconds { get; set; } = 2;

    public ClusterOptions ClusterDetection { get; set; } = new();

    public sealed class ClusterOptions
    {
        /// <summary>The named account cluster detection runs as; empty turns the schedule off.</summary>
        public string RunAsUserName { get; set; } = string.Empty;

        public int IntervalHours { get; set; } = 24;
    }
}

/// <summary>
/// The session a job runs under. Each job records the tenant and user that started it; the worker resolves that user's
/// session scope and every query for the job carries it in SESSION_CONTEXT, so background work sees exactly what the
/// user could. Held per async flow, so concurrent jobs never share a session.
/// </summary>
public sealed class JobSessionHolder
{
    private readonly AsyncLocal<SessionScopeResult?> current = new();

    public SessionScopeResult? Current
    {
        get => current.Value;
        set => current.Value = value;
    }
}

public sealed class JobCurrentSession(JobSessionHolder holder) : ICurrentSession
{
    private SessionScopeSummaryRow? S => holder.Current?.Summary;

    public bool IsAuthenticated => S is not null;

    public Guid? UserId => S?.AppUserId;

    public string? UserName => S is null ? null : $"{S.UserName} (worker)";

    public string? DisplayName => S?.DisplayName;

    public Guid? ScopeKey => S?.SessionScopeId;

    public Guid? RootTenantId => S?.RootTenantId;

    public Guid? ActingTenantId => S?.ActingTenantId;

    public string? ActingTenantName => S?.ActingTenantName;

    public IReadOnlySet<string> Permissions => holder.Current?.Permissions.ToHashSet(StringComparer.Ordinal)
                                               ?? new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>
/// Claims and runs jobs. Two worker copies never take the same job (row locks with READPAST in dbo.uspClaimNextJob).
/// A failed job is retried with back-off and finally marked failed for the administration screen; it is never dropped.
/// </summary>
public sealed partial class JobWorker(IServiceProvider services, JobSessionHolder sessions, IOptions<WorkerOptions> options,
                                      ILogger<JobWorker> logger) : BackgroundService
{
    private readonly string workerName = $"{Environment.MachineName}:{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, workerName);
        while (!stoppingToken.IsCancellationRequested)
        {
            bool ranOne = false;
            try
            {
                ranOne = await RunOneAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException or TimeoutException)
            {
                LogLoopError(logger, ex.GetType().Name);
            }

            if (!ranOne)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.Value.PollSeconds)), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> RunOneAsync(CancellationToken ct)
    {
        ClaimedJobRow? job;
        sessions.Current = null;
        using (var claimScope = services.CreateScope())
        {
            job = await claimScope.ServiceProvider.GetRequiredService<IWorkData>().ClaimNextJobAsync(workerName, ct);
        }

        if (job is null)
        {
            return false;
        }

        LogClaimed(logger, job.JobId, job.JobType, job.Attempts);
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        try
        {
            sessions.Current = await JobSessions.OpenAsync(sp.GetRequiredService<IAccountData>(), job, DateTime.UtcNow.AddHours(2), ct);
            string result = await sp.GetRequiredService<JobDispatcher>().RunAsync(job, ct);
            await sp.GetRequiredService<IWorkData>().CompleteJobAsync(job.JobId, result, ct);
            LogCompleted(logger, job.JobId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Record ids and the exception type only: messages can quote data values.
            LogFailed(logger, job.JobId, ex.GetType().Name);
            string text = $"{ex.GetType().Name} on attempt {job.Attempts}; job {job.JobId}." +
                          (ex is Application.Errors.MeritException ? " " + ex.Message : string.Empty);
            await sp.GetRequiredService<IWorkData>().FailJobAsync(job.JobId, text, CancellationToken.None);
        }
        finally
        {
            if (sessions.Current is { } s)
            {
                await sp.GetRequiredService<IAccountData>().RevokeSessionScopeAsync(s.Summary.SessionScopeId, CancellationToken.None);
            }

            sessions.Current = null;
        }

        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MERIT worker {WorkerName} started")]
    private static partial void LogStarted(ILogger logger, string workerName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Claimed job {JobId} ({JobType}), attempt {Attempt}")]
    private static partial void LogClaimed(ILogger logger, Guid jobId, string jobType, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Completed job {JobId}")]
    private static partial void LogCompleted(ILogger logger, Guid jobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId} failed: {ExceptionType}")]
    private static partial void LogFailed(ILogger logger, Guid jobId, string exceptionType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Worker loop error: {ExceptionType}; retrying")]
    private static partial void LogLoopError(ILogger logger, string exceptionType);
}

/// <summary>
/// Scheduled cluster detection, run as a named account so its actions are audited under it. Groups of cases are flagged
/// for staff review as cluster records and alert tasks; nothing is merged.
/// </summary>
public sealed partial class ClusterScheduler(IServiceProvider services, JobSessionHolder sessions, IOptions<WorkerOptions> options,
                                             ILogger<ClusterScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value.ClusterDetection;
        if (string.IsNullOrWhiteSpace(o.RunAsUserName))
        {
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            using (var scope = services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                try
                {
                    var user = await sp.GetRequiredService<IAccountData>().GetUserForSignInAsync(o.RunAsUserName, stoppingToken);
                    if (user is not null && user.IsActive)
                    {
                        sessions.Current = await sp.GetRequiredService<IAccountData>()
                            .CreateSessionScopeAsync(Guid.NewGuid(), user.AppUserId, DateTime.UtcNow.AddHours(1), ct: stoppingToken);
                        string result = await sp.GetRequiredService<JobDispatcher>().RunAsync(new ClaimedJobRow
                        {
                            JobId = Guid.Empty,
                            JobType = JobTypes.ClusterDetection,
                            RequestedByUserId = user.AppUserId,
                            RootTenantId = user.RootTenantId,
                            PayloadJson = "{}",
                        }, stoppingToken);
                        LogRan(logger, result);
                        await sp.GetRequiredService<IAccountData>().RevokeSessionScopeAsync(sessions.Current.Summary.SessionScopeId, stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogError(logger, ex.GetType().Name);
                }
                finally
                {
                    sessions.Current = null;
                }
            }

            await Task.Delay(TimeSpan.FromHours(Math.Max(1, o.IntervalHours)), stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Cluster detection ran: {Result}")]
    private static partial void LogRan(ILogger logger, string result);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cluster detection failed: {ExceptionType}")]
    private static partial void LogError(ILogger logger, string exceptionType);
}

/// <summary>Reports degraded when a job has waited more than ten minutes or any job has finally failed.</summary>
public sealed class QueueHealthCheck(IServiceProvider services) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = services.CreateScope();
            var depth = await scope.ServiceProvider.GetRequiredService<IWorkData>().GetQueueDepthAsync(cancellationToken);
            var oldest = depth.Where(d => d.OldestWaitingUtc is not null).Select(d => d.OldestWaitingUtc!.Value).DefaultIfEmpty(DateTime.UtcNow).Min();
            int failed = depth.Sum(d => d.Failed);
            var data = new Dictionary<string, object> { ["queued"] = depth.Sum(d => d.Queued), ["failed"] = failed, ["oldestWaitingUtc"] = oldest };
            return DateTime.UtcNow - oldest > TimeSpan.FromMinutes(10) || failed > 0
                ? HealthCheckResult.Degraded("Jobs are waiting or have failed.", data: data)
                : HealthCheckResult.Healthy("Queues are moving.", data);
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            return HealthCheckResult.Unhealthy("The database is not reachable.");
        }
    }
}

internal sealed class UnusedPasswordHasher : IPasswordHasher
{
    public string Hash(string password) => throw new NotSupportedException("The worker does not hash passwords.");

    public PasswordCheck Verify(string hash, string password) => PasswordCheck.Failed;
}

internal sealed class UnusedSecretProtector : ISecretProtector
{
    public string Protect(string plaintext) => throw new NotSupportedException("The worker does not protect secrets.");

    public string Unprotect(string protectedText) => throw new NotSupportedException("The worker does not protect secrets.");
}

internal sealed class UnusedBreachedList : IBreachedPasswordList
{
    public bool IsBreached(string password) => false;
}
