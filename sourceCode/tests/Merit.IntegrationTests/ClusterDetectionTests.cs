using Merit.Application.Security;
using Merit.Data.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Merit.IntegrationTests;

/// <summary>Issue #181: cluster detection reports how many clusters it created, so the worker's log line is true.</summary>
[Collection(MeritCollection.Name)]
public sealed class ClusterDetectionTests(MeritFixture fixture)
{
    [SqlFact]
    public async Task DetectionReportsTheClustersItCreates()
    {
        var people = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var results = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var cases = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var location = Guid.NewGuid();
        var scope = Guid.NewGuid();
        string tag = location.ToString("N")[..8].ToUpperInvariant();
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();
        try
        {
            // Two open cases whose people share one new home: the group detection should turn into a cluster.
            await Sql(c, """
                DECLARE @mia UNIQUEIDENTIFIER = @miaPerson;
                INSERT dbo.location (locationId, rootTenantId, owningTenantId, addressLine1, city, state, zip5, addressKey)
                SELECT @location, rootTenantId, rootTenantId, CONCAT (N'77 Cluster ', @tag, N' St'), N'Baltimore', 'MD', '21201',
                       CONCAT (N'77 CLUSTER ', @tag, N' ST||BALTIMORE|MD|21201')
                  FROM dbo.person WHERE personId = @mia;
                """, ("@miaPerson", fixture.Ids.MiaPersonId), ("@location", location), ("@tag", tag));
            for (int i = 0; i < 2; i++)
            {
                await Sql(c, """
                    INSERT dbo.person (personId, rootTenantId, owningTenantId, lastName, firstName, lastNameKey, dob)
                    SELECT @person, rootTenantId, owningTenantId, CONCAT (N'Cluster', @tag), N'Test', CONCAT (N'CLUSTER', @tag), '2021-01-01'
                      FROM dbo.person WHERE personId = @miaPerson;
                    INSERT dbo.personLocation (personLocationId, rootTenantId, owningTenantId, personId, locationId, role, isPrimary, startDate, source)
                    SELECT NEWID (), rootTenantId, owningTenantId, @person, @location, 'residence', 1, '2020-01-01', 'interview'
                      FROM dbo.person WHERE personId = @person;
                    INSERT dbo.labResult (labResultId, rootTenantId, owningTenantId, personId, analyteSpecimenId, collectedDate, receivedUtc, resultStatus,
                        valueReported, unitReported, valueCanonical, unitCanonical, labOrganizationId, reportedSnapshotJson, intakeBatchId, population)
                    SELECT @result, rootTenantId, owningTenantId, @person, analyteSpecimenId, collectedDate, receivedUtc, resultStatus,
                        valueReported, unitReported, valueCanonical, unitCanonical, labOrganizationId, N'{}', intakeBatchId, population
                      FROM dbo.labResult WHERE labResultId = @miaResult;
                    INSERT dbo.exposureCase (exposureCaseId, rootTenantId, owningTenantId, caseNumber, personId, analyteId, population, classification,
                        computedClassification, caseStatus, qualifyingLabResultId, workflowTemplate, openedDate)
                    SELECT @case, rootTenantId, owningTenantId, CONCAT ('TEST-', LEFT (CONVERT (VARCHAR (36), @case), 8)), @person, analyteId,
                        population, classification, computedClassification, 'open', @result, workflowTemplate, CAST (DATEADD (DAY, -7, SYSUTCDATETIME ()) AS DATE)
                      FROM dbo.exposureCase WHERE exposureCaseId = @miaCase;
                    """, ("@person", people[i]), ("@result", results[i]), ("@case", cases[i]), ("@location", location), ("@tag", tag),
                    ("@miaPerson", fixture.Ids.MiaPersonId), ("@miaResult", fixture.Ids.MiaLabResultId), ("@miaCase", fixture.Ids.MiaCaseId));
            }

            // The worker's way in: its own login, under a supervisor's session scope.
            var user = (Guid)(await Sql(c, "SELECT appUserId FROM auth.appUser WHERE userName = N'supervisor'"))!;
            await Sql(c, """
                DECLARE @expires DATETIME2 (3) = DATEADD (MINUTE, 30, SYSUTCDATETIME ());
                EXEC auth.uspCreateSessionScope @sessionScopeId = @scope, @appUserId = @user, @expiresUtc = @expires;
                """, ("@scope", scope), ("@user", user));
            var acting = (Guid)(await Sql(c, "SELECT actingTenantId FROM auth.sessionScope WHERE sessionScopeId = @scope", ("@scope", scope)))!;
            var work = new WorkData(new Db(Options.Create(new MeritDatabaseOptions { ConnectionString = TestEnvironment.WorkerConnectionString }),
                new ScopeSession(scope, user, acting)));

            Assert.Equal(1, await work.DetectClustersAsync(365, 2));
            Assert.Equal(1, await Sql(c, "SELECT COUNT (*) FROM dbo.cluster WHERE sharedKey = @location AND isDeleted = 0", ("@location", location)));
            // The same group again is the existing cluster, not a new one.
            Assert.Equal(0, await work.DetectClustersAsync(365, 2));
        }
        finally
        {
            await Sql(c, """
                DECLARE @clusters TABLE (clusterId UNIQUEIDENTIFIER);
                INSERT @clusters SELECT clusterId FROM dbo.cluster WHERE sharedKey = @location;
                DELETE dbo.workTask WHERE clusterId IN (SELECT clusterId FROM @clusters) OR exposureCaseId IN (@case1, @case2);
                DELETE dbo.caseCluster WHERE clusterId IN (SELECT clusterId FROM @clusters) OR exposureCaseId IN (@case1, @case2);
                DELETE dbo.cluster WHERE clusterId IN (SELECT clusterId FROM @clusters);
                DELETE dbo.exposureCase WHERE exposureCaseId IN (@case1, @case2);
                DELETE dbo.labResult WHERE labResultId IN (@result1, @result2);
                DELETE dbo.personLocation WHERE personId IN (@person1, @person2);
                DELETE dbo.personAccess WHERE personId IN (@person1, @person2);
                DELETE dbo.person WHERE personId IN (@person1, @person2);
                DELETE dbo.location WHERE locationId = @location;
                DELETE auth.sessionScopeTenant WHERE sessionScopeId = @scope; DELETE auth.sessionScopePermission WHERE sessionScopeId = @scope;
                DELETE auth.sessionScopeRoot WHERE sessionScopeId = @scope; DELETE auth.sessionScopeAdminTenant WHERE sessionScopeId = @scope;
                DELETE auth.sessionScope WHERE sessionScopeId = @scope;
                """, ("@location", location), ("@case1", cases[0]), ("@case2", cases[1]), ("@result1", results[0]), ("@result2", results[1]),
                ("@person1", people[0]), ("@person2", people[1]), ("@scope", scope));
        }
    }

    private static async Task<object?> Sql(SqlConnection c, string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = new SqlCommand(sql, c);
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return await cmd.ExecuteScalarAsync();
    }

    /// <summary>The session a background job runs under: just its scope, as the worker's JobSessions provides.</summary>
    private sealed class ScopeSession(Guid scope, Guid user, Guid acting) : ICurrentSession
    {
        public bool IsAuthenticated => true;
        public Guid? UserId => user;
        public string? UserName => "supervisor";
        public string? DisplayName => "Supervisor";
        public Guid? ScopeKey => scope;
        public Guid? RootTenantId => null;
        public Guid? ActingTenantId => acting;
        public string? ActingTenantName => null;
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
    }
}
