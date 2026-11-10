using System.Net;
using Merit.Application.Data;
using Merit.Application.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Merit.IntegrationTests;

public sealed partial class Hl7IntakeTests
{
    [SqlTheory]
    [InlineData("mdLabElevated", "4", "csv")]
    [InlineData("mdLabBelow", "3", "csv")]
    [InlineData("mdLabElevated", "4", "hl7")]
    [InlineData("mdLabBelow", "3", "hl7")]
    public async Task ManualCompletenessIsNotInferredButImportedReportsAndTimelineStillEvaluate(string template, string value, string channel)
    {
        _ = fixture.Ids;
        await using var harness = await Harness.OpenAsync();
        var rule = Guid.NewGuid();
        var specimen = await SqlAsync<Guid>("SELECT analyteSpecimenId FROM dbo.labResult WHERE labResultId=@a", fixture.Ids.MiaLabResultId);
        var admin = await fixture.SignedInAsync("admin");
        await admin.Http.GetStringAsync("/config/notification-rules?template=" + template);
        var fields = MarylandPolicyDrafts.Get(template).Select(p => new KeyValuePair<string, string>(p.Key, p.Value)).ToList();
        fields.AddRange(new Dictionary<string, string>
        {
            ["notificationRuleId"] = rule.ToString(), ["analyteSpecimenId"] = specimen.ToString(),
            ["calendarName"] = "Synthetic weekdays-only regression calendar", ["holidays"] = "", ["approved"] = "true"
        });
        using var activation = await admin.NoRedirect.PostAsync("/config/notification-rules", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.Redirect, activation.StatusCode);
        try
        {
            await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
            var suffix = Guid.NewGuid().ToString("N");
            var profile = await harness.ProfileAsync(channel);
            if (channel == "csv")
                await Execute(c, "UPDATE dbo.intakeProfile SET columnMapJson=JSON_MODIFY(columnMapJson,'$.sampleType','sampleType') WHERE intakeProfileId=@id", profile);
            string content = channel == "csv"
                ? $"lastName,firstName,dob,sex,loinc,value,unit,collectedDate,reportedDate,accessionNumber,sampleType\r\nReceipt{suffix},AdaReceipt{suffix},2020-01-02,female,77307-7,{value},ug/dL,2026-10-20,2026-10-22,CSV-{suffix},venous\r\n"
                : Message("Receipt" + suffix, "HL7-" + suffix).Replace("|2.1|", "|" + value + "|", StringComparison.Ordinal);
            var imported = await harness.RunAsync(profile, content, departmentReceivedAt: new DateTime(2026, 10, 22, 10, 0, 0));
            Assert.Equal(1, imported.LoadedCount);
            var importedResult = await SqlAsync<Guid>("SELECT labResultId FROM dbo.labResult WHERE intakeBatchId=@a", imported.IntakeBatchId);
            string missing = await SqlAsync<string>("SELECT detail FROM dbo.workTask WHERE labResultId=@a AND queueCode='missingInformation' AND taskStatus='open'", importedResult);
            Assert.Contains("phone", missing, StringComparison.Ordinal);
            Assert.DoesNotContain("firstName", missing, StringComparison.Ordinal);
            Assert.DoesNotContain("lastName", missing, StringComparison.Ordinal);
            Assert.DoesNotContain("dob", missing, StringComparison.Ordinal);

            var manual = await harness.Web.GetRequiredService<ResultService>().SaveManualAsync(new ResultEntryInput(
                fixture.Ids.MiaPersonId,
                await SqlAsync<Guid>("SELECT analyteId FROM config.analyteSpecimen WHERE analyteSpecimenId=@a", specimen),
                specimen, value, "ug/dL", "venous", null, null, new DateOnly(2026, 10, 20), null, new DateOnly(2026, 10, 22), null,
                await SqlAsync<Guid>("SELECT labOrganizationId FROM dbo.labResult WHERE labResultId=@a", fixture.Ids.MiaLabResultId),
                null, "MANUAL-" + suffix, "final", null, null, DepartmentReceivedAt: new DateTime(2026, 10, 22, 10, 0, 0)));
            var care = harness.Web.GetRequiredService<ICareData>();
            await care.ReconcileReceiptAsync(manual.LabResultId, null, CancellationToken.None);
            Assert.Equal(0, await SqlAsync<int>("SELECT COUNT(*) FROM dbo.workTask WHERE labResultId=@a AND (queueCode='missingInformation' OR title=N'Missing information detected')", manual.LabResultId));
            Assert.Equal(1, await SqlAsync<int>("SELECT COUNT(*) FROM dbo.workTask WHERE labResultId=@a AND queueCode='labReportingReview' AND taskStatus='open'", manual.LabResultId));
            // Patient demographics must not be copied into immutable report evidence.
            Assert.Equal(0, await SqlAsync<int>("SELECT COUNT(*) FROM dbo.labResult WHERE labResultId=@a AND (JSON_VALUE(reportedSnapshotJson,'$.firstName') IS NOT NULL OR JSON_VALUE(reportedSnapshotJson,'$.dob') IS NOT NULL)", manual.LabResultId));
            var user = await fixture.SignedInAsync("supervisor");
            Assert.Contains("Automatic report-completeness checks do not apply", await user.Http.GetStringAsync($"/results/{manual.LabResultId}"), StringComparison.Ordinal);

            // A receiving clinical profile can see the result but not its state-owned intake batch.
            await Execute(c, """
                UPDATE dbo.labResult SET owningTenantId=(SELECT tenantId FROM auth.tenant WHERE tenantCode='MD-BCHD') WHERE labResultId=@id;
                UPDATE dbo.workTask SET owningTenantId=(SELECT owningTenantId FROM dbo.labResult WHERE labResultId=@id) WHERE labResultId=@id;
                """, manual.LabResultId);
            await harness.AsAsync("cmbalt");
            Assert.Null((await harness.Web.GetRequiredService<IResultData>().GetLabResultAsync(manual.LabResultId))!.SourceChannel);
            await care.ReconcileReceiptAsync(manual.LabResultId, null, CancellationToken.None);
            Assert.Equal(0, await SqlAsync<int>("SELECT COUNT(*) FROM dbo.workTask WHERE labResultId=@a AND queueCode='missingInformation'", manual.LabResultId));
            var denied = await Assert.ThrowsAsync<SqlException>(() => harness.Web.GetRequiredService<Merit.Data.SqlServer.Db>().ExecuteAsync(
                "dbo.uspIsManualReceiptSource", new { intakeBatchId = imported.IntakeBatchId, rootTenantId = Guid.NewGuid(), manual = false }, CancellationToken.None));
            Assert.Equal(229, denied.Number);
            await harness.AsAsync("admin");

            // The batch channel is authoritative: a JSON marker cannot exempt an imported report.
            await Execute(c, """
                UPDATE dbo.labResult SET reportedSnapshotJson=JSON_MODIFY(JSON_MODIFY(reportedSnapshotJson,'$.source','manualEntry'),'$.lastName',NULL) WHERE labResultId=@id;
                DELETE dbo.workTask WHERE labResultId=@id AND (queueCode='missingInformation' OR title=N'Missing information detected');
                """, importedResult);
            await care.ReconcileReceiptAsync(importedResult, null, CancellationToken.None);
            Assert.Equal(1, await SqlAsync<int>("SELECT COUNT(*) FROM dbo.workTask WHERE labResultId=@a AND title=N'Missing information detected'", importedResult));
            Assert.Contains("lastName", await SqlAsync<string>("SELECT detail FROM dbo.workTask WHERE labResultId=@a AND queueCode='missingInformation' AND taskStatus='open'", importedResult), StringComparison.Ordinal);

            // Simulate a pre-fix finding and pending request. Re-evaluation must not rewrite history.
            await Execute(c, """
                INSERT dbo.workTask(workTaskId,rootTenantId,owningTenantId,queueCode,title,personId,labResultId,taskStatus,completedUtc,completedByUserId)
                SELECT NEWID(),r.rootTenantId,r.owningTenantId,'receiptFinding',N'Missing information detected',r.personId,r.labResultId,'done','2026-10-23T12:00:00',b.submittedByUserId
                FROM dbo.labResult r JOIN dbo.intakeBatch b ON b.intakeBatchId=r.intakeBatchId WHERE r.labResultId=@id;
                INSERT dbo.workTask(workTaskId,rootTenantId,owningTenantId,queueCode,title,personId,labResultId)
                SELECT NEWID(),rootTenantId,owningTenantId,'missingInformation',N'Missing Information Received',personId,labResultId FROM dbo.labResult WHERE labResultId=@id;
                """, manual.LabResultId);
            await care.ReconcileReceiptAsync(manual.LabResultId, null, CancellationToken.None);
            Assert.Equal(new DateTime(2026, 10, 23, 12, 0, 0), await SqlAsync<DateTime>("SELECT completedUtc FROM dbo.workTask WHERE labResultId=@a AND title=N'Missing information detected'", manual.LabResultId));
            var request = await SqlAsync<Guid>("SELECT workTaskId FROM dbo.workTask WHERE labResultId=@a AND queueCode='missingInformation' AND taskStatus='open'", manual.LabResultId);
            await harness.Web.GetRequiredService<CareService>().ReceiveInformationAsync(request, new DateOnly(2026, 10, 23), "Reviewed original paper report: all requested information present; pre-fix manual transcription finding resolved explicitly.", true, CancellationToken.None);
            Assert.Equal("done", await SqlAsync<string>("SELECT taskStatus FROM dbo.workTask WHERE workTaskId=@a AND resolutionNote IS NOT NULL AND completedByUserId IS NOT NULL", request));
            Assert.Equal(1, await SqlAsync<int>("SELECT COUNT(*) FROM logs.auditEvent WHERE entityType='workTask' AND entityId=@a AND eventAction='update'", request));
            await harness.Web.GetRequiredService<CareService>().VerifyTimelineAsync(fixture.Ids.MiaPersonId, manual.LabResultId,
                new DateOnly(2026, 10, 20), "Original report checked: Maryland child eligibility and actual final-result date confirmed.", true, CancellationToken.None);
            Assert.Equal(0, await SqlAsync<int>("SELECT COUNT(*) FROM dbo.workTask WHERE labResultId=@a AND queueCode='labReportingReview' AND taskStatus='open'", manual.LabResultId));
            Assert.Equal(template == "mdLabElevated" ? 1 : 0, await SqlAsync<int>("SELECT COUNT(*) FROM dbo.workTask WHERE labResultId=@a AND title=N'Late laboratory report detected'", manual.LabResultId));
            Assert.Equal(new DateTime(2026, 10, 23, 12, 0, 0), await SqlAsync<DateTime>("SELECT completedUtc FROM dbo.workTask WHERE labResultId=@a AND title=N'Missing information detected'", manual.LabResultId));
        }
        finally
        {
            await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
            await Execute(c, "UPDATE config.notificationRule SET isActive=0 WHERE notificationRuleId=@id", rule);
        }
    }

    private static async Task Execute(SqlConnection connection, string sql, Guid id)
    {
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync();
    }
}
