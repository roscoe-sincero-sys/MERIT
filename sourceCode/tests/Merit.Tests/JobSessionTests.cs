using System.Reflection;
using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Jobs;

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

    /// <summary>IAccountData with only the two calls JobSessions makes.</summary>
    public class FakeAccounts : DispatchProxy
    {
        public List<(Guid User, Guid? Profile, Guid? ViewAs, Guid? Acting)> Created { get; } = [];

        public List<Guid> Revoked { get; } = [];

        public Guid ActingTenantReturned { get; set; }

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
                case nameof(IAccountData.CreateSessionScopeAsync):
                    Created.Add(((Guid)args![1]!, (Guid?)args[3], (Guid?)args[4], (Guid?)args[5]));
                    var summary = new SessionScopeSummaryRow { SessionScopeId = (Guid)args[0]!, AppUserId = (Guid)args[1]!, ActingTenantId = ActingTenantReturned };
                    return Task.FromResult(new SessionScopeResult(summary, [], []));
                case nameof(IAccountData.RevokeSessionScopeAsync):
                    Revoked.Add((Guid)args![0]!);
                    return Task.CompletedTask;
                default:
                    throw new NotSupportedException(method?.Name);
            }
        }
    }
}
