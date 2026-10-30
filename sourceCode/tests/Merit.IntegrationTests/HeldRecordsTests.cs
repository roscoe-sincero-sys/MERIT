using System.Net;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

/// <summary>
/// Issue #9: choosing a held group lists that group's rows only. Groups are a reason, reported value and sender together,
/// so two laboratories held for the same unknown code are two groups, and a bulk action on one never reaches the other.
/// </summary>
[Collection(MeritCollection.Name)]
public sealed class HeldRecordsTests(MeritFixture fixture)
{
    [SqlFact]
    public async Task BatchSummaryCountsRowsOnceAndGroupsUseTheSameScope()
    {
        var labA = Guid.NewGuid();
        var labB = Guid.NewGuid();
        var rows = new List<Guid>();
        try
        {
            Guid batch;
            await using (var c = new SqlConnection(TestEnvironment.AdminConnectionString()))
            {
                await c.OpenAsync();
                await HoldAsync(c, labA, "Scoped Lab A", [("missingDob", null)], rows);
                await HoldAsync(c, labB, "Scoped Lab B", [("unknownUnit", "unexpected")], rows);
                await using var cmd = new SqlCommand("""
                    INSERT dbo.stagingHoldReason (stagingHoldReasonId, rootTenantId, owningTenantId, stagingRowId, reasonCode, reasonMessage)
                    SELECT NEWID (), rootTenantId, owningTenantId, stagingRowId, 'unknownUnit', N'Another cause for the same row'
                      FROM dbo.stagingRow WHERE stagingRowId = @row;
                    SELECT intakeBatchId FROM dbo.stagingRow WHERE stagingRowId = @row;
                    """, c);
                cmd.Parameters.AddWithValue("@row", rows[0]);
                batch = (Guid)(await cmd.ExecuteScalarAsync())!;
            }
            var client = await fixture.SignedInAsync("supervisor");
            using var summary = await JsonAsync(client, $"/api/v1/held/summary?batch={batch}");
            Assert.Equal(1, summary.RootElement.GetProperty("totalCount").GetInt32());
            Assert.Equal("held-groups.csv", summary.RootElement.GetProperty("batchFileName").GetString());
            using var overview = await JsonAsync(client, $"/api/v1/held/overview?batch={batch}");
            Assert.Equal(1, overview.RootElement.GetProperty("totalCount").GetInt32());
            Assert.Equal(2, overview.RootElement.GetProperty("groups").GetArrayLength());
            using var groups = await JsonAsync(client, $"/api/v1/held/groups?batch={batch}");
            Assert.Equal(2, groups.RootElement.GetArrayLength());
            Assert.All(groups.RootElement.EnumerateArray(), g => Assert.Equal(labA, g.GetProperty("senderOrganizationId").GetGuid()));
            Assert.Equal(1, (await RowsAsync(client, $"batch={batch}")).Total);
        }
        finally { await RemoveAsync(labA, labB); }
    }

    [SqlFact]
    public async Task GenericReleaseCannotBypassMatchReviewOrForgeADecisionAsACorrection()
    {
        var lab = Guid.NewGuid();
        var rows = new List<Guid>();
        try
        {
            await using (var c = new SqlConnection(TestEnvironment.AdminConnectionString()))
            {
                await c.OpenAsync();
                await HoldAsync(c, lab, "Match safeguard lab", [("uncertainMatch", null)], rows);
            }
            var client = await fixture.SignedInAsync("supervisor");
            await client.Http.GetStringAsync("/");
            using var release = await client.NoRedirect.PostAsync("/api/v1/held/release", new StringContent(
                JsonSerializer.Serialize(new { stagingRowIds = rows, reason = (string?)null }), System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, release.StatusCode);
            using var outcome = JsonDocument.Parse(await release.Content.ReadAsStringAsync());
            Assert.Equal(0, outcome.RootElement.GetProperty("updated").GetInt32());
            using var correction = await client.NoRedirect.PutAsync($"/api/v1/held/rows/{rows[0]}/correction", new StringContent(
                "{\"corrections\":{\"matchNewPerson\":\"true\"}}", System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, correction.StatusCode);
            using var held = await JsonAsync(client, $"/api/v1/held/rows?reason=uncertainMatch&sender={lab}&exact=true");
            Assert.Equal(rows[0], Assert.Single(held.RootElement.GetProperty("items").EnumerateArray()).GetProperty("stagingRowId").GetGuid());
        }
        finally { await RemoveAsync(lab); }
    }

    [SqlFact]
    public async Task ChoosingAGroupListsOnlyThatSendersRowsWithThatExactValue()
    {
        string code = "T9-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var labA = Guid.NewGuid();
        var labB = Guid.NewGuid();
        var rows = new List<Guid>();
        try
        {
            await using (var c = new SqlConnection(TestEnvironment.AdminConnectionString()))
            {
                await c.OpenAsync();
                // Both laboratories held for the same unknown test code; both also held for a missing date of birth, which
                // has no reported value. Lab B's one other missing-DOB row carries a value, so it is a group of its own.
                await HoldAsync(c, labA, "Lab A " + code, [("unknownTestCode", code), ("unknownTestCode", code), ("unknownTestCode", code),
                    ("missingDob", null), ("missingDob", null)], rows);
                await HoldAsync(c, labB, "Lab B " + code, [("unknownTestCode", code), ("unknownTestCode", code), ("unknownTestCode", code),
                    ("unknownTestCode", code), ("unknownTestCode", code), ("missingDob", null), ("missingDob", "13/45/2020")], rows);
            }

            var supervisor = await fixture.SignedInAsync("supervisor");

            // The groups carry the sender's id, so the page can ask for one group exactly.
            using var groups = await JsonAsync(supervisor, "/api/v1/held/groups");
            var codeGroups = groups.RootElement.EnumerateArray().Where(g => g.GetProperty("reportedValue").GetString() == code).ToList();
            Assert.Equal(2, codeGroups.Count);
            var groupA = Assert.Single(codeGroups, g => g.GetProperty("senderOrganizationId").GetGuid() == labA);
            Assert.Equal(3, groupA.GetProperty("rowCount").GetInt32());

            // Lab A's unknown code: Lab A's 3 rows, not Lab B's 5.
            var a = await RowsAsync(supervisor, $"reason=unknownTestCode&value={code}&sender={labA}&exact=true");
            Assert.Equal(3, a.Total);
            Assert.All(a.Senders, s => Assert.Equal("Lab A " + code, s));

            // Lab A's missing date of birth: a group with no value means rows with no value, from Lab A only.
            var dob = await RowsAsync(supervisor, $"reason=missingDob&sender={labA}&exact=true");
            Assert.Equal(2, dob.Total);
            var dobB = await RowsAsync(supervisor, $"reason=missingDob&sender={labB}&exact=true");
            Assert.Equal(1, dobB.Total);

            // Without exact, a reason and value still list every sender's rows, as other callers expect.
            Assert.Equal(8, (await RowsAsync(supervisor, $"reason=unknownTestCode&value={code}")).Total);

            // An exact group needs its reason.
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await supervisor.NoRedirect.GetAsync($"/api/v1/held/rows?sender={labA}&exact=true")).StatusCode);
        }
        finally
        {
            await RemoveAsync(labA, labB);
        }
    }

    [SqlFact]
    public async Task EachHeldRowSaysWhichReasonsHoldIt()
    {
        // Issue #42: a row held for an uncertain match is decided on Match review; the page needs the codes to say so.
        var lab = Guid.NewGuid();
        var rows = new List<Guid>();
        try
        {
            await using (var c = new SqlConnection(TestEnvironment.AdminConnectionString()))
            {
                await c.OpenAsync();
                await HoldAsync(c, lab, "Lab T42 " + lab.ToString("N")[..6], [("uncertainMatch", null), ("missingDob", null)], rows);
            }

            var supervisor = await fixture.SignedInAsync("supervisor");
            foreach (var reason in new[] { "uncertainMatch", "missingDob" })
            {
                using var page = await JsonAsync(supervisor, $"/api/v1/held/rows?reason={reason}&sender={lab}&exact=true");
                var row = Assert.Single(page.RootElement.GetProperty("items").EnumerateArray());
                Assert.Equal([reason], row.GetProperty("reasonCodes").EnumerateArray().Select(r => r.GetString()));
            }
        }
        finally
        {
            await RemoveAsync(lab);
        }
    }

    [SqlFact]
    public async Task ASecondCorrectionKeepsTheFirst()
    {
        // Issue #10: each save replaced the row's corrections, so correcting the unit erased the corrected test code.
        string code = "T10-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var lab = Guid.NewGuid();
        var rows = new List<Guid>();
        try
        {
            await using (var c = new SqlConnection(TestEnvironment.AdminConnectionString()))
            {
                await c.OpenAsync();
                await HoldAsync(c, lab, "Lab " + code, [("unknownTestCode", code)], rows);
            }

            var supervisor = await fixture.SignedInAsync("supervisor");
            async Task CorrectAsync(object corrections)
            {
                var response = await supervisor.NoRedirect.PutAsync($"/api/v1/held/rows/{rows[0]}/correction",
                    new StringContent(JsonSerializer.Serialize(new { corrections }), System.Text.Encoding.UTF8, "application/json"));
                Assert.True(response.StatusCode == HttpStatusCode.NoContent, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            }

            async Task<Dictionary<string, string?>> CorrectedAsync()
            {
                using var page = await JsonAsync(supervisor, $"/api/v1/held/rows?reason=unknownTestCode&value={code}&sender={lab}&exact=true");
                var row = Assert.Single(page.RootElement.GetProperty("items").EnumerateArray());
                return row.GetProperty("corrected").Deserialize<Dictionary<string, string?>>()!;
            }

            await supervisor.Http.GetStringAsync("/");          // a page sets the XSRF cookie the API's writes need
            Assert.Empty(await CorrectedAsync());
            await CorrectAsync(new { loinc = "5671-3" });
            await CorrectAsync(new { unit = "ug/dL" });
            Assert.Equal(new Dictionary<string, string?> { ["loinc"] = "5671-3", ["unit"] = "ug/dL" }, await CorrectedAsync());

            // Clearing a box removes that correction only; the reported value applies again.
            await CorrectAsync(new Dictionary<string, string?> { ["loinc"] = null });
            Assert.Equal(new Dictionary<string, string?> { ["unit"] = "ug/dL" }, await CorrectedAsync());
        }
        finally
        {
            await RemoveAsync(lab);
        }
    }

    [SqlFact]
    public async Task HomeNamesEachUrgentHeldRecord()
    {
        // Issue #40: every urgent held row on Home read "Elevated result held at intake", with nothing saying which it was.
        string tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var lab = Guid.NewGuid();
        var rows = new List<Guid>();
        try
        {
            await using (var c = new SqlConnection(TestEnvironment.AdminConnectionString()))
            {
                await c.OpenAsync();
                await HoldAsync(c, lab, "Lab " + tag, [("missingDob", null)], rows);
                await using var cmd = new SqlCommand("""
                    UPDATE dbo.stagingRow
                       SET isUrgent = 1,
                           parsedJson = CONCAT (N'{"lastName":"Held', @tag, N'","firstName":"Hannah","loinc":"77307-7","value":"48.0","unit":"ug/dL"}')
                     WHERE stagingRowId = @row;
                    """, c);
                cmd.Parameters.AddWithValue("@tag", tag);
                cmd.Parameters.AddWithValue("@row", rows[0]);
                await cmd.ExecuteNonQueryAsync();
            }

            var supervisor = await fixture.SignedInAsync("supervisor");
            string home = WebUtility.HtmlDecode(await supervisor.Http.GetStringAsync("/"));
            Assert.Contains($"Held{tag}, Hannah: Lead 48.0 ug/dL", home, StringComparison.Ordinal);
            Assert.Contains($"Held at intake from Lab {tag}", home, StringComparison.Ordinal);
            Assert.Matches(@"href=""/app/held\?batch=[0-9a-f-]{36}""", home);
        }
        finally
        {
            await RemoveAsync(lab);
        }
    }

    /// <summary>A sender, one batch from it, and one held row per (reason, reported value), in Maryland.</summary>
    private static async Task HoldAsync(SqlConnection c, Guid senderId, string senderName, (string Reason, string? Value)[] holds, List<Guid> rows)
    {
        var batchId = Guid.NewGuid();
        await using (var cmd = new SqlCommand("""
            DECLARE @md UNIQUEIDENTIFIER = (SELECT tenantId FROM auth.tenant WHERE tenantCode = 'MD-LEAD');
            INSERT dbo.organization (organizationId, rootTenantId, owningTenantId, organizationName, organizationNameKey)
            VALUES (@sender, @md, @md, @name, UPPER (@name));
            INSERT dbo.intakeBatch (intakeBatchId, rootTenantId, owningTenantId, senderOrganizationId, sourceChannel, fileName,
                                    receivedUtc, batchStatus)
            VALUES (@batch, @md, @md, @sender, 'upload', N'held-groups.csv', SYSUTCDATETIME (), 'loaded');
            """, c))
        {
            cmd.Parameters.AddWithValue("@sender", senderId);
            cmd.Parameters.AddWithValue("@name", senderName);
            cmd.Parameters.AddWithValue("@batch", batchId);
            await cmd.ExecuteNonQueryAsync();
        }

        for (int i = 0; i < holds.Length; i++)
        {
            var rowId = Guid.NewGuid();
            rows.Add(rowId);
            await using var cmd = new SqlCommand("""
                DECLARE @md UNIQUEIDENTIFIER = (SELECT tenantId FROM auth.tenant WHERE tenantCode = 'MD-LEAD');
                INSERT dbo.stagingRow (stagingRowId, rootTenantId, owningTenantId, intakeBatchId, rowNumber, parsedJson, rowStatus,
                                       isUrgent, heldSinceUtc)
                VALUES (@row, @md, @md, @batch, @n, N'{"lastName":"Held"}', 'held', 0, SYSUTCDATETIME ());
                INSERT dbo.stagingHoldReason (stagingHoldReasonId, rootTenantId, owningTenantId, stagingRowId, reasonCode, reasonMessage,
                                              reportedValue)
                VALUES (NEWID (), @md, @md, @row, @reason, N'Held for the issue #9 test.', @value);
                """, c);
            cmd.Parameters.AddWithValue("@row", rowId);
            cmd.Parameters.AddWithValue("@batch", batchId);
            cmd.Parameters.AddWithValue("@n", i + 1);
            cmd.Parameters.AddWithValue("@reason", holds[i].Reason);
            cmd.Parameters.AddWithValue("@value", (object?)holds[i].Value ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// Removes the test's senders and everything held from them. Removed, not soft-deleted: the test database keeps its
    /// data between runs, and other suites pick their out-of-scope targets from whatever staging rows exist.
    /// </summary>
    private static async Task RemoveAsync(params Guid[] senders)
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();
        await using var cmd = new SqlCommand($"""
            DECLARE @b TABLE (intakeBatchId UNIQUEIDENTIFIER);
            INSERT @b SELECT intakeBatchId FROM dbo.intakeBatch WHERE senderOrganizationId IN ({string.Join(',', senders.Select(g => $"'{g}'"))});
            DELETE h FROM dbo.stagingHoldReason AS h JOIN dbo.stagingRow AS s ON s.stagingRowId = h.stagingRowId
             WHERE s.intakeBatchId IN (SELECT intakeBatchId FROM @b);
            DELETE dbo.stagingRow WHERE intakeBatchId IN (SELECT intakeBatchId FROM @b);
            DELETE dbo.intakeBatch WHERE intakeBatchId IN (SELECT intakeBatchId FROM @b);
            DELETE dbo.organization WHERE organizationId IN ({string.Join(',', senders.Select(g => $"'{g}'"))});
            """, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<JsonDocument> JsonAsync(MeritClient client, string url)
    {
        var response = await client.NoRedirect.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<(int Total, List<string?> Senders)> RowsAsync(MeritClient client, string query)
    {
        using var page = await JsonAsync(client, "/api/v1/held/rows?pageSize=500&" + query);
        var items = page.RootElement.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("senderName").GetString()).ToList();
        return (page.RootElement.GetProperty("totalCount").GetInt32(), items);
    }
}
