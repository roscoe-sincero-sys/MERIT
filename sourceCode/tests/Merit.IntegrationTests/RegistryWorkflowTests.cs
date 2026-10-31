using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

[Collection(MeritCollection.Name)]
public sealed class RegistryWorkflowTests(MeritFixture fixture)
{
    [SqlFact]
    public async Task SamplesValidateImportRetryEditAndVoidWithConcurrency()
    {
        var client = await fixture.SignedInAsync("supervisor");
        var id = fixture.Ids.InvestigationId!.Value;
        await client.Http.GetStringAsync($"/investigations/{id}");
        var import = Guid.NewGuid();
        async Task<HttpResponseMessage> Upload(string csv)
        {
            using var form = new MultipartFormDataContent {
                { new StringContent(import.ToString()), "importId" },
                { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", "samples.csv" }
            };
            return await client.NoRedirect.PostAsync($"/investigations/{id}/samples/import", form);
        }
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        var tag = "Import-" + import.ToString("N");
        string Csv(string unit) => $"sampleType,sampleValue,unit,sampleDate,roomOrComponent\nxrfPaint,2,mg/cm2,2026-01-01,{tag}\nxrfPaint,1,{unit},2026-01-01,{tag}";
        async Task<int> Count()
        {
            await using var cmd = new SqlCommand("SELECT COUNT (*) FROM dbo.environmentalSample WHERE roomOrComponent = @tag", c); cmd.Parameters.AddWithValue("@tag", tag);
            return (int)(await cmd.ExecuteScalarAsync())!;
        }
        try
        {
            using var invalid = await Upload(Csv("ppm")); Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode); Assert.Equal(0, await Count());
            using var valid = await Upload(Csv("mg/cm2")); Assert.Equal(HttpStatusCode.OK, valid.StatusCode); Assert.Equal(2, await Count());
            using var retry = await Upload(Csv("mg/cm2")); Assert.Equal(HttpStatusCode.OK, retry.StatusCode); Assert.Equal(2, await Count());
            Guid sample; DateTime modified;
            await using (var cmd = new SqlCommand("SELECT TOP (1) environmentalSampleId, modifiedDateUtc FROM dbo.environmentalSample WHERE roomOrComponent = @tag", c))
            {
                cmd.Parameters.AddWithValue("@tag", tag); await using var r = await cmd.ExecuteReaderAsync(); Assert.True(await r.ReadAsync()); sample = r.GetGuid(0); modified = r.GetDateTime(1);
            }
            var fields = new Dictionary<string, string> { ["sampleType"] = "xrfPaint", ["sampleValue"] = "0.01", ["unit"] = "mg/cm2", ["sampleDate"] = "2026-01-01", ["roomOrComponent"] = tag,
                ["originalModifiedUtc"] = modified.ToString("O", CultureInfo.InvariantCulture) };
            using var edit = await client.NoRedirect.PostAsync($"/investigations/{id}/samples/{sample}", new FormUrlEncodedContent(fields)); Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
            using var stale = await client.NoRedirect.PostAsync($"/investigations/{id}/samples/{sample}", new FormUrlEncodedContent(fields)); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            await using (var cmd = new SqlCommand("SELECT sampleValue, exceedsStandard, modifiedDateUtc FROM dbo.environmentalSample WHERE environmentalSampleId = @id", c))
            {
                cmd.Parameters.AddWithValue("@id", sample); await using var r = await cmd.ExecuteReaderAsync(); Assert.True(await r.ReadAsync()); Assert.Equal(0.01m, r.GetDecimal(0)); Assert.False(r.GetBoolean(1)); modified = r.GetDateTime(2);
            }
            using var missing = await client.NoRedirect.PostAsync($"/investigations/{id}/samples/{sample}/void", new FormUrlEncodedContent(new Dictionary<string, string>())); Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
            using var gone = await client.NoRedirect.PostAsync($"/investigations/{id}/samples/{sample}/void", new FormUrlEncodedContent(new Dictionary<string, string> { ["reason"] = "Instrument entry error", ["originalModifiedUtc"] = modified.ToString("O", CultureInfo.InvariantCulture) })); Assert.Equal(HttpStatusCode.OK, gone.StatusCode);
            await using var verify = new SqlCommand("SELECT isDeleted FROM dbo.environmentalSample WHERE environmentalSampleId = @id", c); verify.Parameters.AddWithValue("@id", sample); Assert.True((bool)(await verify.ExecuteScalarAsync())!);
        }
        finally { await using var cmd = new SqlCommand("DELETE dbo.environmentalSample WHERE roomOrComponent = @tag", c); cmd.Parameters.AddWithValue("@tag", tag); await cmd.ExecuteNonQueryAsync(); }
    }

    [SqlFact]
    public async Task DismissalRequiresReasonAndOverdueFilterReturnsOnlyOverdueOpenTasks()
    {
        var client = await fixture.SignedInAsync("supervisor"); await client.Http.GetStringAsync("/");
        using var data = JsonDocument.Parse(await client.Http.GetStringAsync("/api/v1/tasks?overdue=true"));
        foreach (var row in data.RootElement.GetProperty("items").EnumerateArray()) {
            Assert.True(row.GetProperty("isOverdue").GetBoolean()); Assert.Equal("open", row.GetProperty("taskStatus").GetString());
        }
        using var denied = await client.NoRedirect.PostAsJsonAsync("/api/v1/tasks/bulk", new { taskIds = new[] { Guid.NewGuid() }, action = "dismiss" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, denied.StatusCode);
    }

    [SqlFact]
    public async Task DateFirstSearchFindsTheSamePersonAndPeopleFlowRequiresSearchFirst()
    {
        var client = await fixture.SignedInAsync("supervisor");
        var blank = await client.Http.GetStringAsync("/search"); Assert.DoesNotContain("href=\"/people/new\"", blank);
        var found = await client.GetPartialAsync("/search?q=2023-05-14%20Mia%20Rivera"); found.EnsureSuccessStatusCode();
        var html = await found.Content.ReadAsStringAsync(); Assert.Contains($"/people/{fixture.Ids.MiaPersonId}", html); Assert.Contains("href=\"/people/new\"", html);
    }
}
