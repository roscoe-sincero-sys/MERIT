using System.Net;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

[Collection(MeritCollection.Name)]
public sealed class FormDraftTests(MeritFixture fixture)
{
    [SqlFact]
    public async Task InvalidActivityAndInvestigationKeepSubmittedDrafts()
    {
        var client = await fixture.SignedInAsync("supervisor");
        await client.Http.GetStringAsync("/");
        string future = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd");
        using var activity = await client.NoRedirect.PostAsync($"/cases/{fixture.Ids.MiaCaseId}/activities",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["activityType"] = "contactAttempt", ["activityDate"] = future,
                ["note"] = "Retain this activity draft & its date",
            }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, activity.StatusCode);
        string html = WebUtility.HtmlDecode(await activity.Content.ReadAsStringAsync());
        Assert.Contains("Retain this activity draft & its date</textarea>", html, StringComparison.Ordinal);
        Assert.Contains($"name=\"activityDate\" type=\"date\" value=\"{future}\"", html, StringComparison.Ordinal);

        Assert.NotNull(fixture.Ids.InvestigationId);
        using var investigation = await client.NoRedirect.PostAsync($"/investigations/{fixture.Ids.InvestigationId}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["investigationStatus"] = "closed", ["scheduledDate"] = "2026-01-15",
                ["findings"] = "Retain investigation findings", ["referralOutcome"] = "Pending callback",
                ["rowVersion"] = "0000000000000000",
            }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, investigation.StatusCode);
        html = WebUtility.HtmlDecode(await investigation.Content.ReadAsStringAsync());
        Assert.Contains("Retain investigation findings</textarea>", html, StringComparison.Ordinal);
        Assert.Contains("value=\"Pending callback\"", html, StringComparison.Ordinal);
        Assert.Matches("value=\"closed\" selected(?:=\"selected\")?", html);

        using var sample = await client.NoRedirect.PostAsync($"/investigations/{fixture.Ids.InvestigationId}/samples",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["sampleType"] = "dustWipe", ["sampleValue"] = "-1", ["sampleDate"] = "2026-01-01",
                ["unit"] = "ug/ft2", ["notes"] = "Keep sample notes", ["roomOrComponent"] = "Kitchen",
            }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, sample.StatusCode);
        html = WebUtility.HtmlDecode(await sample.Content.ReadAsStringAsync());
        Assert.Contains("value=\"Keep sample notes\"", html, StringComparison.Ordinal);
        Assert.Contains("value=\"Kitchen\"", html, StringComparison.Ordinal);
        Assert.Contains("value=\"-1\"", html, StringComparison.Ordinal);
    }

    [SqlFact]
    public async Task ResultValidationRehydratesLookupNamesAndPreservesCreatinine()
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();
        await using var cmd = new SqlCommand("""
            SELECT TOP (1) analyteId, analyteSpecimenId FROM config.analyteSpecimen WHERE requiresCreatinine = 1 AND isDeleted = 0;
            SELECT TOP (1) organizationId, organizationName FROM dbo.organization WHERE isDeleted = 0;
            SELECT TOP (1) providerId, lastName FROM dbo.provider WHERE isDeleted = 0;
            """, c);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Guid analyte = r.GetGuid(0), specimen = r.GetGuid(1);
        Assert.True(await r.NextResultAsync());
        Assert.True(await r.ReadAsync());
        Guid lab = r.GetGuid(0);
        string labName = r.GetString(1);
        Assert.True(await r.NextResultAsync());
        Assert.True(await r.ReadAsync());
        Guid provider = r.GetGuid(0);
        string providerName = r.GetString(1);

        var client = await fixture.SignedInAsync("supervisor");
        await client.Http.GetStringAsync("/");
        string fields = await client.Http.GetStringAsync($"/results/fields?analyteId={analyte}&analyteSpecimenId={specimen}&creatinine=1.25");
        Assert.Contains("name=\"Creatinine\" value=\"1.25\"", fields, StringComparison.Ordinal);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/people/{fixture.Ids.MiaPersonId}/results");
        request.Headers.Add("HX-Request", "true");
        request.Headers.Add("HX-Request-Type", "partial");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["AnalyteId"] = analyte.ToString(), ["AnalyteSpecimenId"] = specimen.ToString(),
                ["Creatinine"] = "1.25", ["LabOrganizationId"] = lab.ToString(), ["OrderingProviderId"] = provider.ToString(),
            });
        using var response = await client.NoRedirect.SendAsync(request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        string html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains($"value=\"{labName}\"", html, StringComparison.Ordinal);
        Assert.Contains(providerName, html, StringComparison.Ordinal);
        Assert.Contains("name=\"Creatinine\" value=\"1.25\"", html, StringComparison.Ordinal);
    }

    [SqlFact]
    public async Task RelationshipLookupRehydratesAuthorizedSelectionAndHidesOutOfScopeNames()
    {
        var client = await fixture.SignedInAsync("supervisor");
        await client.Http.GetStringAsync("/");
        async Task<HttpResponseMessage> InvalidRelationship(MeritClient user) => await user.NoRedirect.PostAsync(
            $"/people/{fixture.Ids.MiaPersonId}/relationships", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Role"] = "", ["RelatedPersonId"] = fixture.Ids.MiaPersonId.ToString(),
            }));
        using var response = await InvalidRelationship(client);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        string html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Matches("id=\"relationship-RelatedPersonId-text\"[^>]*value=\"[^\"]+\"", html);

        var county = await fixture.SignedInAsync("cmbaltco");
        await county.Http.GetStringAsync("/");
        using var hidden = await InvalidRelationship(county);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, hidden.StatusCode);
        html = await hidden.Content.ReadAsStringAsync();
        Assert.DoesNotContain($"value=\"{fixture.Ids.MiaPersonId}\"", html, StringComparison.OrdinalIgnoreCase);
    }
}
