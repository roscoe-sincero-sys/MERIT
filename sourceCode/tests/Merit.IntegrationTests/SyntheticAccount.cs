using System.Net;
using Merit.Application.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Merit.IntegrationTests;

/// <summary>
/// A synthetic account in the demo state: the demo password, an authenticator secret the test knows, an example.test address,
/// no assignments. Disposing removes it and everything it left behind.
/// </summary>
public sealed class SyntheticAccount : IAsyncDisposable
{
    private readonly string secret = Totp.ToBase32(Totp.NewSecret());
    private readonly string prefix;

    private SyntheticAccount(string prefix) => this.prefix = prefix;

    public Guid Id { get; } = Guid.NewGuid();

    public string Name => prefix + "-" + Id.ToString("N")[..20];

    public string Email => $"{Name}@example.test";

    public string TotpCode() => Totp.Code(Totp.FromBase32(secret), DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);

    public static async Task<SyntheticAccount> CreateAsync(WebApplicationFactory<Program> host, string prefix, bool mfa = true)
    {
        var account = new SyntheticAccount(prefix);
        string protectedSecret = host.Services.GetRequiredService<ISecretProtector>().Protect(account.secret);
        await account.ScalarAsync<int>("""
            INSERT auth.appUser (appUserId, rootTenantId, homeTenantId, userName, displayName, email, passwordHash, securityStamp,
                                 mfaSecretProtected, mfaEnabled)
            SELECT @id, rootTenantId, homeTenantId, @name, N'Synthetic test account', @email, passwordHash, NEWID (),
                   CASE WHEN @mfa = 1 THEN @secret END, @mfa
              FROM auth.appUser WHERE userName = N'admin';
            SELECT 0;
            """, ("@email", account.Email), ("@secret", protectedSecret), ("@mfa", mfa));
        return account;
    }

    /// <summary>Password and authenticator code, as a person signs in; ends wherever sign-in leads.</summary>
    public async Task SignInAsync(MeritClient client, string password, string returnUrl = "/")
    {
        string page = await client.Http.GetStringAsync("/account/signin");
        using var step = await client.NoRedirect.PostAsync("/account/signin",
            MeritClient.Form(page, ("UserName", Name), ("Password", password), ("ReturnUrl", returnUrl)));
        Assert.Equal("/account/mfa", step.Headers.Location?.OriginalString);
        string mfa = await client.Http.GetStringAsync("/account/mfa");
        using var done = await client.NoRedirect.PostAsync("/account/mfa", MeritClient.Form(mfa, ("Code", TotpCode())));
        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
    }

    public async Task AddRecoveryCodeAsync(WebApplicationFactory<Program> host, string code)
    {
        string hash = host.Services.GetRequiredService<IPasswordHasher>().Hash(code);
        await ScalarAsync<int>("INSERT auth.mfaRecoveryCode (mfaRecoveryCodeId, appUserId, codeHash) VALUES (NEWID (), @id, @hash); SELECT 0", ("@hash", hash));
    }

    public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] args)
    {
        await using var connection = new SqlConnection(TestEnvironment.AdminConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", Id);
        command.Parameters.AddWithValue("@name", Name);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync())!;
    }

    public async ValueTask DisposeAsync() => await ScalarAsync<int>("""
        DECLARE @scopes TABLE (id UNIQUEIDENTIFIER); INSERT @scopes SELECT sessionScopeId FROM auth.sessionScope WHERE appUserId = @id;
        DELETE auth.sessionScopePermission WHERE sessionScopeId IN (SELECT id FROM @scopes);
        DELETE auth.sessionScopeAdminTenant WHERE sessionScopeId IN (SELECT id FROM @scopes);
        DELETE auth.sessionScopeRoot WHERE sessionScopeId IN (SELECT id FROM @scopes);
        DELETE auth.sessionScopeTenant WHERE sessionScopeId IN (SELECT id FROM @scopes);
        DELETE auth.sessionScope WHERE appUserId = @id;
        DELETE auth.passwordReset WHERE appUserId = @id;
        DELETE auth.mfaEmailChallenge WHERE appUserId = @id;
        DELETE auth.mfaRecoveryCode WHERE appUserId = @id;
        DELETE auth.userAssignment WHERE appUserId = @id;
        DELETE auth.userProfile WHERE appUserId = @id;
        DELETE auth.appUser WHERE appUserId = @id;
        SELECT 0;
        """);
}


/// <summary>
/// A clock the test moves (#191): cookie expiry, session re-validation and the jurisdiction clock all follow it, so idle
/// and lifetime expiry can be checked without waiting.
/// </summary>
public sealed class TestClock : TimeProvider
{
    private TimeSpan offset;

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;

    public void Advance(TimeSpan by) => offset += by;
}

/// <summary>
/// TestServer has no client address. A request may name one in X-Test-Client-IP, so tests can sign in "from" different
/// addresses (#191). Registered only by the tests' own hosts.
/// </summary>
public sealed class TestClientAddressFilter : IStartupFilter
{
    public const string Header = "X-Test-Client-IP";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (HttpContext http, Func<Task> nextMiddleware) =>
        {
            http.Connection.RemoteIpAddress = http.Request.Headers.TryGetValue(Header, out var value) && IPAddress.TryParse(value, out var ip)
                ? ip
                : IPAddress.Loopback;
            await nextMiddleware();
        });
        next(app);
    };
}
