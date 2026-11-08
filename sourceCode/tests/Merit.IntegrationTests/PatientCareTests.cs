using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Merit.Application;
using Merit.Application.Data;
using Merit.Application.Jobs;
using Merit.Application.Security;
using Merit.Data.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Merit.IntegrationTests;

[Collection(MeritCollection.Name)]
public sealed partial class PatientCareTests(MeritFixture fixture)
{
    private static async Task<object?> Sql(SqlConnection c, string sql, params (string, object?)[] args)
    {
        await using var cmd = new SqlCommand(sql, c);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await cmd.ExecuteScalarAsync();
    }
    private static FormUrlEncodedContent Form(params (string, object?)[] args)
        => new(args.Select(a => new KeyValuePair<string, string>(a.Item1, a.Item2?.ToString() ?? "")));
    private static async Task Saved(HttpResponseMessage response)
    {
        using (response) Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    [SqlFact]
    public async Task CalendarExcludesTriggerDayWeekendsAndOnlyExplicitHolidays()
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        Assert.Equal(new DateTime(2026, 11, 13), await Sql(c, "SELECT config.udfNotificationDueDate ('20261106',7,'calendar',N'[]')"));
        Assert.Equal(new DateTime(2026, 11, 20), await Sql(c, "SET DATEFIRST 7; SELECT config.udfNotificationDueDate ('20261106',10,'business',N'[]')"));
        Assert.Equal(new DateTime(2026, 11, 23), await Sql(c, "SET DATEFIRST 1; SELECT config.udfNotificationDueDate ('20261106',10,'business',N'[\"2026-11-11\"]')"));
        Assert.Equal(new DateTime(2027, 1, 4), await Sql(c, "SELECT config.udfNotificationDueDate ('20261231',1,'business',N'[\"2027-01-01\"]')"));
    }

    [SqlFact]
    public async Task PatientWithoutCasesCanRecordActivityThenLinkSameEventToLaterCase()
    {
        var person = Guid.NewGuid(); var activity = Guid.NewGuid(); var caseId = Guid.NewGuid(); var result = Guid.NewGuid();
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        try
        {
            await Sql(c, """
                INSERT dbo.person (personId,rootTenantId,owningTenantId,lastName,lastNameKey)
                SELECT @id,rootTenantId,owningTenantId,N'Precase Activity',N'PRECASEACTIVITY' FROM dbo.person WHERE personId=@original;
                """, ("@id", person), ("@original", fixture.Ids.MiaPersonId));
            var user = await fixture.SignedInAsync("supervisor");
            var page = await user.Http.GetStringAsync($"/people/{person}/care");
            Assert.Contains("No recorded activities", page, StringComparison.OrdinalIgnoreCase);
            await Saved(await user.NoRedirect.PostAsync($"/people/{person}/care/activity", Form(
                ("personActivityId", activity), ("activityType", "note"), ("activityDate", "2026-01-20"),
                ("audience", "internal"), ("channel", "internal"), ("outcome", "other"), ("note", "Coordinated follow-up before a case existed."))));
            Assert.Equal(DBNull.Value, await Sql(c, "SELECT exposureCaseId FROM dbo.personActivity WHERE personActivityId=@id", ("@id", activity)));
            await Sql(c, """
                INSERT dbo.labResult (labResultId,rootTenantId,owningTenantId,personId,analyteSpecimenId,collectedDate,receivedUtc,resultStatus,
                    valueReported,unitReported,valueCanonical,unitCanonical,labOrganizationId,reportedSnapshotJson,intakeBatchId,population)
                SELECT @result,rootTenantId,owningTenantId,@person,analyteSpecimenId,collectedDate,receivedUtc,resultStatus,
                    valueReported,unitReported,valueCanonical,unitCanonical,labOrganizationId,N'{}',intakeBatchId,population
                FROM dbo.labResult WHERE labResultId=@originalResult;
                INSERT dbo.exposureCase (exposureCaseId,rootTenantId,owningTenantId,caseNumber,personId,analyteId,population,classification,
                    computedClassification,caseStatus,qualifyingLabResultId,workflowTemplate,openedDate)
                SELECT @case,rootTenantId,owningTenantId,CONCAT('CARE-',LEFT(CONVERT(VARCHAR(36),@case),8)),@person,analyteId,population,
                    classification,computedClassification,'open',@result,workflowTemplate,'2026-01-25'
                FROM dbo.exposureCase WHERE exposureCaseId=@originalCase;
                """, ("@person", person), ("@result", result), ("@case", caseId), ("@originalResult", fixture.Ids.MiaLabResultId), ("@originalCase", fixture.Ids.MiaCaseId));
            await Saved(await user.NoRedirect.PostAsync($"/people/{person}/care/link", Form(("activityId", activity), ("exposureCaseId", caseId))));
            Assert.Equal(caseId, await Sql(c, "SELECT exposureCaseId FROM dbo.personActivity WHERE personActivityId=@id", ("@id", activity)));
            Assert.Equal(new DateTime(2026, 1, 20), await Sql(c, "SELECT activityDate FROM dbo.personActivity WHERE personActivityId=@id", ("@id", activity)));
            Assert.Equal(1, await Sql(c, "SELECT COUNT(*) FROM dbo.personActivity WHERE personActivityId=@id", ("@id", activity)));
            var history = await user.Http.GetStringAsync($"/cases/{caseId}");
            Assert.Contains("Coordinated follow-up before a case existed.", history, StringComparison.Ordinal);
            using var wrong = await user.NoRedirect.PostAsync($"/people/{person}/care/link", Form(("activityId", activity), ("exposureCaseId", fixture.Ids.MiaCaseId)));
            Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        }
        finally
        {
            await Sql(c, "DELETE dbo.personActivity WHERE personId=@person; DELETE dbo.exposureCase WHERE exposureCaseId=@case; DELETE dbo.labResult WHERE labResultId=@result; DELETE dbo.personAccess WHERE personId=@person; DELETE dbo.person WHERE personId=@person;",
                ("@person", person), ("@case", caseId), ("@result", result));
        }
    }

    [SqlFact]
    public async Task OriginalDocumentRetryRevisionAndDownloadArePrivateAndAudited()
    {
        var id = Guid.NewGuid(); var revision = Guid.NewGuid(); var person = fixture.Ids.MiaPersonId;
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.7\nprivate original");
        var user = await fixture.SignedInAsync("supervisor"); await user.Http.GetStringAsync($"/people/{person}/care");
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        async Task<HttpResponseMessage> Upload(Guid documentId, Guid? supersedes = null, string name = "scan.pdf", byte[]? content = null)
        {
            var form = new MultipartFormDataContent();
            foreach (var (key, value) in new[] { ("personDocumentId", documentId.ToString()), ("title", "Clinic original"), ("category", "labReport"),
                ("documentDate", "2026-01-20"), ("source", "Clinic fax"), ("supersedesDocumentId", supersedes?.ToString() ?? "") }) form.Add(new StringContent(value), key);
            form.Add(new ByteArrayContent(content ?? bytes), "file", name);
            return await user.NoRedirect.PostAsync($"/people/{person}/care/documents", form);
        }
        try
        {
            await Saved(await Upload(id)); await Saved(await Upload(id)); await Saved(await Upload(revision, id));
            Assert.Equal(2, await Sql(c, "SELECT COUNT(*) FROM dbo.personDocument WHERE personDocumentId IN (@id,@rev)", ("@id", id), ("@rev", revision)));
            Assert.Equal(id, await Sql(c, "SELECT supersedesDocumentId FROM dbo.personDocument WHERE personDocumentId=@id", ("@id", revision)));
            using var download = await user.NoRedirect.GetAsync($"/people/{person}/care/documents/{id}");
            Assert.Equal(HttpStatusCode.OK, download.StatusCode); Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
            Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
            Assert.True(download.Headers.CacheControl?.NoStore);
            Assert.Equal(1, await Sql(c, "SELECT COUNT(*) FROM logs.auditEvent WHERE entityType='patientDocument' AND entityId=@id AND eventAction='export'", ("@id", id)));
            using var mismatch = await Upload(Guid.NewGuid(), null, "scan.png", bytes); Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatch.StatusCode);
            var epi = await fixture.SignedInAsync("epi");
            using var forbidden = await epi.Http.GetAsync($"/people/{person}/care/documents/{id}");
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            var county = await fixture.SignedInAsync("cmbaltco");
            using var outsideScope = await county.Http.GetAsync($"/people/{person}/care/documents/{id}");
            Assert.Equal(HttpStatusCode.NotFound, outsideScope.StatusCode);
            using var wrongPatient = await user.NoRedirect.GetAsync($"/people/{Guid.NewGuid()}/care/documents/{id}"); Assert.Equal(HttpStatusCode.NotFound, wrongPatient.StatusCode);
        }
        finally { await Sql(c, "DELETE dbo.personDocument WHERE personDocumentId=@rev; DELETE dbo.personDocument WHERE personDocumentId=@id", ("@id", id), ("@rev", revision)); }
    }

    [SqlFact]
    public async Task MultipleThresholdRulesUseReceiptAndRequireCompletedMatchingRecipientEvidence()
    {
        var result = Guid.NewGuid(); var censored = Guid.NewGuid(); var below = Guid.NewGuid();
        var rule = Guid.NewGuid(); var rule2 = Guid.NewGuid(); var person = fixture.Ids.MiaPersonId;
        var activity = Guid.NewGuid(); var completion = Guid.NewGuid();
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        var user = await fixture.SignedInAsync("supervisor");
        var admin = await fixture.SignedInAsync("admin"); await admin.Http.GetStringAsync("/config/notification-rules");
        try
        {
            var specimen = (Guid)(await Sql(c, "SELECT analyteSpecimenId FROM dbo.labResult WHERE labResultId=@id", ("@id", fixture.Ids.MiaLabResultId)))!;
            foreach (var (id, days, basis) in new[] { (rule, 7, "calendar"), (rule2, 10, "business") })
            {
                await Saved(await admin.NoRedirect.PostAsync("/config/notification-rules", Form(("notificationRuleId", id), ("ruleName", "Test approved " + id),
                    ("analyteSpecimenId", specimen), ("population", "all"), ("minimumValue", "9000"), ("recipientKind", "caregiver"),
                    ("dueInDays", days), ("dayBasis", basis), ("timeZoneId", "Eastern Standard Time"), ("calendarName", "Test explicit calendar"),
                    ("holidays", "2026-11-11"), ("effectiveFrom", "2026-01-01"), ("approvalReference", "Synthetic test policy"), ("approved", "true"))));
            }
            await Sql(c, """
                INSERT dbo.labResult (labResultId,rootTenantId,owningTenantId,personId,analyteSpecimenId,collectedDate,receivedUtc,resultStatus,
                    valueReported,unitReported,valueCanonical,unitCanonical,labOrganizationId,reportedSnapshotJson,intakeBatchId,population,operator)
                SELECT s.id,rootTenantId,owningTenantId,personId,analyteSpecimenId,'2020-01-01','2026-11-07T01:00:00','final',
                    s.val,unitReported,s.val,unitCanonical,labOrganizationId,N'{}',intakeBatchId,s.pop,s.op
                FROM dbo.labResult CROSS JOIN (VALUES (@id,9999,'child','='),(@censored,10000,'unknown','<'),(@below,1,'adult','=')) AS s(id,val,pop,op)
                WHERE labResultId=@original;
                """, ("@id", result), ("@censored", censored), ("@below", below), ("@original", fixture.Ids.MiaLabResultId));
            await Saved(await admin.NoRedirect.PostAsync($"/config/notification-rules/{rule}/evaluate", Form()));
            await RunNotificationJob(c, rule);
            await Saved(await user.NoRedirect.PostAsync($"/people/{person}/care/evaluate", Form()));
            await Saved(await user.NoRedirect.PostAsync($"/people/{person}/care/evaluate", Form()));
            Assert.Equal(2, await Sql(c, "SELECT COUNT(*) FROM dbo.notificationObligation WHERE labResultId=@id", ("@id", result)));
            Assert.Equal(2, await Sql(c, "SELECT COUNT(*) FROM dbo.notificationObligation WHERE labResultId=@id AND evaluationState='needsReview'", ("@id", censored)));
            Assert.Equal(0, await Sql(c, "SELECT COUNT(*) FROM dbo.notificationObligation WHERE labResultId=@id", ("@id", below)));
            Assert.Equal(new DateTime(2026, 11, 6), await Sql(c, "SELECT anchorDate FROM dbo.notificationObligation WHERE labResultId=@id AND notificationRuleId=@rule", ("@id", result), ("@rule", rule)));
            Assert.Equal(new DateTime(2026, 11, 13), await Sql(c, "SELECT dueDate FROM dbo.notificationObligation WHERE labResultId=@id AND notificationRuleId=@rule", ("@id", result), ("@rule", rule)));
            Assert.Equal(new DateTime(2026, 11, 23), await Sql(c, "SELECT dueDate FROM dbo.notificationObligation WHERE labResultId=@id AND notificationRuleId=@rule", ("@id", result), ("@rule", rule2)));
            var obligation = (Guid)(await Sql(c, "SELECT notificationObligationId FROM dbo.notificationObligation WHERE labResultId=@id AND notificationRuleId=@rule", ("@id", result), ("@rule", rule)))!;
            var task = (Guid)(await Sql(c, "SELECT workTaskId FROM dbo.notificationObligation WHERE notificationObligationId=@id", ("@id", obligation)))!;
            var recipient = (Guid)(await Sql(c, "SELECT TOP(1) relatedPersonId FROM dbo.personRelationship WHERE personId=@id AND isDeleted=0 AND (hasLegalCustody=1 OR isPrimaryContact=1 OR role IN ('parent','legalGuardian','fosterParent','caregiver'))", ("@id", person)))!;
            using var bulk = await user.NoRedirect.PostAsJsonAsync("/api/v1/tasks/bulk", new { taskIds = new[] { task }, action = "complete" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, bulk.StatusCode);
            var version = (byte[])(await Sql(c, "SELECT rowVersion FROM dbo.exposureCase WHERE exposureCaseId=@id", ("@id", fixture.Ids.MiaCaseId)))!;
            using var close = await user.NoRedirect.PostAsync($"/cases/{fixture.Ids.MiaCaseId}/close", Form(("closureReason", "other"),
                ("closedDate", "2026-11-07"), ("rowVersion", "0x" + Convert.ToHexString(version))));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, close.StatusCode);
            Assert.Contains("Complete required notifications", await close.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            using var attempt = await user.NoRedirect.PostAsync($"/people/{person}/care/activity", Form(("personActivityId", activity), ("caseId", fixture.Ids.MiaCaseId),
                ("activityType", "contactAttempt"), ("activityDate", "2026-11-07"), ("audience", "caregiver"), ("channel", "phone"),
                ("outcome", "noAnswer"), ("contactPersonId", recipient), ("note", "No answer"), ("labResultId", result), ("notificationObligationId", obligation)));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, attempt.StatusCode);
            Assert.Equal(0, await Sql(c, "SELECT COUNT(*) FROM dbo.personActivity WHERE personActivityId=@id", ("@id", activity)));
            await Saved(await user.NoRedirect.PostAsync($"/people/{person}/care/activity", Form(("personActivityId", activity), ("caseId", fixture.Ids.MiaCaseId),
                ("activityType", "contactAttempt"), ("activityDate", "2026-11-07"), ("audience", "caregiver"), ("channel", "phone"),
                ("outcome", "noAnswer"), ("contactPersonId", recipient), ("note", "No answer; retain attempt without completing obligation"), ("labResultId", result))));
            Assert.Equal("open", await Sql(c, "SELECT taskStatus FROM dbo.workTask WHERE workTaskId=@id", ("@id", task)));
            await Saved(await user.NoRedirect.PostAsync($"/people/{person}/care/activity", Form(("personActivityId", completion), ("caseId", fixture.Ids.MiaCaseId),
                ("activityType", "notificationCompleted"), ("activityDate", "2026-11-07"), ("audience", "caregiver"), ("channel", "phone"),
                ("outcome", "completed"), ("contactPersonId", recipient), ("note", "Delivered result and instructions"), ("labResultId", result), ("notificationObligationId", obligation), ("confirmed", "true"), ("completeMatching", "true"))));
            Assert.Equal("done", await Sql(c, "SELECT taskStatus FROM dbo.workTask WHERE workTaskId=@id", ("@id", task)));
            Assert.Equal(2, await Sql(c, "SELECT COUNT(*) FROM dbo.notificationObligation WHERE labResultId=@id AND completedActivityId=@activity", ("@id", result), ("@activity", completion)));
            var report = await user.Http.GetStringAsync("/notifications"); Assert.Contains("Completed on time", report, StringComparison.Ordinal);
            await Saved(await user.NoRedirect.PostAsync($"/people/{person}/care/withdraw", Form(("notificationObligationId", obligation), ("reason", "Wrong recipient recorded"))));
            Assert.Equal("open", await Sql(c, "SELECT taskStatus FROM dbo.workTask WHERE workTaskId=@id", ("@id", task)));
            Assert.Equal(true, await Sql(c, "SELECT isVoided FROM dbo.personActivity WHERE personActivityId=@id", ("@id", completion)));
            Assert.Equal(0, await Sql(c, "SELECT COUNT(*) FROM dbo.notificationObligation WHERE labResultId=@id AND completedDate IS NOT NULL", ("@id", result)));
        }
        finally
        {
            await Sql(c, """
                DECLARE @tasks TABLE(id UNIQUEIDENTIFIER); INSERT @tasks SELECT workTaskId FROM dbo.notificationObligation WHERE labResultId IN (@result,@censored,@below);
                DELETE dbo.notificationObligation WHERE labResultId IN (@result,@censored,@below); DELETE dbo.workTask WHERE workTaskId IN (SELECT id FROM @tasks);
                DELETE dbo.personActivity WHERE personActivityId IN (@activity,@completion);
                DELETE dbo.jobQueue WHERE jobType='notificationEvaluation' AND JSON_VALUE(payloadJson,'$.notificationRuleId')=CONVERT(VARCHAR(36),@rule);
                DELETE config.notificationRule WHERE notificationRuleId IN (@rule,@rule2); DELETE dbo.labResult WHERE labResultId IN (@result,@censored,@below);
                """, ("@result", result), ("@censored", censored), ("@below", below), ("@rule", rule), ("@rule2", rule2), ("@activity", activity), ("@completion", completion));
        }
    }

    private static async Task RunNotificationJob(SqlConnection c, Guid rule)
    {
        string json = (string)(await Sql(c, "SELECT TOP(1) j.* FROM dbo.jobQueue AS j WHERE jobType='notificationEvaluation' AND JSON_VALUE(payloadJson,'$.notificationRuleId')=CONVERT(VARCHAR(36),@id) ORDER BY createdDateUtc DESC FOR JSON PATH,WITHOUT_ARRAY_WRAPPER", ("@id", rule)))!;
        var job = JsonSerializer.Deserialize<ClaimedJobRow>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var session = new JobSession();
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<ICurrentSession>(session);
        services.AddMeritApplication(); services.AddMeritSqlServer(o => o.ConnectionString = TestEnvironment.WorkerConnectionString);
        await using var provider = services.BuildServiceProvider();
        var accounts = provider.GetRequiredService<IAccountData>();
        session.Current = await JobSessions.OpenAsync(accounts, job, DateTime.UtcNow.AddHours(1));
        try
        {
            string result = await provider.GetRequiredService<JobDispatcher>().RunAsync(job, CancellationToken.None);
            Assert.Contains("created", result, StringComparison.Ordinal);
            await provider.GetRequiredService<IWorkData>().CompleteJobAsync(job.JobId, result);
        }
        finally { await accounts.RevokeSessionScopeAsync(session.Current.Summary.SessionScopeId); }
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
