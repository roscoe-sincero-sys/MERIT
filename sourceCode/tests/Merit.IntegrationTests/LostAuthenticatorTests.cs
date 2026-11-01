using System.Net;
using System.Text.RegularExpressions;
using Merit.Application.Security;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

[Collection(MeritCollection.Name)]
public sealed class LostAuthenticatorTests(MeritFixture fixture)
{
    private const string Destination = "/account/profiles?from=lost-authenticator";

    [SqlFact]
    public async Task MissingAuthenticatorCanUseAnUnusedRecoveryCodeButCannotReuseIt()
    {
        await using var account = await TestAccount.CreateAsync();
        using var original = fixture.Anonymous();
        var (_, codes) = await EnrollAsync(original, account);
        using var client = fixture.Anonymous();
        await PasswordAsync(client, account, "/account/mfa");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.NoRedirect.GetAsync("/api/v1/me")).StatusCode);
        var normal = await client.Http.GetStringAsync("/account/mfa");
        Assert.Contains("Can't access your authenticator?", WebUtility.HtmlDecode(normal));
        var recovery = await client.Http.GetStringAsync("/account/mfa?useRecoveryCode=true");
        Assert.Contains("No recovery codes available?", recovery);
        Assert.Contains("verify your identity", recovery);
        using var wrong = await client.NoRedirect.PostAsync("/account/mfa",
            MeritClient.Form(recovery, ("UseRecoveryCode", "true"), ("Code", "INVALID")));
        var failed = await wrong.Content.ReadAsStringAsync();
        Assert.Contains("That recovery code did not work", failed);
        Assert.Contains("Use recovery code and sign in", failed);
        Assert.DoesNotContain("value=\"INVALID\"", failed);
        Assert.Equal(1, await account.ScalarAsync("SELECT failedSignInCount FROM auth.appUser WHERE appUserId=@id"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.NoRedirect.GetAsync("/api/v1/me")).StatusCode);
        using var success = await client.NoRedirect.PostAsync("/account/mfa",
            MeritClient.Form(failed, ("UseRecoveryCode", "true"), ("Code", codes[0].ToLowerInvariant())));
        Assert.Equal(Destination, success.Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.NoRedirect.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(1, await account.ScalarAsync("SELECT COUNT(*) FROM auth.mfaRecoveryCode WHERE appUserId=@id AND usedUtc IS NOT NULL"));

        using var retry = fixture.Anonymous();
        await PasswordAsync(retry, account, "/account/mfa");
        var retryPage = await retry.Http.GetStringAsync("/account/mfa?useRecoveryCode=true");
        using var reused = await retry.NoRedirect.PostAsync("/account/mfa",
            MeritClient.Form(retryPage, ("UseRecoveryCode", "true"), ("Code", codes[0])));
        Assert.Contains("That recovery code did not work", await reused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await retry.NoRedirect.GetAsync("/api/v1/me")).StatusCode);
    }

    [SqlFact]
    public async Task AdministratorResetAllowsFreshEnrollmentWithoutPasswordOnlyReplacement()
    {
        await using var account = await TestAccount.CreateAsync();
        using var original = fixture.Anonymous();
        var (oldSecret, oldCodes) = await EnrollAsync(original, account);
        var stamp = await account.ScalarAsync("SELECT securityStamp FROM auth.appUser WHERE appUserId=@id");
        using var pending = fixture.Anonymous();
        await PasswordAsync(pending, account, "/account/mfa");
        var recovery = await pending.Http.GetStringAsync("/account/mfa?useRecoveryCode=true");
        // Following the restart instructions alone must not replace an enrolled authenticator.
        await pending.Http.PostAsync("/account/restart-signin", MeritClient.Form(recovery));
        await PasswordAsync(pending, account, "/account/mfa");
        using var enrollDenied = await pending.NoRedirect.GetAsync("/account/enroll");
        Assert.EndsWith("/account/signin", enrollDenied.Headers.Location?.ToString());
        using var replaceDenied = await pending.NoRedirect.PostAsync("/account/enroll", MeritClient.Form(recovery, ("Code", "123456")));
        Assert.EndsWith("/account/signin", replaceDenied.Headers.Location?.ToString());
        using var resetDenied = await pending.NoRedirect.PostAsync($"/admin/users/{account.Id}/reset-mfa", MeritClient.Form(recovery));
        Assert.Equal(HttpStatusCode.Redirect, resetDenied.StatusCode);
        var city = await fixture.SignedInAsync("cityadmin");
        await city.Http.GetStringAsync("/");
        using var outOfScope = await city.NoRedirect.PostAsync($"/admin/users/{account.Id}/reset-mfa", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.Forbidden, outOfScope.StatusCode);
        Assert.Equal(stamp, await account.ScalarAsync("SELECT securityStamp FROM auth.appUser WHERE appUserId=@id"));

        var admin = await fixture.SignedInAsync("admin");
        var adminPage = await admin.Http.GetStringAsync($"/admin/users/{account.Id}");
        using var reset = await admin.NoRedirect.PostAsync($"/admin/users/{account.Id}/reset-mfa", MeritClient.Form(adminPage));
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        Assert.NotEqual(stamp, await account.ScalarAsync("SELECT securityStamp FROM auth.appUser WHERE appUserId=@id"));
        Assert.Equal(0, await account.ScalarAsync("SELECT COUNT(*) FROM auth.sessionScope WHERE appUserId=@id AND revokedUtc IS NULL"));
        Assert.Equal(0, await account.ScalarAsync("SELECT COUNT(*) FROM auth.mfaRecoveryCode WHERE appUserId=@id AND isDeleted=0"));
        Assert.Equal(1, await account.ScalarAsync("SELECT COUNT(*) FROM auth.appUser WHERE appUserId=@id AND mfaEnabled=0 AND mfaSecretProtected IS NULL"));

        await pending.Http.PostAsync("/account/restart-signin", MeritClient.Form(recovery));
        var (newSecret, _) = await EnrollAsync(pending, account);
        Assert.NotEqual(oldSecret, newSecret);
        using var later = fixture.Anonymous();
        await PasswordAsync(later, account, "/account/mfa");
        var laterPage = await later.Http.GetStringAsync("/account/mfa?useRecoveryCode=true");
        using var oldCode = await later.NoRedirect.PostAsync("/account/mfa", MeritClient.Form(laterPage, ("UseRecoveryCode", "true"), ("Code", oldCodes[0])));
        Assert.Contains("That recovery code did not work", await oldCode.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await later.NoRedirect.GetAsync("/api/v1/me")).StatusCode);
        var mfaPage = await later.Http.GetStringAsync("/account/mfa");
        using var verified = await later.NoRedirect.PostAsync("/account/mfa", MeritClient.Form(mfaPage, ("Code", Code(newSecret))));
        Assert.Equal(Destination, verified.Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.OK, (await later.NoRedirect.GetAsync("/api/v1/me")).StatusCode);
    }

    private static async Task<string> PasswordAsync(MeritClient client, TestAccount account, string expected)
    {
        var page = await client.Http.GetStringAsync("/account/signin");
        using var response = await client.NoRedirect.PostAsync("/account/signin",
            MeritClient.Form(page, ("UserName", account.Name), ("Password", MeritFixture.Password), ("ReturnUrl", Destination)));
        Assert.Equal(expected, response.Headers.Location?.ToString());
        return await client.Http.GetStringAsync(expected);
    }

    private static async Task<(string Secret, string[] Codes)> EnrollAsync(MeritClient client, TestAccount account)
    {
        var page = await PasswordAsync(client, account, "/account/enroll");
        var secret = Regex.Match(page, "<code>([A-Z2-7 ]+)</code>").Groups[1].Value.Replace(" ", "");
        Assert.NotEmpty(secret);
        using var response = await client.NoRedirect.PostAsync("/account/enroll", MeritClient.Form(page, ("Code", Code(secret))));
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        var codes = Regex.Matches(html, "<li data-recovery-code>([A-Z0-9-]+)</li>").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(10, codes.Length);
        return (secret, codes);
    }

    private static string Code(string secret) => Totp.Code(Totp.FromBase32(secret), DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);

    private sealed class TestAccount : IAsyncDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Name => "lost-" + Id;

        public static async Task<TestAccount> CreateAsync()
        {
            var account = new TestAccount();
            await account.ScalarAsync("""
                INSERT auth.appUser(appUserId,rootTenantId,homeTenantId,userName,displayName,passwordHash,securityStamp)
                SELECT @id,rootTenantId,homeTenantId,@name,N'Lost authenticator test',passwordHash,NEWID()
                FROM auth.appUser WHERE userName='admin';
                """);
            return account;
        }

        public async Task<object?> ScalarAsync(string sql)
        {
            await using var connection = new SqlConnection(TestEnvironment.AdminConnectionString());
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@id", Id);
            command.Parameters.AddWithValue("@name", Name);
            return await command.ExecuteScalarAsync();
        }

        public async ValueTask DisposeAsync() => await ScalarAsync("""
            DECLARE @scopes TABLE(id uniqueidentifier); INSERT @scopes SELECT sessionScopeId FROM auth.sessionScope WHERE appUserId=@id;
            DELETE auth.sessionScopePermission WHERE sessionScopeId IN(SELECT id FROM @scopes);
            DELETE auth.sessionScopeAdminTenant WHERE sessionScopeId IN(SELECT id FROM @scopes);
            DELETE auth.sessionScopeRoot WHERE sessionScopeId IN(SELECT id FROM @scopes);
            DELETE auth.sessionScopeTenant WHERE sessionScopeId IN(SELECT id FROM @scopes);
            DELETE auth.sessionScope WHERE appUserId=@id;
            DELETE auth.mfaRecoveryCode WHERE appUserId=@id;
            DELETE auth.appUser WHERE appUserId=@id;
            """);
    }
}
