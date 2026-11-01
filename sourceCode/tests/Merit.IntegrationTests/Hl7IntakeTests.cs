using System.Text.Json;
using Merit.Application;
using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Intake;
using Merit.Application.Jobs;
using Merit.Application.Security;
using Merit.Data.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Merit.IntegrationTests;

[Collection(MeritCollection.Name)]
public sealed class Hl7IntakeTests(MeritFixture fixture)
{
    private static string Segment(string tag, params (int Field, string Value)[] values)
    {
        var fields = new string[values.Max(v => v.Field) + 1]; fields[0] = tag;
        foreach (var (field, value) in values) fields[field] = value;
        return string.Join('|', fields);
    }

    private static string Message(string last, string accession, string clia = "21D0712345", string version = "2.5.1", string county = "005", string status = "F", string dob = "20200102", bool extraObservation = false)
    {
        var message = string.Join('\r', $"MSH|^~\\&|LAB|{clia}|MERIT|MD|20261022||ORU^R01^ORU_R01|{accession}|P|{version}",
            Segment("PID", (1, "1"), (3, last + "^^^LAB^MR"), (5, last + "^Ada" + last), (7, dob), (8, "F"),
                (11, county == "033" ? "9901 Synthetic Way^^Hyattsville^MD^20782^USA^H^^24033" : "9900 Synthetic Way^^Towson^MD^21204^USA^H^^24005")),
            Segment("OBR", (1, "1"), (3, accession), (7, "20261020"), (22, "20261022")),
            Segment("OBX", (1, "1"), (2, "NM"), (3, "77307-7^Lead^LN"), (4, "reading-1"), (5, "2.1"), (6, "ug/dL"), (11, status)));
        if (extraObservation) message += '\r' + Segment("OBX", (1, "2"), (2, "NM"), (3, "77307-7^Lead^LN"), (4, "reading-2"), (5, "2.2"), (6, "ug/dL"), (11, status));
        return message + '\r';
    }

    [SqlFact]
    public async Task BothVersionsMatchAcrossSendersAndCsvWhileKeepingObservationsDuplicatesAndCorrectionsDistinct()
    {
        _ = fixture.Ids;
        await using var harness = await Harness.OpenAsync();
        var suffix = Guid.NewGuid().ToString("N");
        string person = "Native" + suffix, otherPerson = "Different" + suffix;
        var profileA = await harness.ProfileAsync("hl7");
        var profileB = await harness.ProfileAsync("hl7", secondSender: true);
        var csvProfile = await harness.ProfileAsync("csv");
        string contentA = Message(person, "A-" + suffix, extraObservation: true);
        var a = await harness.RunAsync(profileA, contentA);
        Assert.Equal((2, 2, 0), (a.ReceivedCount, a.LoadedCount, a.HeldCount));
        var b = await harness.RunAsync(profileB, Message(person, "B-" + suffix, "21D0900002", "2.3.1")
            + Message(otherPerson, "B2-" + suffix, "21D0900002", "2.3.1", county: "033"));
        Assert.Equal(2, b.LoadedCount);
        var csv = await harness.RunAsync(csvProfile,
            $"lastName,firstName,dob,sex,loinc,value,unit,collectedDate,reportedDate,accessionNumber\r\n{person},Ada{person},2020-01-02,female,77307-7,2.3,ug/dL,2026-10-20,2026-10-22,CSV-{suffix}\r\n");
        Assert.Equal(1, csv.LoadedCount);
        Assert.Equal(2, await SqlAsync<int>("SELECT COUNT(DISTINCT personId) FROM dbo.labResult WHERE intakeBatchId IN (@a,@b,@c)", a.IntakeBatchId, b.IntakeBatchId, csv.IntakeBatchId));
        Assert.Equal(2, await SqlAsync<int>("SELECT COUNT(*) FROM dbo.labResult WHERE intakeBatchId=@a AND resultRowStatus='valid'", a.IntakeBatchId));
        Assert.Equal(contentA, a.RawContent);
        Assert.Equal("2.5.1", await SqlAsync<string>("SELECT TOP 1 JSON_VALUE(reportedSnapshotJson,'$.hl7Version') FROM dbo.labResult WHERE intakeBatchId=@a", a.IntakeBatchId));
        Assert.Equal("21D0712345", await SqlAsync<string>("SELECT TOP 1 JSON_VALUE(reportedSnapshotJson,'$.hl7SendingFacility') FROM dbo.labResult WHERE intakeBatchId=@a", a.IntakeBatchId));
        Assert.Equal(2, await SqlAsync<int>("SELECT COUNT(*) FROM dbo.personIdentifier WHERE personId IN(SELECT personId FROM dbo.labResult WHERE intakeBatchId=@a) AND identifierType='mrn' AND isDeleted=0", b.IntakeBatchId));

        var resend = await harness.RunAsync(profileA, contentA, uploadAgain: true);
        Assert.Equal(2, await SqlAsync<int>("SELECT COUNT(*) FROM dbo.labResult WHERE intakeBatchId=@a AND resultRowStatus='duplicate'", resend.IntakeBatchId));
        var correction = await harness.RunAsync(profileA, Message(person, "A-" + suffix, status: "C"));
        Assert.Equal(1, correction.LoadedCount);
        Assert.Equal(1, await SqlAsync<int>("SELECT COUNT(*) FROM dbo.labResult WHERE intakeBatchId=@a AND supersedesLabResultId IS NOT NULL AND resultStatus='corrected'", correction.IntakeBatchId));
        Assert.Equal(1, await SqlAsync<int>("SELECT COUNT(*) FROM dbo.labResult WHERE intakeBatchId=@a AND resultRowStatus='superseded'", a.IntakeBatchId));

        // Residence-derived county scope applies to native results and person identifiers, too.
        var countyPerson = await SqlAsync<Guid>("SELECT TOP 1 personId FROM dbo.labResult WHERE intakeBatchId=@a", a.IntakeBatchId);
        Assert.NotEmpty((await harness.Web.GetRequiredService<IPersonData>().GetPersonAsync(countyPerson)).Identifiers);
        var foreignPerson = await SqlAsync<Guid>("SELECT TOP 1 personId FROM dbo.labResult WHERE intakeBatchId=@a AND personId<>@b", b.IntakeBatchId, countyPerson);
        await harness.AsAsync("cmbaltco");
        var persons = harness.Web.GetRequiredService<IPersonData>();
        var countyHeader = await persons.GetPersonAsync(countyPerson);
        Assert.Equal(countyPerson, countyHeader.Person.PersonId);
        Assert.Empty(countyHeader.Identifiers);
        var resultId = await SqlAsync<Guid>("SELECT TOP 1 labResultId FROM dbo.labResult WHERE intakeBatchId=@a", a.IntakeBatchId);
        var countyResult = await harness.Web.GetRequiredService<IResultData>().GetLabResultAsync(resultId);
        Assert.DoesNotContain("hl7PatientIdentifiersJson", countyResult!.ReportedSnapshotJson);
        await Assert.ThrowsAsync<NotFoundException>(() => persons.GetPersonAsync(foreignPerson));
        // A sender profile cannot be obtained from a different data root or selected by an arbitrary source tenant id.
        await harness.AsAsync("platform");
        await Assert.ThrowsAsync<PermissionDeniedException>(() => harness.Web.GetRequiredService<IntakeService>().UploadAsync(profileA, "unauthorized.hl7", contentA));
    }

    [SqlFact]
    public async Task MalformedMessagesPauseBeforeAnyRowsAndClinicalProblemsUseTheHeldWorkflow()
    {
        _ = fixture.Ids;
        await using var harness = await Harness.OpenAsync();
        var profile = await harness.ProfileAsync("hl7");
        var suffix = Guid.NewGuid().ToString("N");
        var valid = Message("Invalid" + suffix, suffix);
        var malformed = await harness.RunAsync(profile, valid + Message("MustNotAppear" + suffix, "BAD", version: "2.4"));
        Assert.Equal("paused", malformed.BatchStatus);
        Assert.Contains("message 2", malformed.PauseReason);
        Assert.Contains("2.3.1 or 2.5.1", malformed.PauseReason);
        Assert.Equal(0, malformed.ReceivedCount);
        Assert.Equal(0, await SqlAsync<int>("SELECT COUNT(*) FROM dbo.labResult WHERE intakeBatchId=@a", malformed.IntakeBatchId));
        Assert.Equal(0, await SqlAsync<int>("SELECT COUNT(*) FROM dbo.stagingRow WHERE intakeBatchId=@a", malformed.IntakeBatchId));
        var wrongSender = await harness.RunAsync(profile, valid.Replace("21D0712345", "21D0900002"));
        Assert.Equal("paused", wrongSender.BatchStatus);
        Assert.Contains("selected sender profile", wrongSender.PauseReason);
        var held = await harness.RunAsync(profile, Message("Held" + suffix, "HELD", dob: "2020"));
        Assert.Equal(("loaded", 0, 1), (held.BatchStatus, held.LoadedCount, held.HeldCount));
        Assert.Equal(1, await SqlAsync<int>("SELECT COUNT(*) FROM dbo.stagingHoldReason h JOIN dbo.stagingRow r ON r.stagingRowId=h.stagingRowId WHERE r.intakeBatchId=@a AND h.reasonCode='missingDob'", held.IntakeBatchId));
    }

    private static async Task<T> SqlAsync<T>(string sql, object? a = null, object? b = null, object? c = null)
    {
        await using var connection = new SqlConnection(TestEnvironment.AdminConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@a", a ?? DBNull.Value);
        command.Parameters.AddWithValue("@b", b ?? DBNull.Value);
        command.Parameters.AddWithValue("@c", c ?? DBNull.Value);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly Session webSession = new(), workerSession = new();
        public ServiceProvider Web { get; }
        private ServiceProvider Worker { get; }
        private SessionScopeResult? admin;

        private Harness()
        {
            using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestEnvironment.SourceRoot, "src/Merit.Worker/appsettings.Development.json")));
            var workerConnection = new SqlConnectionStringBuilder(settings.RootElement.GetProperty("ConnectionStrings").GetProperty("Merit").GetString())
            {
                DataSource = TestEnvironment.Server, InitialCatalog = TestEnvironment.Database,
            };
            if (Environment.GetEnvironmentVariable("MERIT_IT_WORKER_PASSWORD") is { Length: > 0 } password) workerConnection.Password = password;
            Web = Services(TestEnvironment.WebConnectionString, webSession);
            Worker = Services(workerConnection.ConnectionString, workerSession);
        }

        private static ServiceProvider Services(string connection, Session session)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ICurrentSession>(session);
            services.AddMeritApplication();
            services.AddMeritSqlServer(options => options.ConnectionString = connection);
            return services.BuildServiceProvider();
        }

        public static async Task<Harness> OpenAsync()
        {
            var harness = new Harness();
            await harness.AsAsync("admin");
            harness.admin = harness.webSession.Current;
            return harness;
        }

        public async Task AsAsync(string user)
        {
            var accounts = Web.GetRequiredService<IAccountData>();
            webSession.Current = null;
            var person = await accounts.GetUserForSignInAsync(user);
            webSession.Current = await accounts.CreateSessionScopeAsync(Guid.NewGuid(), person!.AppUserId, DateTime.UtcNow.AddHours(1));
        }

        public async Task<Guid> ProfileAsync(string type, bool secondSender = false)
        {
            var db = Web.GetRequiredService<Db>();
            var organization = await SqlAsync<Guid>("SELECT TOP 1 organizationId FROM dbo.organization WHERE cliaNumber='21D0712345'");
            if (secondSender)
            {
                organization = await SqlAsync<Guid>("SELECT COALESCE((SELECT TOP 1 organizationId FROM dbo.organization WHERE cliaNumber='21D0900002' AND isDeleted=0),NEWID())");
                await db.ExecuteAsync("dbo.uspSaveOrganization", new { organizationId = organization, organizationName = "Native secondary lab", organizationNameKey = "NATIVESECONDARYLAB", cliaNumber = "21D0900002", typesJson = "[\"laboratory\"]" }, CancellationToken.None);
            }
            var id = Guid.NewGuid();
            var map = new[] { "lastName", "firstName", "dob", "sex", "loinc", "value", "unit", "collectedDate", "reportedDate", "accessionNumber" }.ToDictionary(s => s);
            await db.ExecuteAsync("dbo.uspSaveIntakeProfile", new
            {
                intakeProfileId = id, senderOrganizationId = organization, profileName = "HL7 integration " + id,
                fileType = type, columnMapJson = type == "hl7" ? "{}" : JsonSerializer.Serialize(map), dateFormats = "yyyy-MM-dd",
            }, CancellationToken.None);
            return id;
        }

        public async Task<IntakeBatchRow> RunAsync(Guid profile, string content, bool uploadAgain = false)
        {
            webSession.Current = admin;
            var batch = await Web.GetRequiredService<IntakeService>().UploadAsync(profile, "native-" + Guid.NewGuid() + (content.StartsWith("MSH") ? ".hl7" : ".csv"), content, uploadAgain);
            var json = await SqlAsync<string>("SELECT j.* FROM dbo.jobQueue j WHERE payloadJson LIKE '%' + CONVERT(nvarchar(36),@a) + '%' FOR JSON PATH,WITHOUT_ARRAY_WRAPPER", batch);
            var job = JsonSerializer.Deserialize<ClaimedJobRow>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            workerSession.Current = null;
            var accounts = Worker.GetRequiredService<IAccountData>();
            workerSession.Current = await JobSessions.OpenAsync(accounts, job, DateTime.UtcNow.AddHours(1));
            var result = await Worker.GetRequiredService<JobDispatcher>().RunAsync(job, CancellationToken.None);
            await Worker.GetRequiredService<IWorkData>().CompleteJobAsync(job.JobId, result);
            return (await Worker.GetRequiredService<IIntakeData>().GetBatchAsync(batch, true))!;
        }

        public async ValueTask DisposeAsync()
        {
            await Web.DisposeAsync();
            await Worker.DisposeAsync();
        }

        private sealed class Session : ICurrentSession
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
            public bool IsPlatformAdmin => Current?.Summary.IsPlatformAdmin ?? false;
        }
    }
}
