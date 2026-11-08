using Merit.Application.Errors;
using Merit.Application.Intake;
using Microsoft.Extensions.DependencyInjection;

namespace Merit.IntegrationTests;

public sealed partial class Hl7IntakeTests
{
    [SqlTheory]
    [InlineData("hl7", "2020-08-03T10:00:00", "2020-08-17", "5")]
    [InlineData("csv", "2025-03-03T10:00:00", "2025-03-17", "3.5")]
    public async Task VerifiedHistoricalImportSelectsReceiptPeriodAndPreservesUpload(string format, string receipt, string due, string minimum)
    {
        _ = fixture.Ids;
        await using var harness = await Harness.OpenAsync(); var profile = await harness.ProfileAsync(format);
        string name = "Receipt" + Guid.NewGuid().ToString("N"); var rule = Guid.NewGuid();
        var specimen = await SqlAsync<Guid>("SELECT analyteSpecimenId FROM dbo.labResult WHERE labResultId=@a", fixture.Ids.MiaLabResultId);
        var admin = await fixture.SignedInAsync("admin"); var page = await admin.Http.GetStringAsync("/config/notification-rules");
        try
        {
            using var saved = await admin.NoRedirect.PostAsync("/config/notification-rules", MeritClient.Form(page,
                ("notificationRuleId", rule.ToString()), ("ruleName", "Historical receipt fixture " + rule),
                ("analyteSpecimenId", specimen.ToString()), ("population", "child"), ("minimumValue", minimum),
                ("recipientKind", "caregiver"), ("dueInDays", "10"), ("dayBasis", "business"), ("timeZoneId", "Eastern Standard Time"),
                ("calendarName", "Synthetic weekdays"), ("holidays", ""), ("effectiveFrom", receipt[..4] + "-01-01"),
                ("effectiveTo", receipt[..4] + "-12-31"), ("approvalReference", "Synthetic integration test only"), ("approved", "true")));
            Assert.Equal(System.Net.HttpStatusCode.Redirect, saved.StatusCode);
            string content = format == "hl7"
                ? Message(name, name, dob: "20180102").Replace("20261020", "20200801").Replace("20261022", "20200803").Replace("|2.1|", "|6|")
                : $"lastName,firstName,dob,sex,loinc,value,unit,collectedDate,reportedDate,accessionNumber\r\n{name},Ada{name},2023-01-02,female,77307-7,4,ug/dL,2025-03-01,2025-03-03,{name}\r\n";
            var batch = await harness.RunAsync(profile, content, departmentReceivedAt: DateTime.Parse(receipt));
            Assert.Equal(1, batch.LoadedCount);
            Assert.NotNull(batch.DepartmentReceivedUtc);
            Assert.True(batch.ReceivedUtc > batch.DepartmentReceivedUtc);
            Assert.Equal(content, batch.RawContent);
            Assert.Equal(DateTime.Parse(due), await SqlAsync<DateTime>("SELECT n.dueDate FROM dbo.notificationObligation n JOIN dbo.labResult r ON r.labResultId=n.labResultId WHERE r.intakeBatchId=@a AND n.notificationRuleId=@b", batch.IntakeBatchId, rule));
            Assert.Equal(DateTime.Parse(receipt[..10]), await SqlAsync<DateTime>("SELECT CAST(receivedUtc AT TIME ZONE 'UTC' AT TIME ZONE 'Eastern Standard Time' AS DATE) FROM dbo.labResult WHERE intakeBatchId=@a", batch.IntakeBatchId));
            Assert.Equal("verifiedHistoricalBatch", await SqlAsync<string>("SELECT JSON_VALUE(reportedSnapshotJson,'$.departmentReceiptSource') FROM dbo.labResult WHERE intakeBatchId=@a", batch.IntakeBatchId));
            var supervisor = await fixture.SignedInAsync("supervisor");
            Assert.Contains("Historical Department receipt", await supervisor.Http.GetStringAsync("/intake"), StringComparison.Ordinal);
        }
        finally
        {
            await SqlAsync<int>("""
                DECLARE @tasks TABLE(id UNIQUEIDENTIFIER);
                INSERT @tasks SELECT workTaskId FROM dbo.notificationObligation WHERE notificationRuleId=@a;
                DELETE dbo.notificationObligation WHERE notificationRuleId=@a;
                DELETE dbo.workTask WHERE workTaskId IN(SELECT id FROM @tasks);
                DELETE config.notificationRule WHERE notificationRuleId=@a; SELECT 0;
                """, rule);
        }
    }

    [SqlFact]
    public async Task HistoricalReceiptRequiresProofAndRejectsAmbiguousTimeAndImpossibleRows()
    {
        _ = fixture.Ids; await using var harness = await Harness.OpenAsync(); var profile = await harness.ProfileAsync("hl7");
        var intake = harness.Web.GetRequiredService<IntakeService>(); string content = Message("Receipt" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), dob: "20180102")
            .Replace("20261020", "20200801").Replace("20261022", "20200803");
        await Assert.ThrowsAsync<ValidationException>(() => intake.UploadAsync(profile, "test.hl7", content, historical: true));
        await Assert.ThrowsAsync<ValidationException>(() => intake.UploadAsync(profile, "test.hl7", content, historical: true,
            departmentReceivedAt: new DateTime(2025,11,2,1,30,0), receiptEvidence: "Ambiguous clock", receiptConfirmed: true));
        await Assert.ThrowsAsync<ValidationException>(() => intake.UploadAsync(profile, "test.hl7", content, historical: true,
            departmentReceivedAt: DateTime.UtcNow.AddYears(1), receiptEvidence: "Future clock", receiptConfirmed: true));
        var held = await harness.RunAsync(profile, content, departmentReceivedAt: new DateTime(2020,7,31,10,0,0));
        Assert.Equal((0,1), (held.LoadedCount,held.HeldCount));
        Assert.Equal(1, await SqlAsync<int>("SELECT COUNT(*) FROM dbo.stagingHoldReason h JOIN dbo.stagingRow s ON s.stagingRowId=h.stagingRowId WHERE s.intakeBatchId=@a AND h.reasonCode='impossibleDates'", held.IntakeBatchId));
    }
}
