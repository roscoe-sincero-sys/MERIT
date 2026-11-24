using Merit.Application;
using Merit.Application.Data;
using Merit.Application.Jobs;
using Merit.Application.Security;
using Merit.Data.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Merit.IntegrationTests;

/// <summary>
/// Live SQL integration tests for issue #311: job claim fencing tokens and lease-renewal heartbeats.
/// </summary>
[Collection(MeritCollection.Name)]
public sealed class JobLeaseFencingTests(MeritFixture fixture)
{
    private static async Task<object?> SqlAsync(SqlConnection c, string sql, params (string Name, object? Value)[] args)
    {
        await using var cmd = new SqlCommand(sql, c);
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return await cmd.ExecuteScalarAsync();
    }

    private static ServiceProvider CreateWorkerServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICurrentSession>(new AnonymousWorkerSession());
        services.AddMeritApplication();
        services.AddMeritSqlServer(o => o.ConnectionString = TestEnvironment.WorkerConnectionString);
        return services.BuildServiceProvider();
    }

    [SqlFact]
    public async Task ReclaimedJobCannotBeCompletedOrFailedByStaleTokenAndCanBeFinishedByCurrentToken()
    {
        _ = fixture.Ids;
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();
        await using var sp = CreateWorkerServices();
        var work = sp.GetRequiredService<IWorkData>();

        Guid job1 = Guid.NewGuid();
        Guid job2 = Guid.NewGuid();
        try
        {
            await SqlAsync(c, """
                INSERT dbo.jobQueue (jobId, rootTenantId, owningTenantId, jobType, payloadJson, payloadVersion, jobStatus, availableUtc, requestedByUserId)
                SELECT @j1, p.rootTenantId, p.rootTenantId, 'clusterDetection', N'{}', 1, 'queued', DATEADD (YEAR, -1, SYSUTCDATETIME ()), u.appUserId
                  FROM dbo.person AS p CROSS JOIN auth.appUser AS u
                 WHERE p.personId = @person AND u.userName = N'admin';
                """, ("@j1", job1), ("@person", fixture.Ids.MiaPersonId));

            // 1. Worker A claims job1.
            var claimA = await work.ClaimNextJobAsync("worker-a", leaseSeconds: 300);
            Assert.NotNull(claimA);
            Assert.Equal(job1, claimA.JobId);
            Assert.NotEqual(Guid.Empty, claimA.ClaimToken);

            // 2. Move claimedUtc past the threshold and reclaim under Worker B.
            await SqlAsync(c, "UPDATE dbo.jobQueue SET claimedUtc = DATEADD (SECOND, -600, SYSUTCDATETIME ()) WHERE jobId = @id", ("@id", job1));

            var claimB = await work.ClaimNextJobAsync("worker-b", leaseSeconds: 300);
            Assert.NotNull(claimB);
            Assert.Equal(job1, claimB.JobId);
            Assert.NotEqual(Guid.Empty, claimB.ClaimToken);
            Assert.NotEqual(claimA.ClaimToken, claimB.ClaimToken);

            // 3. Stale token A cannot renew, complete, or fail the job.
            Assert.False(await work.RenewJobClaimAsync(job1, claimA.ClaimToken));
            Assert.Equal(0, await work.CompleteJobAsync(job1, "{\"from\":\"a\"}", claimA.ClaimToken));
            Assert.Equal(0, await work.FailJobAsync(job1, "stale failure from a", claimA.ClaimToken));
            Assert.Equal(0, await work.CompleteJobAsync(job1, "{\"from\":\"null\"}", null));

            Assert.Equal("running", await SqlAsync(c, "SELECT jobStatus FROM dbo.jobQueue WHERE jobId = @id", ("@id", job1)));
            Assert.Equal("worker-b", await SqlAsync(c, "SELECT claimedBy FROM dbo.jobQueue WHERE jobId = @id", ("@id", job1)));
            Assert.Equal(DBNull.Value, await SqlAsync(c, "SELECT resultJson FROM dbo.jobQueue WHERE jobId = @id", ("@id", job1)));

            // 4. Current token B completes job1.
            Assert.Equal(1, await work.CompleteJobAsync(job1, "{\"from\":\"b\"}", claimB.ClaimToken));
            Assert.Equal("succeeded", await SqlAsync(c, "SELECT jobStatus FROM dbo.jobQueue WHERE jobId = @id", ("@id", job1)));
            Assert.Equal("{\"from\":\"b\"}", await SqlAsync(c, "SELECT resultJson FROM dbo.jobQueue WHERE jobId = @id", ("@id", job1)));

            // 5. Verify FailJobAsync with current token B also applies cleanly after reclaim.
            await SqlAsync(c, """
                INSERT dbo.jobQueue (jobId, rootTenantId, owningTenantId, jobType, payloadJson, payloadVersion, jobStatus, availableUtc, requestedByUserId)
                SELECT @j2, p.rootTenantId, p.rootTenantId, 'clusterDetection', N'{}', 1, 'queued', DATEADD (YEAR, -1, SYSUTCDATETIME ()), u.appUserId
                  FROM dbo.person AS p CROSS JOIN auth.appUser AS u
                 WHERE p.personId = @person AND u.userName = N'admin';
                """, ("@j2", job2), ("@person", fixture.Ids.MiaPersonId));

            var fA = await work.ClaimNextJobAsync("worker-a", leaseSeconds: 300);
            Assert.NotNull(fA);
            Assert.Equal(job2, fA.JobId);
            await SqlAsync(c, "UPDATE dbo.jobQueue SET claimedUtc = DATEADD (SECOND, -600, SYSUTCDATETIME ()) WHERE jobId = @id", ("@id", job2));
            var fB = await work.ClaimNextJobAsync("worker-b", leaseSeconds: 300);
            Assert.NotNull(fB);
            Assert.Equal(job2, fB.JobId);

            Assert.Equal(0, await work.FailJobAsync(job2, "stale error", fA.ClaimToken));
            Assert.Equal(1, await work.FailJobAsync(job2, "current error from b", fB.ClaimToken));
            Assert.Equal("current error from b", await SqlAsync(c, "SELECT lastError FROM dbo.jobQueue WHERE jobId = @id", ("@id", job2)));
        }
        finally
        {
            await SqlAsync(c, "DELETE FROM dbo.jobQueue WHERE jobId IN (@j1, @j2)", ("@j1", job1), ("@j2", job2));
        }
    }

    [SqlFact]
    public async Task JobWithActiveHeartbeatIsNotReclaimedPastLeaseThreshold()
    {
        _ = fixture.Ids;
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();
        await using var sp = CreateWorkerServices();
        var work = sp.GetRequiredService<IWorkData>();

        Guid jobId = Guid.NewGuid();
        try
        {
            await SqlAsync(c, """
                INSERT dbo.jobQueue (jobId, rootTenantId, owningTenantId, jobType, payloadJson, payloadVersion, jobStatus, availableUtc, requestedByUserId)
                SELECT @id, p.rootTenantId, p.rootTenantId, 'clusterDetection', N'{}', 1, 'queued', DATEADD (YEAR, -1, SYSUTCDATETIME ()), u.appUserId
                  FROM dbo.person AS p CROSS JOIN auth.appUser AS u
                 WHERE p.personId = @person AND u.userName = N'admin';
                """, ("@id", jobId), ("@person", fixture.Ids.MiaPersonId));

            // Claim with a short 2-second lease, then renew every 400ms for 2.8 seconds (exceeding the lease duration).
            var claimed = await work.ClaimNextJobAsync("worker-long", leaseSeconds: 2);
            Assert.NotNull(claimed);
            Assert.Equal(jobId, claimed.JobId);

            for (int i = 0; i < 7; i++)
            {
                await Task.Delay(400);
                Assert.True(await work.RenewJobClaimAsync(jobId, claimed.ClaimToken));
                var stolen = await work.ClaimNextJobAsync("worker-stealer", leaseSeconds: 2);
                Assert.True(stolen is null || stolen.JobId != jobId, "Active heartbeating job must not be reclaimed.");
            }

            Assert.Equal("worker-long", await SqlAsync(c, "SELECT claimedBy FROM dbo.jobQueue WHERE jobId = @id", ("@id", jobId)));
            Assert.Equal(1, await work.CompleteJobAsync(jobId, "{\"ok\":true}", claimed.ClaimToken));
        }
        finally
        {
            await SqlAsync(c, "DELETE FROM dbo.jobQueue WHERE jobId = @id", ("@id", jobId));
        }
    }

    private sealed class AnonymousWorkerSession : ICurrentSession
    {
        public bool IsAuthenticated => false;
        public Guid? UserId => null;
        public string? UserName => null;
        public string? DisplayName => null;
        public Guid? ScopeKey => null;
        public Guid? RootTenantId => null;
        public Guid? ActingTenantId => null;
        public string? ActingTenantName => null;
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>(StringComparer.Ordinal);
    }
}
