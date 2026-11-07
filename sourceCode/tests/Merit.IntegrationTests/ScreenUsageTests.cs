using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Merit.Application.Data;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

[Collection(MeritCollection.Name)]
public sealed class ScreenUsageTests(MeritFixture fixture)
{
    [SqlFact]
    public async Task ADelayedPredecessorRequestsRetryAndIsLinkedOnlyAfterValidation()
    {
        await using var account = await UserAsync("usagerace");
        using var client = fixture.Anonymous();
        await account.SignInAsync(client, MeritFixture.Password);
        await client.Http.GetStringAsync("/account/profiles");
        Guid tab = Guid.NewGuid(), predecessor = Guid.NewGuid(), successor = Guid.NewGuid();
        var next = new ScreenVisitInput(successor, "result.new", tab, predecessor, "link");
        using (var early = await client.Http.PostAsJsonAsync("/api/v1/usage/visits", next))
            Assert.Equal(HttpStatusCode.Accepted, early.StatusCode);
        Assert.Equal(1, await account.ScalarAsync<int>("SELECT COUNT (*) FROM logs.screenVisit WHERE screenVisitId = @visit AND previousVisitId IS NULL", ("@visit", successor)));
        using (var late = await client.Http.PostAsJsonAsync("/api/v1/usage/visits", new ScreenVisitInput(predecessor, "person", tab, NavigationType: "link")))
            Assert.Equal(HttpStatusCode.NoContent, late.StatusCode);
        using (var retried = await client.Http.PostAsJsonAsync("/api/v1/usage/visits", next))
            Assert.Equal(HttpStatusCode.NoContent, retried.StatusCode);
        Assert.Equal(1, await account.ScalarAsync<int>("SELECT COUNT (*) FROM logs.screenVisit WHERE screenVisitId = @visit AND previousVisitId = @previous", ("@visit", successor), ("@previous", predecessor)));
        // A different tab never becomes a valid predecessor, including on a replay.
        Guid otherTab = await VisitAsync(client, "home", Guid.NewGuid());
        var invalid = new ScreenVisitInput(Guid.NewGuid(), "result.new", tab, otherTab, "link");
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var response = await client.Http.PostAsJsonAsync("/api/v1/usage/visits", invalid);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
        Assert.Equal(1, await account.ScalarAsync<int>("SELECT COUNT (*) FROM logs.screenVisit WHERE screenVisitId = @visit AND previousVisitId IS NULL", ("@visit", invalid.VisitId)));
        var reference = await account.ScalarAsync<Guid>("SELECT sessionReference FROM logs.screenVisit WHERE screenVisitId = @visit", ("@visit", successor));
        // Each connection gets an unnamed temporary primary key, without shared constraint names.
        var reports = await Task.WhenAll(Enumerable.Range(0, 8).Select(n => client.Http.GetStringAsync(n % 2 == 0
            ? "/admin/usage" : $"/admin/usage/sessions/{reference}")));
        Assert.All(reports, html => Assert.Contains("Usage", html));
    }

    [SqlFact]
    public async Task FullEmptyScreensAreRecordedButPollingAssetsAndFragmentsAreNot()
    {
        await using var account = await UserAsync("usagepage");
        using var client = fixture.Anonymous();
        await account.SignInAsync(client, MeritFixture.Password);
        string page = await client.Http.GetStringAsync("/intake");
        Guid id = VisitId(page);
        Assert.Equal(1, await account.ScalarAsync<int>("SELECT COUNT (*) FROM logs.screenVisit WHERE screenVisitId = @visit AND appUserId = @id AND screenCode = 'intake'", ("@visit", id)));
        int before = await CountAsync(account);
        using (await client.GetPartialAsync("/intake")) { }
        using (await client.Http.GetAsync("/api/v1/held/overview")) { }
        using (await client.Http.GetAsync("/js/merit.js")) { }
        using (await client.Http.GetAsync("/app/queues")) { }
        Assert.Equal(before, await CountAsync(account));
        using (await client.Http.GetAsync("/account/profiles")) { }
        using (await client.Http.GetAsync("/help")) { }
        using (await client.Http.GetAsync("/help/guides/operations-guide.html")) { }
        using (await client.Http.GetAsync("/config/settings")) { }
        Assert.Equal(before + 4, await CountAsync(account));
    }

    [SqlFact]
    public async Task BrowserMetadataIsIdempotentPrivateAndOwnedByTheCurrentSession()
    {
        await using var account = await UserAsync("usageprivate");
        using var client = fixture.Anonymous();
        await account.SignInAsync(client, MeritFixture.Password);
        string page = await client.Http.GetStringAsync("/search?q=private-patient-name&token=not-for-usage");
        Guid id = VisitId(page), tab = Guid.NewGuid();
        var input = new { visitId = id, screenCode = "people.search", tabId = tab, previousVisitId = (Guid?)null,
            navigationType = "search", appUserId = Guid.NewGuid(), rootTenantId = Guid.NewGuid(), route = "/patients/private?secret=value" };
        using (var posted = await client.Http.PostAsJsonAsync("/api/v1/usage/visits", input)) Assert.Equal(HttpStatusCode.NoContent, posted.StatusCode);
        using (await client.Http.PostAsJsonAsync("/api/v1/usage/visits", input)) { }
        Assert.Equal(1, await account.ScalarAsync<int>("SELECT COUNT (*) FROM logs.screenVisit WHERE screenVisitId = @visit AND appUserId = @id AND screenCode = 'people.search' AND roleCodes = 'supervisor'", ("@visit", id)));
        var scope = await account.ScalarAsync<Guid>("SELECT TOP (1) sessionScopeId FROM auth.sessionScope WHERE appUserId = @id ORDER BY createdDateUtc DESC");
        Assert.Equal(1, await account.ScalarAsync<int>("SELECT COUNT (*) FROM logs.screenVisit AS v JOIN auth.sessionScope AS s ON s.sessionReference = v.sessionReference WHERE v.screenVisitId = @visit AND s.sessionScopeId = @scope AND v.sessionReference <> s.sessionScopeId", ("@visit", id), ("@scope", scope)));
        using (var invalid = await client.Http.PostAsJsonAsync("/api/v1/usage/visits", new { visitId = Guid.NewGuid(), tabId = tab, screenCode = "private-patient-name", navigationType = "link" })) Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using (await client.Http.PostAsJsonAsync("/api/v1/usage/visits", new { visitId = scope, tabId = tab, screenCode = "home", navigationType = "link" })) { }
        Assert.Equal(0, await account.ScalarAsync<int>("SELECT COUNT (*) FROM logs.screenVisit WHERE screenVisitId = @scope", ("@scope", scope)));
        using (await client.Http.PostAsync("/api/v1/usage/duration", MeritClient.Form(page, ("visitId", id.ToString()), ("durationMs", "5000")))) { }
        using (await client.Http.PostAsync("/api/v1/usage/duration", MeritClient.Form(page, ("visitId", id.ToString()), ("durationMs", "1000")))) { }
        Assert.Equal(5000, await account.ScalarAsync<int>("SELECT durationMs FROM logs.screenVisit WHERE screenVisitId = @visit", ("@visit", id)));

        await using var other = await UserAsync("usageother");
        using var stranger = fixture.Anonymous();
        await other.SignInAsync(stranger, MeritFixture.Password);
        string otherPage = await stranger.Http.GetStringAsync("/account/profiles");
        using (await stranger.Http.PostAsync("/api/v1/usage/duration", MeritClient.Form(otherPage, ("visitId", id.ToString()), ("durationMs", "9999")))) { }
        Assert.Equal(5000, await account.ScalarAsync<int>("SELECT durationMs FROM logs.screenVisit WHERE screenVisitId = @visit", ("@visit", id)));
    }

    [SqlFact]
    public async Task PathsLoopsFlowTimingCsvAndSessionBoundariesAreAvailable()
    {
        await using var account = await UserAsync("usagepaths");
        using var client = fixture.Anonymous();
        await account.SignInAsync(client, MeritFixture.Password);
        await client.Http.GetStringAsync("/account/profiles"); // obtain a current antiforgery token
        Guid tab = Guid.NewGuid();
        foreach (string prefix in new[] { "home", "cases", "queues", "results", "reports", "intake" })
        {
            Guid previous = await VisitAsync(client, prefix, tab);
            previous = await VisitAsync(client, "person", tab, previous);
            await VisitAsync(client, "result.new", tab, previous);
        }
        Guid start = await VisitAsync(client, "people.search", tab);
        Guid person = await VisitAsync(client, "person", tab, start);
        Guid entry = await VisitAsync(client, "result.new", tab, person);
        await VisitAsync(client, "result.saved", tab, entry);
        Guid held = await VisitAsync(client, "held", tab);
        Guid match = await VisitAsync(client, "match", tab, held);
        await VisitAsync(client, "held", tab, match);
        string report = await client.Http.GetStringAsync("/admin/usage");
        Assert.Contains("Five most common paths into Enter a result", report);
        // Usage is retained across test runs; the scoped report can include earlier synthetic sessions.
        Assert.True(int.Parse(Regex.Match(report, @"(\d+) recorded sequences").Groups[1].Value) >= 1);
        Assert.Contains("Held records", report);
        Assert.Contains("Visits by active-profile role", report);
        string paths = report.Split("Five most common paths into Enter a result")[1].Split("Back-and-forth paths")[0];
        Assert.Equal(5, Regex.Matches(paths, "<li>").Count);
        var reference = await account.ScalarAsync<Guid>("SELECT TOP (1) sessionReference FROM logs.screenVisit WHERE appUserId = @id");
        string sequence = await client.Http.GetStringAsync($"/admin/usage/sessions/{reference}");
        Assert.Contains("Session started", sequence);
        Assert.Contains("Result saved outcome", sequence);
        using var csv = await client.Http.GetAsync("/admin/usage/summary.csv");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Normalized route", await csv.Content.ReadAsStringAsync());
        Assert.True(await account.ScalarAsync<int>("SELECT COUNT (*) FROM logs.auditEvent WHERE appUserId = @id AND entityType = 'screenUsage' AND eventAction = 'export' AND entityId IS NULL") > 0);
        string profile = await client.Http.GetStringAsync("/account/profiles");
        using (await client.Http.PostAsync("/account/signout", MeritClient.Form(profile))) { }
        Assert.Equal(1, await account.ScalarAsync<int>("SELECT COUNT (*) FROM logs.signInEvent WHERE appUserId = @id AND sessionReference = @ref AND eventType = 'signedOut'", ("@ref", reference)));
    }

    [SqlFact]
    public async Task UsageIsNotAvailableToOrdinaryStaffOrOutsideTheReadersScope()
    {
        // FirstSignInTests owns the seeded intake account's enrollment; use independent state here.
        await using var ordinary = await UserAsync("usageordinary", role: "intakeClerk");
        using var intake = fixture.Anonymous();
        await ordinary.SignInAsync(intake, MeritFixture.Password);
        using var allowed = await intake.NoRedirect.GetAsync("/intake");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        using var denied = await intake.NoRedirect.GetAsync("/admin/usage");
        Assert.True(denied.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect);

        await using var city = await UserAsync("usagecity", fixture.Ids.BaltimoreCityTenantId);
        await using var county = await UserAsync("usagecounty", fixture.Ids.BaltimoreCountyTenantId);
        using var cityClient = fixture.Anonymous();
        using var countyClient = fixture.Anonymous();
        await city.SignInAsync(cityClient, MeritFixture.Password);
        await county.SignInAsync(countyClient, MeritFixture.Password);
        await cityClient.Http.GetStringAsync("/account/profiles");
        var reference = await city.ScalarAsync<Guid>("SELECT TOP (1) sessionReference FROM logs.screenVisit WHERE appUserId = @id");
        string hidden = await countyClient.Http.GetStringAsync($"/admin/usage/sessions/{reference}");
        Assert.Contains("No visits on this page", hidden);
        Assert.DoesNotContain("Session started", hidden);
        string own = await cityClient.Http.GetStringAsync($"/admin/usage/sessions/{reference}");
        Assert.Contains("My profiles", own);

        // A state supervisor changes working county within one logical sign-in. County readers
        // may see their visit, but not a predecessor or workforce context from another county.
        await using var statewide = await UserAsync("usagemixed");
        using var statewideClient = fixture.Anonymous();
        await statewide.SignInAsync(statewideClient, MeritFixture.Password);
        string form = await statewideClient.Http.GetStringAsync("/account/profiles");
        using (await statewideClient.Http.PostAsync("/account/profiles/switch", MeritClient.Form(form, ("actingTenantId", fixture.Ids.BaltimoreCityTenantId.ToString())))) { }
        Guid tab = Guid.NewGuid();
        Guid predecessor = await VisitAsync(statewideClient, "home", tab);
        form = await statewideClient.Http.GetStringAsync("/account/profiles");
        using (await statewideClient.Http.PostAsync("/account/profiles/switch", MeritClient.Form(form, ("actingTenantId", fixture.Ids.BaltimoreCountyTenantId.ToString())))) { }
        Guid successor = await VisitAsync(statewideClient, "person", tab, predecessor);
        reference = await statewide.ScalarAsync<Guid>("SELECT sessionReference FROM logs.screenVisit WHERE screenVisitId = @visit", ("@visit", successor));
        Assert.Equal(1, await statewide.ScalarAsync<int>("SELECT COUNT (*) FROM logs.screenVisit WHERE screenVisitId = @visit AND previousVisitId = @previous AND sessionReference = @ref AND actingTenantId = @county", ("@visit", successor), ("@previous", predecessor), ("@ref", reference), ("@county", fixture.Ids.BaltimoreCountyTenantId)));
        string visible = await countyClient.Http.GetStringAsync($"/admin/usage/sessions/{reference}");
        Assert.Contains(successor.ToString(), visible);
        Assert.DoesNotContain(predecessor.ToString(), visible);
    }

    [SqlFact]
    public async Task RetentionScrubsWorkforceContextAndCannotBeCalledByTheWebLogin()
    {
        await using var account = await UserAsync("usagepurge");
        using var client = fixture.Anonymous();
        await account.SignInAsync(client, MeritFixture.Password);
        Guid id = VisitId(await client.Http.GetStringAsync("/intake"));
        await account.ScalarAsync<int>("UPDATE logs.screenVisit SET eventUtc = DATEADD (DAY, -31, SYSUTCDATETIME ()) WHERE screenVisitId = @visit; SELECT 0", ("@visit", id));
        await using (var web = new SqlConnection(TestEnvironment.WebConnectionString))
        {
            await web.OpenAsync();
            var exception = await Assert.ThrowsAsync<SqlException>(() => new SqlCommand("EXEC logs.uspPurgeScreenVisits @retentionDays = 30", web).ExecuteScalarAsync());
            Assert.Equal(229, exception.Number);
        }
        await using (var worker = new SqlConnection(TestEnvironment.WorkerConnectionString))
        {
            await worker.OpenAsync();
            await new SqlCommand("EXEC logs.uspPurgeScreenVisits @retentionDays = 30", worker).ExecuteScalarAsync();
        }
        Assert.Equal(1, await account.ScalarAsync<int>("""
            SELECT COUNT (*) FROM logs.screenVisit WHERE screenVisitId = @visit AND isDeleted = 1
               AND appUserId IS NULL AND sessionReference IS NULL AND actingTenantId IS NULL AND roleCodes IS NULL
               AND tabId IS NULL AND previousVisitId IS NULL AND durationMs IS NULL AND profileId IS NULL AND viewAsUserId IS NULL
               AND createdBy = N'MERIT usage retention';
            """, ("@visit", id)));
    }

    [SqlFact]
    public async Task ScopeRenewalKeepsLogicalSessionButFreshSignInStartsAnother()
    {
        await using var account = await UserAsync("usagecontinuity");
        using var client = fixture.Anonymous();
        await account.SignInAsync(client, MeritFixture.Password);
        string page = await client.Http.GetStringAsync("/account/profiles");
        Guid first = VisitId(page);
        Guid reference = await account.ScalarAsync<Guid>("SELECT sessionReference FROM logs.screenVisit WHERE screenVisitId = @visit", ("@visit", first));
        Guid scope = await account.ScalarAsync<Guid>("SELECT TOP (1) sessionScopeId FROM auth.sessionScope WHERE appUserId = @id AND revokedUtc IS NULL ORDER BY createdDateUtc DESC");
        using (var switched = await client.NoRedirect.PostAsync("/account/profiles/switch", MeritClient.Form(page)))
            Assert.Equal(HttpStatusCode.Redirect, switched.StatusCode);
        Guid second = VisitId(await client.Http.GetStringAsync("/account/profiles"));
        Assert.Equal(reference, await account.ScalarAsync<Guid>("SELECT sessionReference FROM logs.screenVisit WHERE screenVisitId = @visit", ("@visit", second)));
        Assert.Equal(1, await account.ScalarAsync<int>("SELECT COUNT (*) FROM auth.sessionScope WHERE appUserId = @id AND sessionReference = @ref AND revokedUtc IS NULL AND sessionScopeId <> @old", ("@ref", reference), ("@old", scope)));
        page = await client.Http.GetStringAsync("/account/profiles");
        using (await client.Http.PostAsync("/account/signout", MeritClient.Form(page))) { }
        await account.SignInAsync(client, MeritFixture.Password);
        Guid third = VisitId(await client.Http.GetStringAsync("/account/profiles"));
        Assert.NotEqual(reference, await account.ScalarAsync<Guid>("SELECT sessionReference FROM logs.screenVisit WHERE screenVisitId = @visit", ("@visit", third)));
    }

    [SqlFact]
    public async Task ConcurrentReconciliationAndCheckpointsDoNotDeadlockOrDoubleCount()
    {
        await using var account = await UserAsync("usageconcurrent");
        using var client = fixture.Anonymous();
        await account.SignInAsync(client, MeritFixture.Password);
        string page = await client.Http.GetStringAsync("/intake");
        Guid id = VisitId(page), tab = Guid.NewGuid();
        DateTime start = await account.ScalarAsync<DateTime>("SELECT SYSUTCDATETIME ()");
        await Task.WhenAll(Enumerable.Range(1, 16).Select(async n => {
            using var metadata = await client.Http.PostAsJsonAsync("/api/v1/usage/visits", new ScreenVisitInput(id, "intake", tab, NavigationType: "direct"));
            Assert.Equal(HttpStatusCode.NoContent, metadata.StatusCode);
            using var duration = await client.Http.PostAsync("/api/v1/usage/duration", MeritClient.Form(page, ("visitId", id.ToString()), ("durationMs", (n * 1000).ToString())));
            Assert.Equal(HttpStatusCode.NoContent, duration.StatusCode);
        }));
        Assert.Equal(16000, await account.ScalarAsync<int>("SELECT durationMs FROM logs.screenVisit WHERE screenVisitId = @visit", ("@visit", id)));
        Assert.Equal(1, await account.ScalarAsync<int>("SELECT COUNT (*) FROM logs.screenVisit WHERE screenVisitId = @visit AND tabId = @tab", ("@visit", id), ("@tab", tab)));
        Assert.Equal(0, await account.ScalarAsync<int>("SELECT COUNT (*) FROM logs.ExecutionLog WHERE ProcedureName IN ('[logs].[uspRecordScreenVisit]', '[logs].[uspSetScreenDuration]') AND StartDateUtc >= @start AND Successful = 0", ("@start", start)));
    }

    [SqlFact]
    public async Task CaptureRequiresAntiforgeryEvenForAuthenticatedBrowserRequests()
    {
        await using var account = await UserAsync("usagexsrf");
        using var client = fixture.Anonymous();
        await account.SignInAsync(client, MeritFixture.Password);
        // NoRedirect/Http normally add the valid header; an explicit incorrect one must be rejected.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/usage/visits") {
            Content = JsonContent.Create(new ScreenVisitInput(Guid.NewGuid(), "home", Guid.NewGuid())) };
        request.Headers.Add("X-XSRF-TOKEN", "invalid");
        using var response = await client.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<SyntheticAccount> UserAsync(string prefix, Guid? tenant = null, string role = "supervisor")
    {
        var account = await SyntheticAccount.CreateAsync(fixture.Factory, prefix);
        await account.ScalarAsync<int>("""
            INSERT auth.userAssignment (userAssignmentId, appUserId, roleId, tenantId, includeDescendants, accessLevel, startDate)
            SELECT NEWID (), @id, r.roleId, COALESCE (@tenant, u.homeTenantId), 1, 'full', '2026-01-01'
              FROM auth.appUser AS u JOIN auth.role AS r ON r.rootTenantId = u.rootTenantId AND r.roleCode = @role AND r.isDeleted = 0
             WHERE u.appUserId = @id;
            SELECT 0;
            """, ("@tenant", (object?)tenant ?? DBNull.Value), ("@role", role));
        return account;
    }

    private static Guid VisitId(string html)
        => Guid.Parse(Regex.Match(html, "<meta name=\"merit-screen-visit\" content=\"([^\"]+)\"").Groups[1].Value);
    private static Task<int> CountAsync(SyntheticAccount account)
        => account.ScalarAsync<int>("SELECT COUNT (*) FROM logs.screenVisit WHERE appUserId = @id");
    private static async Task<Guid> VisitAsync(MeritClient client, string code, Guid tab, Guid? previous = null)
    {
        Guid id = Guid.NewGuid();
        using var response = await client.Http.PostAsJsonAsync("/api/v1/usage/visits", new ScreenVisitInput(id, code, tab, previous, "link"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return id;
    }
}
