using System.Net;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

[Collection(MeritCollection.Name)]
public sealed class UserUnlockTests(MeritFixture fixture)
{
    [SqlFact]
    public async Task UnlockIsScopedPreservesCredentialsAndShowsOneTimeFeedback()
    {
        var id = Guid.NewGuid();
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();
        async Task<object?> Sql(string sql)
        {
            await using var cmd = new SqlCommand(sql, c);
            cmd.Parameters.AddWithValue("@id", id);
            return await cmd.ExecuteScalarAsync();
        }
        try
        {
            await Sql("""
                INSERT auth.appUser (appUserId, rootTenantId, homeTenantId, userName, displayName, passwordHash, securityStamp,
                    mfaEnabled, mfaSecretProtected, failedSignInCount, lockoutEndUtc)
                SELECT @id, rootTenantId, homeTenantId, CONVERT (NVARCHAR (36), @id), N'Unlock test', N'preserve hash', @id,
                    1, N'preserve secret', 5, DATEADD (HOUR, 1, SYSUTCDATETIME ()) FROM auth.appUser WHERE userName = 'admin';
                """);
            var city = await fixture.SignedInAsync("cityadmin");
            await city.Http.GetStringAsync("/");
            using var refused = await city.NoRedirect.PostAsync($"/admin/users/{id}/unlock", new FormUrlEncodedContent([]));
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal(5, await Sql("SELECT failedSignInCount FROM auth.appUser WHERE appUserId = @id"));
            var admin = await fixture.SignedInAsync("admin");
            var before = await admin.Http.GetStringAsync($"/admin/users/{id}");
            Assert.Contains("Unlock account", before);
            Assert.Contains("data-confirm=", before);
            using var unlocked = await admin.NoRedirect.PostAsync($"/admin/users/{id}/unlock", new FormUrlEncodedContent([]));
            Assert.Equal(HttpStatusCode.Redirect, unlocked.StatusCode);
            Assert.Equal(1, await Sql("""
                SELECT COUNT (*) FROM auth.appUser WHERE appUserId = @id AND failedSignInCount = 0 AND lockoutEndUtc IS NULL
                  AND passwordHash = N'preserve hash' AND mfaSecretProtected = N'preserve secret' AND mfaEnabled = 1 AND securityStamp = @id
                """));
            var after = await admin.Http.GetStringAsync($"/admin/users/{id}");
            Assert.Contains("Account unlocked. The person can try signing in again.", after);
            Assert.Contains("role=\"status\"", after);
            Assert.DoesNotContain("Unlock account", after);
            Assert.DoesNotContain("Account unlocked.", await admin.Http.GetStringAsync($"/admin/users/{id}"));
            await Sql("UPDATE auth.appUser SET mfaEnabled = 0 WHERE appUserId = @id");
            Assert.DoesNotContain("Reset MFA after verifying", await admin.Http.GetStringAsync($"/admin/users/{id}"));
        }
        finally { await Sql("DELETE auth.appUser WHERE appUserId = @id"); }
    }
}
