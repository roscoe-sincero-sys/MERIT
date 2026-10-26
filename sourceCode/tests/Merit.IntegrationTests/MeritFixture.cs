using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Merit.Application.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class MeritCollection : ICollectionFixture<MeritFixture>
{
    public const string Name = "merit";
}

/// <summary>Well-known demo records, looked up after deployment.</summary>
public sealed record DemoIds(Guid MiaPersonId, Guid MiaCaseId, string MiaCaseNumber, Guid MiaPersonLocationId, Guid MiaLabResultId,
                             Guid? InvestigationId, Guid? ClusterId, Guid BaltimoreCityTenantId, Guid BaltimoreCountyTenantId);

/// <summary>
/// Deploys the test database once per run, resets MFA so the suite can enroll each demo user, hosts the web
/// application in memory against that database, and hands out signed-in clients (one per user, reused).
/// </summary>
public sealed partial class MeritFixture : IDisposable
{
    public const string Password = "MeritDemo!2026";

    private readonly ConcurrentDictionary<string, MeritClient> clients = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim signInGate = new(1, 1);

    public MeritFixture()
    {
        if (TestEnvironment.SkipReason is not null)
        {
            return;
        }

        TestEnvironment.Deploy();
        using (var c = new SqlConnection(TestEnvironment.AdminConnectionString()))
        {
            c.Open();
            Exec(c, """
                UPDATE auth.appUser SET mfaEnabled = 0, mfaSecretProtected = NULL, failedSignInCount = 0, lockoutEndUtc = NULL
                 WHERE mfaEnabled = 1 OR mfaSecretProtected IS NOT NULL OR failedSignInCount > 0 OR lockoutEndUtc IS NOT NULL;
                DELETE auth.mfaRecoveryCode;
                """);
            Ids = LoadIds(c);
        }

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Merit", TestEnvironment.WebConnectionString);
            b.UseSetting("Merit:SignInAttemptsPerMinute", "10000");
            b.UseSetting("Merit:MfaAttemptsPerMinute", "10000");
            b.UseSetting("DataProtection:KeyPath", Path.Combine(Path.GetTempPath(), "merit-it-keys"));
        });
    }

    public WebApplicationFactory<Program> Factory { get; } = null!;

    public DemoIds Ids { get; } = null!;

    /// <summary>A client with no session.</summary>
    public MeritClient Anonymous() => new(Factory);

    /// <summary>A client signed in as a demo user, enrolling MFA on first use the way a person would.</summary>
    public async Task<MeritClient> SignedInAsync(string userName)
    {
        if (clients.TryGetValue(userName, out var existing))
        {
            return existing;
        }

        await signInGate.WaitAsync();
        try
        {
            if (clients.TryGetValue(userName, out existing))
            {
                return existing;
            }

            var client = new MeritClient(Factory);
            await client.SignInAsync(userName, Password);
            clients[userName] = client;
            return client;
        }
        finally
        {
            signInGate.Release();
        }
    }

    public void Dispose()
    {
        foreach (var c in clients.Values)
        {
            c.Dispose();
        }

        Factory?.Dispose();
        signInGate.Dispose();
    }

    private static DemoIds LoadIds(SqlConnection c)
    {
        using var cmd = new SqlCommand("""
            SELECT p.personId, ec.exposureCaseId, ec.caseNumber,
                   (SELECT TOP (1) personLocationId FROM dbo.personLocation WHERE personId = p.personId AND isDeleted = 0),
                   (SELECT TOP (1) labResultId FROM dbo.labResult WHERE personId = p.personId AND isDeleted = 0),
                   (SELECT TOP (1) investigationId FROM dbo.investigation WHERE isDeleted = 0),
                   (SELECT TOP (1) clusterId FROM dbo.cluster WHERE isDeleted = 0),
                   (SELECT tenantId FROM auth.tenant WHERE tenantCode = 'MD-BCHD'),
                   (SELECT tenantId FROM auth.tenant WHERE tenantCode = 'MD-BCO')
              FROM dbo.person AS p
              JOIN dbo.exposureCase AS ec ON ec.personId = p.personId AND ec.isDeleted = 0
             WHERE p.lastName = N'Rivera' AND p.firstName = N'Mia' AND p.isDeleted = 0;
            """, c);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
        {
            throw new InvalidOperationException("The demo data is missing: Mia Rivera and her case were not found.");
        }

        return new DemoIds(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetGuid(3), r.GetGuid(4),
            r.IsDBNull(5) ? null : r.GetGuid(5), r.IsDBNull(6) ? null : r.GetGuid(6), r.GetGuid(7), r.GetGuid(8));
    }

    internal static void Exec(SqlConnection c, string sql)
    {
        using var cmd = new SqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }
}

/// <summary>
/// An HTTP client for the in-memory server with its own cookies, redirects followed, and the antiforgery token sent the
/// way the browser front ends send it (X-XSRF-TOKEN from the XSRF-TOKEN cookie; the hidden field for forms).
/// </summary>
public sealed partial class MeritClient : IDisposable
{
    private static readonly Uri BaseAddress = new("https://localhost");
    private readonly CookieContainer cookies = new();

    public MeritClient(WebApplicationFactory<Program> factory)
    {
        Http = factory.CreateDefaultClient(BaseAddress, new RedirectHandler(), new XsrfHandler(cookies), new CookieContainerHandler(cookies));
        NoRedirect = factory.CreateDefaultClient(BaseAddress, new XsrfHandler(cookies), new CookieContainerHandler(cookies));
    }

    public HttpClient Http { get; }

    /// <summary>Same cookies, redirects not followed, for checking what an endpoint answers itself.</summary>
    public HttpClient NoRedirect { get; }

    public string? UserName { get; private set; }

    public async Task SignInAsync(string userName, string password)
    {
        var page = await Http.GetStringAsync("/account/signin");
        var response = await Http.PostAsync("/account/signin", Form(page, ("UserName", userName), ("Password", password)));
        var html = await response.Content.ReadAsStringAsync();
        string path = response.RequestMessage!.RequestUri!.AbsolutePath;
        if (path.EndsWith("/enroll", StringComparison.OrdinalIgnoreCase))
        {
            var secret = SecretPattern().Match(html).Groups[1].Value.Replace(" ", string.Empty, StringComparison.Ordinal);
            response = await Http.PostAsync("/account/enroll", Form(html, ("Code", CurrentCode(secret))));
        }
        else if (!path.EndsWith("/mfa", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Sign-in for {userName} ended at {path}.");
        }
        else
        {
            throw new InvalidOperationException($"{userName} is already enrolled; the fixture resets MFA before the run.");
        }

        var me = await NoRedirect.GetAsync("/api/v1/me");
        if (!response.IsSuccessStatusCode || me.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"MFA enrollment for {userName} did not complete ({response.StatusCode}, me {me.StatusCode}).");
        }

        UserName = userName;
    }

    /// <summary>GET with the headers htmx 4 sends when it swaps a partial.</summary>
    public Task<HttpResponseMessage> GetPartialAsync(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("HX-Request", "true");
        request.Headers.Add("HX-Request-Type", "partial");
        return NoRedirect.SendAsync(request);
    }

    public static FormUrlEncodedContent Form(string pageHtml, params (string Key, string Value)[] fields)
        => new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))
            .Append(new KeyValuePair<string, string>("__RequestVerificationToken", TokenPattern().Match(pageHtml).Groups[1].Value)));

    public void Dispose()
    {
        Http.Dispose();
        NoRedirect.Dispose();
    }

    private static string CurrentCode(string secret)
        => Totp.Code(Totp.FromBase32(secret), DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);

    [GeneratedRegex("<code>([A-Z2-7 ]+)</code>")]
    private static partial Regex SecretPattern();

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();

    /// <summary>Adds X-XSRF-TOKEN from the XSRF-TOKEN cookie on unsafe methods, as merit.js and Angular do.</summary>
    private sealed class XsrfHandler(CookieContainer cookies) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head
                && cookies.GetCookies(BaseAddress)["XSRF-TOKEN"] is { } token && !request.Headers.Contains("X-XSRF-TOKEN"))
            {
                request.Headers.Add("X-XSRF-TOKEN", Uri.UnescapeDataString(token.Value));
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
