using System.Net;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

public sealed partial class PatientCareTests
{
    [SqlTheory]
    [InlineData(2020, "2020-08-01", "2020-08-03T10:00", "2020-08-17", "5", "6", true)]
    [InlineData(2025, "2025-03-01", "2025-03-03T10:00", "2025-03-17", "3.5", "6", true)]
    [InlineData(2020, "2020-08-01", "2020-08-03T10:00", "2020-08-17", "5", "4", false)]
    [InlineData(2025, "2025-03-01", "2025-03-03T10:00", "2025-03-17", "3.5", "4", true)]
    public async Task Scenario243ManualHistoricalReceiptCreatesVersionedNotification(int year, string draw, string receipt, string due, string minimum, string value, bool qualifies)
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        var person = await TestingPatient(c, $"{year - 2}-01-01"); var rule = Guid.NewGuid(); var threshold = Guid.NewGuid();
        string accession = "S243-" + Guid.NewGuid().ToString("N"); Guid? result = null;
        try
        {
            var admin = await fixture.SignedInAsync("admin"); await admin.Http.GetStringAsync("/config/notification-rules");
            var specimen = (Guid)(await Sql(c, "SELECT analyteSpecimenId FROM dbo.labResult WHERE labResultId=@id", ("@id", fixture.Ids.MiaLabResultId)))!;
            await admin.Http.GetStringAsync("/config/thresholds/new");
            await Saved(await admin.NoRedirect.PostAsync("/config/thresholds", Form(
                ("ThresholdId", threshold), ("AnalyteSpecimenId", specimen), ("Population", "child"), ("AgeMaxMonths", 71),
                ("ThresholdType", "case"), ("LowerValue", minimum), ("Comparator", ">="), ("IsActive", "true"),
                ("EffectiveFrom", $"{year}-01-01"), ("EffectiveTo", $"{year}-12-31"), ("Authority", "Synthetic scenario 243 operational case rule"))));
            // Explicitly synthetic operational policy: this verifies the engine, not historical legal approval.
            await Saved(await admin.NoRedirect.PostAsync("/config/notification-rules", Form(
                ("notificationRuleId", rule), ("ruleName", $"Scenario 243 {year} synthetic caregiver policy"),
                ("analyteSpecimenId", specimen), ("population", "child"), ("minimumValue", minimum), ("recipientKind", "caregiver"),
                ("dueInDays", 10), ("dayBasis", "business"), ("timeZoneId", "Eastern Standard Time"),
                ("calendarName", "Scenario weekday-only calendar; not program approval"), ("holidays", ""),
                ("effectiveFrom", $"{year}-01-01"), ("effectiveTo", $"{year}-12-31"),
                ("approvalReference", "Synthetic scenario 243 engine test only"), ("approved", "true"))));
            var user = await fixture.SignedInAsync("supervisor");
            await user.Http.GetStringAsync($"/people/{person}/results/new");
            var analyte = await Sql(c, "SELECT analyteId FROM config.analyteSpecimen WHERE analyteSpecimenId=@id", ("@id", specimen));
            var lab = await Sql(c, "SELECT labOrganizationId FROM dbo.labResult WHERE labResultId=@id", ("@id", fixture.Ids.MiaLabResultId));
            using var saved = await user.NoRedirect.PostAsync($"/people/{person}/results", Form(
                ("AnalyteId", analyte), ("AnalyteSpecimenId", specimen), ("ValueText", value), ("Unit", "ug/dL"),
                ("SampleType", "venous"), ("CollectedDate", draw), ("ReportedDate", receipt[..10]),
                ("DepartmentReceivedAt", receipt), ("FinalResultDate", receipt[..10]), ("LabOrganizationId", lab),
                ("AccessionNumber", accession), ("ResultStatus", "final")));
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            result = (Guid?)(await Sql(c, "SELECT labResultId FROM dbo.labResult WHERE personId=@id AND accessionNumber=@accession", ("@id", person), ("@accession", accession)));
            Assert.NotNull(result);
            Assert.Equal(DateTime.Parse(draw), await Sql(c, "SELECT collectedDate FROM dbo.labResult WHERE labResultId=@id", ("@id", result)));
            Assert.Equal(DateTime.Parse(receipt[..10]), await Sql(c, "SELECT CAST(receivedUtc AT TIME ZONE 'UTC' AT TIME ZONE 'Eastern Standard Time' AS DATE) FROM dbo.labResult WHERE labResultId=@id", ("@id", result)));
            if (qualifies) Assert.Equal(DateTime.Parse(due), await Sql(c, "SELECT dueDate FROM dbo.notificationObligation WHERE notificationRuleId=@rule AND labResultId=@result", ("@rule", rule), ("@result", result)));
            Assert.Equal(qualifies ? 1 : 0, await Sql(c, "SELECT COUNT(*) FROM dbo.notificationObligation n JOIN dbo.workTask w ON w.workTaskId=n.workTaskId WHERE n.notificationRuleId=@rule AND n.labResultId=@result AND w.taskStatus='open'", ("@rule", rule), ("@result", result)));
            Assert.Equal(qualifies ? 1 : 0, await Sql(c, "SELECT COUNT(*) FROM dbo.exposureCase WHERE personId=@person", ("@person", person)));
        }
        finally
        {
            await Sql(c, """
                DELETE dbo.notificationObligation WHERE labResultId IN(SELECT labResultId FROM dbo.labResult WHERE personId=@person);
                DELETE dbo.workTask WHERE personId=@person;
                DELETE dbo.caseResultLink WHERE personId=@person;
                DELETE dbo.caseActivity WHERE exposureCaseId IN(SELECT exposureCaseId FROM dbo.exposureCase WHERE personId=@person);
                DELETE dbo.exposureCase WHERE personId=@person;
                """, ("@person", person));
            await CleanReview(c, result is Guid r ? [r] : [], [rule]);
            await Sql(c, "DELETE config.threshold WHERE thresholdId=@threshold", ("@threshold", threshold));
            await CleanTesting(c, person);
        }
    }

    [SqlFact]
    public async Task Scenario243HistoricalClinicalPlanRequiresExplicitCurrentCareDecisionAndAppearsInSummary()
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        var person = await TestingPatient(c, "2018-01-01");
        try
        {
            var result = await TestingResult(c, person, "2020-08-01", 4); var task = Guid.NewGuid();
            var user = await fixture.SignedInAsync("supervisor");
            await user.Http.GetStringAsync($"/people/{person}/care/testing");
            using var denied = await user.NoRedirect.PostAsync($"/people/{person}/care/testing", Form(
                ("workTaskId", task), ("planKind", "confirmation"), ("labResultId", result),
                ("evidence", "Historical draw without a current-care decision"), ("clinicianConfirmed", "true")));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, denied.StatusCode);
            Assert.Contains("Historical policy reconstruction is not supported", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            await Saved(await Plan(person, task, "confirmation", result));
            Assert.Equal("MDH-2025-03", await Sql(c, "SELECT JSON_VALUE(testingPlanJson,'$.policyVersion') FROM dbo.workTask WHERE workTaskId=@id", ("@id", task)));
            Assert.Equal("currentCare", await Sql(c, "SELECT JSON_VALUE(testingPlanJson,'$.policyPurpose') FROM dbo.workTask WHERE workTaskId=@id", ("@id", task)));
            Assert.Equal("true", await Sql(c, "SELECT JSON_VALUE(testingPlanJson,'$.currentGuidanceConfirmed') FROM dbo.workTask WHERE workTaskId=@id", ("@id", task)));
            Assert.Equal(new DateTime(2020, 10, 24), await Sql(c, "SELECT dueDate FROM dbo.workTask WHERE workTaskId=@id", ("@id", task)));
            var summary = await user.Http.GetStringAsync($"/people/{person}");
            Assert.Contains("2020-10-24", summary, StringComparison.Ordinal);
            var testing = await user.Http.GetStringAsync($"/people/{person}/care/testing");
            Assert.Contains("2020-10-24", testing, StringComparison.Ordinal);
            Assert.Contains("Overdue", testing, StringComparison.Ordinal);
            using var rows = JsonDocument.Parse(await user.Http.GetStringAsync("/api/v1/tasks?queue=testingFollowUp&pageSize=500"));
            var row = rows.RootElement.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("workTaskId").GetGuid() == task);
            Assert.True(row.GetProperty("isOverdue").GetBoolean());
            Assert.False(row.TryGetProperty("caregiverPhone", out _));
            Assert.False(row.TryGetProperty("caregiverEmail", out _));
            Assert.False(row.TryGetProperty("caregiverAddress", out _));
        }
        finally { await CleanTesting(c, person); }
    }

    [SqlFact]
    public async Task Scenario243UpcomingTestingHasConsistentSevenDayFlagAndSummary()
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        var person = await TestingPatient(c, "2024-01-01");
        try
        {
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")));
            var draw = today.AddDays(-7); var target = today.AddDays(3);
            var result = await TestingResult(c, person, draw.ToString("yyyy-MM-dd"), 4, "venous");
            await Saved(await Plan(person, Guid.NewGuid(), "later", result, target: target.ToString("yyyy-MM-dd")));
            var user = await fixture.SignedInAsync("supervisor");
            var testing = await user.Http.GetStringAsync($"/people/{person}/care/testing");
            Assert.Contains(target.ToString("yyyy-MM-dd"), testing, StringComparison.Ordinal);
            Assert.Contains("Due within 7 days", testing, StringComparison.OrdinalIgnoreCase);
            var summary = await user.Http.GetStringAsync($"/people/{person}");
            Assert.Contains(target.ToString("yyyy-MM-dd"), summary, StringComparison.Ordinal);
            Assert.Contains("Due within 7 days", summary, StringComparison.Ordinal);
        }
        finally { await CleanTesting(c, person); }
    }
}
