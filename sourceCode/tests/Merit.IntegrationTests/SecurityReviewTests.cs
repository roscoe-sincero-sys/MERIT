using System.Net;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

/// <summary>Issues #270-#272: security reports scoped to what the reviewer reviews, a delegable read-only Security
/// officer role, and exports for sign-in activity, user access and an account's sign-in history.</summary>
[Collection(MeritCollection.Name)]
public sealed class SecurityReviewTests(MeritFixture fixture)
{
    private static async Task<T> Sql<T>(string sql, params (string Name, object Value)[] args)
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static Task<string> DisplayName(string user) => Sql<string>("SELECT displayName FROM auth.appUser WHERE userName=@u", ("@u", user));

    private static Task<Guid> CaseOwnedBy(string tenantCode) => Sql<Guid>("""
        SELECT TOP (1) c.exposureCaseId FROM dbo.exposureCase c JOIN auth.tenant t ON t.tenantId = c.owningTenantId
         WHERE t.tenantCode = @t AND c.isDeleted = 0 ORDER BY c.openedDate DESC
        """, ("@t", tenantCode));

    [SqlFact]
    public async Task AuditIsScopedToTheReviewedTenantsAndReachesDescendantRoots()
    {
        _ = fixture.Ids;
        // Activity in each county: sign-ins by county-only staff, and the statewide administrator opening a case in each.
        await fixture.SignedInAsync("cmbalt");
        await fixture.SignedInAsync("cmbaltco");
        var admin = await fixture.SignedInAsync("admin");
        var cityCase = await CaseOwnedBy("MD-BCHD");
        var countyCase = await CaseOwnedBy("MD-PGC"); // the demo's out-of-scope case (Prince George's County)
        Assert.Equal(HttpStatusCode.OK, (await admin.Http.GetAsync($"/cases/{cityCase}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.Http.GetAsync($"/cases/{countyCase}")).StatusCode);
        Assert.Equal(await Sql<Guid>("SELECT tenantId FROM auth.tenant WHERE tenantCode='MD-BCHD'"),
            await Sql<Guid>("SELECT TOP (1) owningTenantId FROM logs.auditEvent WHERE entityType='exposureCase' AND entityId=@c ORDER BY auditEventId DESC", ("@c", cityCase)));

        string cityStaff = await DisplayName("cmbalt"), countyStaff = await DisplayName("cmbaltco"), state = await DisplayName("admin");

        // Baltimore City's user administrator: city staff, and anyone's events on city records; not Baltimore County staff.
        var cityAdmin = await fixture.SignedInAsync("cityadmin");
        string city = await cityAdmin.Http.GetStringAsync("/admin/audit.csv");
        Assert.Contains(cityStaff, city, StringComparison.Ordinal);
        Assert.DoesNotContain(countyStaff, city, StringComparison.Ordinal);
        Assert.Contains(state, await cityAdmin.Http.GetStringAsync($"/admin/audit.csv?entityId={cityCase}"), StringComparison.Ordinal);
        Assert.DoesNotContain(state, await cityAdmin.Http.GetStringAsync($"/admin/audit.csv?entityId={countyCase}"), StringComparison.Ordinal);
        Assert.DoesNotContain(countyStaff, await cityAdmin.Http.GetStringAsync("/admin/audit"), StringComparison.Ordinal);

        // The state administrator reviews the whole data root, as before.
        string whole = await admin.Http.GetStringAsync("/admin/audit.csv");
        Assert.Contains(cityStaff, whole, StringComparison.Ordinal);
        Assert.Contains(countyStaff, whole, StringComparison.Ordinal);

        // The platform administrator now reviews the data roots below it (metadata only).
        var platform = await fixture.SignedInAsync("platform");
        Assert.Contains(countyStaff, await platform.Http.GetStringAsync("/admin/audit.csv"), StringComparison.Ordinal);
    }

    [SqlFact]
    public async Task SignInActivityIncludesStaffGrantedButNotHomedInTheCounty()
    {
        _ = fixture.Ids;
        // cmboth works in both Baltimore City and Baltimore County: a failed sign-in must appear for both counties' reviewers.
        var stray = fixture.Anonymous();
        string page = await stray.Http.GetStringAsync("/account/signin");
        using (await stray.Http.PostAsync("/account/signin", MeritClient.Form(page, ("UserName", "cmboth"), ("Password", "not-the-password")))) { }
        await Sql<int>("UPDATE auth.appUser SET failedSignInCount=0, lockoutEndUtc=NULL WHERE userName='cmboth'; SELECT 0");
        var county = await fixture.SignedInAsync("countyadmin");
        Assert.Contains("<span class=\"merit-meta\">cmboth</span>", await county.Http.GetStringAsync("/admin/sign-in-activity?hours=1"), StringComparison.Ordinal);
    }

    [SqlFact]
    public async Task SecurityOfficerIsDelegableAndReadOnly()
    {
        _ = fixture.Ids;
        await using var officer = await SyntheticAccount.CreateAsync(fixture.Factory, "secofficer");
        // A state administrator delegates the role at Baltimore County (they hold every right it contains).
        var admin = await fixture.SignedInAsync("admin");
        var role = await Sql<Guid>("SELECT r.roleId FROM auth.role r JOIN auth.tenant t ON t.rootTenantId=r.rootTenantId WHERE t.tenantCode='MD-BCO' AND r.roleCode='securityOfficer' AND r.isDeleted=0");
        var county = await Sql<Guid>("SELECT tenantId FROM auth.tenant WHERE tenantCode='MD-BCO'");
        await admin.Http.GetStringAsync($"/admin/users/{officer.Id}");
        using (var grant = await admin.NoRedirect.PostAsync($"/admin/users/{officer.Id}/assignments", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["roleId"] = role.ToString(), ["tenantId"] = county.ToString(), ["includeDescendants"] = "true", ["accessLevel"] = "full",
        })))
            Assert.Equal(HttpStatusCode.Redirect, grant.StatusCode);
        Assert.Equal(1, await Sql<int>("SELECT COUNT(*) FROM auth.userAssignment WHERE appUserId=@u AND roleId=@r AND isDeleted=0", ("@u", officer.Id), ("@r", role)));

        using var client = fixture.Anonymous();
        await officer.SignInAsync(client, MeritFixture.Password);

        string users = await client.Http.GetStringAsync("/admin/users");
        Assert.Contains("<td>cmbaltco</td>", users, StringComparison.Ordinal);
        Assert.Contains("<td>cmboth</td>", users, StringComparison.Ordinal);
        Assert.DoesNotContain("<td>cmbalt</td>", users, StringComparison.Ordinal);
        Assert.DoesNotContain("/admin/users/new", users, StringComparison.Ordinal);
        string csv = await client.Http.GetStringAsync("/admin/users.csv");
        Assert.StartsWith("\"User name\",\"Name\",\"Email\"", csv.TrimStart((char)0xFEFF), StringComparison.Ordinal);
        Assert.Contains("cmbaltco", csv, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await client.NoRedirect.GetAsync("/admin/sign-in-activity")).StatusCode);
        using (var activity = await client.NoRedirect.GetAsync("/admin/sign-in-activity/activity.csv?hours=24"))
        {
            Assert.Equal(HttpStatusCode.OK, activity.StatusCode);
            Assert.Equal("text/csv", activity.Content.Headers.ContentType?.MediaType);
        }

        Assert.Equal(HttpStatusCode.OK, (await client.NoRedirect.GetAsync("/admin/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.NoRedirect.GetAsync("/admin/usage")).StatusCode);
        Assert.Equal(2, await Sql<int>("SELECT COUNT(DISTINCT entityType) FROM logs.auditEvent WHERE appUserId=@u AND eventAction='export' AND entityType IN ('userAccess','signInActivity')", ("@u", officer.Id)));

        // Read only, and no patient records: no account changes, no unknown-name investigation, no person pages.
        foreach (var url in new[] { "/admin/users/new", "/admin/sign-in-activity/unknown-names", $"/people/{fixture.Ids.MiaPersonId}", "/admin/tenants" })
        {
            using var denied = await client.NoRedirect.GetAsync(url);
            Assert.NotEqual(HttpStatusCode.OK, denied.StatusCode);
        }
    }

    [SqlFact]
    public async Task RefusedGrantIsShownBesideTheFormAndHistoryExports()
    {
        _ = fixture.Ids;
        await using var account = await SyntheticAccount.CreateAsync(fixture.Factory, "grantrefused");
        var admin = await fixture.SignedInAsync("admin");
        var platformRole = await Sql<Guid>("SELECT roleId FROM auth.role WHERE roleCode='platformAdministrator' AND isDeleted=0");
        var county = await Sql<Guid>("SELECT tenantId FROM auth.tenant WHERE tenantCode='MD-BCO'");
        await admin.Http.GetStringAsync($"/admin/users/{account.Id}");
        using (var refused = await admin.NoRedirect.PostAsync($"/admin/users/{account.Id}/assignments", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["roleId"] = platformRole.ToString(), ["tenantId"] = county.ToString(), ["includeDescendants"] = "true", ["accessLevel"] = "full",
        })))
            Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.Contains("Platform accounts hold only platform roles", WebUtility.HtmlDecode(await admin.Http.GetStringAsync($"/admin/users/{account.Id}")), StringComparison.Ordinal);

        var cmbaltco = await Sql<Guid>("SELECT appUserId FROM auth.appUser WHERE userName='cmbaltco'");
        string history = await admin.Http.GetStringAsync($"/admin/users/{cmbaltco}/sign-in-history.csv");
        Assert.StartsWith("\"When (UTC)\",\"Event\"", history.TrimStart((char)0xFEFF), StringComparison.Ordinal);
        Assert.True(await Sql<int>("SELECT COUNT(*) FROM logs.auditEvent WHERE entityType='signInHistory' AND entityId=@u AND eventAction='export'", ("@u", cmbaltco)) > 0);
    }
}
