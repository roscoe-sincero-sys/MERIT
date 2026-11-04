using System.Threading.Channels;
using Merit.Application.Services;

namespace Merit.Web.Infrastructure.Email;

/// <summary>Account mail that must not hold up, or give away anything through, the request that caused it (#190).</summary>
public abstract record AccountMailJob(string? CorrelationId);

/// <summary>"Forgot your password?": looked up and sent off the request, so every answer takes the same time.</summary>
public sealed record PasswordResetJob(string UserName, string? ClientAddress, string PublicBaseUrl, string? CorrelationId) : AccountMailJob(CorrelationId);

/// <summary>"Your password was changed", after a change or a reset.</summary>
public sealed record PasswordChangedNoticeJob(Guid AppUserId, string? CorrelationId) : AccountMailJob(CorrelationId);

/// <summary>
/// A bounded in-memory queue drained by <see cref="AccountMailService"/>. Full means a flood of requests; new ones are
/// then dropped (and logged) rather than slowing the sign-in pages. Jobs hold a user name or id, never a token.
/// </summary>
public sealed partial class AccountMailQueue(ILogger<AccountMailQueue> logger)
{
    private readonly Channel<AccountMailJob> channel = Channel.CreateBounded<AccountMailJob>(
        new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public ChannelReader<AccountMailJob> Reader => channel.Reader;

    public void Enqueue(AccountMailJob job)
    {
        if (!channel.Writer.TryWrite(job))
        {
            LogDropped(logger, job.GetType().Name, job.CorrelationId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Account mail queue full: {Job} dropped, correlation {CorrelationId}")]
    private static partial void LogDropped(ILogger logger, string job, string? correlationId);
}

/// <summary>Sends queued account mail one job at a time, each in its own scope. A failure is logged and the next job runs.</summary>
public sealed partial class AccountMailService(AccountMailQueue queue, IServiceScopeFactory scopes, ILogger<AccountMailService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var passwords = scope.ServiceProvider.GetRequiredService<PasswordService>();
                switch (job)
                {
                    case PasswordResetJob reset:
                        await passwords.RequestResetAsync(reset.UserName, reset.ClientAddress, reset.CorrelationId, reset.PublicBaseUrl, stoppingToken)
                            .ConfigureAwait(false);
                        break;
                    case PasswordChangedNoticeJob notice:
                        await passwords.SendChangedNoticeAsync(notice.AppUserId, notice.CorrelationId, stoppingToken).ConfigureAwait(false);
                        break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogFailed(logger, ex, job.GetType().Name, job.CorrelationId);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Account mail job {Job} failed, correlation {CorrelationId}")]
    private static partial void LogFailed(ILogger logger, Exception exception, string job, string? correlationId);
}
