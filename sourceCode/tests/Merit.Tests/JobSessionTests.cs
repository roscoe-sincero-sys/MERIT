using System.Reflection;
using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Jobs;
using Merit.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Merit.Tests;

/// <summary>Issue #7: a background job runs under the profile, view-as and acting tenant of the session that queued it.</summary>
public sealed class JobSessionTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid Profile = Guid.NewGuid();
    private static readonly Guid ViewAs = Guid.NewGuid();
    private static readonly Guid Pennsylvania = Guid.NewGuid();
    private static readonly Guid Region = Guid.NewGuid();

    [Fact]
    public async Task TheJobSessionCarriesTheQueuingSessionsProfileViewAsAndTenant()
    {
        var accounts = FakeAccounts.Create(actingTenantReturned: Pennsylvania);
        var job = new ClaimedJobRow { RequestedByUserId = User, RequestedProfileId = Profile, RequestedViewAsUserId = ViewAs, RequestedActingTenantId = Pennsylvania };

        var scope = await JobSessions.OpenAsync(accounts, job, DateTime.UtcNow.AddHours(1));

        var call = Assert.Single(FakeAccounts.Of(accounts).Created);
        Assert.Equal((User, (Guid?)Profile, (Guid?)ViewAs, (Guid?)Pennsylvania), call);
        Assert.Equal(Pennsylvania, scope.Summary.ActingTenantId);
        Assert.Empty(FakeAccounts.Of(accounts).Revoked);
    }

    [Fact]
    public async Task AJobQueuedBeforeTheContextWasStoredStillRunsUnderTheMasterProfile()
    {
        var accounts = FakeAccounts.Create(actingTenantReturned: Region);
        await JobSessions.OpenAsync(accounts, new ClaimedJobRow { RequestedByUserId = User }, DateTime.UtcNow.AddHours(1));
        Assert.Equal((User, (Guid?)null, (Guid?)null, (Guid?)null), Assert.Single(FakeAccounts.Of(accounts).Created));
    }

    [Fact]
    public async Task AJobIsRefusedWhenTheRequesterLostTheTenantItWasStartedIn()
    {
        // The scope fell back to another tenant: running there would put the records in the wrong data root.
        var accounts = FakeAccounts.Create(actingTenantReturned: Region);
        var job = new ClaimedJobRow { RequestedByUserId = User, RequestedActingTenantId = Pennsylvania };

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => JobSessions.OpenAsync(accounts, job, DateTime.UtcNow.AddHours(1)));
        Assert.Contains("no longer has access", ex.Message, StringComparison.Ordinal);
        Assert.Single(FakeAccounts.Of(accounts).Revoked);
    }

    [Fact]
    public async Task ClusterSchedulerClearsSessionAndContinuesWhenScopeRevocationFails()
    {
        var accounts = FakeAccounts.Create(actingTenantReturned: Region);
        FakeAccounts.Of(accounts).ThrowOnRevoke = true;
        FakeAccounts.Of(accounts).UserForSignIn = new SignInUserRow { AppUserId = User, UserName = "clusterbot", RootTenantId = Region, IsActive = true };

        var services = new ServiceCollection()
            .AddSingleton(accounts)
            .BuildServiceProvider();
        var holder = new JobSessionHolder();
        var opts = Options.Create(new WorkerOptions { ClusterDetection = new WorkerOptions.ClusterOptions { RunAsUserName = "clusterbot" } });
        var scheduler = new ClusterScheduler(services, holder, opts, NullLogger<ClusterScheduler>.Instance);

        // Even when job execution fails (no JobDispatcher registered) AND RevokeSessionScopeAsync throws,
        // RunCycleAsync must NOT throw and must clear sessions.Current so the worker host keeps running.
        await scheduler.RunCycleAsync(CancellationToken.None);

        Assert.Null(holder.Current);
        Assert.Single(FakeAccounts.Of(accounts).Revoked);
    }

    [Fact]
    public async Task JobWorkerCancelsExecutionAndDiscardsResultWhenLeaseRenewalReportsClaimLost()
    {
        Guid jobId = Guid.NewGuid();
        Guid token = Guid.NewGuid();
        var accounts = FakeAccounts.Create(actingTenantReturned: Region);
        var work = FakeWork.Create(new ClaimedJobRow
        {
            JobId = jobId,
            JobType = JobTypes.ClusterDetection,
            RequestedByUserId = User,
            RootTenantId = Region,
            PayloadJson = "{}",
            Attempts = 1,
            ClaimToken = token,
        }, renewResult: false);

        var services = new ServiceCollection()
            .AddSingleton(accounts)
            .AddSingleton(work)
            .BuildServiceProvider();
        var holder = new JobSessionHolder();
        var opts = Options.Create(new WorkerOptions { LeaseSeconds = 5, HeartbeatMilliseconds = 25, SessionMinutes = 60 });
        var worker = new JobWorker(services, holder, opts, NullLogger<JobWorker>.Instance);

        bool observedCancel = false;
        bool ran = await worker.RunOneAsync(CancellationToken.None, async (_, jobCt) =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), jobCt);
            }
            catch (OperationCanceledException)
            {
                observedCancel = true;
                throw;
            }

            return "{}";
        });

        Assert.True(ran);
        Assert.True(observedCancel, "Job execution should be cancelled when lease renewal returns false.");
        Assert.True(FakeWork.Of(work).RenewCalls >= 1);
        Assert.Equal(0, FakeWork.Of(work).CompleteCalls);
        Assert.Equal(0, FakeWork.Of(work).FailCalls);
        Assert.Null(holder.Current);
        Assert.Single(FakeAccounts.Of(accounts).Revoked);
    }

    public class FakeWork : DispatchProxy
    {
        private ClaimedJobRow? job;
        private bool renewResult;
        public int RenewCalls { get; private set; }
        public int CompleteCalls { get; private set; }
        public int FailCalls { get; private set; }

        public static IWorkData Create(ClaimedJobRow job, bool renewResult)
        {
            var proxy = Create<IWorkData, FakeWork>();
            Of(proxy).job = job;
            Of(proxy).renewResult = renewResult;
            return proxy;
        }

        public static FakeWork Of(IWorkData proxy) => (FakeWork)(object)proxy;

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            nameof(IWorkData.ClaimNextJobAsync) => Task.FromResult(job),
            nameof(IWorkData.RenewJobClaimAsync) => Renew(),
            nameof(IWorkData.CompleteJobAsync) => Complete(),
            nameof(IWorkData.FailJobAsync) => Fail(),
            _ => throw new NotSupportedException(method?.Name),
        };

        private Task<bool> Renew() { RenewCalls++; return Task.FromResult(renewResult); }
        private Task<int> Complete() { CompleteCalls++; return Task.FromResult(1); }
        private Task<int> Fail() { FailCalls++; return Task.FromResult(1); }
    }

    /// <summary>IAccountData with only the calls JobSessions and ClusterScheduler make.</summary>
    public class FakeAccounts : DispatchProxy
    {
        public List<(Guid User, Guid? Profile, Guid? ViewAs, Guid? Acting)> Created { get; } = [];

        public List<Guid> Revoked { get; } = [];

        public Guid ActingTenantReturned { get; set; }

        public bool ThrowOnRevoke { get; set; }

        public SignInUserRow? UserForSignIn { get; set; }

        public static IAccountData Create(Guid actingTenantReturned)
        {
            var proxy = Create<IAccountData, FakeAccounts>();
            Of(proxy).ActingTenantReturned = actingTenantReturned;
            return proxy;
        }

        public static FakeAccounts Of(IAccountData proxy) => (FakeAccounts)(object)proxy;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method?.Name)
            {
                case nameof(IAccountData.GetUserForSignInAsync):
                    return Task.FromResult(UserForSignIn);
                case nameof(IAccountData.CreateSessionScopeAsync):
                    Created.Add(((Guid)args![1]!, (Guid?)args[3], (Guid?)args[4], (Guid?)args[5]));
                    var summary = new SessionScopeSummaryRow { SessionScopeId = (Guid)args[0]!, AppUserId = (Guid)args[1]!, ActingTenantId = ActingTenantReturned };
                    return Task.FromResult(new SessionScopeResult(summary, [], []));
                case nameof(IAccountData.RevokeSessionScopeAsync):
                    Revoked.Add((Guid)args![0]!);
                    if (ThrowOnRevoke)
                    {
                        return Task.FromException(new InvalidOperationException("Transient DB error during cleanup."));
                    }

                    return Task.CompletedTask;
                default:
                    throw new NotSupportedException(method?.Name);
            }
        }
    }
}
