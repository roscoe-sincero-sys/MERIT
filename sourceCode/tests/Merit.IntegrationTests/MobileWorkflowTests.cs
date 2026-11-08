using System.Text.RegularExpressions;

namespace Merit.IntegrationTests;

[Collection(MeritCollection.Name)]
public sealed class MobileWorkflowTests(MeritFixture fixture)
{
    [SqlFact]
    public async Task AuditPageSizeIsBoundedAndPreservedWithoutChangingExportFilters()
    {
        var user=await fixture.SignedInAsync("supervisor");
        var html=await user.Http.GetStringAsync("/admin/audit?pageSize=25&action=view");
        Assert.Contains("data-audit-page-size=\"25\"",html);
        int rows=Regex.Matches(html,"<tr>").Count-1;
        Assert.InRange(rows,1,25);
        Assert.Contains("pageSize=25",html);
        Assert.Contains("action=view",html);
        Assert.DoesNotContain("<td>Signed in</td>",html);
        html=await user.Http.GetStringAsync("/admin/audit?pageSize=999999");
        Assert.Contains("data-audit-page-size=\"100\"",html);
        Assert.InRange(Regex.Matches(html,"<tr>").Count-1,1,100);
    }
}
