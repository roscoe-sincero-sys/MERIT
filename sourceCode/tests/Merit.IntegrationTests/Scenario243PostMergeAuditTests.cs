using System.Net;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

// Regression coverage for the patient-only county routing found during the #243 audit.
public sealed partial class PatientCareTests
{
    [SqlTheory]
    [InlineData("definitionAgency")]
    [InlineData("patientOwner")]
    public async Task ExplicitStateAndLegacyRoutingAreNotRewritten(string routing)
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        var a = await TestingPatient(c); var b = await TestingPatient(c); Guid? rule = null;
        try
        {
            await Sql(c, "UPDATE dbo.person SET owningTenantId=rootTenantId WHERE personId=@id", ("@id", a));
            rule = await WorkflowDefinition(c, a, b, null, true, ("TriggerEvent", "patientReview"), ("MinimumValue", null), ("RoutingPolicy", routing));
            await EvaluateWork(a);
            Assert.Equal(await Sql(c, "SELECT rootTenantId FROM dbo.person WHERE personId=@id", ("@id", a)),
                await Sql(c, "SELECT owningTenantId FROM dbo.workTask WHERE workflowDefinitionId=@rule AND personId=@id", ("@rule", rule), ("@id", a)));
            string original = (string)(await Sql(c, "SELECT workflowSnapshotJson FROM dbo.workTask WHERE workflowDefinitionId=@rule AND personId=@id", ("@rule", rule), ("@id", a)))!;
            await EvaluateWork(a);
            Assert.Equal(original, await Sql(c, "SELECT workflowSnapshotJson FROM dbo.workTask WHERE workflowDefinitionId=@rule AND personId=@id", ("@rule", rule), ("@id", a)));
        }
        finally { await CleanWorkflow(c, [a, b], rule); }
    }

    [SqlTheory]
    [InlineData("patientReview")]
    [InlineData("receivedResult")]
    public async Task CountyProgramAndAdHocWorkUseServingJurisdiction(string trigger)
    {
        await using var c=new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        var a=await TestingPatient(c); var b=await TestingPatient(c); Guid? rule=null;
        try
        {
            await CloneReviewResult(c,Guid.NewGuid(),value:9999999,personOverride:a);
            await Sql(c,"""
                INSERT dbo.personLocation(personLocationId,rootTenantId,owningTenantId,personId,locationId,role,isPrimary,startDate,source)
                SELECT TOP(1) NEWID(),p.rootTenantId,p.owningTenantId,p.personId,l.locationId,'residence',1,'2020-01-01','interview'
                FROM dbo.person p JOIN dbo.location l ON l.rootTenantId=p.rootTenantId AND l.countyFips='005' AND l.isDeleted=0 WHERE p.personId=@person;
                EXEC dbo.uspRefreshPersonAccess @personId=@person;
                """,("@person",a),("@county",fixture.Ids.BaltimoreCountyTenantId));
            rule=await WorkflowDefinition(c,a,b,null,true,("TriggerEvent",trigger),("RoutingPolicy","servingJurisdiction"),("MinimumValue","9999998"),("AssignmentRoleCode","caseManager"));
            await EvaluateWork(a);
            var task=(Guid)(await Sql(c,"SELECT workTaskId FROM dbo.workTask WHERE workflowDefinitionId=@rule AND personId=@person",("@rule",rule),("@person",a)))!;
            Assert.Equal(fixture.Ids.BaltimoreCountyTenantId,
                await Sql(c,"SELECT owningTenantId FROM dbo.workTask WHERE workTaskId=@task",("@task",task)));
            var county=await fixture.SignedInAsync("cmbaltco");
            using(var patient=await county.Http.GetAsync($"/people/{a}")) Assert.Equal(HttpStatusCode.OK,patient.StatusCode);
            using(var accessible=await county.Http.GetAsync($"/work/tasks/{task}")) Assert.Equal(HttpStatusCode.OK,accessible.StatusCode);
            var checklist=await county.Http.GetStringAsync($"/people/{a}/work");
            Assert.Contains(task.ToString(),checklist,StringComparison.Ordinal);
            using var adHoc=await county.NoRedirect.PostAsync($"/people/{a}/work",Form(("workTaskId",Guid.NewGuid()),
                ("title","County follow-up audit"),("description","Synthetic patient-only outreach task"),("completionKind","internal")));
            Assert.Equal(HttpStatusCode.Redirect,adHoc.StatusCode);
            if (trigger == "patientReview")
            {
                await using var mixed = await SyntheticAccount.CreateAsync(fixture.Factory, "mixedprogram");
                await mixed.ScalarAsync<int>("""
                    INSERT auth.userAssignment(userAssignmentId,appUserId,roleId,tenantId,includeDescendants,accessLevel,startDate)
                    SELECT NEWID(),@id,r.roleId,CASE WHEN r.roleCode='caseManager' THEN @city ELSE @county END,1,'full','2020-01-01'
                    FROM auth.role r JOIN auth.appUser u ON u.rootTenantId=r.rootTenantId AND u.appUserId=@id
                    WHERE r.roleCode IN('caseManager','epidemiologist') AND r.isDeleted=0; SELECT 0;
                    """, ("@city", fixture.Ids.BaltimoreCityTenantId), ("@county", fixture.Ids.BaltimoreCountyTenantId));
                using var mixedClient = new MeritClient(fixture.Factory); await mixed.SignInAsync(mixedClient, "MeritDemo!2026");
                await mixedClient.Http.GetStringAsync($"/work/tasks/{task}");
                using var denied = await mixedClient.NoRedirect.PostAsync($"/work/tasks/{task}", Form(("personActivityId", Guid.NewGuid()), ("action", "complete"),
                    ("activityDate", DateTime.Today.ToString("yyyy-MM-dd")), ("note", "Must remain denied"), ("outcome", "completed"), ("audience", "internal"), ("channel", "internal"), ("confirmed", "true")));
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            }
            using var completed=await county.NoRedirect.PostAsync($"/work/tasks/{task}",Form(("personActivityId",Guid.NewGuid()),("action","complete"),
                ("activityDate",DateTime.Today.ToString("yyyy-MM-dd")),("note","Verified county internal program work"),("outcome","completed"),("audience","internal"),("channel","internal"),("confirmed","true")));
            Assert.Equal(HttpStatusCode.Redirect,completed.StatusCode);
        }
        finally { await Sql(c,"DELETE dbo.personLocation WHERE personId=@person",("@person",a)); await CleanWorkflow(c,[a,b],rule); }
    }
}
