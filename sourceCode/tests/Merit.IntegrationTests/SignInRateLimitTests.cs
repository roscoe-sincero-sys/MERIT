using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Merit.IntegrationTests;

/// <summary>
/// Issue #8: the sign-in limits are counted per client address. One client that runs out of attempts is shown a page
/// telling it to wait, and nobody else is held up.
/// </summary>
[Collection(MeritCollection.Name)]
public sealed class SignInRateLimitTests(MeritFixture fixture) : IDisposable
{
    private const int Limit = 3;
    private const string ClientHeader = "X-Test-Client";

    // A server of its own with a low limit, where each test client says which address it comes from.
    private readonly WebApplicationFactory<Program> factory = fixture.Factory.WithWebHostBuilder(b =>
    {
        b.UseSetting("Merit:SignInAttemptsPerMinute", Limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        b.UseSetting("Merit:MfaAttemptsPerMinute", Limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        b.ConfigureServices(s => s.AddTransient<IStartupFilter, ClientAddressFromHeader>());
    });

    [SqlFact]
    public async Task OneClientRunningOutOfAttemptsDoesNotHoldUpAnother()
    {
        using var a = Client("10.0.0.1");
        using var b = Client("10.0.0.2");

        // An unknown user name, so no real account is locked while client A spends its attempts.
        string page = await a.Http.GetStringAsync("/account/signin");
        for (int i = 0; i < Limit; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await WrongPasswordAsync(a, page)).StatusCode);
        }

        var refused = await WrongPasswordAsync(a, page);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.True(refused.Headers.RetryAfter?.Delta is { } wait && wait > TimeSpan.Zero && wait <= TimeSpan.FromMinutes(1));
        string html = await refused.Content.ReadAsStringAsync();
        Assert.Contains("Too many attempts", html, StringComparison.Ordinal);
        Assert.Contains("Back to sign in", html, StringComparison.Ordinal);
        Assert.Contains("'nonce-", string.Join(' ', refused.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);

        // Client B, from another address, still reaches the password check.
        string pageB = await b.Http.GetStringAsync("/account/signin");
        var other = await WrongPasswordAsync(b, pageB);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.DoesNotContain("Too many attempts", await other.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The code steps have their own count, so A's spent password attempts do not block A's code step.
        var code = await a.NoRedirect.PostAsync("/account/mfa", MeritClient.Form(page, ("Code", "000000")));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, code.StatusCode);
    }

    [SqlFact]
    public async Task RejectedFormsCountTowardTheLimit()
    {
        // A post without the antiforgery token is refused before the action runs, but it still spends an attempt,
        // so a script cannot probe the endpoint for free.
        using var c = Client("10.0.0.3");
        for (int i = 0; i < Limit; i++)
        {
            var bare = await c.NoRedirect.PostAsync("/account/signin", new FormUrlEncodedContent([new("UserName", "x"), new("Password", "y")]));
            Assert.Equal(HttpStatusCode.BadRequest, bare.StatusCode);
        }

        string page = await c.Http.GetStringAsync("/account/signin");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await WrongPasswordAsync(c, page)).StatusCode);
    }

    public void Dispose() => factory.Dispose();

    private MeritClient Client(string address)
    {
        var client = new MeritClient(factory);
        client.Http.DefaultRequestHeaders.Add(ClientHeader, address);
        client.NoRedirect.DefaultRequestHeaders.Add(ClientHeader, address);
        return client;
    }

    private static Task<HttpResponseMessage> WrongPasswordAsync(MeritClient client, string page)
        => client.NoRedirect.PostAsync("/account/signin", MeritClient.Form(page, ("UserName", "nobody-rate-limit"), ("Password", "wrong")));

    /// <summary>Sets the connection's address from the test header, ahead of the application's own middleware.</summary>
    private sealed class ClientAddressFromHeader : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((HttpContext http, RequestDelegate n) =>
            {
                if (IPAddress.TryParse(http.Request.Headers[ClientHeader].ToString(), out var address))
                {
                    http.Connection.RemoteIpAddress = address;
                }

                return n(http);
            });
            next(app);
        };
    }
}
