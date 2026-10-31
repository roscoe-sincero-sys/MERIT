using System.Net;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

[Collection(MeritCollection.Name)]
public sealed class FormDraftTests(MeritFixture fixture)
{
    [SqlFact]
    public async Task LookupRedisplayRejectsMergedPeopleAndWrongOrganizationTypes()
    {
        Guid merged = Guid.NewGuid();
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();
        await using var seed = new SqlCommand("""
            INSERT dbo.person (personId, rootTenantId, owningTenantId, lastName, lastNameKey, mergedIntoPersonId)
            SELECT @merged, rootTenantId, owningTenantId, N'MergedLookupTest', N'MERGEDLOOKUPTEST', personId
              FROM dbo.person WHERE personId = @survivor;
            SELECT TOP (1) o.organizationId FROM dbo.organization AS o
              JOIN dbo.organizationType AS t ON t.organizationId = o.organizationId AND t.isDeleted = 0
             WHERE t.organizationTypeCode = 'employer' AND o.isDeleted = 0
               AND NOT EXISTS (SELECT 1 FROM dbo.organizationType AS lab WHERE lab.organizationId = o.organizationId
                                AND lab.organizationTypeCode = 'laboratory' AND lab.isDeleted = 0);
            SELECT TOP (1) s.organizationSiteId, s.locationId, o.organizationName
              FROM dbo.organizationSite AS s JOIN dbo.organization AS o ON o.organizationId = s.organizationId
              JOIN dbo.organizationType AS t ON t.organizationId = o.organizationId AND t.isDeleted = 0
             WHERE t.organizationTypeCode = 'childCare' AND s.isDeleted = 0 AND s.endDate IS NULL
               AND NOT EXISTS (SELECT 1 FROM dbo.organizationType AS school WHERE school.organizationId = o.organizationId
                                AND school.organizationTypeCode = 'school' AND school.isDeleted = 0);
            """, c);
        seed.Parameters.AddWithValue("@merged", merged);
        seed.Parameters.AddWithValue("@survivor", fixture.Ids.MiaPersonId);
        try
        {
            Guid employer;
            string site, siteName;
            await using (var r = await seed.ExecuteReaderAsync())
            {
                Assert.True(await r.ReadAsync()); employer = r.GetGuid(0);
                Assert.True(await r.NextResultAsync()); Assert.True(await r.ReadAsync());
                site = $"{r.GetGuid(0)}:{r.GetGuid(1)}"; siteName = r.GetString(2);
            }
            var user = await fixture.SignedInAsync("supervisor");
            await user.Http.GetStringAsync("/");
            using var relationship = await user.NoRedirect.PostAsync($"/people/{fixture.Ids.MiaPersonId}/relationships",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["Role"] = "", ["RelatedPersonId"] = merged.ToString() }));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, relationship.StatusCode);
            Assert.DoesNotContain("MergedLookupTest", await relationship.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using var request = new HttpRequestMessage(HttpMethod.Post, $"/people/{fixture.Ids.MiaPersonId}/results");
            request.Headers.Add("HX-Request", "true");
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["LabOrganizationId"] = employer.ToString() });
            using var result = await user.NoRedirect.SendAsync(request);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, result.StatusCode);
            Assert.DoesNotContain($"value=\"{employer}\"", await result.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

            async Task<string> RedisplaySite(string role)
            {
                using var response = await user.NoRedirect.PostAsync($"/people/{fixture.Ids.MiaPersonId}/places/add",
                    new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["Role"] = role, ["OrganizationSite"] = site, ["StartDate"] = "2026-02-01", ["EndDate"] = "2026-01-01",
                    }));
                Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
                return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
            }
            Assert.DoesNotContain(siteName, await RedisplaySite("school"), StringComparison.Ordinal);
            Assert.Contains($"value=\"{siteName}\"", await RedisplaySite("childCare"), StringComparison.Ordinal);
        }
        finally
        {
            await using var cleanup = new SqlCommand("DELETE dbo.person WHERE personId = @merged;", c);
            cleanup.Parameters.AddWithValue("@merged", merged);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

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
        Assert.Contains("name=\"rowVersion\" value=\"0000000000000000\"", html, StringComparison.Ordinal);
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
            SELECT TOP (1) o.organizationId, o.organizationName FROM dbo.organization AS o JOIN dbo.organizationType AS t ON t.organizationId = o.organizationId WHERE o.isDeleted = 0 AND o.isActive = 1 AND t.organizationTypeCode = 'laboratory' AND t.isDeleted = 0;
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
        string cleared = await client.Http.GetStringAsync("/results/fields?creatinine=1.25");
        Assert.Contains("Creatinine was cleared", cleared, StringComparison.Ordinal);
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
