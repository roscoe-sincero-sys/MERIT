using System.Net;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.SqlClient;
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
public sealed partial class EndpointTests(MeritFixture fixture, ITestOutputHelper output)
{
    // Actions a signed-out person must reach: sign-in, the denied page, the error page.
    private static readonly HashSet<string> Anonymous = new(StringComparer.Ordinal)
    {
        "GET Account.SignIn", "POST Account.SignIn", "GET Account.Denied", "GET Home.Error",
        "GET Home.Status", "POST Home.Status", "PUT Home.Status", "PATCH Home.Status", "DELETE Home.Status",
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
    public async Task TheSignInPageAndItsStylesheetsLoadBeforeSigningIn()
    {
        // Issue #3: the fallback policy once redirected the static files to sign-in, so the sign-in page rendered unstyled.
        using var client = fixture.Anonymous();
        var page = await client.NoRedirect.GetAsync("/account/signin");
        string html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var assets = System.Text.RegularExpressions.Regex.Matches(html, "(?:href|src)=\"(/(?:css|js|lib)/[^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(assets);
        foreach (var asset in assets)
        {
            var response = await client.NoRedirect.GetAsync(asset);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{asset} -> {(int)response.StatusCode}");
        }
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

            // Razor reads "v@p.ProfileVersion" as an email address and prints it (issue #26).
            foreach (var shown in new[] { fullHtml, partialHtml }.SelectMany(h => UnrenderedRazor().Matches(h)).Select(m => m.Value).Distinct())
            {
                failures.Add($"page    {a.Url} shows unrendered Razor: {shown}");
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
    public async Task SearchShowsOpenCasesAsChipsLinkingToTheCase()
    {
        // Issue #19: the Open cases column showed the raw "PB:confirmed".
        var supervisor = await fixture.SignedInAsync("supervisor");
        var html = await supervisor.Http.GetStringAsync("/search?q=Rivera");
        Assert.DoesNotContain("PB:", html, StringComparison.Ordinal);
        Assert.Matches($$"""<a class="merit-badge merit-badge--\w+" href="/cases/{{fixture.Ids.MiaCaseId}}"><abbr title="[^"]+">PB</abbr> &middot; [A-Z][a-z]+ &middot; {{fixture.Ids.MiaCaseNumber}}</a>""", html);
    }

    [SqlFact]
    public async Task ASessionRenewalReplacesTheScopeWithTheConfiguredLifetime()
    {
        // Issue #21: sign-in, profile switches and the tenant refresh renew the session through one SessionRenewal.
        var supervisor = await fixture.SignedInAsync("supervisor");
        var page = await supervisor.Http.GetStringAsync("/account/profiles");
        var before = await LatestScopeAsync("supervisor");

        var response = await supervisor.Http.PostAsync("/account/view-as/end", MeritClient.Form(page));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await supervisor.NoRedirect.GetAsync("/api/v1/me")).StatusCode);

        var after = await LatestScopeAsync("supervisor");
        Assert.NotEqual(before.Id, after.Id);
        Assert.InRange(after.Expires - after.Created, TimeSpan.FromHours(12) - TimeSpan.FromMinutes(1), TimeSpan.FromHours(12) + TimeSpan.FromMinutes(1));
        Assert.NotNull((await ScopeAsync(before.Id)).Revoked);
    }

    private static async Task<(Guid Id, DateTime Created, DateTime Expires, DateTime? Revoked)> LatestScopeAsync(string userName)
    {
        await using var c = new Microsoft.Data.SqlClient.SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();
        await using var cmd = new Microsoft.Data.SqlClient.SqlCommand("""
            SELECT TOP (1) s.sessionScopeId FROM auth.sessionScope AS s JOIN auth.appUser AS u ON u.appUserId = s.appUserId
             WHERE u.userName = @u ORDER BY s.createdDateUtc DESC
            """, c);
        cmd.Parameters.AddWithValue("@u", userName);
        return await ScopeAsync((Guid)(await cmd.ExecuteScalarAsync())!);
    }

    private static async Task<(Guid Id, DateTime Created, DateTime Expires, DateTime? Revoked)> ScopeAsync(Guid id)
    {
        await using var c = new Microsoft.Data.SqlClient.SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();
        await using var cmd = new Microsoft.Data.SqlClient.SqlCommand(
            "SELECT createdDateUtc, expiresUtc, revokedUtc FROM auth.sessionScope WHERE sessionScopeId = @id", c);
        cmd.Parameters.AddWithValue("@id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        return (id, r.GetDateTime(0), r.GetDateTime(1), r.IsDBNull(2) ? null : r.GetDateTime(2));
    }

    [SqlFact]
    public async Task AnAddressThatMatchesNoPageShowsAMeritPage()
    {
        // Issue #43: routing's empty 404 used to reach the browser, which showed its own blank error page.
        var supervisor = await fixture.SignedInAsync("supervisor");
        var page = await supervisor.NoRedirect.GetAsync("/cases/list/no-such-page");
        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
        string html = await page.Content.ReadAsStringAsync();
        Assert.Contains("<html", html, StringComparison.Ordinal);
        Assert.Contains("Page not found", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/\"", html, StringComparison.Ordinal);

        // The API keeps answering without the HTML page.
        var api = await supervisor.NoRedirect.GetAsync("/api/v1/no-such-endpoint");
        Assert.Equal(HttpStatusCode.NotFound, api.StatusCode);
        Assert.DoesNotContain("<html", await api.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [SqlFact]
    public async Task AFormPostedWithoutScriptListsItsErrorsAtTheTop()
    {
        // Issue #46: the forms are novalidate, so the server's messages are the only ones; a whole-page post shows them in
        // an error summary that links to each field, and beside the field. The result form's page post lost them.
        var supervisor = await fixture.SignedInAsync("supervisor");
        string newPerson = await supervisor.Http.GetStringAsync("/people/new");
        Assert.Contains("novalidate", newPerson, StringComparison.Ordinal);
        Assert.Contains("<abbr title=\"required\" class=\"usa-hint usa-hint--required\">*</abbr>", newPerson, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"error-summary\"", newPerson, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"usa-error-message\"></span>", newPerson, StringComparison.Ordinal);

        var person = await supervisor.Http.PostAsync("/people/new", MeritClient.Form(newPerson, ("LastName", ""), ("Sex", "unknown"), ("Ethnicity", "unknown")));
        string html = await person.Content.ReadAsStringAsync();
        Assert.Contains("id=\"error-summary\"", html, StringComparison.Ordinal);
        Assert.Contains("<a class=\"usa-link\" href=\"#LastName\">Last name is required.</a>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"usa-error-message\" id=\"LastName-error\">Last name is required.</span>", html, StringComparison.Ordinal);
        Assert.Contains("aria-describedby=\"LastName-error\" aria-invalid=\"true\"", html, StringComparison.Ordinal);

        string newResult = await supervisor.Http.GetStringAsync($"/people/{fixture.Ids.MiaPersonId}/results/new");
        var result = await supervisor.Http.PostAsync($"/people/{fixture.Ids.MiaPersonId}/results",
            MeritClient.Form(newResult, ("ValueText", ""), ("ResultStatus", "final")));
        html = await result.Content.ReadAsStringAsync();
        Assert.Contains("<a class=\"usa-link\" href=\"#ValueText\">Result value is required.</a>", html, StringComparison.Ordinal);
        Assert.Contains("<a class=\"usa-link\" href=\"#LabOrganizationId-text\">Laboratory is required.</a>", html, StringComparison.Ordinal);
    }

    [SqlFact]
    public async Task TheAuditLogFiltersPagesAndReadsInWords()
    {
        // Issue #45: one record-id box over an unpaged list of codes and ids.
        var supervisor = await fixture.SignedInAsync("supervisor");
        var mia = fixture.Ids.MiaPersonId;
        string person = await supervisor.Http.GetStringAsync($"/people/{mia}");              // records a view of Mia
        Assert.Contains($"href=\"/admin/audit?entityId={mia}\">Audit log for this person</a>", person, StringComparison.Ordinal);

        // This record's views, in words, the record linked to its page; no address column when no row has one.
        string html = System.Net.WebUtility.HtmlDecode(await supervisor.Http.GetStringAsync($"/admin/audit?entityId={mia}&action=view"));
        Assert.Contains("<td>Viewed</td>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<td>view</td>", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"/people/{mia}\">{mia}</a>", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">Address</th>", html, StringComparison.Ordinal);
        Assert.Matches(@"Events 1.\d+ of [\d,]+, newest first\.", html);

        // Filters: a record type and an action; a day after today has nothing; a later page answers.
        string signIns = await supervisor.Http.GetStringAsync("/admin/audit?action=signIn&recordType=appUser");
        Assert.Contains("<td>Signed in</td>", signIns, StringComparison.Ordinal);
        Assert.DoesNotContain("<td>Viewed</td>", signIns, StringComparison.Ordinal);
        string tomorrow = DateTime.UtcNow.AddDays(2).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains("No events match.", await supervisor.Http.GetStringAsync($"/admin/audit?from={tomorrow}"), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await supervisor.NoRedirect.GetAsync("/admin/audit?page=2")).StatusCode);

        // The filtered view as CSV, and the export is itself in the ledger.
        var csv = await supervisor.NoRedirect.GetAsync($"/admin/audit.csv?entityId={mia}&action=view");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        var lines = (await csv.Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("When,User,Action,Record type,Record id,Rows,Address", lines[0]);
        Assert.All(lines.Skip(1), l => Assert.Contains($",Viewed,Person,{mia},", l, StringComparison.Ordinal));
        string exports = await supervisor.Http.GetStringAsync("/admin/audit?action=export&recordType=auditEvent");
        Assert.Contains("<td>Exported</td>", exports, StringComparison.Ordinal);
    }

    [SqlFact]
    public async Task TheMapsGetTheJurisdictionsCountyOutlines()
    {
        // Issue #48: the case and cluster maps drew points on a blank grid. The outlines come from MERIT, not a tile service.
        var supervisor = await fixture.SignedInAsync("supervisor");
        using var doc = System.Text.Json.JsonDocument.Parse(await supervisor.Http.GetStringAsync("/api/v1/reference/boundaries"));
        var counties = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(24, counties.Count);                                   // Maryland's 23 counties and Baltimore City
        Assert.Contains(counties, c => c.GetProperty("countyFips").GetString() == "510" && c.GetProperty("name").GetString() == "Baltimore City");
        Assert.All(counties, c =>
        {
            Assert.Contains(c.GetProperty("geometry").GetProperty("type").GetString(), new[] { "Polygon", "MultiPolygon" });
            Assert.InRange(c.GetProperty("labelLatitude").GetDecimal(), 37.8m, 39.8m);
            Assert.InRange(c.GetProperty("labelLongitude").GetDecimal(), -79.5m, -75.0m);
        });

        // Outlines are loaded at install: the web login cannot change them, and only a polygon is accepted.
        await using (var web = new SqlConnection(TestEnvironment.WebConnectionString))
        {
            await web.OpenAsync();
            var denied = await Assert.ThrowsAsync<SqlException>(() => SetBoundaryAsync(web, """{"type":"Polygon","coordinates":[]}"""));
            Assert.Equal(229, denied.Number);                                // EXECUTE permission denied
        }

        await using var admin = new SqlConnection(TestEnvironment.AdminConnectionString());
        await admin.OpenAsync();
        var refused = await Assert.ThrowsAsync<SqlException>(() => SetBoundaryAsync(admin, """{"type":"Point","coordinates":[-76.6,39.3]}"""));
        Assert.Equal(50000, refused.Number);

        static Task SetBoundaryAsync(SqlConnection c, string boundary)
        {
            var cmd = new SqlCommand("config.uspSetSubJurisdictionBoundary", c) { CommandType = System.Data.CommandType.StoredProcedure };
            cmd.Parameters.AddWithValue("@tenantCode", "MD-LEAD");
            cmd.Parameters.AddWithValue("@countyFips", "510");
            cmd.Parameters.AddWithValue("@boundary", boundary);
            return cmd.ExecuteNonQueryAsync();
        }
    }

    [SqlFact]
    public async Task ResultPagesAndLabelsUseWordsNotCodes()
    {
        // Issues #37 and #38: "Icp ms" for ICP-MS, file fields shown as lastName and zip5, and "analyzed ," with no date.
        var supervisor = await fixture.SignedInAsync("supervisor");
        string html = System.Net.WebUtility.HtmlDecode(await supervisor.Http.GetStringAsync($"/results/{fixture.Ids.MiaLabResultId}"));
        Assert.DoesNotContain("Icp ms", html, StringComparison.Ordinal);
        Assert.DoesNotContain("analyzed ,", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">lastName<", html, StringComparison.Ordinal);

        using var labels = System.Text.Json.JsonDocument.Parse(await supervisor.Http.GetStringAsync("/api/v1/reference/labels"));
        Assert.Equal("ICP-MS", labels.RootElement.GetProperty("codes").GetProperty("icpMs").GetString());
        Assert.Equal("Uncertain person match", labels.RootElement.GetProperty("codes").GetProperty("uncertainMatch").GetString());
        Assert.Equal("Last name", labels.RootElement.GetProperty("fields").GetProperty("lastName").GetString());
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

    [SqlFact]
    public async Task APartialCarriesTheHeadersNonceVerbatim()
    {
        // Issue #53: hx-csp rewrites a partial's nonce by matching the header's value in the raw text; a "+" written as
        // "&#x2B;" never matched, and the swapped-in elements lost their htmx attributes.
        var supervisor = await fixture.SignedInAsync("supervisor");
        for (int i = 0; i < 20; i++)
        {
            var partial = await supervisor.GetPartialAsync($"/people/{fixture.Ids.MiaPersonId}/edit");
            string csp = string.Join(' ', partial.Headers.GetValues("Content-Security-Policy"));
            string nonce = System.Text.RegularExpressions.Regex.Match(csp, "'nonce-([^']+)'").Groups[1].Value;
            string body = await partial.Content.ReadAsStringAsync();
            Assert.Contains($"hx-nonce=\"{nonce}\"", body, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A Razor expression printed as text: "@" right after a letter or digit, then a C# member access such as
    /// p.ProfileVersion or Model.Name. Email addresses (lower-case domains) do not match.
    /// </summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"(?<=[A-Za-z0-9])@[A-Za-z_]\w*\.[A-Z]\w*")]
    private static partial System.Text.RegularExpressions.Regex UnrenderedRazor();

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
