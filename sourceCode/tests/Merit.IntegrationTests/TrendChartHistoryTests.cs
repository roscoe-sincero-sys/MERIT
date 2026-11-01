using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

[Collection(MeritCollection.Name)]
public sealed class TrendChartHistoryTests(MeritFixture fixture)
{
    [SqlFact]
    public async Task HistoricalPointsHaveDetailLinksAndCurrentReferenceWarning()
    {
        var id = Guid.NewGuid();
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();
        async Task Sql(string text)
        {
            await using var cmd = new SqlCommand(text, c);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@original", fixture.Ids.MiaLabResultId);
            await cmd.ExecuteNonQueryAsync();
        }
        try
        {
            await Sql("""
                INSERT dbo.labResult (labResultId, rootTenantId, owningTenantId, personId, analyteSpecimenId, collectedDate, receivedUtc, resultStatus,
                    valueReported, unitReported, valueCanonical, unitCanonical, labOrganizationId, reportedSnapshotJson, intakeBatchId, population)
                SELECT @id, rootTenantId, owningTenantId, personId, analyteSpecimenId, '1900-01-01', receivedUtc, resultStatus,
                    45, unitReported, 45, unitCanonical, labOrganizationId, N'{}', intakeBatchId, population
                FROM dbo.labResult WHERE labResultId = @original;
                """);
            var client = await fixture.SignedInAsync("supervisor");
            var html = await client.Http.GetStringAsync($"/people/{fixture.Ids.MiaPersonId}");
            Assert.Contains("Some plotted results predate a displayed level", html);
            Assert.Contains("they do not reconstruct historical classifications", html);
            Assert.Contains("aria-label=\"Current reference levels\"", html);
            Assert.Contains($"href=\"/results/{id}\" aria-label=\"1900-01-01:", html);
            Assert.Contains("(effective ", html);
        }
        finally { await Sql("DELETE dbo.labResult WHERE labResultId = @id"); }
    }
}
