using System.Net;
using System.Net.Http.Json;
using Merit.Application.Data;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

/// <summary>Issues #277 and #278: a county security review sees the county's usage and every account that can reach,
/// or did reach, the county's data, with nothing from sibling counties.</summary>
[Collection(MeritCollection.Name)]
public sealed class CountyReviewScopeTests(MeritFixture fixture)
{
    private static async Task<T> Sql<T>(string sql, params (string Name, object Value)[] args)
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>An account holding only the Security officer role at Baltimore City, signed in normally.</summary>
    private async Task<(SyntheticAccount Account, MeritClient Client)> CityOfficerAsync(string prefix)
    {
        var officer = await SyntheticAccount.CreateAsync(fixture.Factory, prefix);
        await officer.ScalarAsync<int>("""
            INSERT auth.userAssignment (userAssignmentId, appUserId, roleId, tenantId, includeDescendants, accessLevel, startDate)
            SELECT NEWID (), @id, r.roleId, t.tenantId, 1, 'full', '2026-01-01'
              FROM auth.tenant t JOIN auth.role r ON r.rootTenantId = t.rootTenantId AND r.roleCode = 'securityOfficer' AND r.isDeleted = 0
             WHERE t.tenantCode = 'MD-BCHD';
            UPDATE auth.appUser SET homeTenantId = (SELECT tenantId FROM auth.tenant WHERE tenantCode = 'MD-BCHD') WHERE appUserId = @id;
            SELECT COUNT (*) FROM auth.userAssignment WHERE appUserId = @id AND isDeleted = 0;
            """);
        var client = fixture.Anonymous();
        await officer.SignInAsync(client, MeritFixture.Password);
        return (officer, client);
    }

    [SqlFact]
    public async Task SecurityOfficerWithoutClinicalGrantsReviewsItsCountyUsageOnly()
    {
        _ = fixture.Ids;
        // A screen visit by Baltimore City staff, and one by Baltimore County staff.
        var city = await fixture.SignedInAsync("cmbalt");
        var county = await fixture.SignedInAsync("cmbaltco");
        Guid cityVisit = Guid.NewGuid(), countyVisit = Guid.NewGuid();
        using (var r = await city.Http.PostAsJsonAsync("/api/v1/usage/visits", new ScreenVisitInput(cityVisit, "people.search", Guid.NewGuid(), NavigationType: "link")))
            Assert.True(r.IsSuccessStatusCode, r.StatusCode.ToString());
        using (var r = await county.Http.PostAsJsonAsync("/api/v1/usage/visits", new ScreenVisitInput(countyVisit, "people.search", Guid.NewGuid(), NavigationType: "link")))
            Assert.True(r.IsSuccessStatusCode, r.StatusCode.ToString());
        var cityReference = await Sql<Guid>("SELECT sessionReference FROM logs.screenVisit WHERE screenVisitId=@v", ("@v", cityVisit));
        var countyReference = await Sql<Guid>("SELECT sessionReference FROM logs.screenVisit WHERE screenVisitId=@v", ("@v", countyVisit));

        var (officer, client) = await CityOfficerAsync("usageofficer");
        await using (officer)
        using (client)
        {
            Assert.Equal(0, await Sql<int>("SELECT COUNT(*) FROM auth.sessionScopeTenant st JOIN auth.sessionScope s ON s.sessionScopeId=st.sessionScopeId WHERE s.appUserId=@u AND s.revokedUtc IS NULL", ("@u", officer.Id)));
            Assert.Contains("Find or add a person", await client.Http.GetStringAsync($"/admin/usage/sessions/{cityReference}?days=1"), StringComparison.Ordinal);
            Assert.Contains(cityVisit.ToString(), await client.Http.GetStringAsync($"/admin/usage/sessions/{cityReference}/page.csv?days=1"), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Find or add a person", await client.Http.GetStringAsync("/admin/usage/summary.csv?days=1"), StringComparison.Ordinal);
            // A sibling county's session is not part of the city's review.
            using (var sibling = await client.NoRedirect.GetAsync($"/admin/usage/sessions/{countyReference}/page.csv?days=1"))
                Assert.DoesNotContain(countyVisit.ToString(), await sibling.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
            // Still read only: no patient records and no account changes.
            using (var person = await client.NoRedirect.GetAsync($"/people/{fixture.Ids.MiaPersonId}")) Assert.NotEqual(HttpStatusCode.OK, person.StatusCode);
            using (var add = await client.NoRedirect.GetAsync("/admin/users/new")) Assert.NotEqual(HttpStatusCode.OK, add.StatusCode);
        }
    }

    [SqlFact]
    public async Task CountyReviewIncludesStatewideActorsWhoReachedCountyDataButNotSiblingStaff()
    {
        _ = fixture.Ids;
        // The statewide administrator reads a Baltimore City case, then mistypes their password once.
        var admin = await fixture.SignedInAsync("admin");
        var cityCase = await Sql<Guid>("SELECT TOP (1) c.exposureCaseId FROM dbo.exposureCase c JOIN auth.tenant t ON t.tenantId=c.owningTenantId WHERE t.tenantCode='MD-BCHD' AND c.isDeleted=0 ORDER BY c.openedDate DESC");
        Assert.Equal(HttpStatusCode.OK, (await admin.Http.GetAsync($"/cases/{cityCase}")).StatusCode);
        using (var stray = fixture.Anonymous())
        {
            string page = await stray.Http.GetStringAsync("/account/signin");
            using (await stray.Http.PostAsync("/account/signin", MeritClient.Form(page, ("UserName", "admin"), ("Password", "not-the-password")))) { }
        }
        await Sql<int>("UPDATE auth.appUser SET failedSignInCount=0, lockoutEndUtc=NULL WHERE userName='admin'; SELECT 0");

        var (officer, client) = await CityOfficerAsync("actorofficer");
        await using (officer)
        using (client)
        {
            string state = await Sql<string>("SELECT displayName FROM auth.appUser WHERE userName='admin'");
            Assert.Contains(state, await client.Http.GetStringAsync($"/admin/audit.csv?entityId={cityCase}"), StringComparison.Ordinal);
            string users = await client.Http.GetStringAsync("/admin/users.csv");
            Assert.Contains("\"admin\",", users, StringComparison.Ordinal);       // statewide actor with audited access to a city case
            Assert.Contains("\"cmbalt\",", users, StringComparison.Ordinal);      // city staff
            Assert.DoesNotContain("\"cmbaltco\",", users, StringComparison.Ordinal); // Baltimore County only
            Assert.DoesNotContain("\"platform\",", users, StringComparison.Ordinal); // platform roles see no records
            Assert.Contains("\"admin\"", await client.Http.GetStringAsync("/admin/sign-in-activity/activity.csv?hours=1"), StringComparison.Ordinal);
            var adminId = await Sql<Guid>("SELECT appUserId FROM auth.appUser WHERE userName='admin'");
            Assert.Contains("passwordFailed", await client.Http.GetStringAsync($"/admin/users/{adminId}/sign-in-history.csv"), StringComparison.Ordinal);
            // Seeing an outside actor grants nothing: their account stays with its own administrators.
            Assert.Contains("\"admin\",\"Avery Admin (state administrator)\"", users, StringComparison.Ordinal);
            Assert.EndsWith("\"no\"", users.Split('\n').Select(l => l.TrimEnd('\r')).First(l => l.StartsWith("\"admin\",", StringComparison.Ordinal)), StringComparison.Ordinal);
        }
    }
}
