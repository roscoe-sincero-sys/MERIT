using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

[Collection(MeritCollection.Name)]
public sealed class RecordCorrectionTests(MeritFixture fixture)
{
    private static async Task<object?> Sql(SqlConnection c, string sql, params (string, object?)[] args)
    {
        await using var cmd = new SqlCommand(sql, c);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await cmd.ExecuteScalarAsync();
    }

    [SqlFact]
    public async Task ReopenedCaseCreatesOneReviewTaskAndInlineActionsCannotTargetAnotherCase()
    {
        var person = Guid.NewGuid(); var result = Guid.NewGuid(); var id = Guid.NewGuid();
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        try
        {
            await Sql(c, """
                INSERT dbo.person (personId, rootTenantId, owningTenantId, lastName, lastNameKey)
                SELECT @person, rootTenantId, owningTenantId, N'Reopen test', N'REOPENTEST' FROM dbo.person WHERE personId = @originalPerson;
                INSERT dbo.labResult (labResultId, rootTenantId, owningTenantId, personId, analyteSpecimenId, collectedDate, receivedUtc, resultStatus,
                    valueReported, unitReported, valueCanonical, unitCanonical, labOrganizationId, reportedSnapshotJson, intakeBatchId, population)
                SELECT @result, rootTenantId, owningTenantId, @person, analyteSpecimenId, collectedDate, receivedUtc, resultStatus,
                    valueReported, unitReported, valueCanonical, unitCanonical, labOrganizationId, N'{}', intakeBatchId, population
                FROM dbo.labResult WHERE labResultId = @originalResult;
                INSERT dbo.exposureCase (exposureCaseId, rootTenantId, owningTenantId, caseNumber, personId, analyteId, population, classification,
                    computedClassification, caseStatus, qualifyingLabResultId, workflowTemplate, openedDate, closedDate, closureReason)
                SELECT @id, rootTenantId, owningTenantId, CONCAT ('TEST-', LEFT (CONVERT (VARCHAR (36), @id), 8)), @person, analyteId, population, classification,
                    computedClassification, 'closed', @result, workflowTemplate, openedDate, openedDate, 'other'
                FROM dbo.exposureCase WHERE exposureCaseId = @originalCase;
                """, ("@person", person), ("@result", result), ("@id", id), ("@originalPerson", fixture.Ids.MiaPersonId), ("@originalResult", fixture.Ids.MiaLabResultId), ("@originalCase", fixture.Ids.MiaCaseId));
            var version = (byte[])(await Sql(c, "SELECT rowVersion FROM dbo.exposureCase WHERE exposureCaseId = @id", ("@id", id)))!;
            var client = await fixture.SignedInAsync("supervisor"); await client.Http.GetStringAsync($"/cases/{id}");
            var fields = new Dictionary<string, string> { ["rowVersion"] = "0x" + Convert.ToHexString(version), ["reason"] = "New information received" };
            using var reopened = await client.NoRedirect.PostAsync($"/cases/{id}/reopen", new FormUrlEncodedContent(fields)); Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
            Assert.Equal("open", await Sql(c, "SELECT caseStatus FROM dbo.exposureCase WHERE exposureCaseId = @id", ("@id", id)));
            var task = (Guid)(await Sql(c, "SELECT workTaskId FROM dbo.workTask WHERE exposureCaseId = @id AND title = 'Review reopened case'", ("@id", id)))!;
            using var wrong = await client.NoRedirect.PostAsync($"/cases/{fixture.Ids.MiaCaseId}/tasks/{task}", new FormUrlEncodedContent(new Dictionary<string, string> { ["action"] = "complete" })); Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
            using var completed = await client.NoRedirect.PostAsync($"/cases/{id}/tasks/{task}", new FormUrlEncodedContent(new Dictionary<string, string> { ["action"] = "complete" })); Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
            Assert.Equal("done", await Sql(c, "SELECT taskStatus FROM dbo.workTask WHERE workTaskId = @task", ("@task", task)));
            using var duplicate = await client.NoRedirect.PostAsync($"/cases/{id}/reopen", new FormUrlEncodedContent(fields)); Assert.False(duplicate.IsSuccessStatusCode);
            Assert.Equal(1, await Sql(c, "SELECT COUNT (*) FROM dbo.workTask WHERE exposureCaseId = @id", ("@id", id)));
        }
        finally { await Sql(c, "DELETE dbo.workTask WHERE exposureCaseId = @id; DELETE dbo.caseActivity WHERE exposureCaseId = @id; DELETE dbo.exposureCase WHERE exposureCaseId = @id; DELETE dbo.labResult WHERE labResultId = @result; DELETE dbo.person WHERE personId = @person", ("@id", id), ("@person", person), ("@result", result)); }
    }

    [SqlFact]
    public async Task ExtractFindingsLinkVisibleLegacySurrogatesAndIgnoreUntrustedPersonMetadata()
    {
        var id = Guid.NewGuid(); var forged = Guid.NewGuid();
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        string report = JsonSerializer.Serialize(new[] {
            new { RecordId = fixture.Ids.MiaPersonId.ToString("N")[..15].ToUpperInvariant(), FieldName = "DOB", Message = "Check date", PersonId = forged },
            new { RecordId = "UNKNOWN", FieldName = "DOB", Message = "Check date", PersonId = forged }
        });
        try
        {
            await Sql(c, """
                INSERT dbo.extractRun (extractRunId, rootTenantId, owningTenantId, extractType, periodStart, periodEnd, extractLayoutId, runStatus, requestedByUserId, validationReportJson)
                SELECT TOP (1) @id, p.rootTenantId, p.rootTenantId, l.extractType, '2026-01-01', '2026-03-31', l.extractLayoutId, 'failed', u.appUserId, @report
                FROM dbo.person AS p CROSS JOIN config.extractLayout AS l CROSS JOIN auth.appUser AS u
                WHERE p.personId = @person AND u.userName = 'supervisor';
                """, ("@id", id), ("@person", fixture.Ids.MiaPersonId), ("@report", report));
            var client = await fixture.SignedInAsync("supervisor");
            using var response = await client.GetPartialAsync($"/extracts/{id}/findings"); response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains($"href=\"/people/{fixture.Ids.MiaPersonId}\"", html); Assert.DoesNotContain($"/people/{forged}", html);
            Assert.Equal(report, await Sql(c, "SELECT validationReportJson FROM dbo.extractRun WHERE extractRunId = @id", ("@id", id)));
        }
        finally { await Sql(c, "DELETE dbo.extractRun WHERE extractRunId = @id", ("@id", id)); }
    }

    [SqlFact]
    public async Task SourceCanBeConfirmedAndEndedWithoutDuplicatingItsProductOrLosingDrafts()
    {
        var id = Guid.NewGuid(); var product = Guid.NewGuid(); var person = fixture.Ids.MiaPersonId;
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        try
        {
            await Sql(c, """
                INSERT dbo.product (productId, rootTenantId, owningTenantId, productName, brand, countryOfOrigin)
                SELECT @product, rootTenantId, owningTenantId, N'Correction test', N'Example', 'US' FROM dbo.person WHERE personId = @person;
                INSERT dbo.exposureSource (exposureSourceId, rootTenantId, owningTenantId, personId, sourceType, sourceDescription, productId, startDate)
                SELECT @id, rootTenantId, owningTenantId, personId, 'consumerProduct', N'Original note', @product, '2026-01-01' FROM dbo.person WHERE personId = @person;
                """, ("@product", product), ("@id", id), ("@person", person));
            var stamp = (DateTime)(await Sql(c, "SELECT modifiedDateUtc FROM dbo.exposureSource WHERE exposureSourceId = @id", ("@id", id)))!;
            var client = await fixture.SignedInAsync("supervisor"); await client.Http.GetStringAsync($"/people/{person}");
            var fields = new Dictionary<string, string> { ["ExposureSourceId"] = id.ToString(), ["OriginalModifiedUtc"] = stamp.ToString("O", CultureInfo.InvariantCulture),
                ["SourceType"] = "consumerProduct", ["Description"] = "Corrected note", ["ProductName"] = "Correction test", ["ProductBrand"] = "Example", ["ProductCountry"] = "US",
                ["StartDate"] = "2026-01-01", ["EndDate"] = "2025-12-01", ["IsConfirmed"] = "true" };
            using var invalid = await client.NoRedirect.PostAsync($"/people/{person}/sources", new FormUrlEncodedContent(fields)); Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
            var html = await invalid.Content.ReadAsStringAsync(); Assert.Contains("Corrected note", html); Assert.Matches("id=\"source-IsConfirmed\"[^>]*checked", html);
            fields["EndDate"] = "2026-02-01";
            using var saved = await client.NoRedirect.PostAsync($"/people/{person}/sources", new FormUrlEncodedContent(fields)); Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.Equal(product, await Sql(c, "SELECT productId FROM dbo.exposureSource WHERE exposureSourceId = @id AND isConfirmed = 1 AND endDate = '2026-02-01'", ("@id", id)));
            Assert.Equal(DBNull.Value, await Sql(c, "SELECT exposureCaseId FROM dbo.exposureSource WHERE exposureSourceId = @id", ("@id", id)));
            using var stale = await client.NoRedirect.PostAsync($"/people/{person}/sources", new FormUrlEncodedContent(fields)); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            stamp = (DateTime)(await Sql(c, "SELECT modifiedDateUtc FROM dbo.exposureSource WHERE exposureSourceId = @id", ("@id", id)))!;
            fields["OriginalModifiedUtc"] = stamp.ToString("O", CultureInfo.InvariantCulture);
            fields["ProductName"] = " ";
            using var cleared = await client.NoRedirect.PostAsync($"/people/{person}/sources", new FormUrlEncodedContent(fields)); Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
            Assert.Equal(DBNull.Value, await Sql(c, "SELECT productId FROM dbo.exposureSource WHERE exposureSourceId = @id", ("@id", id)));
            Assert.Equal(1, await Sql(c, "SELECT COUNT (*) FROM dbo.product WHERE productId = @product AND isDeleted = 0", ("@product", product)));
        }
        finally { await Sql(c, "DELETE dbo.exposureSource WHERE exposureSourceId = @id; DELETE dbo.product WHERE productId = @product", ("@id", id), ("@product", product)); }
    }

    [SqlFact]
    public async Task EmploymentEditsRetainWorksiteSourceAndAnalytesAndCanEndAndVerify()
    {
        var id = Guid.NewGuid(); var person = fixture.Ids.MiaPersonId;
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        try
        {
            await Sql(c, """
                INSERT dbo.employment (employmentId, rootTenantId, owningTenantId, personId, organizationId, worksiteLocationId, jobTitle,
                    occupationCode, occupationCodeVersion, startDate, source)
                SELECT TOP (1) @id, p.rootTenantId, p.owningTenantId, p.personId, e.organizationId, e.worksiteLocationId, N'Original title', '1234', '2010', '2026-01-01', 'labReport'
                FROM dbo.person AS p CROSS JOIN dbo.employment AS e WHERE p.personId = @person AND e.worksiteLocationId IS NOT NULL AND e.isDeleted = 0;
                INSERT dbo.employmentAnalyte (employmentAnalyteId, rootTenantId, owningTenantId, personId, employmentId, analyteId)
                SELECT NEWID (), e.rootTenantId, e.owningTenantId, e.personId, e.employmentId, a.analyteId FROM dbo.employment AS e CROSS JOIN config.analyte AS a
                WHERE e.employmentId = @id AND a.code = 'PB';
                """, ("@id", id), ("@person", person));
            var worksite = (Guid)(await Sql(c, "SELECT worksiteLocationId FROM dbo.employment WHERE employmentId = @id", ("@id", id)))!;
            var org = (Guid)(await Sql(c, "SELECT organizationId FROM dbo.employment WHERE employmentId = @id", ("@id", id)))!;
            var version = (byte[])(await Sql(c, "SELECT rowVersion FROM dbo.employment WHERE employmentId = @id", ("@id", id)))!;
            var analyte = (Guid)(await Sql(c, "SELECT analyteId FROM dbo.employmentAnalyte WHERE employmentId = @id", ("@id", id)))!;
            var client = await fixture.SignedInAsync("supervisor");
            using var form = await client.GetPartialAsync($"/people/{person}/employment/{id}/edit"); form.EnsureSuccessStatusCode();
            Assert.Contains("name=\"Source\" value=\"labReport\"", await form.Content.ReadAsStringAsync());
            var fields = new Dictionary<string, string> { ["EmploymentId"] = id.ToString(), ["OrganizationId"] = org.ToString(), ["RowVersion"] = "0x" + Convert.ToHexString(version),
                ["JobTitle"] = "Updated title", ["OccupationCode"] = "1234", ["StartDate"] = "2026-01-01", ["EndDate"] = "2026-02-01", ["Verify"] = "true", ["Source"] = "labReport", ["AnalyteIds"] = analyte.ToString() };
            using var saved = await client.NoRedirect.PostAsync($"/people/{person}/employment", new FormUrlEncodedContent(fields)); Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.Equal(worksite, await Sql(c, "SELECT worksiteLocationId FROM dbo.employment WHERE employmentId = @id AND endDate = '2026-02-01' AND verifiedDate IS NOT NULL AND source = 'labReport' AND occupationCodeVersion = '2010'", ("@id", id)));
            Assert.Equal(analyte, await Sql(c, "SELECT analyteId FROM dbo.employmentAnalyte WHERE employmentId = @id AND isDeleted = 0", ("@id", id)));
            using var stale = await client.NoRedirect.PostAsync($"/people/{person}/employment", new FormUrlEncodedContent(fields)); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            await Sql(c, "DELETE dbo.employmentAnalyte WHERE employmentId = @id", ("@id", id));
            using var withoutAnalytes = await client.GetPartialAsync($"/people/{person}/employment/{id}/edit");
            withoutAnalytes.EnsureSuccessStatusCode();
            Assert.Contains("Updated title", await withoutAnalytes.Content.ReadAsStringAsync());
        }
        finally { await Sql(c, "DELETE dbo.employmentAnalyte WHERE employmentId = @id; DELETE dbo.employment WHERE employmentId = @id", ("@id", id)); }
    }

    [SqlFact]
    public async Task PlaceCorrectionActuallyChangesYearAndScheduleAndRejectsStaleEdits()
    {
        var id = Guid.NewGuid(); var location = Guid.NewGuid(); var person = fixture.Ids.MiaPersonId;
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        try
        {
            await Sql(c, """
                INSERT dbo.location (locationId, rootTenantId, owningTenantId, country, addressLine1, city, state, zip5, countyFips, addressKey, yearBuilt, housingType)
                SELECT @location, rootTenantId, owningTenantId, 'US', N'9456 Correction Ave', N'Baltimore', 'MD', '21223', '510', N'9456 CORRECTION AVE||BALTIMORE|MD|21223', 1980, 'singleFamily' FROM dbo.person WHERE personId = @person;
                INSERT dbo.personLocation (personLocationId, rootTenantId, owningTenantId, personId, locationId, role, isPrimary, startDate, source)
                SELECT @id, rootTenantId, owningTenantId, personId, @location, 'relativeHome', 0, '2026-01-01', 'interview' FROM dbo.person WHERE personId = @person;
                """, ("@id", id), ("@location", location), ("@person", person));
            var version = (byte[])(await Sql(c, "SELECT rowVersion FROM dbo.personLocation WHERE personLocationId = @id", ("@id", id)))!;
            var client = await fixture.SignedInAsync("supervisor"); await client.Http.GetStringAsync($"/people/{person}");
            var fields = new Dictionary<string, string> { ["RowVersion"] = "0x" + Convert.ToHexString(version), ["YearBuilt"] = "1960", ["ScheduleType"] = "weekdays", ["HoursPerWeek"] = "10", ["ScheduleNote"] = "Historical schedule" };
            using var saved = await client.NoRedirect.PostAsync($"/people/{person}/places/{id}/edit", new FormUrlEncodedContent(fields)); Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.Equal(1960, await Sql(c, "SELECT yearBuilt FROM dbo.location WHERE locationId = @location", ("@location", location)));
            Assert.Equal("weekdays", await Sql(c, "SELECT scheduleType FROM dbo.personLocation WHERE personLocationId = @id", ("@id", id)));
            using var stale = await client.NoRedirect.PostAsync($"/people/{person}/places/{id}/edit", new FormUrlEncodedContent(fields)); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            using var ended = await client.NoRedirect.PostAsync($"/people/{person}/places/{id}/end", new FormUrlEncodedContent(new Dictionary<string, string> { ["endDate"] = "2026-02-01" })); Assert.Equal(HttpStatusCode.OK, ended.StatusCode);
            Assert.Equal(new DateTime(2026, 2, 1), await Sql(c, "SELECT endDate FROM dbo.personLocation WHERE personLocationId = @id", ("@id", id)));
        }
        finally { await Sql(c, "DELETE dbo.personLocation WHERE personLocationId = @id; DELETE dbo.location WHERE locationId = @location", ("@id", id), ("@location", location)); }
    }
}
