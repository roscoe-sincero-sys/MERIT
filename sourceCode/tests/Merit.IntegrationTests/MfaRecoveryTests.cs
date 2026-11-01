using System.Net;
using System.Text.RegularExpressions;
using Merit.Application.Security;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

[Collection(MeritCollection.Name)]
public sealed class MfaRecoveryTests(MeritFixture fixture)
{
    [SqlFact]
    public async Task EnrollmentCanRestartAndRecoveryContinuationPreservesOnlyLocalDestinations()
    {
        var id = Guid.NewGuid(); var userName = id.ToString();
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        async Task Sql(string text)
        {
            await using var cmd = new SqlCommand(text, c); cmd.Parameters.AddWithValue("@id", id); cmd.Parameters.AddWithValue("@name", userName); await cmd.ExecuteNonQueryAsync();
        }
        try
        {
            await Sql("""
                INSERT auth.appUser (appUserId, rootTenantId, homeTenantId, userName, displayName, passwordHash, securityStamp)
                SELECT @id, rootTenantId, homeTenantId, @name, N'MFA recovery test', passwordHash, NEWID ()
                FROM auth.appUser WHERE userName = 'admin';
                """);
            using var client = fixture.Anonymous();
            async Task SignIn()
            {
                var signinHtml = await client.Http.GetStringAsync("/account/signin");
                using var response = await client.NoRedirect.PostAsync("/account/signin", MeritClient.Form(signinHtml, ("UserName", userName), ("Password", MeritFixture.Password), ("ReturnUrl", "/cases?sort=opened&desc=false")));
                Assert.EndsWith("/enroll", response.Headers.Location?.ToString());
            }
            await SignIn();
            var pendingHtml = await client.Http.GetStringAsync("/account/enroll");
            using var restart = await client.NoRedirect.PostAsync("/account/restart-signin", MeritClient.Form(pendingHtml));
            Assert.EndsWith("/signin", restart.Headers.Location?.ToString());
            using var pending = await client.NoRedirect.GetAsync("/account/enroll"); Assert.False(pending.IsSuccessStatusCode);
            await SignIn();
            var html = await client.Http.GetStringAsync("/account/enroll");
            var secret = Regex.Match(html, "<code>([A-Z2-7 ]+)</code>").Groups[1].Value.Replace(" ", "");
            var code = Totp.Code(Totp.FromBase32(secret), DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
            using var enrolled = await client.NoRedirect.PostAsync("/account/enroll", MeritClient.Form(html, ("Code", code)));
            enrolled.EnsureSuccessStatusCode();
            var recoveryHtml = await enrolled.Content.ReadAsStringAsync();
            Assert.Contains("/cases?sort=opened&desc=false", WebUtility.HtmlDecode(recoveryHtml));
            using var notSaved = await client.NoRedirect.PostAsync("/account/recovery/continue", MeritClient.Form(recoveryHtml));
            Assert.Equal(HttpStatusCode.BadRequest, notSaved.StatusCode);
            foreach (var (destination, expected) in new[] { ("/cases?sort=opened&desc=false", "/cases?sort=opened&desc=false"), ("https://example.com", "/") })
            {
                using var continued = await client.NoRedirect.PostAsync("/account/recovery/continue", MeritClient.Form(recoveryHtml, ("savedCodes", "true"), ("returnUrl", destination)));
                Assert.Equal(expected, continued.Headers.Location?.ToString());
            }
        }
        finally { await Sql("DELETE auth.sessionScopeTenant WHERE sessionScopeId IN (SELECT sessionScopeId FROM auth.sessionScope WHERE appUserId = @id); DELETE auth.sessionScope WHERE appUserId = @id; DELETE auth.mfaRecoveryCode WHERE appUserId = @id; DELETE auth.appUser WHERE appUserId = @id;"); }
    }
}
