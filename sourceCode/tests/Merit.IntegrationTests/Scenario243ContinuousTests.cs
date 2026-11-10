using System.Net;
using System.Text;
using System.Text.Json;
using Merit.Application;
using Merit.Application.Data;
using Merit.Application.Jobs;
using Merit.Application.Security;
using Merit.Data.SqlServer;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit.Abstractions;

namespace Merit.IntegrationTests;

public sealed class Scenario243ReplayFactAttribute : FactAttribute
{
    public Scenario243ReplayFactAttribute()
    {
        if (!TestEnvironment.Database.StartsWith("MERIT_SCENARIO_243_", StringComparison.Ordinal))
            Skip = "Opt-in replay: set MERIT_IT_DATABASE to a dedicated MERIT_SCENARIO_243_* database. Never run clock instrumentation in MERIT or MERIT_IT.";
        else Skip = TestEnvironment.SkipReason;
    }
}

/// <summary>One fresh tenant, real HTTP account creation/MFA/configuration, two policy periods and actual workers.
/// Only the dedicated test database's business-date function is instrumented. Security/audit wall time stays real.</summary>
[Collection(MeritCollection.Name)]
public sealed class Scenario243ContinuousTests(MeritFixture fixture, ITestOutputHelper output)
{
    private readonly BusinessClock clock = new();
    private const string Password = "Scenario243!SyntheticPassphrase";
    private readonly List<MeritClient> clients = [];
    private readonly List<string> evidence = [];
    private Guid root, lab, specimen, analyte;

    private void Record(string text) { evidence.Add(text); output.WriteLine(text); }
    private static FormUrlEncodedContent Form(params (string Key, object? Value)[] values)
        => new(values.Select(v => new KeyValuePair<string,string>(v.Key,v.Value?.ToString() ?? "")));
    private static async Task<object?> Sql(string text, params (string Name,object? Value)[] values)
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        await using var cmd = new SqlCommand(text,c); foreach(var (key,value) in values) cmd.Parameters.AddWithValue(key,value ?? DBNull.Value);
        return await cmd.ExecuteScalarAsync();
    }
    private static async Task Saved(HttpResponseMessage response)
    {
        using(response) Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    [Scenario243ReplayFact]
    public async Task FreshMarylandDelegationConfigurationImportsAndManualResultsAcrossPeriods()
    {
        _ = fixture.Ids;
        var original = (string)(await Sql("SELECT OBJECT_DEFINITION(OBJECT_ID('auth.udfSessionToday'))"))!;
        await using var host = fixture.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<IClock>(clock))));
        try
        {
            // Bootstrap only: the seeded platform account must exist at the initial business date.
            await Sql("UPDATE auth.userAssignment SET startDate='2020-01-01' WHERE appUserId=(SELECT appUserId FROM auth.appUser WHERE userName='platform')");
            await SetDate(new DateOnly(2020,8,3));
            var platform = new MeritClient(host); clients.Add(platform); await platform.SignInAsync("platform", "MeritDemo!2026");
            string suffix=Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            var platformTenant=(Guid)(await Sql("SELECT homeTenantId FROM auth.appUser WHERE userName='platform'"))!;
            var region=await Tenant(platform,platformTenant,"R3-REPLAY-"+suffix,"region");
            var regional=await Account(platform,region,"region"+suffix,"administrator",host);
            await Grant(platform,regional.Id,region,"supervisor"); regional.Client.Dispose();
            regional=(regional.Id,await Login(regional.Id,host));
            root=await Tenant(regional.Client,region,"MD-REPLAY-"+suffix,"state");
            var admin=await Account(regional.Client,root,"md"+suffix,"administrator",host);
            await Grant(regional.Client,admin.Id,root,"supervisor"); admin.Client.Dispose(); admin=(admin.Id,await Login(admin.Id,host));
            var supervisor=await Account(admin.Client,root,"sup"+suffix,"supervisor",host);
            await Grant(admin.Client,supervisor.Id,root,"userAdministrator"); supervisor.Client.Dispose(); supervisor=(supervisor.Id,await Login(supervisor.Id,host));
            var clerk=await Account(supervisor.Client,root,"clerk"+suffix,"intakeClerk",host);
            var manager=await Account(supervisor.Client,root,"cm"+suffix,"caseManager",host);
            var investigator=await Account(supervisor.Client,root,"inv"+suffix,"investigator",host);
            var epi=await Account(supervisor.Client,root,"epi"+suffix,"epidemiologist",host);
            var lhd=await Tenant(admin.Client,root,"LHD-REPLAY-"+suffix,"jurisdiction");
            var localManager=await Account(admin.Client,lhd,"lhd"+suffix,"caseManager",host);
            Record("PASS: platform → regional admin → Maryland admin → supervisor → clerk/case manager/investigator/epidemiologist. Every account created through HTTP and signed in with password and MFA.");
            Record("PASS: actual HTTP tenant creation refreshes the creator's scope: immediate account creation worked for the new state and LHD without a manual sign-out. Raw procedure scopes alone do not test this behavior.");

            Assert.Equal(0,(int)(await Sql("SELECT COUNT(*) FROM config.notificationRule WHERE rootTenantId=@root",("@root",root)))!);
            Assert.Equal(0,(int)(await Sql("SELECT COUNT(*) FROM config.workflowDefinition WHERE rootTenantId=@root",("@root",root)))!);
            Assert.Equal(0,(int)(await Sql("SELECT COUNT(*) FROM config.threshold WHERE rootTenantId=@root AND isActive=1 AND effectiveFrom<='2020-08-03'",("@root",root)))!);
            var settings=await admin.Client.Http.GetStringAsync("/config/settings");
            Assert.Contains("name=\"settingValue\"",settings,StringComparison.Ordinal);
            await admin.Client.Http.GetStringAsync("/config/settings");
            foreach(var (key,value) in new[] {("statePostalCode","MD"),("stateFips","24"),("adultAgeCutoffYears","18"),("investigationLookbackDays","365"),("siteInvestigationMinHours","10"),("contactAttemptsBeforeLetter","3"),("clusterWindowDays","90"),("clusterMinCount","3")})
                await Saved(await admin.Client.NoRedirect.PostAsync("/config/settings/"+key,Form(("settingValue",value),("effectiveFrom","2020-01-01"))));

            await admin.Client.Http.GetStringAsync("/reporters");
            await Saved(await admin.Client.NoRedirect.PostAsync("/reporters",Form(("Kind","laboratory"),("Name","Scenario 243 laboratory"),("Clia","21D0712345"))));
            lab=(Guid)(await Sql("SELECT organizationId FROM dbo.organization WHERE rootTenantId=@root AND cliaNumber='21D0712345' AND isDeleted=0",("@root",root)))!;
            specimen=(Guid)(await Sql("SELECT s.analyteSpecimenId FROM config.analyteSpecimen s JOIN config.analyte a ON a.analyteId=s.analyteId WHERE a.code='PB' AND s.specimen='wholeBlood'"))!;
            analyte=(Guid)(await Sql("SELECT analyteId FROM config.analyteSpecimen WHERE analyteSpecimenId=@id",("@id",specimen)))!;
            Assert.Contains("No laboratory import formats",await clerk.Client.Http.GetStringAsync("/intake"),StringComparison.Ordinal);
            var hl7=await Profile(admin.Client,"hl7"); var csv=await Profile(admin.Client,"csv");
            var rule2020=await Configure(admin.Client,2020,"5",10);
            var previewHigh2020=await Person(clerk.Client,"PreviewHigh2020"+suffix,"2018-01-01");
            var previewLow2020=await Person(clerk.Client,"PreviewLow2020"+suffix,"2018-01-02");
            await Manual(clerk.Client,previewHigh2020,2020,"6",suffix+"-preview-high-old");
            await Manual(clerk.Client,previewLow2020,2020,"4",suffix+"-preview-low-old");
            var program2020=await ProgramRule(admin.Client,2020,"5",7,previewHigh2020,previewLow2020);
            var imported2020=await Upload(clerk.Client,clerk.Id,hl7,Hl7("Old"+suffix,"6"),"old.hl7","2020-08-03T10:00");
            Assert.Equal(1,(int)(await Sql("SELECT COUNT(*) FROM dbo.personIdentifier WHERE identifierValue=@value",("@value","Old"+suffix)))!);
            Assert.Equal(1,(int)(await Sql("SELECT COUNT(*) FROM dbo.personRelationship rel JOIN dbo.labResult r ON r.personId=rel.personId JOIN dbo.personContactMethod m ON m.personId=rel.relatedPersonId WHERE r.labResultId=@id AND m.contactValue='4105550199' AND rel.verifiedDate IS NULL",("@id",imported2020.Result)))!);
            Record("PASS: administrator created HL7/CSV formats through HTTP; intake clerk imported PID-3 without identifier display permission.");
            var supported231=await Upload(clerk.Client,clerk.Id,hl7,Hl7("Old231"+suffix,"4").Replace("2.5.1","2.3.1",StringComparison.Ordinal),"old231.hl7","2020-08-03T10:00");
            Assert.NotNull(supported231.Result);
            await Check(imported2020.Result!.Value,rule2020,"2020-08-17",1);
            var importedCase=(Guid)(await Sql("SELECT exposureCaseId FROM dbo.exposureCase WHERE qualifyingLabResultId=@result",("@result",imported2020.Result)))!;
            Assert.Equal(lhd,await Sql("SELECT owningTenantId FROM dbo.exposureCase WHERE exposureCaseId=@id",("@id",importedCase)));
            using(var localCase=await localManager.Client.Http.GetAsync($"/cases/{importedCase}")) Assert.Equal(HttpStatusCode.OK,localCase.StatusCode);
            var programTask2020=(Guid)(await Sql("SELECT workTaskId FROM dbo.workTask WHERE workflowDefinitionId=@rule AND labResultId=@result",("@rule",program2020),("@result",imported2020.Result)))!;
            Assert.Equal(new DateTime(2020,8,10),await Sql("SELECT dueDate FROM dbo.workTask WHERE workTaskId=@id",("@id",programTask2020)));
            Assert.Equal(lhd,await Sql("SELECT owningTenantId FROM dbo.workTask WHERE workTaskId=@id",("@id",programTask2020)));
            using(var localTask=await localManager.Client.Http.GetAsync($"/work/tasks/{programTask2020}")) Assert.Equal(HttpStatusCode.OK,localTask.StatusCode);
            string programSnapshot2020=(string)(await Sql("SELECT workflowSnapshotJson FROM dbo.workTask WHERE workTaskId=@id",("@id",programTask2020)))!;
            Record("PASS: approved synthetic 2020 program version generated county-owned work due 2020-08-10 from the imported result; the new LHD user can open it.");
            Record("PASS: county-resident imported case routes to the LHD and is accessible to its case manager.");
            var oldPatient=await Person(clerk.Client,"Manual2020"+suffix,"2018-01-01");
            var manual2020=await Manual(clerk.Client,oldPatient,2020,"6",suffix+"-manual-old"); await Check(manual2020,rule2020,"2020-08-17",1);
            var belowPatient=await Person(clerk.Client,"Below2020"+suffix,"2018-01-02");
            var below2020=await Manual(clerk.Client,belowPatient,2020,"4",suffix+"-below-old"); await Check(below2020,rule2020,null,0);
            string priorSnapshot=(string)(await Sql("SELECT policySnapshotJson FROM dbo.notificationObligation WHERE notificationRuleId=@rule AND labResultId=@result",("@rule",rule2020),("@result",manual2020)))!;
            Record("PASS: 2020 HL7 and manual 6 ug/dL produce cases and caregiver tasks due 2020-08-17; 4 ug/dL produces neither under the selected test policy.");

            await SetDate(new DateOnly(2025,3,3));
            var rule2025=await Configure(admin.Client,2025,"3.5",10);
            var previewHigh2025=await Person(clerk.Client,"PreviewHigh2025"+suffix,"2023-01-01");
            var previewLow2025=await Person(clerk.Client,"PreviewLow2025"+suffix,"2023-01-02");
            await Manual(clerk.Client,previewHigh2025,2025,"4",suffix+"-preview-high-new");
            await Manual(clerk.Client,previewLow2025,2025,"3",suffix+"-preview-low-new");
            var program2025=await ProgramRule(admin.Client,2025,"3.5",5,previewHigh2025,previewLow2025);
            var imported2025=await Upload(clerk.Client,clerk.Id,csv,$"lastName,firstName,dob,sex,loinc,value,unit,collectedDate,reportedDate,accessionNumber,sampleType\r\nNew{suffix},Ada{suffix},2023-01-01,female,77307-7,4,ug/dL,2025-03-01,2025-03-03,CSV-{suffix},venous\r\n","new.csv","2025-03-03T10:00");
            await Check(imported2025.Result!.Value,rule2025,"2025-03-17",1);
            var newPatient=await Person(clerk.Client,"Manual2025"+suffix,"2023-01-02");
            var manual2025=await Manual(clerk.Client,newPatient,2025,"4",suffix+"-manual-new"); await Check(manual2025,rule2025,"2025-03-17",1);
            Assert.Equal(new DateTime(2025,3,8),await Sql("SELECT dueDate FROM dbo.workTask WHERE workflowDefinitionId=@rule AND labResultId=@result",("@rule",program2025),("@result",imported2025.Result)));
            Assert.Equal(new DateTime(2025,3,8),await Sql("SELECT dueDate FROM dbo.workTask WHERE workflowDefinitionId=@rule AND labResultId=@result",("@rule",program2025),("@result",manual2025)));
            Assert.Equal(programSnapshot2020,await Sql("SELECT workflowSnapshotJson FROM dbo.workTask WHERE workTaskId=@id",("@id",programTask2020)));
            Assert.Equal(new DateTime(2020,8,10),await Sql("SELECT dueDate FROM dbo.workTask WHERE workTaskId=@id",("@id",programTask2020)));
            Record("PASS: synthetic 2025 program version generated CSV/manual work due 2025-03-08; the 2020 program snapshot and target remained unchanged.");
            Assert.Equal(priorSnapshot,await Sql("SELECT policySnapshotJson FROM dbo.notificationObligation WHERE notificationRuleId=@rule AND labResultId=@result",("@rule",rule2020),("@result",manual2020)));
            Assert.Equal(new DateTime(2020,8,17),await Sql("SELECT dueDate FROM dbo.notificationObligation WHERE notificationRuleId=@rule AND labResultId=@result",("@rule",rule2020),("@result",manual2020)));
            Record("PASS: same Maryland tenant changed to 2025 rules; CSV and manual 4 ug/dL now create cases/tasks due 2025-03-17. Original 2020 obligation snapshot and target remain unchanged.");

            // PR #239 templates: the administrator activates the shipped Maryland drafts through the real form.
            var templates=new Dictionary<string,Guid>();
            foreach(var key in new[] {"mdChild","mdOwnerChild","mdLabElevated","mdLabBelow"}) templates[key]=await Template(admin.Client,key);
            var tplCsv=await Upload(clerk.Client,clerk.Id,csv,$"lastName,firstName,dob,sex,loinc,value,unit,collectedDate,reportedDate,accessionNumber,sampleType\r\nTpl{suffix},Ada{suffix},2023-01-03,female,77307-7,4,ug/dL,2025-03-01,2025-03-03,TPL-{suffix},venous\r\n","tpl.csv","2025-03-03T10:00");
            var tplHigh=await Person(clerk.Client,"TplHigh"+suffix,"2023-01-04");
            var tplManualHigh=await Manual(clerk.Client,tplHigh,2025,"4",suffix+"-tpl-high");
            var tplLow=await Person(clerk.Client,"TplLow"+suffix,"2023-01-05");
            var tplManualLow=await Manual(clerk.Client,tplLow,2025,"3",suffix+"-tpl-low");
            async Task<int> Obligations(Guid result,Guid rule)=>(int)(await Sql("SELECT COUNT(*) FROM dbo.notificationObligation n JOIN dbo.workTask w ON w.workTaskId=n.workTaskId WHERE n.labResultId=@id AND n.notificationRuleId=@rule AND n.evaluationState='needsReview' AND n.dueDate='2025-03-17' AND w.taskStatus='open'",("@id",result),("@rule",rule)))!;
            async Task<string> Receipt(Guid result,string queue)=>(await Sql("SELECT STRING_AGG(CONCAT(taskStatus,'|',detail),' ;; ') FROM dbo.workTask WHERE labResultId=@id AND queueCode=@queue AND isDeleted=0",("@id",result),("@queue",queue)))?.ToString() ?? "";
            foreach(var result in new[] {tplCsv.Result!.Value,tplManualHigh})
            {
                Assert.Equal(1,await Obligations(result,templates["mdChild"])); Assert.Equal(1,await Obligations(result,templates["mdOwnerChild"]));
                Assert.Contains("open|",await Receipt(result,"labReportingReview"),StringComparison.Ordinal);
            }
            Assert.Equal(0,await Obligations(tplManualLow,templates["mdChild"])); Assert.Equal(0,await Obligations(tplManualLow,templates["mdOwnerChild"]));
            Assert.Contains("open|",await Receipt(tplManualLow,"labReportingReview"),StringComparison.Ordinal);
            Record("PASS: PR #239 drafts (child caregiver notice, residence-owner notice, elevated and below-reference lab reporting) were activated from the draft links. Qualifying 2025 CSV/manual results created review-gated caregiver and owner obligations due 2025-03-17; 3 ug/dL created none. All three results opened laboratory-reporting timeline review instead of assuming a final-result date or Maryland eligibility.");
            Assert.DoesNotContain("dob",await Receipt(tplCsv.Result!.Value,"missingInformation"),StringComparison.Ordinal);
            foreach(var result in new[] {tplManualHigh,tplManualLow})
            {
                Assert.Empty(await Receipt(result,"missingInformation"));
                Assert.DoesNotContain("Missing/unusable mapped report fields",await Receipt(result,"receiptFinding"),StringComparison.Ordinal);
            }
            Record("PASS #262: manual entry under both lab-reporting drafts does not invent missing-report fields from the limited transcription. CSV source completeness and manual reporting-timeline reviews remain active.");

            await manager.Client.Http.GetStringAsync($"/people/{oldPatient}/care/testing");
            using(var rejected=await manager.Client.NoRedirect.PostAsync($"/people/{oldPatient}/care/testing",Form(("workTaskId",Guid.NewGuid()),("planKind","early"),("labResultId",manual2020),("evidence","Historical policy replay, not current treatment"),("clinicianConfirmed","true"))))
                Assert.Equal(HttpStatusCode.UnprocessableEntity,rejected.StatusCode);
            Record("LIMIT: historical clinical policy reconstruction remains unsupported. The merged current-care guard correctly rejects silent application of the 2025 policy to the 2020 source.");

            var caregiver=await Person(manager.Client,"Caregiver"+suffix,"1985-01-01");
            await manager.Client.Http.GetStringAsync($"/people/{newPatient}/relationships");
            await Saved(await manager.Client.NoRedirect.PostAsync($"/people/{newPatient}/relationships",Form(("RelatedPersonId",caregiver),("Role","legalGuardian"),("Source","interview"),("IsPrimaryContact","true"),("HasLegalCustody","true"))));
            await Saved(await manager.Client.NoRedirect.PostAsync($"/people/{caregiver}/contacts",Form(("ContactType","phone"),("ContactValue","4105550243"),("IsPreferred","true"))));
            await Saved(await manager.Client.NoRedirect.PostAsync($"/people/{caregiver}/contacts",Form(("ContactType","email"),("ContactValue","caregiver243@example.test"))));
            await manager.Client.Http.GetStringAsync($"/people/{caregiver}/places/add");
            await Saved(await manager.Client.NoRedirect.PostAsync($"/people/{caregiver}/places/add",Form(("Role","residence"),("AddressLine1","243 Scenario Way"),("City","Towson"),("State","MD"),("Zip5","21204"),("CountyFips","005"),("StartDate","2025-03-01"),("Source","interview"),("PrimaryChoice","new"),("PrimaryReason","mostOvernights"))));
            var plan=Guid.NewGuid(); await manager.Client.Http.GetStringAsync($"/people/{newPatient}/care/testing");
            await Saved(await manager.Client.NoRedirect.PostAsync($"/people/{newPatient}/care/testing",Form(("workTaskId",plan),("planKind","early"),("labResultId",manual2025),("targetDate","2025-03-06"),("evidence","Synthetic reviewed current clinician plan"),("clinicianConfirmed","true"))));
            var roster=await manager.Client.Http.GetStringAsync("/testing?dueFilter=next7&planKind=early");
            Assert.Contains(plan.ToString(),roster,StringComparison.Ordinal); Assert.Contains("Due within 7 days",roster,StringComparison.Ordinal);
            var export=await manager.Client.Http.GetStringAsync("/testing.csv?dueFilter=next7&planKind=early");
            Assert.Contains("caregiver243@example.test",export,StringComparison.Ordinal); Assert.Contains("4105550243",export,StringComparison.Ordinal);
            Assert.Contains("243 Scenario Way",export,StringComparison.Ordinal);
            Assert.Contains("Due within 7 days",await manager.Client.Http.GetStringAsync($"/people/{newPatient}"),StringComparison.Ordinal);
            Assert.DoesNotContain("caregiver243@example.test",await epi.Client.Http.GetStringAsync("/testing.csv?dueFilter=next7&planKind=early"),StringComparison.Ordinal);
            await SetDate(new DateOnly(2025,3,7));
            Assert.Contains(plan.ToString(),await manager.Client.Http.GetStringAsync("/testing?dueFilter=overdue&planKind=early"),StringComparison.Ordinal);
            Assert.Contains("Overdue",await manager.Client.Http.GetStringAsync($"/people/{newPatient}"),StringComparison.Ordinal);
            Record("PASS: real case-manager profile sees upcoming and overdue testing in roster and patient overview; CSV contains verified caregiver phone/email/mailing address. Epidemiologist export excludes contact details.");
        }
        finally
        {
            foreach(var client in clients)
            {
                try { using var signedOut=await client.NoRedirect.PostAsync("/account/signout",Form()); }
                catch(ObjectDisposedException) { /* Replaced after a grant revoked its old scope. */ }
                finally { client.Dispose(); }
            }
            await TestSessionScopes.RevokeAllAsync();
            try { await Sql(System.Text.RegularExpressions.Regex.Replace(original,@"\bCREATE\s+(?:OR\s+ALTER\s+)?FUNCTION\b","CREATE OR ALTER FUNCTION",System.Text.RegularExpressions.RegexOptions.IgnoreCase)); }
            finally
            {
                string path=Path.Combine(TestEnvironment.SourceRoot,".artifacts","scenario-243-replay-evidence.txt");
                await File.WriteAllLinesAsync(path,evidence,Encoding.UTF8);
            }
        }

        async Task<MeritClient> Login(Guid id,Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
        {
            string name=(string)(await Sql("SELECT userName FROM auth.appUser WHERE appUserId=@id",("@id",id)))!;
            // Already-enrolled accounts use their actual authenticator secret kept by their first client.
            var previous=clients.Last(c => c.UserName==name && c.TotpSecret is not null);
            var client=new MeritClient(factory); clients.Add(client);
            var page=await client.Http.GetStringAsync("/account/signin"); using var step=await client.Http.PostAsync("/account/signin",MeritClient.Form(page,("UserName",name),("Password",Password)));
            string html=await step.Content.ReadAsStringAsync();
            using var done=await client.Http.PostAsync("/account/mfa",MeritClient.Form(html,("Code",previous!.CurrentTotpCode())));
            Assert.Equal(HttpStatusCode.OK,(await client.Http.GetAsync("/api/v1/me")).StatusCode); return client;
        }
    }

    private async Task SetDate(DateOnly date)
    {
        clock.Today=date;
        await Sql($"CREATE OR ALTER FUNCTION auth.udfSessionToday() RETURNS DATE AS BEGIN RETURN CONVERT(DATE,'{date:yyyyMMdd}',112); END;");
        Record("BUSINESS DATE: "+date.ToString("yyyy-MM-dd")+"; authentication/audit wall time is unchanged.");
    }
    private async Task<Guid> Tenant(MeritClient actor,Guid parent,string code,string type)
    {
        await actor.Http.GetStringAsync("/admin/tenants");
        await Saved(await actor.NoRedirect.PostAsync("/admin/tenants",Form(("ParentTenantId",parent),("TenantCode",code),("TenantName",code),("TenantType",type),
            ("StatePostalCode",type=="state"?"MD":""),("StateFips",type=="state"?"24":""),("TimeZone","Eastern Standard Time"),("EffectiveFrom",clock.Today.ToString("yyyy-MM-dd")),("SeedThresholds","false"),("CountyFipsList",type=="jurisdiction"?"005":""),("NewCounties",type=="state"?"005 Baltimore County":""))));
        return (Guid)(await Sql("SELECT tenantId FROM auth.tenant WHERE tenantCode=@code",("@code",code)))!;
    }
    private async Task<(Guid Id,MeritClient Client)> Account(MeritClient actor,Guid tenant,string name,string role,Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host)
    {
        var roleId=await Role(tenant,role); await actor.Http.GetStringAsync("/admin/users/new");
        await Saved(await actor.NoRedirect.PostAsync("/admin/users",Form(("UserName",name),("DisplayName",name),("NewPassword",Password),("HomeTenantId",tenant),
            ("IsActive","true"),("RequirePasswordChange","false"),("FirstRoleId",roleId),("FirstTenantId",tenant),("FirstIncludeDescendants","true"),("FirstAccessLevel","full"))));
        var id=(Guid)(await Sql("SELECT appUserId FROM auth.appUser WHERE userName=@name",("@name",name)))!;
        var client=new MeritClient(host); clients.Add(client); await client.SignInAsync(name,Password); return(id,client);
    }
    private static async Task<Guid> Role(Guid tenant,string role) => (Guid)(await Sql("SELECT roleId FROM auth.role WHERE rootTenantId=(SELECT rootTenantId FROM auth.tenant WHERE tenantId=@root) AND roleCode=@role AND isDeleted=0",("@root",tenant),("@role",role)))!;
    private async Task Grant(MeritClient actor,Guid user,Guid tenant,string role)
    {
        await actor.Http.GetStringAsync($"/admin/users/{user}"); await Saved(await actor.NoRedirect.PostAsync($"/admin/users/{user}/assignments",Form(("roleId",await Role(tenant,role)),("tenantId",tenant),("includeDescendants","true"),("accessLevel","full"))));
    }
    private async Task<Guid> Configure(MeritClient actor,int year,string minimum,int days)
    {
        await actor.Http.GetStringAsync("/config/thresholds/new");
        await Saved(await actor.NoRedirect.PostAsync("/config/thresholds",Form(("ThresholdId",Guid.NewGuid()),("AnalyteSpecimenId",specimen),("Population","child"),("AgeMaxMonths",71),
            ("ThresholdType","case"),("LowerValue",minimum),("Comparator",">="),("IsActive","true"),("EffectiveFrom",$"{year}-01-01"),("EffectiveTo",$"{year}-12-31"),("Authority","Synthetic scenario 243, not program approval"))));
        var id=Guid.NewGuid(); await actor.Http.GetStringAsync("/config/notification-rules");
        await Saved(await actor.NoRedirect.PostAsync("/config/notification-rules",Form(("notificationRuleId",id),("ruleName",$"Scenario {year}"),("analyteSpecimenId",specimen),("population","child"),
            ("minimumValue",minimum),("recipientKind","caregiver"),("dueInDays",days),("dayBasis","business"),("timeZoneId","Eastern Standard Time"),("calendarName","Synthetic weekdays only"),("holidays",""),
            ("effectiveFrom",$"{year}-01-01"),("effectiveTo",$"{year}-12-31"),("approvalReference","Synthetic test scenario; no historical legal approval"),("approved","true")))); return id;
    }
    private async Task<Guid> Template(MeritClient actor,string key)
    {
        string page=await actor.Http.GetStringAsync("/config/notification-rules?template="+key);
        var draft=Merit.Application.Services.MarylandPolicyDrafts.Get(key);
        Assert.Equal(draft["ruleName"],System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(page,"id=\"ruleName\" name=\"ruleName\" value=\"([^\"]*)\"").Groups[1].Value));
        var id=Guid.NewGuid();
        var values=draft.Select(d=>(d.Key,(object?)d.Value)).ToList();
        values.AddRange([("notificationRuleId",id),("analyteSpecimenId",specimen),("calendarName","Synthetic weekdays only"),("holidays",""),("approved","true")]);
        await Saved(await actor.NoRedirect.PostAsync("/config/notification-rules",Form(values.ToArray())));
        Assert.Equal(1,(int)(await Sql("SELECT COUNT(*) FROM config.notificationRule WHERE notificationRuleId=@id",("@id",id)))!);
        return id;
    }
    private async Task<Guid> ProgramRule(MeritClient actor,int year,string minimum,int days,Guid sampleA,Guid sampleB)
    {
        var id=Guid.NewGuid(); await actor.Http.GetStringAsync("/config/workflows");
        await Saved(await actor.NoRedirect.PostAsync("/config/workflows/draft",Form(("WorkflowDefinitionId",id),("Name","Scenario program "+root),("Version",year==2020?1:2),
            ("Description","Synthetic program milestone; not Maryland program-owner policy approval"),("TriggerEvent","receivedResult"),("Population","child"),("CompletionKind","internal"),
            ("AnalyteSpecimenId",specimen),("MinimumValue",minimum),("RoutingPolicy","servingJurisdiction"),("DueInDays",days),("DayBasis","calendar"),
            ("TimeZoneId","Eastern Standard Time"),("EffectiveFrom",$"{year}-01-01"),("EffectiveTo",$"{year}-12-31"))));
        using var preview=await actor.NoRedirect.PostAsync($"/config/workflows/{id}/preview",Form(("samplePersonId",sampleA),("secondSamplePersonId",sampleB)));
        Assert.Equal(HttpStatusCode.OK,preview.StatusCode);
        string html=await preview.Content.ReadAsStringAsync(); Assert.Contains("Publish this previewed version",html,StringComparison.Ordinal);
        string stamp=System.Text.RegularExpressions.Regex.Match(html,"name=\"versionStamp\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(stamp);
        await Saved(await actor.NoRedirect.PostAsync($"/config/workflows/{id}/publish",Form(("versionStamp",stamp),("samplePersonId",sampleA),("secondSamplePersonId",sampleB),
            ("approvalReference","Synthetic scenario approval only; not legal or clinician approval"),("approved","true"))));
        return id;
    }
    private async Task<Guid> Person(MeritClient actor,string name,string dob)
    {
        await actor.Http.GetStringAsync("/people/new"); await Saved(await actor.NoRedirect.PostAsync("/people/new",Form(("LastName",name),("FirstName","Ada"+name),("Dob",dob),("Sex","female"),("Ethnicity","unknown"))));
        return (Guid)(await Sql("SELECT personId FROM dbo.person WHERE rootTenantId=@root AND lastName=@name AND isDeleted=0",("@root",root),("@name",name)))!;
    }
    private async Task<Guid> Manual(MeritClient actor,Guid patient,int year,string value,string accession)
    {
        string day=year==2020?"2020-08-01":"2025-03-01",receipt=year==2020?"2020-08-03":"2025-03-03";
        await actor.Http.GetStringAsync($"/people/{patient}/results/new");
        await Saved(await actor.NoRedirect.PostAsync($"/people/{patient}/results",Form(("AnalyteId",analyte),("AnalyteSpecimenId",specimen),("ValueText",value),("Unit","ug/dL"),("SampleType","venous"),
            ("CollectedDate",day),("ReportedDate",receipt),("FinalResultDate",receipt),("DepartmentReceivedAt",receipt+"T10:00"),("LabOrganizationId",lab),("AccessionNumber",accession),("ResultStatus","final"))));
        return (Guid)(await Sql("SELECT labResultId FROM dbo.labResult WHERE rootTenantId=@root AND accessionNumber=@accession",("@root",root),("@accession",accession)))!;
    }
    private async Task Check(Guid result,Guid rule,string? due,int cases)
    {
        Assert.Equal(cases,(int)(await Sql("SELECT COUNT(*) FROM dbo.exposureCase WHERE qualifyingLabResultId=@id",("@id",result)))!);
        Assert.Equal(cases,(int)(await Sql("SELECT COUNT(*) FROM dbo.notificationObligation n JOIN dbo.workTask w ON w.workTaskId=n.workTaskId WHERE n.labResultId=@id AND n.notificationRuleId=@rule AND w.taskStatus='open'",("@id",result),("@rule",rule)))!);
        if(due is not null) Assert.Equal(DateTime.Parse(due),await Sql("SELECT dueDate FROM dbo.notificationObligation WHERE labResultId=@id AND notificationRuleId=@rule",("@id",result),("@rule",rule)));
    }
    private async Task<Guid> Profile(MeritClient actor,string format)
    {
        var fields=new[] {"lastName","firstName","dob","sex","loinc","value","unit","collectedDate","reportedDate","accessionNumber","sampleType"};
        await actor.Http.GetStringAsync("/config/intake-formats");
        var values=new List<(string Key,object? Value)> {("SenderOrganizationId",lab),("ProfileName","Scenario replay "+format),("FileType",format),("HeaderRow",1),("DateFormats","yyyy-MM-dd")};
        if(format!="hl7") values.AddRange(fields.Select(f=>("Columns["+f+"]",(object?)f)));
        await Saved(await actor.NoRedirect.PostAsync("/config/intake-formats",Form(values.ToArray())));
        return (Guid)(await Sql("SELECT intakeProfileId FROM dbo.intakeProfile WHERE rootTenantId=@root AND profileName=@name AND isActive=1",("@root",root),("@name","Scenario replay "+format)))!;
    }
    private async Task<(Guid Batch,Guid? Result)> Upload(MeritClient actor,Guid user,Guid profile,string content,string filename,string receipt,int expectedLoaded=1,bool uploadAgain=false)
    {
        await actor.Http.GetStringAsync("/intake"); using var form=new MultipartFormDataContent();
        foreach(var (key,value) in new[] {("intakeProfileId",profile.ToString()),("historical","true"),("departmentReceivedAt",receipt),("receiptEvidence","Synthetic receipt ledger scenario 243"),("receiptConfirmed","true")}) form.Add(new StringContent(value),key);
        if(uploadAgain) form.Add(new StringContent("true"),"uploadAgain");
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(content)),"file",filename); await Saved(await actor.NoRedirect.PostAsync("/intake/upload",form));
        var batch=(Guid)(await Sql("SELECT TOP(1) intakeBatchId FROM dbo.intakeBatch WHERE rootTenantId=@root AND fileName=@name ORDER BY receivedUtc DESC",("@root",root),("@name",filename)))!;
        string json=(string)(await Sql("SELECT TOP(1) * FROM dbo.jobQueue WHERE JSON_VALUE(payloadJson,'$.intakeBatchId')=CONVERT(VARCHAR(36),@id) FOR JSON PATH,WITHOUT_ARRAY_WRAPPER",("@id",batch)))!;
        var job=JsonSerializer.Deserialize<ClaimedJobRow>(json,new JsonSerializerOptions {PropertyNameCaseInsensitive=true})!;
        await using var services=await Services(user,true,job);
        var result=await services.GetRequiredService<JobDispatcher>().RunAsync(job,default); await services.GetRequiredService<IWorkData>().CompleteJobAsync(job.JobId,result);
        Assert.Equal(expectedLoaded,(int)(await Sql("SELECT loadedCount FROM dbo.intakeBatch WHERE intakeBatchId=@id",("@id",batch)))!);
        if(filename.EndsWith(".hl7",StringComparison.Ordinal) && expectedLoaded==1)
        {
            Assert.DoesNotContain(Permissions.PersonViewIdentifiers,services.GetRequiredService<ICurrentSession>().Permissions);
            var staged=(Guid)(await Sql("SELECT stagingRowId FROM dbo.stagingRow WHERE intakeBatchId=@id",("@id",batch)))!;
            await Assert.ThrowsAsync<Merit.Application.Errors.NotFoundException>(()=>services.GetRequiredService<IIntakeData>().PersistIdentifiersAsync(staged,fixture.Ids.MiaPersonId));
        }
        return (batch,expectedLoaded==0?null:(Guid)(await Sql("SELECT labResultId FROM dbo.labResult WHERE intakeBatchId=@id AND resultRowStatus='valid'",("@id",batch)))!);
    }
    private async Task<ServiceProvider> Services(Guid user,bool worker,ClaimedJobRow? job=null)
    {
        var session=new ReplaySession(); var services=new ServiceCollection(); services.AddLogging(); services.AddSingleton<ICurrentSession>(session);
        services.AddMeritApplication(); services.Replace(ServiceDescriptor.Singleton<IClock>(clock)); services.AddMeritSqlServer(o=>o.ConnectionString=worker?TestEnvironment.WorkerConnectionString:TestEnvironment.WebConnectionString);
        var provider=services.BuildServiceProvider(); var accounts=provider.GetRequiredService<IAccountData>();
        session.Current=job is null?await accounts.CreateSessionScopeAsync(TestSessionScopes.Track(Guid.NewGuid()),user,DateTime.UtcNow.AddHours(1)):await JobSessions.OpenAsync(accounts,job,DateTime.UtcNow.AddHours(1));
        TestSessionScopes.Track(session.Current.Summary.SessionScopeId); return provider;
    }
    private static string Hl7(string name,string value)
        => $"MSH|^~\\&|LAB|21D0712345|MERIT|MD|20200803||ORU^R01^ORU_R01|{name}|P|2.5.1\rPID|1||{name}^^^LAB^MR||{name}^Ada{name}||20180101|F|||9900 Synthetic Way^^Towson^MD^21204^USA^H^^24005||4105550249\rNK1|1|Guardian{name}^Grace||20 Caregiver St^^Towson^MD^21204|4105550199\rOBR|1||{name}||||20200801\rOBX|1|NM|77307-7^Lead^LN|reading-1|{value}|ug/dL|||||F\r";
    private sealed class BusinessClock : IClock
    {
        public DateOnly Today {get;set;}=new(2020,8,3);
        public TimeZoneInfo Zone=>TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        public DateTime ToLocal(DateTime utc)=>TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc,DateTimeKind.Utc),Zone);
    }
    private sealed class ReplaySession : ICurrentSession
    {
        public SessionScopeResult? Current {get;set;}
        public bool IsAuthenticated=>Current is not null;
        public Guid? UserId=>Current?.Summary.AppUserId;
        public string? UserName=>Current?.Summary.UserName;
        public string? DisplayName=>Current?.Summary.DisplayName;
        public Guid? ScopeKey=>Current?.Summary.SessionScopeId;
        public Guid? RootTenantId=>Current?.Summary.RootTenantId;
        public Guid? ActingTenantId=>Current?.Summary.ActingTenantId;
        public string? ActingTenantName=>Current?.Summary.ActingTenantName;
        public IReadOnlySet<string> Permissions=>Current?.Permissions.ToHashSet(StringComparer.Ordinal) ?? [];
    }
}
