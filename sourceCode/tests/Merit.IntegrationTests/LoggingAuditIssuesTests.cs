using System.Net;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

/// <summary>
/// SQL-backed integration tests covering logging & audit issues #325, #326, #331, #332, and #333.
/// </summary>
[Collection(MeritCollection.Name)]
public sealed class LoggingAuditIssuesTests(MeritFixture fixture)
{
    private static async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] args)
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        object? result = await cmd.ExecuteScalarAsync();
        return result is DBNull or null ? default! : (T)result;
    }

    private static async Task<long> MaxAuditIdAsync()
        => await ScalarAsync<long>("SELECT COALESCE (MAX (auditEventId), 0) FROM logs.auditEvent");

    [SqlFact]
    public async Task PermissionDeniedIsAuditedAndExecutionLogAttributes404ToAppUser()
    {
        // #325: ASP.NET Core policy refusal writes permissionDenied to logs.auditEvent with traceId.
        var intake = await fixture.SignedInAsync("intake");
        long beforeDenied = await MaxAuditIdAsync();
        using (var resp = await intake.Http.GetAsync("/admin/audit"))
        {
            Assert.True(resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect);
        }

        int deniedCount = await ScalarAsync<int>("""
            SELECT COUNT (*) FROM logs.auditEvent
             WHERE auditEventId > @after AND eventAction = 'permissionDenied' AND entityType = 'auditEvent'
               AND traceId IS NOT NULL AND sessionReference IS NOT NULL
            """, ("@after", beforeDenied));
        Assert.Equal(1, deniedCount);

        // #326 + #325: SQL 51404 (NotFound) in logs.ExecutionLog is attributed to the application user, not meritWeb.
        var supervisor = await fixture.SignedInAsync("supervisor");
        long maxExecBefore = await ScalarAsync<long>("SELECT COALESCE (MAX (ExecutionLogId), 0) FROM logs.ExecutionLog");
        using (var notFound = await supervisor.Http.GetAsync($"/people/{Guid.NewGuid()}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        }

        string? createdBy = await ScalarAsync<string?>("""
            SELECT TOP (1) createdBy FROM logs.ExecutionLog
             WHERE ExecutionLogId > @after AND ErrorNumber = 51404
             ORDER BY ExecutionLogId DESC
            """, ("@after", maxExecBefore));
        Assert.NotNull(createdBy);
        Assert.Contains("supervisor", createdBy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("meritWeb", createdBy, StringComparison.OrdinalIgnoreCase);
    }

    [SqlFact]
    public async Task PreviouslyUnauditedPatientAndSecurityReadsNowWriteAuditEventsWithCorrelation()
    {
        var supervisor = await fixture.SignedInAsync("supervisor");
        Guid personId = fixture.Ids.MiaPersonId;

        // #331: person tabs & forms write view person with owningTenantId and #326 sessionReference + traceId.
        foreach (string path in new[]
        {
            $"/people/{personId}/results",
            $"/people/{personId}/places",
            $"/people/{personId}/edit",
            $"/people/{personId}/results/new",
        })
        {
            long after = await MaxAuditIdAsync();
            using var resp = await supervisor.Http.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            int count = await ScalarAsync<int>("""
                SELECT COUNT (*) FROM logs.auditEvent
                 WHERE auditEventId > @after AND eventAction = 'view' AND entityType = 'person'
                   AND entityId = @personId AND owningTenantId IS NOT NULL
                   AND sessionReference IS NOT NULL AND traceId IS NOT NULL
                """, ("@after", after), ("@personId", personId));
            Assert.True(count == 1, $"Expected 1 view person audit row for {path}, found {count}.");
        }

        // #331: case list, investigation list, match-review queue, and audit log view are audited for supervisor.
        foreach (var (path, entityType) in new[]
        {
            ("/cases", "exposureCase"),
            ("/investigations", "investigation"),
            ("/api/v1/match-review?pageSize=25", "stagingRow"),
            ("/admin/audit", "auditEvent"),
        })
        {
            long after = await MaxAuditIdAsync();
            using var resp = await supervisor.Http.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            int count = await ScalarAsync<int>("""
                SELECT COUNT (*) FROM logs.auditEvent
                 WHERE auditEventId > @after AND eventAction = 'view' AND entityType = @entityType
                   AND sessionReference IS NOT NULL AND traceId IS NOT NULL
                """, ("@after", after), ("@entityType", entityType));
            Assert.True(count >= 1, $"Expected >=1 view {entityType} audit row for {path}, found {count}.");
        }

        // #331: sign-in activity view is audited for an administrator / security reviewer.
        var admin = await fixture.SignedInAsync("admin");
        long afterActivity = await MaxAuditIdAsync();
        using (var activityResp = await admin.Http.GetAsync("/admin/sign-in-activity"))
        {
            Assert.Equal(HttpStatusCode.OK, activityResp.StatusCode);
        }

        int activityCount = await ScalarAsync<int>("""
            SELECT COUNT (*) FROM logs.auditEvent
             WHERE auditEventId > @after AND eventAction = 'view' AND entityType = 'signInActivity'
               AND sessionReference IS NOT NULL AND traceId IS NOT NULL
            """, ("@after", afterActivity));
        Assert.True(activityCount >= 1);

        // #331 + #326: case picker logs to both logs.searchLog (with sessionReference & searchSource) and logs.auditEvent.
        long afterPicker = await MaxAuditIdAsync();
        using (var pickerResp = await supervisor.Http.GetAsync("/api/v1/cases/picker?q=Ri"))
        {
            Assert.Equal(HttpStatusCode.OK, pickerResp.StatusCode);
        }

        int pickerAudit = await ScalarAsync<int>("""
            SELECT COUNT (*) FROM logs.auditEvent
             WHERE auditEventId > @after AND eventAction = 'search' AND entityType = 'exposureCase'
            """, ("@after", afterPicker));
        Assert.Equal(1, pickerAudit);

        int pickerSearchLog = await ScalarAsync<int>("""
            SELECT COUNT (*) FROM logs.searchLog
             WHERE searchText = N'Ri' AND searchSource = 'casePicker' AND sessionReference IS NOT NULL
               AND searchedUtc >= DATEADD (MINUTE, -2, SYSUTCDATETIME ())
            """);
        Assert.True(pickerSearchLog >= 1);
    }

    [SqlFact]
    public async Task ResultsListRecordsFilterAndManifestAndTimelineDeduplicatesAndMarksIncidentalCaseReads()
    {
        var supervisor = await fixture.SignedInAsync("supervisor");

        // #333: Results list records non-PHI filterJson and logs.auditEventRecord manifest.
        long afterList = await MaxAuditIdAsync();
        using (var listResp = await supervisor.Http.GetAsync("/api/v1/results?pageSize=5"))
        {
            Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        }

        long listAuditId = await ScalarAsync<long>("""
            SELECT TOP (1) auditEventId FROM logs.auditEvent
             WHERE auditEventId > @after AND eventAction = 'view' AND entityType = 'labResult'
               AND filterJson IS NOT NULL
             ORDER BY auditEventId DESC
            """, ("@after", afterList));
        Assert.True(listAuditId > 0);

        int manifestRows = await ScalarAsync<int>(
            "SELECT COUNT (*) FROM logs.auditEventRecord WHERE auditEventId = @id AND isDeleted = 0",
            ("@id", listAuditId));
        Assert.True(manifestRows > 0, "Expected manifest rows in logs.auditEventRecord for results list.");

        // Audit records screen serves the manifest for authorized audit.view callers.
        using (var manifestPage = await supervisor.Http.GetAsync($"/admin/audit/{listAuditId}/records"))
        {
            Assert.Equal(HttpStatusCode.OK, manifestPage.StatusCode);
        }

        // #333: Timeline writes 1 deduplicated view person row and marks side-effect exposureCase reads as incidental.
        long afterTimeline = await MaxAuditIdAsync();
        using (var timelineResp = await supervisor.Http.GetAsync($"/api/v1/people/{fixture.Ids.MiaPersonId}/timeline"))
        {
            Assert.Equal(HttpStatusCode.OK, timelineResp.StatusCode);
        }

        int personViewRows = await ScalarAsync<int>("""
            SELECT COUNT (*) FROM logs.auditEvent
             WHERE auditEventId > @after AND eventAction = 'view' AND entityType = 'person' AND entityId = @personId
            """, ("@after", afterTimeline), ("@personId", fixture.Ids.MiaPersonId));
        Assert.Equal(1, personViewRows);

        int incidentalCaseRows = await ScalarAsync<int>("""
            SELECT COUNT (*) FROM logs.auditEvent
             WHERE auditEventId > @after AND eventAction = 'view' AND entityType = 'exposureCase' AND viewKind = 'incidental'
            """, ("@after", afterTimeline));
        Assert.True(incidentalCaseRows >= 1, "Expected side-effect case reads on timeline to be marked viewKind='incidental'.");

        // Correlation check (#326): sessionReference on logs.auditEvent joins to logs.signInEvent.sessionReference.
        int joinedSignIn = await ScalarAsync<int>("""
            SELECT COUNT (*) FROM logs.auditEvent AS a
              JOIN logs.signInEvent AS s ON s.sessionReference = a.sessionReference
             WHERE a.auditEventId > @after AND a.entityType = 'person' AND a.entityId = @personId
            """, ("@after", afterTimeline), ("@personId", fixture.Ids.MiaPersonId));
        Assert.True(joinedSignIn >= 1, "Expected logs.auditEvent.sessionReference to join to logs.signInEvent.sessionReference.");
    }
}
