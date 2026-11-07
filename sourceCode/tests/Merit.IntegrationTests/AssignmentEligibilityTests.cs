using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace Merit.IntegrationTests;

[Collection(MeritCollection.Name)]
public sealed class AssignmentEligibilityTests(MeritFixture fixture)
{
    private async Task<SyntheticAccount> WorkerAsync(string role, Guid tenant, string access = "full", string dates = "current")
    {
        var account = await SyntheticAccount.CreateAsync(fixture.Factory, "assignment");
        await account.ScalarAsync<int>("""
            INSERT auth.userAssignment (userAssignmentId, appUserId, roleId, tenantId, includeDescendants, accessLevel, startDate, endDate)
            SELECT NEWID (), @id, r.roleId, @tenant, 1, @access,
                   CASE WHEN @dates = 'future' THEN '2099-01-01' ELSE '2020-01-01' END,
                   CASE WHEN @dates = 'expired' THEN '2020-12-31' END
              FROM auth.role AS r JOIN auth.appUser AS u ON u.rootTenantId = r.rootTenantId AND u.appUserId = @id
             WHERE r.roleCode = @role AND r.isDeleted = 0;
            SELECT 0;
            """, ("@tenant", tenant), ("@role", role), ("@access", access), ("@dates", dates));
        return account;
    }

    [SqlTheory]
    [InlineData("caseManager", "full", "current", true)]
    [InlineData("epidemiologist", "full", "current", false)]
    [InlineData("caseManager", "read", "current", false)]
    [InlineData("caseManager", "full", "future", false)]
    [InlineData("caseManager", "full", "expired", false)]
    public async Task PickersAndEligibilityRespectCapabilityAccessLevelAndGrantDates(string role, string access, string dates, bool eligible)
    {
        await using var worker = await WorkerAsync(role, fixture.Ids.BaltimoreCityTenantId, access, dates);
        var client = await fixture.SignedInAsync("supervisor");
        string json = await client.Http.GetStringAsync("/api/v1/users/assignable");
        Assert.Equal(eligible, json.Contains(worker.Id.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(eligible ? 1 : 0, await worker.ScalarAsync<int>("SELECT COUNT (*) FROM auth.tvfAssignableUserTenants ('case.edit') WHERE appUserId = @id AND tenantId = @tenant", ("@tenant", fixture.Ids.BaltimoreCityTenantId)));
        Assert.Equal(0, await worker.ScalarAsync<int>("SELECT COUNT (*) FROM auth.tvfAssignableUserTenants ('case.edit') WHERE appUserId = @id AND tenantId = @tenant", ("@tenant", fixture.Ids.BaltimoreCountyTenantId)));
    }

    [SqlFact]
    public async Task BulkAssignmentIsAtomicForMixedScopesAndRejectsReadOnlyWorkers()
    {
        await using var worker = await WorkerAsync("caseManager", fixture.Ids.BaltimoreCityTenantId);
        await using var reader = await WorkerAsync("epidemiologist", fixture.Ids.BaltimoreCityTenantId);
        Guid city = Guid.NewGuid(), county = Guid.NewGuid();
        try
        {
            await worker.ScalarAsync<int>("""
                INSERT dbo.workTask (workTaskId, rootTenantId, owningTenantId, queueCode, title)
                SELECT @cityTask, rootTenantId, tenantId, 'caseFollowUp', N'Assignment eligibility test' FROM auth.tenant WHERE tenantId = @city;
                INSERT dbo.workTask (workTaskId, rootTenantId, owningTenantId, queueCode, title)
                SELECT @countyTask, rootTenantId, tenantId, 'caseFollowUp', N'Assignment eligibility test' FROM auth.tenant WHERE tenantId = @county;
                SELECT 0;
                """, ("@cityTask", city), ("@countyTask", county), ("@city", fixture.Ids.BaltimoreCityTenantId), ("@county", fixture.Ids.BaltimoreCountyTenantId));
            var client = await fixture.SignedInAsync("supervisor");
            await client.Http.GetStringAsync("/");
            using (var denied = await client.Http.PostAsJsonAsync("/api/v1/tasks/bulk", new { taskIds = new[] { city, county }, action = "assign", assignToUserId = worker.Id }))
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, denied.StatusCode);
                string reason = await denied.Content.ReadAsStringAsync();
                Assert.Contains("No tasks were assigned", reason);
                Assert.Contains("1 of 2 selected tasks", reason);
            }
            Assert.Equal(0, await worker.ScalarAsync<int>("SELECT COUNT (*) FROM dbo.workTask WHERE workTaskId IN (@city, @county) AND assignedToUserId IS NOT NULL", ("@city", city), ("@county", county)));
            using (var denied = await client.Http.PostAsJsonAsync("/api/v1/tasks/bulk", new { taskIds = new[] { city }, action = "assign", assignToUserId = reader.Id }))
                Assert.Equal(HttpStatusCode.UnprocessableEntity, denied.StatusCode);
            using (var accepted = await client.Http.PostAsJsonAsync("/api/v1/tasks/bulk", new { taskIds = new[] { city }, action = "assign", assignToUserId = worker.Id }))
                Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            Assert.Equal(1, await worker.ScalarAsync<int>("SELECT COUNT (*) FROM dbo.workTask WHERE workTaskId = @task AND assignedToUserId = @id", ("@task", city)));
            await worker.ScalarAsync<int>("UPDATE auth.userAssignment SET isDeleted = 1 WHERE appUserId = @id; SELECT 0");
            Assert.DoesNotContain(worker.Id.ToString(), await client.Http.GetStringAsync("/api/v1/users/assignable"));
            // History remains attached after loss of eligibility.
            Assert.Equal(1, await worker.ScalarAsync<int>("SELECT COUNT (*) FROM dbo.workTask WHERE workTaskId = @task AND assignedToUserId = @id", ("@task", city)));
        }
        finally { await worker.ScalarAsync<int>("DELETE dbo.workTask WHERE workTaskId IN (@city, @county); SELECT 0", ("@city", city), ("@county", county)); }
    }

    [SqlFact]
    public async Task CaseWriteRejectsIneligibleAssigneeAndKeepsExistingAssignmentVisible()
    {
        await using var worker = await WorkerAsync("caseManager", fixture.Ids.BaltimoreCityTenantId);
        await using var reader = await WorkerAsync("epidemiologist", fixture.Ids.BaltimoreCityTenantId);
        Guid id = fixture.Ids.MiaCaseId;
        Assert.NotEqual(Guid.Empty, id);
        string previous = await worker.ScalarAsync<string>("SELECT COALESCE (CONVERT (VARCHAR (36), assignedToUserId), '') FROM dbo.exposureCase WHERE exposureCaseId = @case", ("@case", id));
        string category = await worker.ScalarAsync<string>("SELECT exposureCategory FROM dbo.exposureCase WHERE exposureCaseId = @case", ("@case", id));
        string status = await worker.ScalarAsync<string>("SELECT caseStatus FROM dbo.exposureCase WHERE exposureCaseId = @case", ("@case", id));
        var client = await fixture.SignedInAsync("supervisor");
        async Task<HttpResponseMessage> Assign(Guid target)
        {
            string page = await client.Http.GetStringAsync($"/cases/{id}");
            Assert.Contains(fixture.Ids.MiaCaseNumber, page);
            string version = Convert.ToHexString(await worker.ScalarAsync<byte[]>("SELECT rowVersion FROM dbo.exposureCase WHERE exposureCaseId = @case", ("@case", id)));
            var response = await client.Http.PostAsync($"/cases/{id}/update", MeritClient.Form(page, ("assignedToUserId", target.ToString()), ("exposureCategory", category), ("caseStatus", status), ("rowVersion", version)));
            return response;
        }
        try
        {
            using (var denied = await Assign(reader.Id)) Assert.Equal(HttpStatusCode.UnprocessableEntity, denied.StatusCode);
            using (var saved = await Assign(worker.Id)) Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            await worker.ScalarAsync<int>("UPDATE auth.userAssignment SET isDeleted = 1 WHERE appUserId = @id; SELECT 0");
            string page = await client.Http.GetStringAsync($"/cases/{id}");
            Assert.Contains("existing assignee; no longer eligible", page);
            using (var retained = await Assign(worker.Id)) Assert.Equal(HttpStatusCode.OK, retained.StatusCode);
        }
        finally { await worker.ScalarAsync<int>("UPDATE dbo.exposureCase SET assignedToUserId = TRY_CAST (@old AS UNIQUEIDENTIFIER) WHERE exposureCaseId = @case; SELECT 0", ("@old", previous), ("@case", id)); }
    }

    [SqlFact]
    public async Task TaskAssignmentRequiresAccessToItsActualPatient()
    {
        await using var worker = await WorkerAsync("caseManager", fixture.Ids.BaltimoreCountyTenantId);
        Guid task = Guid.NewGuid(), person = Guid.NewGuid();
        try
        {
            await worker.ScalarAsync<int>("""
                INSERT dbo.person (personId, rootTenantId, owningTenantId, lastName, firstName, lastNameKey)
                SELECT @person, rootTenantId, @city, N'Eligibility', N'Isolated test', N'ELIGIBILITY' FROM auth.appUser WHERE appUserId = @id;
                INSERT dbo.workTask (workTaskId, rootTenantId, owningTenantId, queueCode, title, personId)
                SELECT @task, rootTenantId, @county, 'caseFollowUp', N'Patient scope test', @person FROM auth.appUser WHERE appUserId = @id;
                SELECT 0;
                """, ("@task", task), ("@person", person), ("@city", fixture.Ids.BaltimoreCityTenantId), ("@county", fixture.Ids.BaltimoreCountyTenantId));
            var client = await fixture.SignedInAsync("supervisor");
            await client.Http.GetStringAsync("/");
            using var response = await client.Http.PostAsJsonAsync("/api/v1/tasks/bulk", new { taskIds = new[] { task }, action = "assign", assignToUserId = worker.Id });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(1, await worker.ScalarAsync<int>("SELECT COUNT (*) FROM dbo.workTask WHERE workTaskId = @task AND assignedToUserId IS NULL", ("@task", task)));
            // Read access to the actual patient can come from a different current grant than task-edit access.
            await worker.ScalarAsync<int>("""
                INSERT auth.userAssignment (userAssignmentId, appUserId, roleId, tenantId, includeDescendants, accessLevel, startDate)
                SELECT NEWID (), @id, r.roleId, @city, 1, 'read', '2020-01-01'
                  FROM auth.role AS r JOIN auth.appUser AS u ON u.rootTenantId = r.rootTenantId AND u.appUserId = @id
                 WHERE r.roleCode = 'intakeClerk' AND r.isDeleted = 0;
                SELECT 0;
                """, ("@city", fixture.Ids.BaltimoreCityTenantId));
            using var allowed = await client.Http.PostAsJsonAsync("/api/v1/tasks/bulk", new { taskIds = new[] { task }, action = "assign", assignToUserId = worker.Id });
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }
        finally { await worker.ScalarAsync<int>("DELETE dbo.workTask WHERE workTaskId = @task; DELETE dbo.person WHERE personId = @person; SELECT 0", ("@task", task), ("@person", person)); }
    }

    [SqlFact]
    public async Task ConfiguredIntakeOffersTheActualHeaderWithoutInventingASampleLayout()
    {
        await using var account = await SyntheticAccount.CreateAsync(fixture.Factory, "intakeheader");
        Guid profile = await account.ScalarAsync<Guid>("SELECT TOP (1) intakeProfileId FROM dbo.intakeProfile WHERE rootTenantId = (SELECT rootTenantId FROM auth.appUser WHERE appUserId = @id) AND isActive = 1 AND isDeleted = 0 AND fileType = 'csv' AND headerSignature IS NOT NULL");
        string expected = await account.ScalarAsync<string>("SELECT headerSignature FROM dbo.intakeProfile WHERE intakeProfileId = @profile", ("@profile", profile));
        var client = await fixture.SignedInAsync("supervisor");
        using var header = await client.Http.GetAsync($"/intake/profiles/{profile}/header.txt");
        Assert.Equal(HttpStatusCode.OK, header.StatusCode);
        Assert.Equal("text/plain", header.Content.Headers.ContentType?.MediaType);
        Assert.Equal(expected, (await header.Content.ReadAsStringAsync()).TrimEnd());
    }

    [SqlFact]
    public async Task EmptyIntakeHasRecoveryInstructionsAndCannotQueueAnInactiveFormat()
    {
        await using var account = await SyntheticAccount.CreateAsync(fixture.Factory, "emptyintake");
        string ids = await account.ScalarAsync<string>("SELECT STRING_AGG (CONVERT (VARCHAR (36), intakeProfileId), ',') FROM dbo.intakeProfile WHERE rootTenantId = (SELECT rootTenantId FROM auth.appUser WHERE appUserId = @id) AND isActive = 1 AND isDeleted = 0");
        Guid profile = Guid.Parse(ids.Split(',')[0]);
        var client = await fixture.SignedInAsync("supervisor");
        string configured = await client.Http.GetStringAsync("/intake");
        Assert.Contains("Format requirements", configured, StringComparison.OrdinalIgnoreCase);
        try
        {
            await account.ScalarAsync<int>("UPDATE dbo.intakeProfile SET isActive = 0 WHERE intakeProfileId IN (SELECT TRY_CAST (value AS UNIQUEIDENTIFIER) FROM STRING_SPLIT (@ids, ',')); SELECT 0", ("@ids", ids));
            string page = await client.Http.GetStringAsync("/intake");
            Assert.Contains("No laboratory import formats are configured", page);
            Assert.DoesNotContain("type=\"file\"", page);
            using var form = new MultipartFormDataContent { { new StringContent(profile.ToString()), "intakeProfileId" }, { new ByteArrayContent(Encoding.UTF8.GetBytes("not queued")), "file", "inactive-profile.csv" } };
            using var rejected = await client.Http.PostAsync("/intake/upload", form);
            Assert.Contains("no longer available", await rejected.Content.ReadAsStringAsync());
            Assert.Equal(0, await account.ScalarAsync<int>("SELECT COUNT (*) FROM dbo.intakeBatch WHERE fileName = 'inactive-profile.csv'"));
        }
        finally { await account.ScalarAsync<int>("UPDATE dbo.intakeProfile SET isActive = 1 WHERE intakeProfileId IN (SELECT TRY_CAST (value AS UNIQUEIDENTIFIER) FROM STRING_SPLIT (@ids, ',')); SELECT 0", ("@ids", ids)); }
    }
}
