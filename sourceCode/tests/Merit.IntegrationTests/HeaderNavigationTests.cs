using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;

namespace Merit.IntegrationTests;

/// <summary>
/// Issue #184: the grouped header is the only header, whatever a leftover Navigation:Enhanced setting says. Issue #180:
/// it lists the audit log for audit.view, which supervisors hold without tenant.administer.
/// </summary>
[Collection(MeritCollection.Name)]
public sealed partial class HeaderNavigationTests(MeritFixture fixture)
{
    [SqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheGroupedHeaderIsTheOnlyHeaderAndListsTheAuditLogForAuditViewOnly(bool leftoverSetting)
    {
        // The setting is gone; an old deployment that still sets it, either way, gets the same header.
        using var host = fixture.Factory.WithWebHostBuilder(b => b.UseSetting("Navigation:Enhanced", leftoverSetting ? "true" : "false"));
        var supervisor = (await fixture.SignedInAsync("supervisor")).On(host);
        var caseManager = (await fixture.SignedInAsync("cmbalt")).On(host);

        string home = await supervisor.Http.GetStringAsync("/");
        Assert.Contains("merit-nav-group", home, StringComparison.Ordinal);
        Assert.DoesNotContain("data-enhanced-navigation", home, StringComparison.Ordinal);
        Assert.Matches(AuditLink(), home);
        Assert.Matches(CurrentAuditLink(), await supervisor.Http.GetStringAsync("/admin/audit"));

        Assert.DoesNotMatch(AuditLink(), await caseManager.Http.GetStringAsync("/"));
    }

    [GeneratedRegex("<a href=\"/admin/audit\" aria-current=\"(page|false)\"[^>]*>Audit log</a>")]
    private static partial Regex AuditLink();

    [GeneratedRegex("<a href=\"/admin/audit\" aria-current=\"page\"[^>]*>Audit log</a>")]
    private static partial Regex CurrentAuditLink();
}
