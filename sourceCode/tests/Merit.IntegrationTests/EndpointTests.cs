using System.Net;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Merit.IntegrationTests;

/// <summary>One action as routed: its URL with demo ids filled in, or null when a route value has no demo record.</summary>
public sealed record RoutedAction(string Method, string Pattern, string Controller, string Action, string? Url, bool IsApi);

/// <summary>
/// Endpoint inventory at runtime (every action refuses an anonymous caller), every MVC page in its full and htmx partial
/// shape, and every API read, against the deployed test database.
/// </summary>
[Collection(MeritCollection.Name)]
public sealed class EndpointTests(MeritFixture fixture, ITestOutputHelper output)
{
    // Actions a signed-out person must reach: sign-in, the denied page, the error page.
    private static readonly HashSet<string> Anonymous = new(StringComparer.Ordinal)
    {
        "GET Account.SignIn", "POST Account.SignIn", "GET Account.Denied", "GET Home.Error",
    };

    private IReadOnlyList<RoutedAction> Actions()
    {
        var sources = fixture.Factory.Services.GetRequiredService<IEnumerable<EndpointDataSource>>();
        return sources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Select(e => (Endpoint: e, Descriptor: e.Metadata.GetMetadata<ControllerActionDescriptor>(),
                Methods: e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []))
            .Where(x => x.Descriptor is not null)
            .SelectMany(x => x.Methods.Select(m => new RoutedAction(m, "/" + x.Endpoint.RoutePattern.RawText!.TrimStart('/'),
                x.Descriptor!.ControllerName, x.Descriptor.ActionName, Fill(x.Endpoint.RoutePattern.RawText!, x.Descriptor.ControllerName),
                x.Endpoint.RoutePattern.RawText!.TrimStart('/').StartsWith("api/", StringComparison.Ordinal))))
            .DistinctBy(a => a.Method + " " + a.Pattern)
            .OrderBy(a => a.Pattern, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Fills route values from the demo data; null when a value has no known record.</summary>
    private string? Fill(string pattern, string controller)
    {
        var ids = fixture.Ids;
        var url = "/" + pattern.TrimStart('/');
        foreach (var segment in url.Split('/').Where(s => s.StartsWith('{')))
        {
            string name = segment.Trim('{', '}').TrimStart('*').Split(':', '=', '?')[0];
            string? value = (name, controller) switch
            {
                ("id", "People") or ("personId", _) => ids.MiaPersonId.ToString(),
                ("id", "Cases") or ("exposureCaseId", _) => ids.MiaCaseId.ToString(),
                ("id", "Results") or ("labResultId", _) => ids.MiaLabResultId.ToString(),
                ("id", "Investigations") => ids.InvestigationId?.ToString(),
                ("id", "Clusters") or ("clusterId", _) => ids.ClusterId?.ToString(),
                ("id", "ResultsApi") => ids.MiaPersonId.ToString(),
                ("personLocationId", _) => ids.MiaPersonLocationId.ToString(),
                ("code", "Reports") => "testingCounts",
                ("path", "AppHost") => "queues",
                _ => null,
            };
            if (value is null)
            {
                return null;
            }

            url = url.Replace(segment, value, StringComparison.Ordinal);
        }

        return url;
    }

    private static string AnyGuids(string pattern)
        => string.Join('/', ("/" + pattern.TrimStart('/')).Split('/').Select(s => s.StartsWith('{') ? Guid.NewGuid().ToString() : s));

    [SqlFact]
    public void TheInventoryIsNotEmpty()
    {
        var actions = Actions();
        output.WriteLine($"{actions.Count} routed actions; {actions.Count(a => a.Url is null)} have route values without demo records.");
        Assert.True(actions.Count > 100);
    }

    [SqlFact]
    public async Task EveryActionRefusesAnAnonymousCaller()
    {
        using var client = fixture.Anonymous();
        var failures = new List<string>();
        foreach (var a in Actions().Where(a => !Anonymous.Contains($"{a.Method} {a.Controller}.{a.Action}")))
        {
            var url = a.Url ?? AnyGuids(a.Pattern);
            var request = new HttpRequestMessage(new HttpMethod(a.Method), url);
            if (a.Method != "GET")
            {
                request.Content = a.IsApi ? new StringContent("{}", System.Text.Encoding.UTF8, "application/json") : new FormUrlEncodedContent([]);
            }

            var response = await client.NoRedirect.SendAsync(request);
            bool refused = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest
                           || (response.StatusCode == HttpStatusCode.Redirect
                               && response.Headers.Location?.ToString().Contains("/account/signin", StringComparison.OrdinalIgnoreCase) == true);
            if (!refused)
            {
                failures.Add($"{a.Method} {url} -> {(int)response.StatusCode}");
            }
        }

        Assert.True(failures.Count == 0, "Reachable without signing in:\n" + string.Join('\n', failures));
    }

    [SqlFact]
    public async Task EveryPageRendersInFullAndAsAnHtmxPartial()
    {
        var supervisor = await fixture.SignedInAsync("supervisor");
        var admin = await fixture.SignedInAsync("admin");
        var failures = new List<string>();
        int checkedCount = 0;
        foreach (var a in Actions().Where(a => a.Method == "GET" && !a.IsApi && a.Controller is not "Account" and not "AppHost" and not "Lookup"))
        {
            if (a.Url is null)
            {
                output.WriteLine($"skipped (no demo record for a route value): {a.Pattern}");
                continue;
            }

            var client = supervisor;
            var full = await client.NoRedirect.GetAsync(a.Url);
            if (full.StatusCode == HttpStatusCode.Forbidden || full.Headers.Location?.ToString().Contains("denied", StringComparison.Ordinal) == true)
            {
                client = admin;
                full = await client.NoRedirect.GetAsync(a.Url);
            }

            string fullHtml = await full.Content.ReadAsStringAsync();
            if ((int)full.StatusCode >= 400 || (full.StatusCode == HttpStatusCode.OK && !fullHtml.Contains("<html", StringComparison.Ordinal)
                                                && !IsPartialOnly(a.Pattern)))
            {
                failures.Add($"full    {a.Url} -> {(int)full.StatusCode}{(full.StatusCode == HttpStatusCode.OK ? " without the layout" : string.Empty)}");
            }

            var partial = await client.GetPartialAsync(a.Url);
            string partialHtml = await partial.Content.ReadAsStringAsync();
            if ((int)partial.StatusCode >= 400)
            {
                failures.Add($"partial {a.Url} -> {(int)partial.StatusCode}");
            }
            else if (IsHtmxSection(a.Pattern) && partialHtml.Contains("<html", StringComparison.Ordinal))
            {
                failures.Add($"partial {a.Url} returned the whole page instead of the partial");
            }

            checkedCount++;
        }

        output.WriteLine($"{checkedCount} pages checked in both shapes.");
        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    [SqlFact]
    public async Task EveryApiReadAnswersWithJson()
    {
        var supervisor = await fixture.SignedInAsync("supervisor");
        var failures = new List<string>();
        foreach (var a in Actions().Where(a => a.Method == "GET" && a.IsApi && a.Url is not null))
        {
            var response = await supervisor.NoRedirect.GetAsync(a.Url);
            string type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            bool ok = response.StatusCode == HttpStatusCode.OK
                      && (type == "application/json" || (a.Pattern.EndsWith("/export", StringComparison.Ordinal) && type == "text/csv"));
            if (!ok)
            {
                failures.Add($"{a.Url} -> {(int)response.StatusCode} {type}");
            }
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    [SqlFact]
    public async Task UnsafeRequestsWithoutTheAntiforgeryTokenAreRejected()
    {
        var supervisor = await fixture.SignedInAsync("supervisor");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tasks/bulk")
        {
            Content = new StringContent("""{"taskIds":[],"action":"complete"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-XSRF-TOKEN", "not-the-token");
        var response = await supervisor.NoRedirect.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SqlFact]
    public async Task ResponsesCarryTheContentSecurityPolicyWithANonce()
    {
        var supervisor = await fixture.SignedInAsync("supervisor");
        var response = await supervisor.NoRedirect.GetAsync("/");
        var csp = string.Join(' ', response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("'nonce-", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
    }

    /// <summary>Person page sections and the place dialogs are the htmx partial endpoints: htmx swaps only the section.</summary>
    private static bool IsHtmxSection(string pattern)
        => pattern.StartsWith("/people/{id:guid}/", StringComparison.Ordinal) && !pattern.EndsWith("/edit", StringComparison.Ordinal)
           || pattern.StartsWith("/people/{personId:guid}/places", StringComparison.Ordinal)
           || pattern == "/home/queues";

    /// <summary>Endpoints that only ever return a fragment (type-ahead lists, dialogs).</summary>
    private static bool IsPartialOnly(string pattern)
        => pattern.EndsWith("/add", StringComparison.Ordinal) || pattern.Contains("/primary", StringComparison.Ordinal)
           || pattern.EndsWith("/investigations/new", StringComparison.Ordinal) || pattern == "/results/fields"
           || pattern.Contains("{episodeId", StringComparison.Ordinal)
           || pattern.EndsWith(".csv", StringComparison.Ordinal);   // file downloads, not pages
}
