using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Merit.Application;
using Merit.Application.Data;
using Merit.Application.Jobs;
using Merit.Application.Security;
using Merit.Data.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Merit.IntegrationTests;

public sealed partial class PatientCareTests
{
    private async Task<Guid> WorkflowCase(SqlConnection c,Guid person,Guid result)
    {
        var id=Guid.NewGuid();
        await Sql(c,"""
            INSERT dbo.exposureCase(exposureCaseId,rootTenantId,owningTenantId,caseNumber,personId,analyteId,population,classification,computedClassification,caseStatus,qualifyingLabResultId,workflowTemplate,openedDate)
            SELECT @case,rootTenantId,owningTenantId,CONCAT('WF-',LEFT(CONVERT(VARCHAR(36),@case),8)),@person,analyteId,population,classification,computedClassification,'open',@result,workflowTemplate,'2026-09-01'
            FROM dbo.exposureCase WHERE exposureCaseId=@original;
            EXEC dbo.uspRefreshPersonAccess @personId=@person;
            """,("@case",id),("@person",person),("@result",result),("@original",fixture.Ids.MiaCaseId));
        return id;
    }
    private async Task<Guid> WorkflowDefinition(SqlConnection c,Guid patientA,Guid patientB,Guid? caseA=null,bool publish=true,params (string,object?)[] overrides)
    {
        var id=Guid.NewGuid(); var user=await fixture.SignedInAsync("admin"); await user.Http.GetStringAsync("/config/workflows");
        var specimen=await Sql(c,"SELECT analyteSpecimenId FROM dbo.labResult WHERE labResultId=@id",("@id",fixture.Ids.MiaLabResultId));
        var values=new Dictionary<string,object?> { ["WorkflowDefinitionId"]=id,["Name"]="Regression workflow "+id,["Version"]=1,["Description"]="Synthetic approved task; verify source evidence.",
            ["TriggerEvent"]="receivedResult",["Population"]="all",["CompletionKind"]="internal",["EffectiveFrom"]="2020-01-01",["TimeZoneId"]="Eastern Standard Time",
            ["AnalyteSpecimenId"]=specimen,["MinimumValue"]="1000",["DueInDays"]="7",["MinimumContactAttempts"]="0",["ClosureReason"]="all",["DayBasis"]="calendar" };
        foreach(var (key,value) in overrides) values[key]=value;
        await Saved(await user.NoRedirect.PostAsync("/config/workflows/draft",Form(values.Select(v=>(v.Key,v.Value)).ToArray())));
        using var preview=await user.NoRedirect.PostAsync($"/config/workflows/{id}/preview",Form(("samplePersonId",patientA),("secondSamplePersonId",patientB),("sampleCaseId",caseA)));
        Assert.Equal(HttpStatusCode.OK,preview.StatusCode);
        string html=await preview.Content.ReadAsStringAsync(); Assert.Contains("Publish this previewed version",html,StringComparison.Ordinal);
        if(publish)
        {
            var stamp=await Sql(c,"SELECT CONVERT(VARCHAR(16),CAST(rowVersion AS BINARY(8)),2) FROM config.workflowDefinition WHERE workflowDefinitionId=@id",("@id",id));
            Assert.Matches("^[0-9A-Fa-f]{16}$",Assert.IsType<string>(stamp));
            await Saved(await user.NoRedirect.PostAsync($"/config/workflows/{id}/publish",Form(("versionStamp",stamp),("approvalReference","Synthetic program-owner approval"),("approved","true"),("samplePersonId",patientA),("secondSamplePersonId",patientB),("sampleCaseId",caseA))));
        }
        return id;
    }
    private async Task EvaluateWork(Guid person)
    {
        var user=await fixture.SignedInAsync("supervisor"); await user.Http.GetStringAsync($"/people/{person}/work");
        await Saved(await user.NoRedirect.PostAsync($"/people/{person}/work/evaluate",Form()));
    }
    private async Task<HttpResponseMessage> ResolveWork(Guid task,string action="complete",string outcome="completed",Guid? patientRecipient=null,string note="Verified actual source evidence",params (string,object?)[] extra)
    {
        var user=await fixture.SignedInAsync("supervisor"); await user.Http.GetStringAsync($"/work/tasks/{task}");
        var values=new Dictionary<string,object?> { ["personActivityId"]=Guid.NewGuid(),["action"]=action,["activityDate"]="2026-10-02",["note"]=note,
            ["outcome"]=outcome,["audience"]=patientRecipient is null ? "internal":"patient",["channel"]=patientRecipient is null ? "internal":"phone",["contactPersonId"]=patientRecipient,["confirmed"]="true" };
        foreach(var (key,value) in extra) values[key]=value;
        return await user.NoRedirect.PostAsync($"/work/tasks/{task}",Form(values.Select(v=>(v.Key,v.Value)).ToArray()));
    }
    private async Task<HttpResponseMessage> CloseReviewed(SqlConnection c,Guid caseId,string reason,string? token=null,bool confirmed=true)
    {
        var user=await fixture.SignedInAsync("supervisor"); await user.Http.GetStringAsync($"/cases/{caseId}");
        var version=(byte[])(await Sql(c,"SELECT rowVersion FROM dbo.exposureCase WHERE exposureCaseId=@id",("@id",caseId)))!;
        token??=(string)(await Sql(c,"SELECT dbo.udfCaseClosureReviewStamp(@id)",("@id",caseId)))!;
        return await user.NoRedirect.PostAsync($"/cases/{caseId}/close",Form(("rowVersion","0x"+Convert.ToHexString(version)),("closureReason",reason),("closedDate","2026-10-03"),("note","Reviewed required work and source evidence"),("closureReviewToken",token),("closureReviewConfirmed",confirmed ? "true":"false")));
    }
    private static Task CleanWorkflow(SqlConnection c,Guid[] people,Guid? definition=null) => Sql(c,"""
        DELETE dbo.workTask WHERE workflowDefinitionId=@definition OR personId IN(SELECT CONVERT(UNIQUEIDENTIFIER,[value]) FROM OPENJSON(@people))
         OR exposureCaseId IN(SELECT exposureCaseId FROM dbo.exposureCase WHERE personId IN(SELECT CONVERT(UNIQUEIDENTIFIER,[value]) FROM OPENJSON(@people)));
        DELETE dbo.personActivity WHERE personId IN(SELECT CONVERT(UNIQUEIDENTIFIER,[value]) FROM OPENJSON(@people));
        DELETE dbo.caseActivity WHERE exposureCaseId IN(SELECT exposureCaseId FROM dbo.exposureCase WHERE personId IN(SELECT CONVERT(UNIQUEIDENTIFIER,[value]) FROM OPENJSON(@people)));
        DELETE dbo.exposureCase WHERE personId IN(SELECT CONVERT(UNIQUEIDENTIFIER,[value]) FROM OPENJSON(@people));
        DELETE dbo.labResult WHERE personId IN(SELECT CONVERT(UNIQUEIDENTIFIER,[value]) FROM OPENJSON(@people));
        DELETE dbo.personAccess WHERE personId IN(SELECT CONVERT(UNIQUEIDENTIFIER,[value]) FROM OPENJSON(@people));
        DELETE dbo.person WHERE personId IN(SELECT CONVERT(UNIQUEIDENTIFIER,[value]) FROM OPENJSON(@people));
        DELETE dbo.jobQueue WHERE jobType='workflowEvaluation' AND JSON_VALUE(payloadJson,'$.workflowDefinitionId')=CONVERT(VARCHAR(36),@definition);
        DELETE config.workflowDefinition WHERE workflowDefinitionId=@definition;
        """,("@people",JsonSerializer.Serialize(people)),("@definition",definition));

    [SqlFact]
    public async Task DraftPreviewPublicationAndRealWorkerBackfillRespectConditionsAndPreserveVersion()
    {
        await using var c=new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync(); var a=await TestingPatient(c); var b=await TestingPatient(c); Guid? rule=null;
        try
        {
            await CloneReviewResult(c,Guid.NewGuid(),value:9999999,personOverride:a); await CloneReviewResult(c,Guid.NewGuid(),value:1,personOverride:b);
            rule=await WorkflowDefinition(c,a,b,null,true,("MinimumValue","9999998"));
            var admin=await fixture.SignedInAsync("admin");
            await Saved(await admin.NoRedirect.PostAsync($"/config/workflows/{rule}/evaluate",Form()));
            string json=(string)(await Sql(c,"SELECT TOP(1) * FROM dbo.jobQueue WHERE jobType='workflowEvaluation' AND JSON_VALUE(payloadJson,'$.workflowDefinitionId')=CONVERT(VARCHAR(36),@id) FOR JSON PATH,WITHOUT_ARRAY_WRAPPER",("@id",rule)))!;
            var job=JsonSerializer.Deserialize<ClaimedJobRow>(json,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!;
            var session=new JobSession(); var services=new ServiceCollection(); services.AddLogging(); services.AddSingleton<ICurrentSession>(session); services.AddMeritApplication(); services.AddMeritSqlServer(o=>o.ConnectionString=TestEnvironment.WorkerConnectionString);
            await using(var provider=services.BuildServiceProvider())
            {
                var accounts=provider.GetRequiredService<IAccountData>(); session.Current=await JobSessions.OpenAsync(accounts,job,DateTime.UtcNow.AddHours(1));
                try { await provider.GetRequiredService<JobDispatcher>().RunAsync(job,default); } finally { await accounts.RevokeSessionScopeAsync(session.Current.Summary.SessionScopeId); }
            }
            await EvaluateWork(a); await EvaluateWork(b);
            Assert.Equal(1,await Sql(c,"SELECT COUNT(*) FROM dbo.workTask WHERE workflowDefinitionId=@rule AND personId=@person",("@rule",rule),("@person",a)));
            Assert.Equal(0,await Sql(c,"SELECT COUNT(*) FROM dbo.workTask WHERE workflowDefinitionId=@rule AND personId=@person",("@rule",rule),("@person",b)));
            string snapshot=(string)(await Sql(c,"SELECT workflowSnapshotJson FROM dbo.workTask WHERE workflowDefinitionId=@rule AND personId=@person",("@rule",rule),("@person",a)))!;
            await Saved(await admin.NoRedirect.PostAsync($"/config/workflows/{rule}/retire",Form())); await EvaluateWork(a);
            Assert.Equal(snapshot,await Sql(c,"SELECT workflowSnapshotJson FROM dbo.workTask WHERE workflowDefinitionId=@rule AND personId=@person",("@rule",rule),("@person",a)));
        }
        finally { await CleanWorkflow(c,[a,b],rule); }
    }

    [SqlFact]
    public async Task FailedAttemptKeepsDeliveryOpenAndRequiredEvidenceCompletesOneActivityAtomically()
    {
        await using var c=new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync(); var a=await TestingPatient(c); var b=await TestingPatient(c); Guid? rule=null;
        try
        {
            await CloneReviewResult(c,Guid.NewGuid(),personOverride:a); rule=await WorkflowDefinition(c,a,b,null,true,("CompletionKind","delivery"),("evidenceFields","docRef | Document reference | text | true"));
            await EvaluateWork(a); var task=(Guid)(await Sql(c,"SELECT workTaskId FROM dbo.workTask WHERE workflowDefinitionId=@rule AND personId=@person",("@rule",rule),("@person",a)))!;
            await Saved(await ResolveWork(task,"attempt","noAnswer",a));
            Assert.Equal("open",await Sql(c,"SELECT taskStatus FROM dbo.workTask WHERE workTaskId=@id",("@id",task)));
            using(var missing=await ResolveWork(task,patientRecipient:a)) Assert.Equal(HttpStatusCode.UnprocessableEntity,missing.StatusCode);
            Assert.Equal(1,await Sql(c,"SELECT COUNT(*) FROM dbo.personActivity WHERE personId=@id",("@id",a)));
            await Saved(await ResolveWork(task,patientRecipient:a,extra:[("evidence_docRef","LETTER-123")]));
            Assert.Equal("completed",await Sql(c,"SELECT resolutionKind FROM dbo.workTask WHERE workTaskId=@id AND completionActivityId IS NOT NULL",("@id",task)));
            Assert.Equal(2,await Sql(c,"SELECT COUNT(*) FROM dbo.personActivity WHERE personId=@id",("@id",a)));
        }
        finally { await CleanWorkflow(c,[a,b],rule); }
    }

    [SqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostToFollowupRequiresThreeActualAttemptsOrAnAuthorizedExplicitWaiver(bool waive)
    {
        await using var c=new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync(); var a=await TestingPatient(c); var b=await TestingPatient(c); Guid? rule=null;
        try
        {
            var result=Guid.NewGuid(); await CloneReviewResult(c,result,personOverride:a); var caseId=await WorkflowCase(c,a,result);
            rule=await WorkflowDefinition(c,a,b,caseId,true,("TriggerEvent","caseOpened"),("BlocksClosure","true"),("ClosureReason","lostToFollowUp"),("MinimumContactAttempts","3"),("AllowWaiver","true"));
            await EvaluateWork(a); var task=(Guid)(await Sql(c,"SELECT workTaskId FROM dbo.workTask WHERE workflowDefinitionId=@rule AND exposureCaseId=@case",("@rule",rule),("@case",caseId)))!;
            using(var blocked=await CloseReviewed(c,caseId,"lostToFollowUp")) Assert.Equal(HttpStatusCode.UnprocessableEntity,blocked.StatusCode);
            if(waive)
            {
                var city=await fixture.SignedInAsync("cmbalt"); await city.Http.GetStringAsync($"/work/tasks/{task}");
                using(var denied=await city.NoRedirect.PostAsync($"/work/tasks/{task}",Form(("personActivityId",Guid.NewGuid()),("action","waive"),("activityDate","2026-10-02"),("note","Attempted unauthorized exception"),("confirmed","true")))) Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
                await Saved(await ResolveWork(task,"waive"));
                Assert.Equal("waived",await Sql(c,"SELECT resolutionKind FROM dbo.workTask WHERE workTaskId=@id",("@id",task)));
            }
            else
            {
                for(int i=0;i<3;i++) await Saved(await ResolveWork(task,"attempt","noAnswer",a,note:$"Actual attempt {i+1} verified in source log"));
                await Saved(await ResolveWork(task));
                Assert.Equal(3,await Sql(c,"SELECT COUNT(*) FROM dbo.vwPatientActivityStream WHERE exposureCaseId=@case AND activityType='contactAttempt' AND outcome='noAnswer' AND isVoided=0",("@case",caseId)));
                await Sql(c,"UPDATE dbo.personActivity SET isVoided=1,voidReason=N'Synthetic source correction' WHERE personActivityId=(SELECT TOP(1) personActivityId FROM dbo.personActivity WHERE exposureCaseId=@case AND activityType='contactAttempt')",("@case",caseId));
                using(var staleEvidence=await CloseReviewed(c,caseId,"lostToFollowUp")) Assert.Equal(HttpStatusCode.UnprocessableEntity,staleEvidence.StatusCode);
                await Sql(c,"UPDATE dbo.personActivity SET isVoided=0,voidReason=NULL WHERE exposureCaseId=@case AND voidReason=N'Synthetic source correction'",("@case",caseId));
            }
            using var closed=await CloseReviewed(c,caseId,"lostToFollowUp"); Assert.Equal(HttpStatusCode.OK,closed.StatusCode);
        }
        finally { await CleanWorkflow(c,[a,b],rule); }
    }

    [SqlFact]
    public async Task DeceasedClosureUsesItsOwnPolicyAndRejectsAStaleOutstandingWorkReview()
    {
        await using var c=new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync(); var a=await TestingPatient(c); var b=await TestingPatient(c); Guid? rule=null;
        try
        {
            var result=Guid.NewGuid(); await CloneReviewResult(c,result,personOverride:a); var caseId=await WorkflowCase(c,a,result);
            rule=await WorkflowDefinition(c,a,b,caseId,true,("TriggerEvent","caseOpened"),("BlocksClosure","true"),("ClosureReason","lostToFollowUp"),("MinimumContactAttempts","3"));
            await EvaluateWork(a); string old=(string)(await Sql(c,"SELECT dbo.udfCaseClosureReviewStamp(@case)",("@case",caseId)))!;
            await Sql(c,"INSERT dbo.workTask(workTaskId,rootTenantId,owningTenantId,personId,exposureCaseId,queueCode,title) SELECT NEWID(),rootTenantId,owningTenantId,personId,exposureCaseId,'siteReview',N'Newly arrived routine work' FROM dbo.exposureCase WHERE exposureCaseId=@case",("@case",caseId));
            using(var stale=await CloseReviewed(c,caseId,"deceased",old)) Assert.Equal(HttpStatusCode.UnprocessableEntity,stale.StatusCode);
            using(var closed=await CloseReviewed(c,caseId,"deceased")) Assert.Equal(HttpStatusCode.OK,closed.StatusCode);
            Assert.Equal(2,await Sql(c,"SELECT COUNT(*) FROM dbo.workTask WHERE exposureCaseId=@case AND resolutionKind='autoDismissed'",("@case",caseId)));
            Assert.Equal(0,await Sql(c,"SELECT COUNT(*) FROM dbo.personActivity WHERE personId=@person",("@person",a)));
        }
        finally { await CleanWorkflow(c,[a,b],rule); }
    }

    [SqlFact]
    public async Task CorrectingCompletionReopensWorkAndCancelsOnlyItsUnfulfilledRecurrence()
    {
        await using var c=new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync(); var a=await TestingPatient(c); var b=await TestingPatient(c); Guid? rule=null;
        try
        {
            await CloneReviewResult(c,Guid.NewGuid(),personOverride:a); rule=await WorkflowDefinition(c,a,b,null,true,("RecurrenceDays","30")); await EvaluateWork(a);
            var task=(Guid)(await Sql(c,"SELECT workTaskId FROM dbo.workTask WHERE workflowDefinitionId=@rule AND personId=@person",("@rule",rule),("@person",a)))!;
            await Saved(await ResolveWork(task));
            Assert.Equal(new DateTime(2026,11,1),await Sql(c,"SELECT dueDate FROM dbo.workTask WHERE workflowOccurrenceKey LIKE CONCAT('repeat/',CONVERT(VARCHAR(36),@id),'/%') AND taskStatus='open'",("@id",task)));
            var activity=(Guid)(await Sql(c,"SELECT completionActivityId FROM dbo.workTask WHERE workTaskId=@id",("@id",task)))!;
            var user=await fixture.SignedInAsync("supervisor"); await user.Http.GetStringAsync($"/people/{a}/care");
            await Saved(await user.NoRedirect.PostAsync($"/people/{a}/work/correct",Form(("activityId",activity),("sourceKind","patient"),("reason","Wrong completion was recorded; original evidence retained"))));
            Assert.Equal("open",await Sql(c,"SELECT taskStatus FROM dbo.workTask WHERE workTaskId=@id",("@id",task)));
            Assert.Equal(true,await Sql(c,"SELECT isVoided FROM dbo.personActivity WHERE personActivityId=@id",("@id",activity)));
            Assert.Equal(1,await Sql(c,"SELECT COUNT(*) FROM dbo.workTask WHERE workflowDefinitionId=@rule AND resolutionKind='autoDismissed'",("@rule",rule)));
            await Saved(await ResolveWork(task));
            Assert.Equal(1,await Sql(c,"SELECT COUNT(*) FROM dbo.workTask WHERE workflowDefinitionId=@rule AND taskStatus='open'",("@rule",rule)));
        }
        finally { await CleanWorkflow(c,[a,b],rule); }
    }

    [SqlFact]
    public async Task BulkInternalCompletionCreatesOneLinkedActivityAndContactEventsNeedARealRecipientOutcome()
    {
        await using var c=new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync(); var person=await TestingPatient(c); var task=Guid.NewGuid();
        try
        {
            var result=Guid.NewGuid(); await CloneReviewResult(c,result,personOverride:person); var caseId=await WorkflowCase(c,person,result);
            await Sql(c,"INSERT dbo.workTask(workTaskId,rootTenantId,owningTenantId,personId,exposureCaseId,queueCode,title) SELECT @id,rootTenantId,owningTenantId,personId,exposureCaseId,'siteReview',N'Internal source review' FROM dbo.exposureCase WHERE exposureCaseId=@case",("@id",task),("@case",caseId));
            var user=await fixture.SignedInAsync("supervisor"); await user.Http.GetStringAsync($"/cases/{caseId}");
            using(var blank=await user.NoRedirect.PostAsJsonAsync("/api/v1/tasks/bulk",new{taskIds=new[]{task},action="complete"})) Assert.Equal(HttpStatusCode.UnprocessableEntity,blank.StatusCode);
            using(var done=await user.NoRedirect.PostAsJsonAsync("/api/v1/tasks/bulk",new{taskIds=new[]{task},action="complete",note="Original source and jurisdiction reviewed"})) Assert.Equal(HttpStatusCode.OK,done.StatusCode);
            using(var retry=await user.NoRedirect.PostAsJsonAsync("/api/v1/tasks/bulk",new{taskIds=new[]{task},action="complete",note="Original source and jurisdiction reviewed"})) Assert.Equal(HttpStatusCode.OK,retry.StatusCode);
            Assert.Equal(1,await Sql(c,"SELECT COUNT(*) FROM dbo.personActivity WHERE personId=@person",("@person",person)));
            Assert.Equal("completed",await Sql(c,"SELECT resolutionKind FROM dbo.workTask WHERE workTaskId=@task AND completionActivityId IS NOT NULL",("@task",task)));
            using(var noRecipient=await user.NoRedirect.PostAsync($"/cases/{caseId}/activities",Form(("activityType","contactAttempt"),("activityDate","2026-10-02"),("outcome","noAnswer"),("note","Unspecified contact")))) Assert.Equal(HttpStatusCode.UnprocessableEntity,noRecipient.StatusCode);
            using(var attempt=await user.NoRedirect.PostAsync($"/cases/{caseId}/activities",Form(("activityType","contactAttempt"),("activityDate","2026-10-02"),("outcome","noAnswer"),("contactPersonId",person),("note","Actual patient contact attempt")))) Assert.Equal(HttpStatusCode.OK,attempt.StatusCode);
            var activity=(Guid)(await Sql(c,"SELECT TOP(1) caseActivityId FROM dbo.caseActivity WHERE exposureCaseId=@case AND activityType='contactAttempt'",("@case",caseId)))!;
            await user.Http.GetStringAsync($"/people/{person}/care");
            await Saved(await user.NoRedirect.PostAsync($"/people/{person}/work/correct",Form(("activityId",activity),("sourceKind","case"),("reason","The source log shows this entry was erroneous"))));
            Assert.Equal(0,await Sql(c,"SELECT COUNT(*) FROM dbo.vwPatientActivityStream WHERE exposureCaseId=@case AND activityType='contactAttempt' AND isVoided=0",("@case",caseId)));
        }
        finally { await CleanWorkflow(c,[person]); }
    }

    [SqlFact]
    public async Task TransferringRequiredWorkPreservesAnOpenReceivingTaskAndItsOriginalDeadline()
    {
        await using var c=new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync(); var a=await TestingPatient(c); var b=await TestingPatient(c); Guid? rule=null;
        try
        {
            var result=Guid.NewGuid(); await CloneReviewResult(c,result,personOverride:a); var caseId=await WorkflowCase(c,a,result);
            rule=await WorkflowDefinition(c,a,b,null,true,("TriggerEvent","patientReview"),("BlocksClosure","true")); await EvaluateWork(a);
            var task=(Guid)(await Sql(c,"SELECT workTaskId FROM dbo.workTask WHERE workflowDefinitionId=@rule AND personId=@person AND exposureCaseId IS NULL",("@rule",rule),("@person",a)))!;
            var due=await Sql(c,"SELECT dueDate FROM dbo.workTask WHERE workTaskId=@id",("@id",task));
            await Saved(await ResolveWork(task,"transfer",extra:[("transferCaseId",caseId)]));
            Assert.Equal("transferred",await Sql(c,"SELECT resolutionKind FROM dbo.workTask WHERE workTaskId=@id",("@id",task)));
            Assert.Equal(due,await Sql(c,"SELECT dueDate FROM dbo.workTask WHERE workflowDefinitionId=@rule AND exposureCaseId=@case AND taskStatus='open'",("@rule",rule),("@case",caseId)));
            using var blocked=await CloseReviewed(c,caseId,"other"); Assert.Equal(HttpStatusCode.UnprocessableEntity,blocked.StatusCode);
        }
        finally { await CleanWorkflow(c,[a,b],rule); }
    }

    [SqlFact]
    public async Task MergedAliasesRetainHistoryButCannotAcquireNewProgramOrAdHocObligations()
    {
        await using var c=new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync(); var a=await TestingPatient(c); var b=await TestingPatient(c); Guid? rule=null;
        try
        {
            rule=await WorkflowDefinition(c,a,b,null,true,("TriggerEvent","patientReview"),("MinimumValue",null),("AnalyteSpecimenId",null));
            await Sql(c,"UPDATE dbo.person SET mergedIntoPersonId=@canonical WHERE personId=@alias",("@canonical",a),("@alias",b));
            await EvaluateWork(a); await EvaluateWork(b);
            Assert.Equal(1,await Sql(c,"SELECT COUNT(*) FROM dbo.workTask WHERE personId=@person AND workflowDefinitionId=@rule",("@person",a),("@rule",rule)));
            Assert.Equal(0,await Sql(c,"SELECT COUNT(*) FROM dbo.workTask WHERE personId=@person AND workflowDefinitionId=@rule",("@person",b),("@rule",rule)));
            var user=await fixture.SignedInAsync("supervisor");
            using var rejected=await user.NoRedirect.PostAsync($"/people/{b}/work",Form(("workTaskId",Guid.NewGuid()),("title","Do not duplicate merged work"),("description","Canonical patient receives the work"),("completionKind","internal")));
            Assert.Equal(HttpStatusCode.UnprocessableEntity,rejected.StatusCode);
        }
        finally { await CleanWorkflow(c,[a,b],rule); }
    }

    [SqlFact]
    public async Task AWorkerCanCancelMistakenAdHocWorkWithAReasonWithoutClaimingCompletion()
    {
        await using var c=new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync(); var person=await TestingPatient(c); var task=Guid.NewGuid();
        try
        {
            var user=await fixture.SignedInAsync("supervisor"); await user.Http.GetStringAsync($"/people/{person}/work");
            await Saved(await user.NoRedirect.PostAsync($"/people/{person}/work",Form(("workTaskId",task),("title","Review an optional source"),("description","Ad-hoc task entered for review"),("completionKind","internal"))));
            using(var noReason=await user.NoRedirect.PostAsJsonAsync("/api/v1/tasks/bulk",new{taskIds=new[]{task},action="dismiss"})) Assert.Equal(HttpStatusCode.UnprocessableEntity,noReason.StatusCode);
            using(var cancelled=await user.NoRedirect.PostAsJsonAsync("/api/v1/tasks/bulk",new{taskIds=new[]{task},action="dismiss",note="Created in error; no program requirement applies"})) Assert.Equal(HttpStatusCode.OK,cancelled.StatusCode);
            Assert.Equal("dismissed",await Sql(c,"SELECT taskStatus FROM dbo.workTask WHERE workTaskId=@id AND completionActivityId IS NULL",("@id",task)));
            Assert.Equal(0,await Sql(c,"SELECT COUNT(*) FROM dbo.personActivity WHERE personId=@id",("@id",person)));
        }
        finally { await CleanWorkflow(c,[person]); }
    }
}
