using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

public sealed partial class PatientCareTests
{
    private async Task<Guid> TestingPatient(SqlConnection c, string dob = "2022-01-01")
    {
        var id = Guid.NewGuid();
        await Sql(c, """
            INSERT dbo.person(personId,rootTenantId,owningTenantId,lastName,lastNameKey,dob)
            SELECT @id,rootTenantId,owningTenantId,N'Testing plan patient',N'TESTINGPLANPATIENT',@dob FROM dbo.person WHERE personId=@original;
            """, ("@id", id), ("@dob", dob), ("@original", fixture.Ids.MiaPersonId));
        return id;
    }
    private async Task<Guid> TestingResult(SqlConnection c, Guid person, string draw, decimal value, string sample = "capillary")
    {
        var id = Guid.NewGuid(); await CloneReviewResult(c, id, sample, "{\"state\":\"MD\"}", value, person);
        await Sql(c, "UPDATE dbo.labResult SET collectedDate=@draw,ageAtCollectionMonths=DATEDIFF(MONTH,(SELECT dob FROM dbo.person WHERE personId=@person),@draw) WHERE labResultId=@id",
            ("@id", id), ("@draw", draw), ("@person", person));
        return id;
    }
    private static Task CleanTesting(SqlConnection c, Guid person) => Sql(c, """
        DELETE dbo.workTask WHERE personId=@person; DELETE dbo.caseActivity WHERE exposureCaseId IN(SELECT exposureCaseId FROM dbo.exposureCase WHERE personId=@person);
        DELETE dbo.exposureCase WHERE personId=@person; DELETE dbo.personActivity WHERE personId=@person;
        DELETE dbo.labResult WHERE personId=@person; DELETE dbo.personAccess WHERE personId=@person; DELETE dbo.person WHERE personId=@person;
        """, ("@person", person));
    private async Task<HttpResponseMessage> Plan(Guid person, Guid id, string kind, Guid? result = null, Guid? caseId = null, bool remediation = false, string? target = null)
    {
        var user = await fixture.SignedInAsync("supervisor"); await user.Http.GetStringAsync($"/people/{person}/care/testing");
        return await user.NoRedirect.PostAsync($"/people/{person}/care/testing", Form(("workTaskId", id), ("planKind", kind), ("labResultId", result),
            ("exposureCaseId", caseId), ("targetDate", target), ("evidence", "Synthetic original reports and clinician-approved Maryland plan reviewed, including episode and remediation evidence."),
            ("clinicianConfirmed", "true"), ("remediationConfirmed", remediation ? "true" : "false")));
    }

    [SqlTheory]
    [InlineData("3.5", "2026-03-26", false)]
    [InlineData("9.99", "2026-03-26", false)]
    [InlineData("10", "2026-01-29", false)]
    [InlineData("19.99", "2026-01-29", false)]
    [InlineData("20", "2026-01-15", false)]
    [InlineData("44.99", "2026-01-15", false)]
    [InlineData("45", "2026-01-01", true)]
    public async Task ConfirmationUsesVerifiedWindowsAndUrgentCoordination(string level, string expected, bool urgent)
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        var person = await TestingPatient(c);
        try
        {
            var result = await TestingResult(c, person, "2026-01-01", decimal.Parse(level, System.Globalization.CultureInfo.InvariantCulture)); var task = Guid.NewGuid();
            await Saved(await Plan(person, task, "confirmation", result));
            Assert.Equal(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), await Sql(c, "SELECT dueDate FROM dbo.workTask WHERE workTaskId=@id", ("@id", task)));
            Assert.Equal(urgent, await Sql(c, "SELECT isUrgent FROM dbo.workTask WHERE workTaskId=@id", ("@id", task)));
            var user = await fixture.SignedInAsync("supervisor");
            using var bulk = await user.NoRedirect.PostAsJsonAsync("/api/v1/tasks/bulk", new { taskIds = new[] { task }, action = "complete" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, bulk.StatusCode);
        }
        finally { await CleanTesting(c, person); }
    }

    [SqlFact]
    public async Task InitialCapillaryDIsNotResetByBelowLevelEOrElevatedFAndOnlyVenousGCompletesIt()
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync(); var person = await TestingPatient(c);
        try
        {
            await TestingResult(c, person, "2026-01-01", 1); await TestingResult(c, person, "2026-01-15", 2); await TestingResult(c, person, "2026-02-01", 3);
            var d = await TestingResult(c, person, "2026-03-01", 4); var e = await TestingResult(c, person, "2026-03-15", 2); var f = await TestingResult(c, person, "2026-03-25", 4);
            using (var wrongInitial = await Plan(person, Guid.NewGuid(), "confirmation", f)) Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongInitial.StatusCode);
            var task = Guid.NewGuid(); await Saved(await Plan(person, task, "confirmation", d));
            await AsCareData(data => data.ReconcileTestingAsync(f, default)); await AsCareData(data => data.ReconcileTestingAsync(f, default));
            Assert.Equal(new DateTime(2026,5,24), await Sql(c, "SELECT dueDate FROM dbo.workTask WHERE workTaskId=@id", ("@id", task)));
            Assert.Equal(1, await Sql(c, "SELECT COUNT(*) FROM dbo.workTask WHERE personId=@person AND queueCode='testingReview' AND taskStatus='open'", ("@person", person)));
            var user = await fixture.SignedInAsync("supervisor");
            async Task<HttpResponseMessage> Complete(Guid result) => await user.NoRedirect.PostAsync($"/people/{person}/care/testing/resolve", Form(("workTaskId",task), ("completedLabResultId",result), ("evidence","Verified actual source test"), ("confirmed","true")));
            using (var capillary = await Complete(e)) Assert.Equal(HttpStatusCode.UnprocessableEntity, capillary.StatusCode);
            var g = await TestingResult(c, person, "2026-04-01", 4, "venous"); await Saved(await Complete(g));
            var next = Guid.NewGuid(); await Saved(await Plan(person, next, "early", g));
            Assert.Equal(new DateTime(2026,7,1), await Sql(c, "SELECT dueDate FROM dbo.workTask WHERE workTaskId=@id", ("@id",next)));
            Assert.Equal("done", await Sql(c,"SELECT taskStatus FROM dbo.workTask WHERE workTaskId=@id",("@id",task)));
        }
        finally { await CleanTesting(c,person); }
    }

    [SqlFact]
    public async Task ScreeningUsesBirthdaysRejectsDuplicateProofAndKeepsCatchupBeforeAgeSix()
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync(); var person = await TestingPatient(c,"2023-01-01");
        try
        {
            var twelve = Guid.NewGuid(); var twentyFour = Guid.NewGuid();
            await Saved(await Plan(person,twelve,"screen12")); await Saved(await Plan(person,twentyFour,"screen24"));
            Assert.Equal(new DateTime(2024,1,1), await Sql(c,"SELECT dueDate FROM dbo.workTask WHERE workTaskId=@id",("@id",twelve)));
            Assert.Equal(new DateTime(2025,1,1), await Sql(c,"SELECT dueDate FROM dbo.workTask WHERE workTaskId=@id",("@id",twentyFour)));
            var result = await TestingResult(c,person,"2024-02-01",1); var user = await fixture.SignedInAsync("supervisor");
            async Task<HttpResponseMessage> Complete(Guid task) => await user.NoRedirect.PostAsync($"/people/{person}/care/testing/resolve",Form(("workTaskId",task),("completedLabResultId",result),("evidence","Actual age-appropriate screening verified"),("confirmed","true")));
            using(var wrongVisit = await Complete(twentyFour)) Assert.Equal(HttpStatusCode.UnprocessableEntity,wrongVisit.StatusCode);
            await Saved(await Complete(twelve));
            result = await TestingResult(c,person,"2025-02-01",1);
            await Saved(await Complete(twentyFour));
            using(var duplicate = await Plan(person,Guid.NewGuid(),"screen24")) Assert.Equal(HttpStatusCode.UnprocessableEntity,duplicate.StatusCode);
        }
        finally { await CleanTesting(c,person); }
    }

    [SqlTheory]
    [InlineData("early", "10", "2026-04-30")]
    [InlineData("later", "10", "2026-07-31")]
    [InlineData("early", "20", "2026-02-28")]
    [InlineData("later", "20", "2026-04-30")]
    [InlineData("later", "4", "2026-10-31")]
    [InlineData("later", "2", "2026-04-30")]
    public async Task VenousFollowupUsesCalendarMonthsAndRequiresCurrentReviewedAnchor(string kind,string level,string expected)
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync(); var person=await TestingPatient(c);
        try
        {
            var result=await TestingResult(c,person,"2026-01-31",decimal.Parse(level,System.Globalization.CultureInfo.InvariantCulture),"venous"); var task=Guid.NewGuid();
            await Saved(await Plan(person,task,kind,result));
            Assert.Equal(DateTime.Parse(expected,System.Globalization.CultureInfo.InvariantCulture),await Sql(c,"SELECT dueDate FROM dbo.workTask WHERE workTaskId=@id",("@id",task)));
            using(var extension=await Plan(person,Guid.NewGuid(),kind,result,target:"2027-01-01")) Assert.Equal(HttpStatusCode.UnprocessableEntity,extension.StatusCode);
        }
        finally { await CleanTesting(c,person); }
    }

    [SqlFact]
    public async Task BelowLevelClosureNeedsRemediationConsecutiveLowResultsAndFreshReadiness()
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync(); var person=await TestingPatient(c); var caseId=Guid.NewGuid();
        try
        {
            var first=await TestingResult(c,person,"2026-01-01",5,"venous");
            await Sql(c,"""
                INSERT dbo.exposureCase(exposureCaseId,rootTenantId,owningTenantId,caseNumber,personId,analyteId,population,classification,computedClassification,caseStatus,qualifyingLabResultId,workflowTemplate,openedDate)
                SELECT @case,rootTenantId,owningTenantId,CONCAT('TEST-',LEFT(CONVERT(VARCHAR(36),@case),8)),@person,analyteId,population,classification,computedClassification,'open',@result,workflowTemplate,'2026-01-01'
                FROM dbo.exposureCase WHERE exposureCaseId=@original;
                """,("@case",caseId),("@person",person),("@result",first),("@original",fixture.Ids.MiaCaseId));
            await TestingResult(c,person,"2026-02-01",2,"venous"); var last=await TestingResult(c,person,"2026-04-30",2,"venous");
            using(var tooSoon=await Plan(person,Guid.NewGuid(),"discharge",last,caseId,true)) Assert.Equal(HttpStatusCode.UnprocessableEntity,tooSoon.StatusCode);
            await Sql(c,"UPDATE dbo.labResult SET collectedDate='2026-05-01' WHERE labResultId=@id",("@id",last));
            using(var noRemediation=await Plan(person,Guid.NewGuid(),"discharge",last,caseId)) Assert.Equal(HttpStatusCode.UnprocessableEntity,noRemediation.StatusCode);
            await Saved(await Plan(person,Guid.NewGuid(),"discharge",last,caseId,true));
            var rebound=await TestingResult(c,person,"2026-06-01",4,"venous");
            var user=await fixture.SignedInAsync("supervisor"); await user.Http.GetStringAsync($"/cases/{caseId}");
            async Task<HttpResponseMessage> Close()
            {
                var version=(byte[])(await Sql(c,"SELECT rowVersion FROM dbo.exposureCase WHERE exposureCaseId=@id",("@id",caseId)))!;
                return await user.NoRedirect.PostAsync($"/cases/{caseId}/close",Form(("closureReason","belowLevelOnRetest"),("closedDate","2026-10-01"),("note","Remediation evidence reviewed"),("rowVersion","0x"+Convert.ToHexString(version))));
            }
            using(var stale=await Close()) Assert.Equal(HttpStatusCode.UnprocessableEntity,stale.StatusCode);
            await Sql(c,"UPDATE dbo.labResult SET resultRowStatus='enteredInError' WHERE labResultId=@id",("@id",rebound));
            using(var closed=await Close()) Assert.Equal(HttpStatusCode.OK,closed.StatusCode);
            Assert.Equal("closed",await Sql(c,"SELECT caseStatus FROM dbo.exposureCase WHERE exposureCaseId=@id",("@id",caseId)));
        }
        finally { await CleanTesting(c,person); }
    }
}
