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
        // Use "epi" (lacks audit.view) — never "intake", whose MFA enrollment is owned by FirstSignInTests.
        var epi = await fixture.SignedInAsync("epi");
        long beforeDenied = await MaxAuditIdAsync();
        using (var resp = await epi.NoRedirect.GetAsync("/admin/audit"))
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

    [SqlFact]
    public async Task PatientDataWritesAuditResidenceChangeHeldRejectionAndResultCaseOpening()
    {
        // #332 behavioral tests: residence add via web endpoint, held-row batch rejection (with manifest),
        // qualifying lab result + automatic case opening via web endpoint, and manifest retention purger.
        var supervisor = await fixture.SignedInAsync("supervisor");

        // Create a fresh synthetic child so we exercise real residence addition + automatic case opening cleanly.
        string newPersonPage = await supervisor.Http.GetStringAsync("/people/new");
        string suffix = Guid.NewGuid().ToString("N")[..6];
        Guid newPersonId;
        using (var createPerson = await supervisor.NoRedirect.PostAsync("/people/new",
            MeritClient.Form(newPersonPage,
                ("LastName", "AuditChild" + suffix),
                ("FirstName", "Test"),
                ("Dob", "2023-06-01"),
                ("Sex", "female"))))
        {
            Assert.Equal(HttpStatusCode.Redirect, createPerson.StatusCode);
            string loc = createPerson.Headers.Location!.ToString();
            newPersonId = Guid.Parse(loc.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1]);
        }

        // 1. Primary residence add via POST /people/{id}/places/add as supervisor writes update person with appUserId, sessionReference & owningTenantId.
        long afterPlace = await MaxAuditIdAsync();
        string newPlaceHtml = await supervisor.Http.GetStringAsync($"/people/{newPersonId}/places/add");
        using (var addResp = await supervisor.NoRedirect.PostAsync($"/people/{newPersonId}/places/add",
            MeritClient.Form(newPlaceHtml,
                ("Role", "residence"),
                ("AddressLine1", $"100 Audit Way {suffix}"),
                ("City", "Towson"),
                ("State", "MD"),
                ("Zip5", "21204"),
                ("CountyFips", "005"),
                ("StartDate", "2024-01-01"),
                ("Source", "interview"))))
        {
            Assert.Equal(HttpStatusCode.OK, addResp.StatusCode);
        }

        int placeAudits = await ScalarAsync<int>("""
            SELECT COUNT (*) FROM logs.auditEvent
             WHERE auditEventId > @after AND eventAction = 'update' AND entityType = 'person'
               AND entityId = @personId AND appUserId IS NOT NULL
               AND sessionReference IS NOT NULL AND owningTenantId IS NOT NULL
            """, ("@after", afterPlace), ("@personId", newPersonId));
        Assert.True(placeAudits >= 1, "Expected POST /people/{id}/places/add to write update person with appUserId, sessionReference and owningTenantId.");

        // 2. Held-row batch rejection writes update stagingRow with recordCount = 2 and 2 manifest rows.
        long afterReject = await MaxAuditIdAsync();
        Guid batchId = await ScalarAsync<Guid>("SELECT TOP (1) intakeBatchId FROM dbo.intakeBatch WHERE isDeleted = 0");
        Guid rootId = await ScalarAsync<Guid>("SELECT rootTenantId FROM dbo.intakeBatch WHERE intakeBatchId = @b", ("@b", batchId));
        Guid ownerId = await ScalarAsync<Guid>("SELECT owningTenantId FROM dbo.intakeBatch WHERE intakeBatchId = @b", ("@b", batchId));
        Guid row1 = Guid.NewGuid(), row2 = Guid.NewGuid();
        await using (var c = new SqlConnection(TestEnvironment.AdminConnectionString()))
        {
            await c.OpenAsync();
            await using (var seed = new SqlCommand("""
                INSERT dbo.stagingRow (stagingRowId, rootTenantId, owningTenantId, intakeBatchId, rowNumber, parsedJson, rowStatus)
                VALUES (@r1, @root, @owner, @batch, 9901, N'{}', 'held'),
                       (@r2, @root, @owner, @batch, 9902, N'{}', 'held');
                """, c))
            {
                seed.Parameters.AddWithValue("@r1", row1);
                seed.Parameters.AddWithValue("@r2", row2);
                seed.Parameters.AddWithValue("@root", rootId);
                seed.Parameters.AddWithValue("@owner", ownerId);
                seed.Parameters.AddWithValue("@batch", batchId);
                await seed.ExecuteNonQueryAsync();
            }

            using var resp = await supervisor.Http.PostAsync("/api/v1/held/reject",
                new StringContent(System.Text.Json.JsonSerializer.Serialize(new { stagingRowIds = new[] { row1, row2 }, reason = "Audit test rejection" }),
                    System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            await using var cleanup = new SqlCommand("UPDATE dbo.stagingRow SET isDeleted = 1 WHERE stagingRowId IN (@r1, @r2)", c);
            cleanup.Parameters.AddWithValue("@r1", row1);
            cleanup.Parameters.AddWithValue("@r2", row2);
            await cleanup.ExecuteNonQueryAsync();
        }

        long rejectAuditId = await ScalarAsync<long>("""
            SELECT TOP (1) auditEventId FROM logs.auditEvent
             WHERE auditEventId > @after AND eventAction = 'update' AND entityType = 'stagingRow'
               AND recordCount = 2 AND owningTenantId = @owner AND appUserId IS NOT NULL
             ORDER BY auditEventId DESC
            """, ("@after", afterReject), ("@owner", ownerId));
        Assert.True(rejectAuditId > 0, "Expected batch held-row rejection to write update stagingRow with recordCount = 2.");

        int rejectManifest = await ScalarAsync<int>(
            "SELECT COUNT (*) FROM logs.auditEventRecord WHERE auditEventId = @id AND entityId IN (@r1, @r2) AND isDeleted = 0",
            ("@id", rejectAuditId), ("@r1", row1), ("@r2", row2));
        Assert.Equal(2, rejectManifest);

        // 3. Qualifying lab result for the child opens a case automatically -> both create labResult and create exposureCase are audited in-transaction.
        long afterResult = await MaxAuditIdAsync();
        Guid leadAnalyteId = await ScalarAsync<Guid>("SELECT TOP (1) analyteId FROM config.analyte WHERE code = 'PB' AND isDeleted = 0");
        Guid leadSpecimenId = await ScalarAsync<Guid>("""
            SELECT TOP (1) s.analyteSpecimenId
              FROM config.analyteSpecimen AS s
             WHERE s.analyteId = @a AND s.specimen = 'wholeBlood' AND s.isDeleted = 0
            """, ("@a", leadAnalyteId));
        Guid labOrgId = await ScalarAsync<Guid>("""
            SELECT TOP (1) o.organizationId
              FROM dbo.organization AS o
              JOIN dbo.organizationType AS ot ON ot.organizationId = o.organizationId
             WHERE o.rootTenantId = @r AND o.isDeleted = 0 AND ot.organizationTypeCode = 'laboratory' AND ot.isDeleted = 0
            """, ("@r", rootId));

        string newResultPage = await supervisor.Http.GetStringAsync($"/people/{newPersonId}/results/new");
        using (var saveResult = await supervisor.NoRedirect.PostAsync($"/people/{newPersonId}/results",
            MeritClient.Form(newResultPage,
                ("AnalyteId", leadAnalyteId.ToString()),
                ("AnalyteSpecimenId", leadSpecimenId.ToString()),
                ("ValueText", "15"),
                ("Unit", "ug/dL"),
                ("SampleType", "venous"),
                ("CollectedDate", "2026-02-01"),
                ("ReportedDate", "2026-02-03"),
                ("FinalResultDate", "2026-02-03"),
                ("DepartmentReceivedAt", "2026-02-03T10:00"),
                ("AccessionNumber", "AUD-" + suffix),
                ("ResultStatus", "final"),
                ("LabOrganizationId", labOrgId.ToString()))))
        {
            Assert.Equal(HttpStatusCode.OK, saveResult.StatusCode);
        }

        int resultAudit = await ScalarAsync<int>("""
            SELECT COUNT (*) FROM logs.auditEvent
             WHERE auditEventId > @after AND eventAction = 'create' AND entityType = 'labResult'
               AND appUserId IS NOT NULL AND sessionReference IS NOT NULL AND owningTenantId IS NOT NULL
            """, ("@after", afterResult));
        Assert.Equal(1, resultAudit);

        int caseAudit = await ScalarAsync<int>("""
            SELECT COUNT (*) FROM logs.auditEvent
             WHERE auditEventId > @after AND eventAction = 'create' AND entityType = 'exposureCase'
               AND appUserId IS NOT NULL AND sessionReference IS NOT NULL AND owningTenantId IS NOT NULL
            """, ("@after", afterResult));
        Assert.Equal(1, caseAudit);

        // 4. Manifest retention purger (logs.uspPurgeAuditEventRecords) scrubs expired rows and preserves fresh rows.
        Guid expiredRecId = Guid.NewGuid(), freshRecId = Guid.NewGuid();
        Guid expiredEntityId = Guid.NewGuid(), freshEntityId = Guid.NewGuid();
        await using (var c = new SqlConnection(TestEnvironment.AdminConnectionString()))
        {
            await c.OpenAsync();
            await using (var seed = new SqlCommand("""
                INSERT logs.auditEventRecord (auditEventRecordId, auditEventId, rootTenantId, entityType, entityId, createdDateUtc)
                VALUES (@expId, @aid, @root, 'person', @expEnt, DATEADD (DAY, -400, SYSUTCDATETIME ())),
                       (@freshId, @aid, @root, 'person', @freshEnt, SYSUTCDATETIME ());
                """, c))
            {
                seed.Parameters.AddWithValue("@expId", expiredRecId);
                seed.Parameters.AddWithValue("@freshId", freshRecId);
                seed.Parameters.AddWithValue("@aid", rejectAuditId);
                seed.Parameters.AddWithValue("@root", rootId);
                seed.Parameters.AddWithValue("@expEnt", expiredEntityId);
                seed.Parameters.AddWithValue("@freshEnt", freshEntityId);
                await seed.ExecuteNonQueryAsync();
            }

            await using var purgeCmd = new SqlCommand("logs.uspPurgeAuditEventRecords", c) { CommandType = System.Data.CommandType.StoredProcedure };
            purgeCmd.Parameters.AddWithValue("@retentionDays", 365);
            int purged = (int)(await purgeCmd.ExecuteScalarAsync())!;
            Assert.True(purged >= 1);

            bool expDeleted = await ScalarAsync<bool>("SELECT isDeleted FROM logs.auditEventRecord WHERE auditEventRecordId = @id", ("@id", expiredRecId));
            Guid expEntityAfter = await ScalarAsync<Guid>("SELECT entityId FROM logs.auditEventRecord WHERE auditEventRecordId = @id", ("@id", expiredRecId));
            bool freshDeleted = await ScalarAsync<bool>("SELECT isDeleted FROM logs.auditEventRecord WHERE auditEventRecordId = @id", ("@id", freshRecId));
            Guid freshEntityAfter = await ScalarAsync<Guid>("SELECT entityId FROM logs.auditEventRecord WHERE auditEventRecordId = @id", ("@id", freshRecId));

            Assert.True(expDeleted);
            Assert.Equal(Guid.Empty, expEntityAfter);
            Assert.False(freshDeleted);
            Assert.Equal(freshEntityId, freshEntityAfter);

            // Soft-delete the synthetic fresh test row too.
            await using var cleanup = new SqlCommand("UPDATE logs.auditEventRecord SET isDeleted = 1 WHERE auditEventRecordId = @id", c);
            cleanup.Parameters.AddWithValue("@id", freshRecId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
