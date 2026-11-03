using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;

namespace Merit.IntegrationTests;

/// <summary>
/// Issue #180: the audit log is in the menu for audit.view in both headers. Supervisors hold audit.view without
/// tenant.administer, so the Users page's Audit log button was out of their reach in the default header.
/// </summary>
[Collection(MeritCollection.Name)]
public sealed partial class HeaderNavigationTests(MeritFixture fixture)
{
    [SqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothHeadersListTheAuditLogForAuditViewOnly(bool enhanced)
    {
        // The test host runs as Development, which turns the grouped header on; this host sets it explicitly.
        using var host = fixture.Factory.WithWebHostBuilder(b => b.UseSetting("Navigation:Enhanced", enhanced ? "true" : "false"));
        var supervisor = (await fixture.SignedInAsync("supervisor")).On(host);
        var caseManager = (await fixture.SignedInAsync("cmbalt")).On(host);

        string home = await supervisor.Http.GetStringAsync("/");
        Assert.Contains(enhanced ? "merit-nav-group" : "<ul class=\"merit-nav\">", home, StringComparison.Ordinal);
        Assert.Matches(AuditLink(), home);
        Assert.Matches(CurrentAuditLink(), await supervisor.Http.GetStringAsync("/admin/audit"));

        Assert.DoesNotMatch(AuditLink(), await caseManager.Http.GetStringAsync("/"));
    }

    [GeneratedRegex("<a href=\"/admin/audit\" aria-current=\"(page|false)\"[^>]*>Audit log</a>")]
    private static partial Regex AuditLink();

    [GeneratedRegex("<a href=\"/admin/audit\" aria-current=\"page\"[^>]*>Audit log</a>")]
    private static partial Regex CurrentAuditLink();
}
