using System.Text.Json;

namespace Merit.IntegrationTests;

[Collection(MeritCollection.Name)]
public sealed class ClusterPickerTests(MeritFixture fixture)
{
    [SqlFact]
    public async Task PickerSearchesVisibleCasesByNameAndNumberAndKeepsOtherTenantsHidden()
    {
        var supervisor = await fixture.SignedInAsync("supervisor");
        foreach (var q in new[] { "Rivera", fixture.Ids.MiaCaseNumber })
        {
            var json = await supervisor.Http.GetStringAsync("/api/v1/cases/picker?q=" + Uri.EscapeDataString(q));
            Assert.Contains(fixture.Ids.MiaCaseNumber, json);
            using var result = JsonDocument.Parse(json);
            Assert.InRange(result.RootElement.GetArrayLength(), 1, 10);
        }
        var county = await fixture.SignedInAsync("cmbaltco");
        var hidden = await county.Http.GetStringAsync("/api/v1/cases/picker?q=" + Uri.EscapeDataString(fixture.Ids.MiaCaseNumber));
        Assert.DoesNotContain(fixture.Ids.MiaCaseNumber, hidden);
        Assert.Equal("[]", await supervisor.Http.GetStringAsync("/api/v1/cases/picker?q=x"));
    }
}
