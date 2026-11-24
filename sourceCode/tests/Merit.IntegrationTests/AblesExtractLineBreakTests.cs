using System.Net;
using System.Text.Json;
using Merit.Application;
using Merit.Application.Data;
using Merit.Application.Jobs;
using Merit.Application.Security;
using Merit.Data.SqlServer;
using Merit.Domain.Extracts;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Merit.IntegrationTests;

/// <summary>
/// Integration tests for issue #312: multi-line exposure source descriptions must not split ABLES extract records,
/// and saving a source through the web collapses embedded line breaks.
/// </summary>
[Collection(MeritCollection.Name)]
public sealed class AblesExtractLineBreakTests(MeritFixture fixture)
{
    private static async Task<object?> SqlAsync(SqlConnection c, string sql, params (string Name, object? Value)[] args)
    {
        await using var cmd = new SqlCommand(sql, c);
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return await cmd.ExecuteScalarAsync();
    }

    [SqlFact]
    public async Task AblesExtractWithMultilineExposureSourceProducesOneLinePerRecordAndReportsIssue()
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();

        Guid personId = Guid.NewGuid();
        Guid labResultId = Guid.NewGuid();
        Guid caseId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        Guid? extractRunId = null;
        Guid? jobId = null;
        const string multilineDescription = "Imported spice\r\nGround turmeric\nBatch 42\rFrom market";
        const string expectedSingleLine = "Imported spice Ground turmeric Batch 42 From market";

        try
        {
            await SqlAsync(c, """
                INSERT dbo.person (personId, rootTenantId, owningTenantId, firstName, lastName, firstNameKey, lastNameKey, dob, sex, ethnicity)
                SELECT @person, rootTenantId, owningTenantId, N'AblesTest', N'Multiline', N'ABLESTEST', N'MULTILINE', '1990-05-10', 'female', 'notHispanic'
                  FROM dbo.person WHERE personId = @template;

                INSERT dbo.labResult (labResultId, rootTenantId, owningTenantId, personId, analyteSpecimenId, collectedDate,
                                      receivedUtc, resultStatus, resultRowStatus, population, ageAtCollectionMonths,
                                      operator, valueCanonical, valueReported, unitReported, unitCanonical,
                                      labOrganizationId, reportedSnapshotJson, intakeBatchId)
                SELECT @result, p.rootTenantId, p.owningTenantId, @person, s.analyteSpecimenId, '2026-06-15',
                       '2026-06-16T12:00:00', 'final', 'valid', 'adult', 433,
                       '=', 18.0, 18.0, 'ug/dL', 'ug/dL',
                       src.labOrganizationId, src.reportedSnapshotJson, src.intakeBatchId
                  FROM dbo.person AS p
                  JOIN dbo.labResult AS src ON src.labResultId = @templateResult
                  JOIN config.analyte AS a ON a.code = 'PB'
                  JOIN config.analyteSpecimen AS s ON s.analyteId = a.analyteId AND s.specimen = 'wholeBlood'
                 WHERE p.personId = @person;

                INSERT dbo.exposureCase (exposureCaseId, rootTenantId, owningTenantId, caseNumber, personId, analyteId,
                                         population, classification, computedClassification, caseStatus,
                                         qualifyingLabResultId, exposureCategory, workflowTemplate, openedDate)
                SELECT @case, p.rootTenantId, p.owningTenantId, CONCAT('ABLES-312-', LEFT(CONVERT(VARCHAR(36), @case), 8)),
                       @person, a.analyteId, 'adult', 'confirmed', 'confirmed', 'open',
                       @result, 'otherNonOccupational', 'adultLead', '2026-06-16'
                  FROM dbo.person AS p
                  JOIN config.analyte AS a ON a.code = 'PB'
                 WHERE p.personId = @person;

                INSERT dbo.exposureSource (exposureSourceId, rootTenantId, owningTenantId, personId, exposureCaseId,
                                           sourceType, sourceDescription, startDate, isConfirmed)
                SELECT @source, rootTenantId, owningTenantId, @person, @case,
                       'spiceFood', @desc, '2026-06-01', 1
                  FROM dbo.person WHERE personId = @person;

                EXEC dbo.uspRefreshPersonAccess @personId = @person;
                """,
                ("@person", personId),
                ("@template", fixture.Ids.MiaPersonId),
                ("@templateResult", fixture.Ids.MiaLabResultId),
                ("@result", labResultId),
                ("@case", caseId),
                ("@source", sourceId),
                ("@desc", multilineDescription));

            var admin = await fixture.SignedInAsync("admin");
            await admin.Http.GetStringAsync("/extracts");
            using (var req = await admin.NoRedirect.PostAsync("/extracts",
                new FormUrlEncodedContent([new("extractType", "ABLES"), new("year", "2026")])))
            {
                Assert.True(req.StatusCode is HttpStatusCode.OK or HttpStatusCode.Found, $"Unexpected status {req.StatusCode}");
            }

            extractRunId = (Guid)(await SqlAsync(c, """
                SELECT TOP (1) extractRunId FROM dbo.extractRun
                 WHERE extractType = 'ABLES' AND periodStart = '2026-01-01'
                 ORDER BY createdDateUtc DESC;
                """))!;
            jobId = (Guid)(await SqlAsync(c, "SELECT jobId FROM dbo.extractRun WHERE extractRunId = @id", ("@id", extractRunId)))!;

            string jobJson = (string)(await SqlAsync(c,
                "SELECT * FROM dbo.jobQueue WHERE jobId = @id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER",
                ("@id", jobId)))!;
            var job = JsonSerializer.Deserialize<ClaimedJobRow>(jobJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

            var jobSession = new JobSession();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ICurrentSession>(jobSession);
            services.AddMeritApplication();
            services.AddMeritSqlServer(o => o.ConnectionString = TestEnvironment.WorkerConnectionString);
            await using (var provider = services.BuildServiceProvider())
            {
                var accounts = provider.GetRequiredService<IAccountData>();
                jobSession.Current = await JobSessions.OpenAsync(accounts, job, DateTime.UtcNow.AddHours(1));
                try
                {
                    await provider.GetRequiredService<JobDispatcher>().RunAsync(job, default);
                }
                finally
                {
                    await accounts.RevokeSessionScopeAsync(jobSession.Current.Summary.SessionScopeId);
                }
            }

            int recordCount = (int)(await SqlAsync(c, "SELECT recordCount FROM dbo.extractRun WHERE extractRunId = @id", ("@id", extractRunId)))!;
            string fileContent = (string)(await SqlAsync(c, "SELECT fileContent FROM dbo.extractRun WHERE extractRunId = @id", ("@id", extractRunId)))!;
            string reportJson = (string)(await SqlAsync(c, "SELECT validationReportJson FROM dbo.extractRun WHERE extractRunId = @id", ("@id", extractRunId)))!;

            Assert.True(recordCount >= 1);
            var lines = fileContent.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            // Delimited ABLES layout has 1 header line + exactly recordCount data rows.
            Assert.Equal(recordCount + 1, lines.Length);
            Assert.All(lines, line => Assert.Equal(-1, line.IndexOfAny(['\r', '\n'])));

            string surrogateId = ExtractCodes.SurrogateId(personId);
            string personLine = Assert.Single(lines.Skip(1), l => l.Contains(surrogateId, StringComparison.Ordinal));
            Assert.Contains(expectedSingleLine, personLine, StringComparison.Ordinal);

            using var reportDoc = JsonDocument.Parse(reportJson);
            Assert.Contains(reportDoc.RootElement.EnumerateArray(), item =>
                item.GetProperty("RecordId").GetString() == surrogateId
                && item.GetProperty("FieldName").GetString() == "Process"
                && (item.GetProperty("Message").GetString() ?? string.Empty).Contains("Line break", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await SqlAsync(c, """
                IF @runId IS NOT NULL DELETE dbo.extractRun WHERE extractRunId = @runId;
                IF @jobId IS NOT NULL DELETE dbo.jobQueue WHERE jobId = @jobId;
                DELETE dbo.exposureSource WHERE exposureSourceId = @source;
                DELETE dbo.exposureCase WHERE exposureCaseId = @case;
                DELETE dbo.labResult WHERE labResultId = @result;
                DELETE dbo.personAccess WHERE personId = @person;
                DELETE dbo.person WHERE personId = @person;
                """,
                ("@runId", extractRunId),
                ("@jobId", jobId),
                ("@source", sourceId),
                ("@case", caseId),
                ("@result", labResultId),
                ("@person", personId));
        }
    }

    [SqlFact]
    public async Task SavingExposureSourceViaWebCollapsesEmbeddedLineBreaksAndUsesSingleLineInput()
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();
        Guid personId = fixture.Ids.MiaPersonId;
        string marker = "Issue312-" + Guid.NewGuid().ToString("N")[..8];
        Guid? createdSourceId = null;

        try
        {
            var client = await fixture.SignedInAsync("supervisor");
            await client.Http.GetStringAsync($"/people/{personId}");

            using (var formResp = await client.GetPartialAsync($"/people/{personId}/sources/add"))
            {
                formResp.EnsureSuccessStatusCode();
                string formHtml = await formResp.Content.ReadAsStringAsync();
                Assert.Contains("id=\"source-Description\"", formHtml, StringComparison.Ordinal);
                Assert.DoesNotContain("<textarea", formHtml, StringComparison.OrdinalIgnoreCase);
            }

            var fields = new Dictionary<string, string>
            {
                ["SourceType"] = "spiceFood",
                ["Description"] = $"{marker} line 1\r\nline 2\nline 3",
                ["StartDate"] = "2026-06-01",
            };
            using var saved = await client.NoRedirect.PostAsync($"/people/{personId}/sources", new FormUrlEncodedContent(fields));
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

            createdSourceId = (Guid?)(await SqlAsync(c,
                "SELECT TOP (1) exposureSourceId FROM dbo.exposureSource WHERE personId = @person AND sourceDescription LIKE @prefix ORDER BY createdDateUtc DESC",
                ("@person", personId), ("@prefix", marker + "%")));
            Assert.NotNull(createdSourceId);

            string stored = (string)(await SqlAsync(c,
                "SELECT sourceDescription FROM dbo.exposureSource WHERE exposureSourceId = @id",
                ("@id", createdSourceId)))!;
            Assert.Equal($"{marker} line 1 line 2 line 3", stored);
        }
        finally
        {
            if (createdSourceId is Guid id)
            {
                await SqlAsync(c, "DELETE dbo.exposureSource WHERE exposureSourceId = @id", ("@id", id));
            }
        }
    }

    private sealed class JobSession : ICurrentSession
    {
        public SessionScopeResult? Current { get; set; }
        public bool IsAuthenticated => Current is not null;
        public Guid? UserId => Current?.Summary.AppUserId;
        public string? UserName => Current?.Summary.UserName;
        public string? DisplayName => Current?.Summary.DisplayName;
        public Guid? ScopeKey => Current?.Summary.SessionScopeId;
        public Guid? RootTenantId => Current?.Summary.RootTenantId;
        public Guid? ActingTenantId => Current?.Summary.ActingTenantId;
        public string? ActingTenantName => Current?.Summary.ActingTenantName;
        public IReadOnlySet<string> Permissions => Current?.Permissions.ToHashSet(StringComparer.Ordinal) ?? [];
    }
}
