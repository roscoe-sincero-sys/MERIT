using System.Net;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

public sealed partial class PatientCareTests
{
    [SqlFact]
    public async Task HomePrioritizesImportForClerksAndFollowUpForCaseManagers()
    {
        // FirstSignInTests owns enrollment of the seeded intake account. Keep this check independent of test order.
        await using var account=await SyntheticAccount.CreateAsync(fixture.Factory,"homeclerk");
        await account.ScalarAsync<int>("""
            INSERT auth.userAssignment(userAssignmentId,appUserId,roleId,tenantId,includeDescendants,accessLevel,startDate)
            SELECT NEWID(),@id,r.roleId,u.homeTenantId,1,'full','2020-01-01'
              FROM auth.role r JOIN auth.appUser u ON u.rootTenantId=r.rootTenantId AND u.appUserId=@id
             WHERE r.roleCode='intakeClerk' AND r.isDeleted=0;
            SELECT 0;
            """);
        using var clerk=fixture.Anonymous();
        await account.SignInAsync(clerk,MeritFixture.Password);
        var home=await clerk.Http.GetStringAsync("/");
        var start=home[home.IndexOf("<section class=\"merit-section\" aria-labelledby=\"start-heading\">",StringComparison.Ordinal)..];
        start=start[..start.IndexOf("</section>",StringComparison.Ordinal)];
        Assert.Contains("class=\"usa-button \" href=\"/intake\"",start);
        Assert.Contains("Find a patient / enter a paper result",start);
        Assert.DoesNotContain("Work on my assigned follow-up",start);
        Assert.DoesNotContain("/config/thresholds",start);
        var manager=await fixture.SignedInAsync("supervisor");
        home=await manager.Http.GetStringAsync("/");
        Assert.Contains("class=\"usa-button\" href=\"/app/queues?mine=true\">Work on my assigned follow-up",home);
    }

    [SqlFact]
    public async Task ExplicitCorrectionWithoutAccessionPreservesSourceAndRejectsStaleOrWrongPatientEdits()
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        var person = await TestingPatient(c); var original = Guid.NewGuid();
        try
        {
            await CloneReviewResult(c, original, value: 1, personOverride: person);
            var user = await fixture.SignedInAsync("supervisor");
            var entry = await user.Http.GetStringAsync($"/people/{person}/results/new");
            Assert.Contains("Date of birth:", entry); Assert.Contains("2022", entry);
            Assert.Contains("id=\"CollectedDate\" name=\"CollectedDate\" type=\"date\" value=\"\"", entry);
            var specimen = await Sql(c,"SELECT analyteSpecimenId FROM dbo.labResult WHERE labResultId=@id",("@id",original));
            var analyte = await Sql(c,"SELECT analyteId FROM config.analyteSpecimen WHERE analyteSpecimenId=@id",("@id",specimen));
            var lab = await Sql(c,"SELECT labOrganizationId FROM dbo.labResult WHERE labResultId=@id",("@id",original));
            var version = Convert.ToHexString((byte[])(await Sql(c,"SELECT rowVersion FROM dbo.labResult WHERE labResultId=@id",("@id",original)))!);
            Task<HttpResponseMessage> Correct(Guid patient, string stamp, bool confirmed = true) => user.NoRedirect.PostAsync($"/people/{patient}/results", Form(
                ("AnalyteId",analyte),("AnalyteSpecimenId",specimen),("LabOrganizationId",lab),("Unit","ug/dL"),("ValueText","2"),
                ("CollectedDate","2026-09-01"),("SampleType","venous"),("TestMethod","icpMs"),("ResultStatus","corrected"),
                ("CorrectsLabResultId",original),("OriginalRowVersion",stamp),("CorrectionReason","Transcription: verified original source"),("ConfirmCorrection",confirmed ? "true":"false")));
            await user.Http.GetStringAsync($"/results/{original}/correct");
            using(var unconfirmed=await Correct(person,version,false)) Assert.Contains("Verify the original",await unconfirmed.Content.ReadAsStringAsync());
            using(var wrong=await Correct(fixture.Ids.MiaPersonId,version)) Assert.Contains("original result is no longer",await wrong.Content.ReadAsStringAsync());
            using(var stale=await Correct(person,"0000000000000000")) Assert.Equal(HttpStatusCode.Conflict,stale.StatusCode);
            Assert.Equal("valid",await Sql(c,"SELECT resultRowStatus FROM dbo.labResult WHERE labResultId=@id",("@id",original)));
            using(var saved=await Correct(person,version)) { saved.EnsureSuccessStatusCode(); Assert.Contains("Result saved",await saved.Content.ReadAsStringAsync()); }
            var replacement=(Guid)(await Sql(c,"SELECT labResultId FROM dbo.labResult WHERE supersedesLabResultId=@id",("@id",original)))!;
            Assert.Equal("superseded",await Sql(c,"SELECT resultRowStatus FROM dbo.labResult WHERE labResultId=@id",("@id",original)));
            Assert.Equal(1,await Sql(c,"SELECT COUNT(*) FROM logs.auditEvent WHERE entityType='labResult' AND entityId=@id AND eventAction='update'",("@id",original)));
            Assert.Equal("{}",await Sql(c,"SELECT reportedSnapshotJson FROM dbo.labResult WHERE labResultId=@id",("@id",original)));
            Assert.Equal(await Sql(c,"SELECT receivedUtc FROM dbo.labResult WHERE labResultId=@id",("@id",original)),
                await Sql(c,"SELECT receivedUtc FROM dbo.labResult WHERE labResultId=@id",("@id",replacement)));
            var old=await user.Http.GetStringAsync($"/results/{original}"); Assert.Contains($"/results/{replacement}",old);
            var detail=await user.Http.GetStringAsync($"/results/{replacement}"); Assert.Contains($"/results/{original}",detail); Assert.Contains("preserved at processing",detail);
            string evaluation=(string)(await Sql(c,"SELECT classificationSnapshotJson FROM dbo.labResult WHERE labResultId=@id",("@id",replacement)))!;
            using(var json=JsonDocument.Parse(evaluation)) {
                Assert.Equal("2026-09-01",json.RootElement.GetProperty("CollectionDate").GetString());
                Assert.NotEmpty(json.RootElement.GetProperty("Thresholds").EnumerateArray());
            }
            var rv=Convert.ToHexString((byte[])(await Sql(c,"SELECT rowVersion FROM dbo.labResult WHERE labResultId=@id",("@id",replacement)))!);
            using(var missing=await user.NoRedirect.PostAsync($"/results/{replacement}/withdraw",Form(("rowVersion",rv),("confirmed","true")))) Assert.Contains("reason of 1 to 500",await missing.Content.ReadAsStringAsync());
            await Saved(await user.NoRedirect.PostAsync($"/results/{replacement}/withdraw",Form(("rowVersion",rv),("reason","Wrong patient on source review"),("confirmed","true"))));
            Assert.Equal("enteredInError",await Sql(c,"SELECT resultRowStatus FROM dbo.labResult WHERE labResultId=@id",("@id",replacement)));
            Assert.Equal(evaluation,await Sql(c,"SELECT classificationSnapshotJson FROM dbo.labResult WHERE labResultId=@id",("@id",replacement)));
            using(var repeated=await user.NoRedirect.PostAsync($"/results/{replacement}/withdraw",Form(("rowVersion",rv),("reason","Repeated"),("confirmed","true")))) Assert.Contains("Only a current valid result",await repeated.Content.ReadAsStringAsync());
        }
        finally
        {
            await Sql(c,"DELETE dbo.caseResultLink WHERE personId=@p; UPDATE dbo.labResult SET supersedesLabResultId=NULL WHERE personId=@p;",("@p",person));
            await CleanWorkflow(c,[person]);
        }
    }

    [SqlFact]
    public async Task ThreeRealClosureReopenCyclesRemainChronologicalAndDoNotInventResultMembership()
    {
        await using var c=new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        var person=await TestingPatient(c); var result=Guid.NewGuid();
        try
        {
            await CloneReviewResult(c,result,value:1,personOverride:person);
            var caseId=await WorkflowCase(c,person,result); var user=await fixture.SignedInAsync("supervisor");
            for(int cycle=1;cycle<=3;cycle++)
            {
                await user.Http.GetStringAsync($"/cases/{caseId}");
                var version=Convert.ToHexString((byte[])(await Sql(c,"SELECT rowVersion FROM dbo.exposureCase WHERE exposureCaseId=@id",("@id",caseId)))!);
                using(var closed=await user.NoRedirect.PostAsync($"/cases/{caseId}/close",Form(("rowVersion",version),("closedDate",DateTime.Now.ToString("yyyy-MM-dd")),("closureReason","other"),("note",$"Episode {cycle} reviewed")))) closed.EnsureSuccessStatusCode();
                version=Convert.ToHexString((byte[])(await Sql(c,"SELECT rowVersion FROM dbo.exposureCase WHERE exposureCaseId=@id",("@id",caseId)))!);
                using(var reopened=await user.NoRedirect.PostAsync($"/cases/{caseId}/reopen",Form(("rowVersion",version),("reason",$"Cycle {cycle} new follow-up")))) reopened.EnsureSuccessStatusCode();
                // Routine review task is completed for the next independent closure cycle.
                await Sql(c,"UPDATE dbo.workTask SET taskStatus='done' WHERE exposureCaseId=@id",("@id",caseId));
            }
            var html=await user.Http.GetStringAsync($"/cases/{caseId}");
            for(int cycle=1;cycle<=3;cycle++) { Assert.Contains($"Episode {cycle} reviewed",html); Assert.Contains($"Cycle {cycle} new follow-up",html); }
            Assert.Contains("No result associations were recorded",html);
            var all=await user.Http.GetStringAsync($"/cases/{caseId}?results=all"); Assert.Contains("Legacy qualifying/highest result; episode unrecorded",all);
            Assert.Equal(3,await Sql(c,"SELECT COUNT(*) FROM dbo.caseActivity WHERE exposureCaseId=@id AND lifecycleKind='closed'",("@id",caseId)));
            Assert.Equal(3,await Sql(c,"SELECT COUNT(*) FROM dbo.caseActivity WHERE exposureCaseId=@id AND lifecycleKind='reopened'",("@id",caseId)));
            var patientPage=await user.Http.GetStringAsync($"/people/{person}");
            Assert.Contains("Reopened 3 time(s)",patientPage);
            using var timeline=JsonDocument.Parse(await user.Http.GetStringAsync($"/api/v1/people/{person}/timeline"));
            var events=timeline.RootElement.GetProperty("events").EnumerateArray().Where(e=>e.GetProperty("kind").GetString()=="case").ToList();
            Assert.Equal(7,events.Count); Assert.All(events,e=>Assert.Equal(e.GetProperty("date").GetString(),e.GetProperty("endDate").GetString()));

            // A newly processed result is associated explicitly with this reopening, while
            // the older qualifying result remains legacy evidence rather than guessed membership.
            var specimen=await Sql(c,"SELECT analyteSpecimenId FROM dbo.labResult WHERE labResultId=@id",("@id",result));
            var analyte=await Sql(c,"SELECT analyteId FROM config.analyteSpecimen WHERE analyteSpecimenId=@id",("@id",specimen));
            var lab=await Sql(c,"SELECT labOrganizationId FROM dbo.labResult WHERE labResultId=@id",("@id",result));
            await user.Http.GetStringAsync($"/people/{person}/results/new");
            using(var saved=await user.NoRedirect.PostAsync($"/people/{person}/results",Form(("AnalyteId",analyte),("AnalyteSpecimenId",specimen),
                ("LabOrganizationId",lab),("Unit","ug/dL"),("ValueText","2"),("CollectedDate","2026-09-02"),("SampleType","venous"),("TestMethod","icpMs"),("ResultStatus","final")))) saved.EnsureSuccessStatusCode();
            var opening=(Guid)(await Sql(c,"SELECT openingActivityId FROM dbo.caseResultLink WHERE exposureCaseId=@id",("@id",caseId)))!;
            Assert.Equal("reopened",await Sql(c,"SELECT lifecycleKind FROM dbo.caseActivity WHERE caseActivityId=@id",("@id",opening)));
            html=await user.Http.GetStringAsync($"/cases/{caseId}");
            Assert.DoesNotContain("No result associations were recorded",html); Assert.Contains("Current episode",html);
            await Sql(c,"UPDATE dbo.workTask SET taskStatus='done' WHERE exposureCaseId=@id",("@id",caseId));
            var rv=Convert.ToHexString((byte[])(await Sql(c,"SELECT rowVersion FROM dbo.exposureCase WHERE exposureCaseId=@id",("@id",caseId)))!);
            using(var closed=await user.NoRedirect.PostAsync($"/cases/{caseId}/close",Form(("rowVersion",rv),("closedDate",DateTime.Now.ToString("yyyy-MM-dd")),("closureReason","other"),("note","Fourth closure after source review")))) closed.EnsureSuccessStatusCode();
            rv=Convert.ToHexString((byte[])(await Sql(c,"SELECT rowVersion FROM dbo.exposureCase WHERE exposureCaseId=@id",("@id",caseId)))!);
            using(var reopened=await user.NoRedirect.PostAsync($"/cases/{caseId}/reopen",Form(("rowVersion",rv),("reason","Fourth episode")))) reopened.EnsureSuccessStatusCode();
            html=await user.Http.GetStringAsync($"/cases/{caseId}"); Assert.Contains("No result associations were recorded",html);
            Assert.Equal(opening,await Sql(c,"SELECT openingActivityId FROM dbo.caseResultLink WHERE exposureCaseId=@id",("@id",caseId)));
            all=await user.Http.GetStringAsync($"/cases/{caseId}?results=all"); Assert.Contains("Associated with this case; earlier or unrecorded episode",all);
        }
        finally { await Sql(c,"DELETE dbo.caseResultLink WHERE personId=@p",("@p",person)); await CleanWorkflow(c,[person]); }
    }
}
