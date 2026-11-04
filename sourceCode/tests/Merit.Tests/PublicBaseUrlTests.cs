using Merit.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;

namespace Merit.Tests;

/// <summary>
/// Issue #190: reset links point to Merit:PublicBaseUrl. Outside Development a requester's Host header is never used
/// (password-reset poisoning), and without an https setting forgotten-password links are not offered.
/// </summary>
public sealed class PublicBaseUrlTests
{
    [Theory]
    [InlineData("Production", "https://merit.health.example.gov/", "https://merit.health.example.gov")]
    [InlineData("Production", "https://merit.health.example.gov/merit/", "https://merit.health.example.gov/merit")]
    [InlineData("Production", "http://merit.health.example.gov", null)]   // a plain-http link is refused outside Development
    [InlineData("Production", null, null)]                                 // the attacker's Host header is never the answer
    [InlineData("Development", null, "https://evil.example")]              // local runs use the request's own address
    [InlineData("Development", "http://localhost:5000", "http://localhost:5000")]
    public void LinksUseTheConfiguredAddress(string environment, string? configured, string? expected)
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("evil.example");
        var settings = new Dictionary<string, string?>();
        if (configured is not null)
        {
            settings[PublicBaseUrl.Setting] = configured;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        IHostEnvironment env = new HostingEnvironment { EnvironmentName = environment };
        Assert.Equal(expected, PublicBaseUrl.Resolve(http.Request, configuration, env));
    }
}
