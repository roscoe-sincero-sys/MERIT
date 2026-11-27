using System.Data;
using System.Net;
using System.Text.Json;
using Dapper;
using Merit.Domain.Ids;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Merit.IntegrationTests;

/// <summary>
/// End-to-end SQL + HTTP integration tests covering issues #327, #328, #329, #330, #332 (row history), #335, #337, and #338.
/// </summary>
[Collection(MeritCollection.Name)]
public sealed class LoggingWave2IntegrationTests(MeritFixture fixture)
{
    [SqlFact]
    public async Task Issue332And335_DataWriteAndAuditCommitOrRollbackTogetherWithRowHistory()
    {
        var supervisor = await fixture.SignedInAsync("supervisor");

        // 1. Successful create commits dbo.person + logs.auditEvent + logs.auditEventChange together.
        string newPersonPage = await supervisor.Http.GetStringAsync("/people/new");
        using var createResp = await supervisor.NoRedirect.PostAsync("/people/new",
            MeritClient.Form(newPersonPage,
                ("LastName", "TxGuarantee"),
                ("FirstName", "WaveTwo"),
                ("Dob", "2022-04-15"),
                ("Sex", "female"),
                ("Ethnicity", "unknown")));
        Assert.Equal(HttpStatusCode.Redirect, createResp.StatusCode);
        string location = createResp.Headers.Location!.ToString();
        Guid createdPersonId = Guid.Parse(location.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1]);

        await using var conn = new SqlConnection(TestEnvironment.AdminConnectionString());
        await conn.OpenAsync();

        var createdAudit = await conn.QuerySingleOrDefaultAsync<(long auditEventId, string? beforeJson, string? afterJson)>(
            """
            SELECT TOP (1) e.auditEventId, c.beforeJson, c.afterJson
              FROM logs.auditEvent AS e
              JOIN logs.auditEventChange AS c ON c.auditEventId = e.auditEventId AND c.isDeleted = 0
             WHERE e.entityType = 'person' AND e.entityId = @id AND e.eventAction = 'create'
             ORDER BY e.auditEventId DESC
            """, new { id = createdPersonId });
        Assert.True(createdAudit.auditEventId > 0);
        Assert.Null(createdAudit.beforeJson);
        Assert.NotNull(createdAudit.afterJson);
        Assert.Contains("TxGuarantee", createdAudit.afterJson, StringComparison.Ordinal);

        // 2. Rolled-back write (future DOB triggers THROW 51422 inside dbo.uspSavePerson) leaves ZERO person and ZERO audit rows.
        Guid failedPersonId = TimeOrderedId.New();
        var supSession = await conn.QuerySingleAsync<(Guid appUserId, Guid rootTenantId, Guid scopeId)>(
            """
            SELECT TOP (1) s.appUserId, s.rootTenantId, s.sessionScopeId
              FROM auth.sessionScope AS s
              JOIN auth.appUser AS u ON u.appUserId = s.appUserId
             WHERE u.userName = 'supervisor' AND s.revokedUtc IS NULL AND s.expiresUtc > SYSUTCDATETIME ()
             ORDER BY s.createdDateUtc DESC
            """);
        await conn.ExecuteAsync("EXEC sys.sp_set_session_context @key = N'scopeKey', @value = @v;", new { v = supSession.scopeId.ToString() });
        await conn.ExecuteAsync("EXEC sys.sp_set_session_context @key = N'AppUser', @value = N'supervisor';");

        var ex = await Assert.ThrowsAsync<SqlException>(() => conn.ExecuteAsync("dbo.uspSavePerson", new
        {
            personId = failedPersonId,
            lastName = "ShouldRollback",
            firstName = "NeverSaved",
            lastNameKey = "SHOULDROLLBACK",
            firstNameKey = "NEVERSAVED",
            dob = DateTime.UtcNow.Date.AddYears(1),
        }, commandType: CommandType.StoredProcedure));
        Assert.Equal(51422, ex.Number);

        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT (*) FROM dbo.person WHERE personId = @id", new { id = failedPersonId }));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT (*) FROM logs.auditEvent WHERE entityId = @id", new { id = failedPersonId }));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT (*) FROM logs.auditEventChange WHERE entityId = @id", new { id = failedPersonId }));

        // 3. Purge of logs.auditEventChange scrubs expired snapshot and preserves fresh snapshot.
        Guid oldChangeId = TimeOrderedId.New();
        await conn.ExecuteAsync(
            """
            INSERT logs.auditEventChange (auditEventChangeId, auditEventId, rootTenantId, owningTenantId, entityType, entityId, changedUtc, beforeJson, afterJson)
            VALUES (@id, @auditId, @root, @root, 'person', @personId, DATEADD (DAY, -400, SYSUTCDATETIME ()), N'{"lastName":"Old"}', N'{"lastName":"Older"}');
            """, new { id = oldChangeId, auditId = createdAudit.auditEventId, root = supSession.rootTenantId, personId = createdPersonId });

        int purged = await conn.ExecuteScalarAsync<int>("logs.uspPurgeAuditEventChanges", new { retentionDays = 365 }, commandType: CommandType.StoredProcedure);
        Assert.True(purged >= 1);
        var oldRow = await conn.QuerySingleAsync<(bool isDeleted, string? beforeJson, string? afterJson)>(
            "SELECT isDeleted, beforeJson, afterJson FROM logs.auditEventChange WHERE auditEventChangeId = @id", new { id = oldChangeId });
        Assert.True(oldRow.isDeleted);
        Assert.Null(oldRow.beforeJson);
        Assert.Null(oldRow.afterJson);
    }

    [SqlFact]
    public async Task Issue329_SearchLogPurgerScrubsExpiredRowsAndSummaryHidesSearchText()
    {
        await using var conn = new SqlConnection(TestEnvironment.AdminConnectionString());
        await conn.OpenAsync();
        var user = await conn.QuerySingleAsync<(Guid appUserId, Guid rootTenantId)>(
            "SELECT TOP (1) appUserId, rootTenantId FROM auth.appUser WHERE userName = 'supervisor' AND isDeleted = 0");

        Guid oldId = TimeOrderedId.New();
        Guid freshId = TimeOrderedId.New();
        await conn.ExecuteAsync(
            """
            INSERT logs.searchLog (searchLogId, appUserId, rootTenantId, searchText, resultCount, searchedUtc, clientAddress, existsElsewhere, searchSource)
            VALUES (@oldId, @u, @r, N'Sensitive Patient Name 1999', 0, DATEADD (DAY, -120, SYSUTCDATETIME ()), '198.51.100.10', 1, 'global'),
                   (@freshId, @u, @r, N'Fresh Search Query', 2, SYSUTCDATETIME (), '198.51.100.11', 0, 'global');
            """, new { oldId, freshId, u = user.appUserId, r = user.rootTenantId });

        int purged = await conn.ExecuteScalarAsync<int>("logs.uspPurgeSearchLog", new { retentionDays = 90 }, commandType: CommandType.StoredProcedure);
        Assert.True(purged >= 1);

        var oldAfter = await conn.QuerySingleAsync<(bool isDeleted, string searchText, string? clientAddress)>(
            "SELECT isDeleted, searchText, clientAddress FROM logs.searchLog WHERE searchLogId = @id", new { id = oldId });
        Assert.True(oldAfter.isDeleted);
        Assert.Equal(string.Empty, oldAfter.searchText);
        Assert.Null(oldAfter.clientAddress);

        var freshAfter = await conn.QuerySingleAsync<(bool isDeleted, string searchText)>(
            "SELECT isDeleted, searchText FROM logs.searchLog WHERE searchLogId = @id", new { id = freshId });
        Assert.False(freshAfter.isDeleted);
        Assert.Equal("Fresh Search Query", freshAfter.searchText);
    }

    [SqlFact]
    public async Task Issue337_SignInActivityEvaluatesPrecedingHourAcrossWindowBoundaryAndUsesMatchingEventTimestamps()
    {
        var admin = await fixture.SignedInAsync("admin");
        await using var conn = new SqlConnection(TestEnvironment.AdminConnectionString());
        await conn.OpenAsync();

        var targetUser = await conn.QuerySingleAsync<(Guid appUserId, Guid rootTenantId)>(
            "SELECT TOP (1) appUserId, rootTenantId FROM auth.appUser WHERE userName = 'epi' AND isDeleted = 0");
        string testIp = "198.51.100.237";
        DateTime now = DateTime.UtcNow;
        DateTime failTime = now.AddMinutes(-75);
        DateTime failInWindow = now.AddMinutes(-40);
        DateTime successNearBoundary = now.AddMinutes(-50);
        DateTime laterSuccess = now.AddMinutes(-5);

        await conn.ExecuteAsync(
            """
            INSERT logs.signInEvent (signInEventId, eventUtc, eventType, appUserId, rootTenantId, clientAddress)
            VALUES (NEWID (), @f1, 'passwordFailed', @u, @r, @ip),
                   (NEWID (), DATEADD (SECOND, 10, @f1), 'passwordFailed', @u, @r, @ip),
                   (NEWID (), DATEADD (SECOND, 20, @f1), 'passwordFailed', @u, @r, @ip),
                   (NEWID (), @s1, 'sessionStarted', @u, @r, @ip),
                   (NEWID (), @f2, 'passwordFailed', @u, @r, @ip),
                   (NEWID (), @s2, 'sessionStarted', @u, @r, @ip);
            """, new { f1 = failTime, s1 = successNearBoundary, f2 = failInWindow, s2 = laterSuccess, u = targetUser.appUserId, r = targetUser.rootTenantId, ip = testIp });

        using var csvResp = await admin.Http.GetAsync("/admin/sign-in-activity/activity.csv?hours=1");
        Assert.Equal(HttpStatusCode.OK, csvResp.StatusCode);
        string csv = await csvResp.Content.ReadAsStringAsync();

        Assert.Contains($"\"Signed in after failures\",\"{testIp}\"", csv, StringComparison.Ordinal);

        string addrLine = csv.Split('\n').First(l => l.StartsWith($"\"Failures by address\",\"{testIp}\",", StringComparison.Ordinal));
        Assert.Contains(failInWindow.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture), addrLine, StringComparison.Ordinal);
        Assert.DoesNotContain(laterSuccess.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture), addrLine, StringComparison.Ordinal);

        await conn.ExecuteAsync("UPDATE logs.signInEvent SET isDeleted = 1 WHERE clientAddress = @ip", new { ip = testIp });
    }

    [SqlFact]
    public async Task Issue327And328And330And338_AnomaliesStructuredExportUserActivityAndOperationalDiagnostics()
    {
        var admin = await fixture.SignedInAsync("admin");
        await using var conn = new SqlConnection(TestEnvironment.AdminConnectionString());
        await conn.OpenAsync();

        var sup = await conn.QuerySingleAsync<(Guid appUserId, Guid rootTenantId)>(
            "SELECT TOP (1) appUserId, rootTenantId FROM auth.appUser WHERE userName = 'supervisor' AND isDeleted = 0");

        // #327: Trigger midSessionIpDrift with two distinct IPs on one synthetic sessionReference.
        Guid driftSession = TimeOrderedId.New();
        await conn.ExecuteAsync(
            """
            INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, clientAddress, sessionReference)
            VALUES (@u, @r, 'view', 'person', '203.0.113.10', @s),
                   (@u, @r, 'view', 'person', '203.0.113.20', @s);
            """, new { u = sup.appUserId, r = sup.rootTenantId, s = driftSession });

        var firstScan = (await conn.QueryAsync<dynamic>("logs.uspRunSecurityFindingScan", new { hours = 24 }, commandType: CommandType.StoredProcedure)).ToList();
        Assert.Contains(firstScan, r => (string)r.ruleCode == "midSessionIpDrift");

        using var alertsHtml = await admin.Http.GetAsync("/admin/security-alerts?hours=24");
        Assert.Equal(HttpStatusCode.OK, alertsHtml.StatusCode);
        Assert.Contains("midSessionIpDrift", await alertsHtml.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var alertsJsonl = await admin.Http.GetAsync("/admin/security-alerts.jsonl?hours=24");
        Assert.Equal(HttpStatusCode.OK, alertsJsonl.StatusCode);
        Assert.Contains("midSessionIpDrift", await alertsJsonl.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // #328: Structured JSONL export with pseudonymization.
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        using var exportResp = await admin.Http.GetAsync($"/admin/log-export/download?format=jsonl&streams=audit,signin,search&from={today}&to={today}&pseudonymize=true");
        Assert.Equal(HttpStatusCode.OK, exportResp.StatusCode);
        string ndjson = await exportResp.Content.ReadAsStringAsync();
        var lines = ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        using var sample = JsonDocument.Parse(lines[0]);
        Assert.True(sample.RootElement.TryGetProperty("stream", out _));
        Assert.DoesNotContain("203.0.113.10", ndjson, StringComparison.Ordinal);

        // #330: Unified per-user activity summary + per-user usage filter + operational logs.
        using var userAct = await admin.Http.GetAsync($"/admin/users/{sup.appUserId}/activity?days=30");
        Assert.Equal(HttpStatusCode.OK, userAct.StatusCode);
        Assert.Contains("User activity:", await userAct.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var usageFiltered = await admin.Http.GetAsync($"/admin/usage?days=30&userId={sup.appUserId}");
        Assert.Equal(HttpStatusCode.OK, usageFiltered.StatusCode);

        // #338: Shared log writer redacts disallowed KeyParameters tokens.
        var p = new DynamicParameters();
        p.Add("@ProcedureName", "[dbo].[uspSyntheticTest]");
        p.Add("@KeyParameters", "password=SuperSecret123");
        p.Add("@StartDateUtc", DateTime.UtcNow);
        p.Add("@ReCreatedAfterRollback", false);
        p.Add("@ExecutionLogId", dbType: DbType.Int64, direction: ParameterDirection.Output);
        await conn.ExecuteAsync("logs.uspStartExecutionLogging", p, commandType: CommandType.StoredProcedure);
        long execId = p.Get<long>("@ExecutionLogId");
        string? storedKey = await conn.ExecuteScalarAsync<string?>("SELECT KeyParameters FROM logs.ExecutionLog WHERE ExecutionLogId = @id", new { id = execId });
        Assert.Equal("[redacted: disallowed diagnostic key]", storedKey);
        Assert.DoesNotContain("SuperSecret123", storedKey ?? string.Empty, StringComparison.Ordinal);
        await conn.ExecuteAsync("UPDATE logs.ExecutionLog SET isDeleted = 1 WHERE ExecutionLogId = @id", new { id = execId });

        // #330: Platform-admin operational logs & DDL integrity check.
        var platform = await fixture.SignedInAsync("platform");
        using var opsResp = await platform.Http.GetAsync("/admin/operations-log?hours=24");
        Assert.Equal(HttpStatusCode.OK, opsResp.StatusCode);
        string opsHtml = await opsResp.Content.ReadAsStringAsync();
        Assert.Contains("DDL audit integrity", opsHtml, StringComparison.Ordinal);
    }
}
