using System.Text.Json;
using System.Text.RegularExpressions;

namespace Merit.IntegrationTests;

[Collection(MeritCollection.Name)]
public sealed class ProfileContextTests(MeritFixture fixture)
{
    [SqlFact]
    public async Task SwitchRetainsWorkingTenantAndAllowsOnlyLocalReturnPaths()
    {
        var client = await fixture.SignedInAsync("cmboth");
        var html = await client.Http.GetStringAsync("/account/profiles");
        var select = Regex.Match(html, "<select[^>]*id=\"actingTenantId\"[\\s\\S]*?</select>").Value;
        var original = Regex.Match(select, "<option[^>]*value=\"([^\"]+)\"[^>]*selected").Groups[1].Value;
        async Task<string?> ActingName()
        {
            using var json = JsonDocument.Parse(await client.Http.GetStringAsync("/api/v1/me"));
            return json.RootElement.GetProperty("actingTenantName").GetString();
        }
        try
        {
            using var first = await client.NoRedirect.PostAsync("/account/profiles/switch", new FormUrlEncodedContent(new Dictionary<string, string> {
                ["actingTenantId"] = fixture.Ids.BaltimoreCountyTenantId.ToString() }));
            Assert.Equal("/account/profiles", first.Headers.Location?.ToString());
            var tenant = await ActingName();
            Assert.Contains("County", tenant);
            await client.Http.GetStringAsync("/account/profiles");
            using var switched = await client.NoRedirect.PostAsync("/account/profiles/switch", new FormUrlEncodedContent(new Dictionary<string, string> {
                ["returnUrl"] = "/search?q=Rivera" }));
            Assert.Equal("/search?q=Rivera", switched.Headers.Location?.ToString());
            Assert.Equal(tenant, await ActingName());
            await client.Http.GetStringAsync("/account/profiles");
            using var external = await client.NoRedirect.PostAsync("/account/profiles/switch", new FormUrlEncodedContent(new Dictionary<string, string> {
                ["returnUrl"] = "https://example.com" }));
            Assert.Equal("/account/profiles", external.Headers.Location?.ToString());
            Assert.DoesNotContain("Return to previous page", await client.Http.GetStringAsync("/account/profiles?returnUrl=https://example.com"));
        }
        finally
        {
            await client.Http.GetStringAsync("/account/profiles");
            using var restored = await client.NoRedirect.PostAsync("/account/profiles/switch", new FormUrlEncodedContent(new Dictionary<string, string> { ["actingTenantId"] = original }));
        }
    }
}
