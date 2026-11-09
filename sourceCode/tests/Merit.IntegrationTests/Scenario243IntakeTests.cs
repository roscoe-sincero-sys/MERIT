using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Merit.IntegrationTests;

public sealed partial class Hl7IntakeTests
{
    [SqlTheory]
    [InlineData(2020, "hl7")]
    [InlineData(2025, "csv")]
    public async Task Scenario243HistoricalImportsPreserveDrawButUseActualUploadReceipt(int year, string type)
    {
        _ = fixture.Ids;
        await using var harness = await Harness.OpenAsync();
        var profile = await harness.ProfileAsync(type);
        string name = "Scenario243" + Guid.NewGuid().ToString("N");
        string content = type == "hl7"
            ? Message(name, name, dob: "20180102").Replace("20261020", "20200801").Replace("20261022", "20200803")
            : $"lastName,firstName,dob,sex,loinc,value,unit,collectedDate,reportedDate,accessionNumber\r\n{name},Ada{name},2023-01-02,female,77307-7,4,ug/dL,2025-03-01,2025-03-03,{name}\r\n";
        var batch = await harness.RunAsync(profile, content);
        Assert.Equal(1, batch.LoadedCount);
        Assert.Equal(new DateTime(year, year == 2020 ? 8 : 3, 1),
            await SqlAsync<DateTime>("SELECT collectedDate FROM dbo.labResult WHERE intakeBatchId=@a AND resultRowStatus='valid'", batch.IntakeBatchId));
        // Unattested uploads keep actual upload time as receivedUtc.
        var received = await SqlAsync<DateTime>("SELECT receivedUtc FROM dbo.labResult WHERE intakeBatchId=@a AND resultRowStatus='valid'", batch.IntakeBatchId);
        Assert.InRange(Math.Abs((received - batch.ReceivedUtc).TotalMilliseconds), 0, 10); // SQL DATETIME precision.
        Assert.NotEqual(year, batch.ReceivedUtc.Year);
    }

    [SqlFact]
    public async Task Scenario243VerifiedHistoricalImportsPreserve2020ObligationsAndExposeLhdAndCaregiverGaps()
    {
        _ = fixture.Ids;
        await using var harness = await Harness.OpenAsync();
        var hl7Profile = await harness.ProfileAsync("hl7");
        var db = harness.Web.GetRequiredService<Merit.Data.SqlServer.Db>();
        var organization = await SqlAsync<Guid>("SELECT TOP 1 organizationId FROM dbo.organization WHERE cliaNumber='21D0712345'");
        var csvProfile = Guid.NewGuid();
        var csvMap = new[]
        {
            "lastName", "firstName", "dob", "sex", "loinc", "value", "unit", "sampleType", "collectedDate", "reportedDate",
            "accessionNumber", "addressLine1", "city", "state", "zip5", "countyFips", "guardianName", "guardianPhone",
        }.ToDictionary(s => s);
        await db.ExecuteAsync("dbo.uspSaveIntakeProfile", new
        {
            intakeProfileId = csvProfile, senderOrganizationId = organization, profileName = "Scenario 243 CSV " + csvProfile,
            fileType = "csv", columnMapJson = JsonSerializer.Serialize(csvMap), dateFormats = "yyyy-MM-dd",
        }, CancellationToken.None);

        var specimen = await SqlAsync<Guid>("SELECT analyteSpecimenId FROM dbo.labResult WHERE labResultId=@a", fixture.Ids.MiaLabResultId);
        Guid threshold2020 = Guid.NewGuid(), threshold2025 = Guid.NewGuid(), rule2020 = Guid.NewGuid(), rule2025 = Guid.NewGuid();
        string suffix = Guid.NewGuid().ToString("N");
        var admin = await fixture.SignedInAsync("admin");
        try
        {
            var thresholdPage = await admin.Http.GetStringAsync("/config/thresholds/new");
            foreach (var (id, year, min) in new[] { (threshold2020, 2020, "5"), (threshold2025, 2025, "3.5") })
            {
                using var tResp = await admin.NoRedirect.PostAsync("/config/thresholds", MeritClient.Form(thresholdPage,
                    ("ThresholdId", id.ToString()), ("AnalyteSpecimenId", specimen.ToString()), ("Population", "child"), ("AgeMaxMonths", "71"),
                    ("ThresholdType", "case"), ("LowerValue", min), ("Comparator", ">="), ("IsActive", "true"),
                    ("EffectiveFrom", $"{year}-01-01"), ("EffectiveTo", $"{year}-12-31"), ("Authority", $"Synthetic scenario 243 {year} case threshold")));
                Assert.Equal(System.Net.HttpStatusCode.Redirect, tResp.StatusCode);
            }

            var rulePage = await admin.Http.GetStringAsync("/config/notification-rules");
            foreach (var (id, year, min) in new[] { (rule2020, 2020, "5"), (rule2025, 2025, "3.5") })
            {
                using var rResp = await admin.NoRedirect.PostAsync("/config/notification-rules", MeritClient.Form(rulePage,
                    ("notificationRuleId", id.ToString()), ("ruleName", $"Scenario 243 {year} caregiver rule {id}"),
                    ("analyteSpecimenId", specimen.ToString()), ("population", "child"), ("minimumValue", min),
                    ("recipientKind", "caregiver"), ("dueInDays", "10"), ("dayBasis", "business"), ("timeZoneId", "Eastern Standard Time"),
                    ("calendarName", "Synthetic weekdays"), ("holidays", ""), ("effectiveFrom", $"{year}-01-01"),
                    ("effectiveTo", $"{year}-12-31"), ("approvalReference", "Synthetic scenario 243 test"), ("approved", "true")));
                Assert.Equal(System.Net.HttpStatusCode.Redirect, rResp.StatusCode);
            }

            // Step 4: 2020 HL7 imports (6 ug/dL qualifies under 2020 >= 5 rule; 4 ug/dL does not).
            string hl7High = Message("S243H20" + suffix, "H20-" + suffix, dob: "20180102")
                .Replace("20261020", "20200801").Replace("20261022", "20200803").Replace("|2.1|", "|6|");
            string hl7Low = Message("S243L20" + suffix, "L20-" + suffix, dob: "20180102")
                .Replace("20261020", "20200801").Replace("20261022", "20200803").Replace("|2.1|", "|4|");
            var batch2020High = await harness.RunAsync(hl7Profile, hl7High, departmentReceivedAt: new DateTime(2020, 8, 3, 10, 0, 0));
            var batch2020Low = await harness.RunAsync(hl7Profile, hl7Low, departmentReceivedAt: new DateTime(2020, 8, 3, 10, 0, 0));
            Assert.Equal(1, batch2020High.LoadedCount);
            Assert.Equal(1, batch2020Low.LoadedCount);
            Assert.Equal(new DateTime(2020, 8, 17), await SqlAsync<DateTime>(
                "SELECT n.dueDate FROM dbo.notificationObligation n JOIN dbo.labResult r ON r.labResultId=n.labResultId WHERE r.intakeBatchId=@a AND n.notificationRuleId=@b",
                batch2020High.IntakeBatchId, rule2020));
            Assert.Equal(0, await SqlAsync<int>(
                "SELECT COUNT(*) FROM dbo.notificationObligation n JOIN dbo.labResult r ON r.labResultId=n.labResultId WHERE r.intakeBatchId=@a AND n.notificationRuleId IN (@b,@c)",
                batch2020Low.IntakeBatchId, rule2020, rule2025));

            // Step 6: 2025 CSV import at 4 ug/dL (venous 77307-7) with Baltimore County residence and caregiver fields.
            string csv2025 = string.Join("\r\n",
                "lastName,firstName,dob,sex,loinc,value,unit,sampleType,collectedDate,reportedDate,accessionNumber,addressLine1,city,state,zip5,countyFips,guardianName,guardianPhone",
                $"S243Csv25{suffix},Ada{suffix},2023-01-02,female,77307-7,4,ug/dL,venous,2025-03-01,2025-03-03,C25-{suffix},9900 Synthetic Way,Towson,MD,21204,005,Grace Guardian{suffix},4105550199",
                "");
            var batch2025 = await harness.RunAsync(csvProfile, csv2025, departmentReceivedAt: new DateTime(2025, 3, 3, 10, 0, 0));
            Assert.Equal(1, batch2025.LoadedCount);
            Assert.Equal(new DateTime(2025, 3, 17), await SqlAsync<DateTime>(
                "SELECT n.dueDate FROM dbo.notificationObligation n JOIN dbo.labResult r ON r.labResultId=n.labResultId WHERE r.intakeBatchId=@a AND n.notificationRuleId=@b",
                batch2025.IntakeBatchId, rule2025));

            // 2020 obligations remain unchanged after 2025 processing.
            Assert.Equal(new DateTime(2020, 8, 17), await SqlAsync<DateTime>(
                "SELECT n.dueDate FROM dbo.notificationObligation n JOIN dbo.labResult r ON r.labResultId=n.labResultId WHERE r.intakeBatchId=@a AND n.notificationRuleId=@b",
                batch2020High.IntakeBatchId, rule2020));
            Assert.Equal(0, await SqlAsync<int>(
                "SELECT COUNT(*) FROM dbo.notificationObligation n JOIN dbo.labResult r ON r.labResultId=n.labResultId WHERE r.intakeBatchId=@a AND n.notificationRuleId IN (@b,@c)",
                batch2020Low.IntakeBatchId, rule2020, rule2025));

            // County residence routes the case to the serving jurisdiction.
            var case2025Owner = await SqlAsync<Guid>(
                "SELECT c.owningTenantId FROM dbo.exposureCase c JOIN dbo.labResult r ON r.personId=c.personId WHERE r.intakeBatchId=@a",
                batch2025.IntakeBatchId);
            var rootTenantId = await SqlAsync<Guid>("SELECT rootTenantId FROM dbo.person WHERE personId=@a", fixture.Ids.MiaPersonId);
            Assert.Equal(fixture.Ids.BaltimoreCountyTenantId, case2025Owner);

            // Preserve reported contact details, but require staff verification before outreach use.
            var guardianId = await SqlAsync<Guid>(
                "SELECT rel.relatedPersonId FROM dbo.personRelationship rel JOIN dbo.labResult r ON r.personId=rel.personId WHERE r.intakeBatchId=@a",
                batch2025.IntakeBatchId);
            Assert.Equal(1, await SqlAsync<int>("SELECT COUNT(*) FROM dbo.personContactMethod WHERE personId=@a", guardianId));
            Assert.Equal(0, await SqlAsync<int>(
                "SELECT COUNT(*) FROM dbo.personRelationship rel JOIN dbo.labResult r ON r.personId=rel.personId WHERE r.intakeBatchId=@a AND rel.verifiedDate IS NOT NULL",
                batch2025.IntakeBatchId));
        }
        finally
        {
            await SqlAsync<int>("""
                DECLARE @tasks TABLE(id UNIQUEIDENTIFIER);
                INSERT @tasks SELECT workTaskId FROM dbo.notificationObligation WHERE notificationRuleId IN (@a,@b);
                DELETE dbo.notificationObligation WHERE notificationRuleId IN (@a,@b);
                DELETE dbo.workTask WHERE workTaskId IN (SELECT id FROM @tasks);
                DELETE config.notificationRule WHERE notificationRuleId IN (@a,@b);
                SELECT 0;
                """, rule2020, rule2025);
            await SqlAsync<int>("DELETE config.threshold WHERE thresholdId IN (@a,@b); SELECT 0;", threshold2020, threshold2025);
        }
    }
}
