using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

/// <summary>Issue #163: a new account can be given its first access in the same save, and one without access is prompted for it.</summary>
[Collection(MeritCollection.Name)]
public sealed class UserCreationTests(MeritFixture fixture)
{
    [SqlFact]
    public async Task ANewUserGetsFirstAccessInOneSaveOrIsSentToGiveAccess()
    {
        var admin = await fixture.SignedInAsync("admin");
        string form = await admin.Http.GetStringAsync("/admin/users/new");
        Assert.Contains("First access", form, StringComparison.Ordinal);
        string role = FirstOption(form, "FirstRoleId");
        string tenant = FirstOption(form, "FirstTenantId");
        string withAccess = $"first-access-{Guid.NewGuid():N}"[..30];
        string withoutAccess = $"no-access-{Guid.NewGuid():N}"[..30];
        try
        {
            // With a role: one save creates the account and its grant.
            using var created = await admin.NoRedirect.PostAsync("/admin/users", MeritClient.Form(form,
                ("UserName", withAccess), ("DisplayName", "First access test"), ("NewPassword", $"Kestrel-{Guid.NewGuid():N}"),
                ("HomeTenantId", tenant), ("IsActive", "true"), ("FirstRoleId", role), ("FirstTenantId", tenant),
                ("FirstAccessLevel", "read"), ("FirstIncludeDescendants", "false")));
            Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
            Assert.Equal(1, await ScalarAsync("""
                SELECT COUNT (*) FROM auth.userAssignment AS a JOIN auth.appUser AS u ON u.appUserId = a.appUserId
                 WHERE u.userName = @name AND a.roleId = @role AND a.tenantId = @tenant AND a.accessLevel = 'read'
                   AND a.includeDescendants = 0 AND a.endDate IS NULL
                """, withAccess, role, tenant));
            string page = WebUtility.HtmlDecode(await admin.Http.GetStringAsync(created.Headers.Location!.OriginalString));
            Assert.Contains("User saved with their first access.", page, StringComparison.Ordinal);
            Assert.DoesNotContain("No access yet", page, StringComparison.Ordinal);

            // Without one: saved, then taken straight to giving access, with the page saying why.
            form = await admin.Http.GetStringAsync("/admin/users/new");
            using var bare = await admin.NoRedirect.PostAsync("/admin/users", MeritClient.Form(form,
                ("UserName", withoutAccess), ("DisplayName", "No access test"), ("NewPassword", $"Kestrel-{Guid.NewGuid():N}"),
                ("HomeTenantId", tenant), ("IsActive", "true"), ("FirstRoleId", ""), ("FirstTenantId", tenant), ("FirstAccessLevel", "full")));
            Assert.Equal(HttpStatusCode.Redirect, bare.StatusCode);
            Assert.EndsWith("#give-access", bare.Headers.Location!.OriginalString, StringComparison.Ordinal);
            Assert.Equal(0, await ScalarAsync("""
                SELECT COUNT (*) FROM auth.userAssignment AS a JOIN auth.appUser AS u ON u.appUserId = a.appUserId WHERE u.userName = @name
                """, withoutAccess, role, tenant));
            page = WebUtility.HtmlDecode(await admin.Http.GetStringAsync(bare.Headers.Location!.OriginalString.Split('#')[0]));
            Assert.Contains("Next, give them access", page, StringComparison.Ordinal);
            Assert.Contains("No access yet: the person can sign in but sees nothing.", page, StringComparison.Ordinal);
            Assert.Contains("id=\"give-access\"", page, StringComparison.Ordinal);
        }
        finally
        {
            await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
            await c.OpenAsync();
            // A new account also has its master profile; its grants go first.
            await using var cmd = new SqlCommand("""
                DECLARE @u TABLE (appUserId UNIQUEIDENTIFIER);
                INSERT @u SELECT appUserId FROM auth.appUser WHERE userName IN (@a, @b);
                DELETE g FROM auth.userProfileGrant AS g JOIN auth.userProfile AS p ON p.userProfileId = g.userProfileId
                 WHERE p.appUserId IN (SELECT appUserId FROM @u);
                DELETE auth.userProfile WHERE appUserId IN (SELECT appUserId FROM @u);
                DELETE auth.userAssignment WHERE appUserId IN (SELECT appUserId FROM @u);
                DELETE auth.appUser WHERE appUserId IN (SELECT appUserId FROM @u);
                """, c);
            cmd.Parameters.AddWithValue("@a", withAccess);
            cmd.Parameters.AddWithValue("@b", withoutAccess);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static string FirstOption(string html, string selectId)
        => Regex.Match(html, $"<select[^>]*id=\"{selectId}\"[^>]*>.*?<option value=\"([0-9a-fA-F-]{{36}})\"", RegexOptions.Singleline).Groups[1].Value;

    private static async Task<int> ScalarAsync(string sql, string name, string role, string tenant)
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@role", Guid.Parse(role));
        cmd.Parameters.AddWithValue("@tenant", Guid.Parse(tenant));
        return (int)(await cmd.ExecuteScalarAsync())!;
    }
}
