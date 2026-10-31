using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

[Collection(MeritCollection.Name)]
public sealed class WorkflowRecoveryTests(MeritFixture fixture)
{
    [SqlFact]
    public async Task RepeatedTaskCompletionIsIdempotentAndReopenRestoresIt()
    {
        var task = Guid.NewGuid();
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();
        try
        {
            await using (var cmd = new SqlCommand("""
                INSERT dbo.workTask (workTaskId, rootTenantId, owningTenantId, queueCode, title)
                SELECT @id, tenantId, tenantId, 'caseFollowUp', N'Idempotence test' FROM auth.tenant WHERE tenantCode = 'MD-LEAD';
                """, c))
            {
                cmd.Parameters.AddWithValue("@id", task);
                await cmd.ExecuteNonQueryAsync();
            }
            var client = await fixture.SignedInAsync("supervisor");
            await client.Http.GetStringAsync("/");
            async Task<int> Act(string action)
            {
                using var response = await client.Http.PostAsJsonAsync("/api/v1/tasks/bulk", new { taskIds = new[] { task }, action });
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return json.RootElement.GetProperty("updated").GetInt32();
            }
            async Task<byte[]> Version()
            {
                await using var cmd = new SqlCommand("SELECT rowVersion FROM dbo.workTask WHERE workTaskId = @id", c);
                cmd.Parameters.AddWithValue("@id", task);
                return (byte[])(await cmd.ExecuteScalarAsync())!;
            }
            Assert.Equal(1, await Act("complete"));
            var completed = await Version();
            Assert.Equal(0, await Act("complete"));
            Assert.Equal(completed, await Version());
            Assert.Equal(1, await Act("reopen"));
            Assert.Equal(0, await Act("reopen"));
        }
        finally
        {
            await using var cmd = new SqlCommand("DELETE dbo.workTask WHERE workTaskId = @id", c);
            cmd.Parameters.AddWithValue("@id", task);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [SqlFact]
    public async Task EmptyTaskOffsetsReturnTheRealTotalAndLastValidPage()
    {
        var client = await fixture.SignedInAsync("supervisor");
        using var first = JsonDocument.Parse(await client.Http.GetStringAsync("/api/v1/tasks?pageSize=1"));
        int total = first.RootElement.GetProperty("totalCount").GetInt32();
        Assert.True(total > 0);
        int auditCount = await fixture.ListAuditCountAsync("workTask");
        using var last = JsonDocument.Parse(await client.Http.GetStringAsync("/api/v1/tasks?page=100000&pageSize=1"));
        Assert.Equal(auditCount + 1, await fixture.ListAuditCountAsync("workTask"));
        Assert.Equal(total, last.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(total, last.RootElement.GetProperty("pageNumber").GetInt32());
        Assert.Single(last.RootElement.GetProperty("items").EnumerateArray());

        using var empty = JsonDocument.Parse(await client.Http.GetStringAsync("/api/v1/tasks?queue=missing-queue&page=100000"));
        Assert.Equal(auditCount + 2, await fixture.ListAuditCountAsync("workTask"));
        Assert.Equal(0, empty.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, empty.RootElement.GetProperty("pageNumber").GetInt32());
        Assert.Empty(empty.RootElement.GetProperty("items").EnumerateArray());
    }

    [SqlFact]
    public async Task HtmxPermissionDenialIsMarkedAndStatusPagesDoNotReplaceIt()
    {
        var client = await fixture.SignedInAsync("cmbalt");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/tenants");
        request.Headers.Add("HX-Request", "true");
        request.Headers.Add("HX-Request-Type", "partial");
        using var response = await client.NoRedirect.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("true", response.Headers.GetValues("X-Merit-Denied").Single());
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [SqlFact]
    public async Task AnonymousHtmxRequestsDoNotRedirectAwayFromDrafts()
    {
        using var client = fixture.Anonymous();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/people/new");
        request.Headers.Add("HX-Request", "true");
        using var response = await client.NoRedirect.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("HX-Redirect"));
        Assert.Null(response.Headers.Location);
    }

    [SqlFact]
    public async Task DialogExceptionsTargetTheDialogAndPageExceptionsTargetThePage()
    {
        var client = await fixture.SignedInAsync("supervisor");
        foreach (bool dialog in new[] { false, true })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/people/{Guid.NewGuid()}");
            request.Headers.Add("HX-Request", "true");
            request.Headers.Add("HX-Request-Type", "partial");
            if (dialog) request.Headers.Add("X-Merit-Dialog", "true");
            using var response = await client.Http.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(dialog ? "#dialog-alert-region" : "#alert-region", response.Headers.GetValues("HX-Retarget").Single());
            Assert.Contains("role=\"alert\"", await response.Content.ReadAsStringAsync());
        }
    }
}
