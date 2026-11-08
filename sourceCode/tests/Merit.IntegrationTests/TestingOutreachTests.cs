using System.Net;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

public sealed partial class PatientCareTests
{
    [SqlFact]
    public async Task MixedTenantReadAccessDoesNotInheritAnotherTenantsContactRights()
    {
        await using var c=new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        await using var account=await SyntheticAccount.CreateAsync(fixture.Factory,"mixedroster");
        var patient=await TestingPatient(c,"2024-01-01"); var contact=await TestingPatient(c,"1990-01-01");
        try
        {
            await account.ScalarAsync<int>("""
                INSERT auth.userAssignment(userAssignmentId,appUserId,roleId,tenantId,includeDescendants,accessLevel,startDate)
                SELECT NEWID(),@id,r.roleId,CASE WHEN r.roleCode='caseManager' THEN @city ELSE r.rootTenantId END,1,'full','2020-01-01'
                FROM auth.role r JOIN auth.appUser u ON u.rootTenantId=r.rootTenantId AND u.appUserId=@id
                WHERE r.roleCode IN('caseManager','epidemiologist') AND r.isDeleted=0;
                SELECT 0;
                """,("@city",fixture.Ids.BaltimoreCityTenantId));
            await Sql(c,"""
                UPDATE dbo.person SET owningTenantId=@county WHERE personId IN(@patient,@contact);
                INSERT dbo.personRelationship(personRelationshipId,rootTenantId,owningTenantId,personId,relatedPersonId,role,source,verifiedDate)
                SELECT NEWID(),rootTenantId,owningTenantId,@patient,@contact,'legalGuardian','selfReport','2026-01-01' FROM dbo.person WHERE personId=@patient;
                INSERT dbo.personContactMethod(personContactMethodId,rootTenantId,owningTenantId,personId,contactType,contactValue)
                SELECT NEWID(),rootTenantId,owningTenantId,@contact,'phone',N'15555559997' FROM dbo.person WHERE personId=@contact;
                INSERT dbo.workTask(workTaskId,rootTenantId,owningTenantId,personId,queueCode,title,testingPlanJson)
                SELECT NEWID(),rootTenantId,owningTenantId,@patient,'testingFollowUp',N'Mixed tenant permission fixture',N'{"kind":"later"}' FROM dbo.person WHERE personId=@patient;
                """,("@patient",patient),("@contact",contact),("@county",fixture.Ids.BaltimoreCountyTenantId));
            using var client=new MeritClient(fixture.Factory); await account.SignInAsync(client,"MeritDemo!2026");
            var csv=await client.Http.GetStringAsync("/testing.csv?planKind=later");
            Assert.Contains(patient.ToString(),csv,StringComparison.Ordinal);
            Assert.DoesNotContain("15555559997",csv,StringComparison.Ordinal);
            Assert.Contains("Contact details unavailable in this profile",csv,StringComparison.Ordinal);
        }
        finally
        {
            await Sql(c,"DELETE dbo.personRelationship WHERE personId=@patient; DELETE dbo.personContactMethod WHERE personId=@contact",("@patient",patient),("@contact",contact));
            await CleanTesting(c,patient); await CleanTesting(c,contact);
        }
    }

    [SqlFact]
    public async Task TestingExportIncludesEveryFilteredPageAuditsAndRequiresExportPermission()
    {
        await using var c=new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        await using var account=await SyntheticAccount.CreateAsync(fixture.Factory,"outreach");
        string tag="OutreachPage"+Guid.NewGuid().ToString("N");
        await account.ScalarAsync<int>("""
            INSERT auth.userAssignment(userAssignmentId,appUserId,roleId,tenantId,includeDescendants,accessLevel,startDate)
            SELECT NEWID(),@id,r.roleId,r.rootTenantId,1,'full','2020-01-01' FROM auth.role r
            JOIN auth.appUser u ON u.rootTenantId=r.rootTenantId AND u.appUserId=@id WHERE r.roleCode='investigator' AND r.isDeleted=0;
            SELECT 0;
            """);
        try
        {
            await Sql(c,"""
                DECLARE @people TABLE(id UNIQUEIDENTIFIER); INSERT @people SELECT TOP(51) NEWID() FROM sys.all_objects;
                INSERT dbo.person(personId,rootTenantId,owningTenantId,lastName,lastNameKey,dob)
                SELECT x.id,p.rootTenantId,p.owningTenantId,@tag,UPPER(@tag),'2024-01-01' FROM @people x CROSS JOIN dbo.person p WHERE p.personId=@original;
                INSERT dbo.workTask(workTaskId,rootTenantId,owningTenantId,personId,queueCode,title,dueDate,testingPlanJson,assignedToUserId)
                SELECT NEWID(),rootTenantId,owningTenantId,personId,'testingFollowUp',N'Pagination fixture',
                DATEADD(DAY,2,CONVERT(DATE,SYSUTCDATETIME() AT TIME ZONE 'UTC' AT TIME ZONE 'Eastern Standard Time')),N'{"kind":"later"}',@assignee
                FROM dbo.person WHERE lastName=@tag;
                """,("@tag",tag),("@original",fixture.Ids.MiaPersonId),("@assignee",account.Id));
            var supervisor=await fixture.SignedInAsync("supervisor");
            string query=$"assignedToUserId={account.Id}&planKind=later&dueFilter=next7";
            var second=await supervisor.Http.GetStringAsync($"/testing?{query}&page=2");
            Assert.Contains("51 matching tasks",second,StringComparison.Ordinal); Assert.Contains("Page 2",second,StringComparison.Ordinal);
            var csv=await supervisor.Http.GetStringAsync($"/testing.csv?{query}&page=2");
            Assert.Equal(52,csv.Split("\r\n",StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.Equal(51,csv.Split(tag,StringSplitOptions.None).Length-1);
            Assert.True((int)(await Sql(c,"SELECT COUNT(*) FROM logs.auditEvent WHERE entityType='testingOutreach' AND eventAction='export'"))!>0);
            using var reader=new MeritClient(fixture.Factory); await account.SignInAsync(reader,"MeritDemo!2026");
            using var forbidden=await reader.Http.GetAsync($"/testing.csv?{query}"); Assert.Equal(HttpStatusCode.Forbidden,forbidden.StatusCode);
            Assert.Contains("51 matching tasks",await reader.Http.GetStringAsync($"/testing?{query}"),StringComparison.Ordinal);
        }
        finally
        {
            await Sql(c,"""
                DELETE dbo.workTask WHERE personId IN(SELECT personId FROM dbo.person WHERE lastName=@tag);
                DELETE dbo.personAccess WHERE personId IN(SELECT personId FROM dbo.person WHERE lastName=@tag);
                DELETE dbo.person WHERE lastName=@tag;
                """,("@tag",tag));
        }
    }

    [SqlTheory]
    [InlineData(-1,"overdue","Overdue",true)]
    [InlineData(0,"today","Due today",true)]
    [InlineData(1,"next7","Due within 7 days",true)]
    [InlineData(7,"next7","Due within 7 days",true)]
    [InlineData(8,"next7","Scheduled",false)]
    public async Task TestingRosterAndPatientUseSameLocalDateBoundaries(int offset,string filter,string status,bool included)
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        var patient = await TestingPatient(c,"2024-01-01");
        try
        {
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")));
            var source = await TestingResult(c,patient,today.AddDays(-14).ToString("yyyy-MM-dd"),4,"venous"); var task=Guid.NewGuid();
            await Saved(await Plan(patient,task,"later",source,target:today.AddDays(offset).ToString("yyyy-MM-dd")));
            var user=await fixture.SignedInAsync("supervisor");
            string target=$"/people/{patient}/care/testing#plan-{task}";
            var html=await user.Http.GetStringAsync($"/testing?dueFilter={filter}&planKind=later");
            Assert.Equal(included,html.Contains(target,StringComparison.Ordinal));
            var csv=await user.Http.GetStringAsync($"/testing.csv?dueFilter={filter}&planKind=later");
            Assert.Equal(included,csv.Contains(patient.ToString(),StringComparison.Ordinal));
            var summary=await user.Http.GetStringAsync($"/people/{patient}"); Assert.Contains(status,summary,StringComparison.Ordinal);
            Assert.Contains(status,await user.Http.GetStringAsync($"/people/{patient}/care/testing"),StringComparison.Ordinal);
            await Sql(c,"UPDATE dbo.workTask SET taskStatus='dismissed' WHERE workTaskId=@id",("@id",task));
            Assert.DoesNotContain(target,await user.Http.GetStringAsync("/testing?planKind=later"),StringComparison.Ordinal);
            Assert.Contains(target,await user.Http.GetStringAsync("/testing?taskStatus=dismissed&planKind=later"),StringComparison.Ordinal);
            Assert.DoesNotContain("<strong>"+status+"</strong>",await user.Http.GetStringAsync($"/people/{patient}"),StringComparison.Ordinal);
        }
        finally { await CleanTesting(c,patient); }
    }

    [SqlFact]
    public async Task TestingContactsRequireVerifiedUnambiguousRelationshipAndAuthorizedProfile()
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        var patient=await TestingPatient(c,"2024-01-01"); var contact=await TestingPatient(c,"1990-01-01"); var second=await TestingPatient(c,"1991-01-01");
        var relationship=Guid.NewGuid(); var other=Guid.NewGuid(); var task=Guid.NewGuid(); var address=Guid.NewGuid();
        try
        {
            await Sql(c,"""
                UPDATE dbo.person SET firstName=N'=FORMULA_SENTINEL',lastName=N'ContactSentinel' WHERE personId=@contact;
                INSERT dbo.personContactMethod(personContactMethodId,rootTenantId,owningTenantId,personId,contactType,contactValue)
                SELECT NEWID(),rootTenantId,owningTenantId,@contact,'phone',N'+15555552446' FROM dbo.person WHERE personId=@contact;
                INSERT dbo.personContactMethod(personContactMethodId,rootTenantId,owningTenantId,personId,contactType,contactValue)
                SELECT NEWID(),rootTenantId,owningTenantId,@contact,'email',N'outreach-caregiver@example.test' FROM dbo.person WHERE personId=@contact;
                INSERT dbo.location(locationId,rootTenantId,owningTenantId,addressLine1,city,state,zip5,addressKey)
                SELECT @address,rootTenantId,owningTenantId,N'2446 Outreach Way',N'Baltimore','MD','21201',CONVERT(NVARCHAR(36),@address) FROM dbo.person WHERE personId=@contact;
                INSERT dbo.personLocation(personLocationId,rootTenantId,owningTenantId,personId,locationId,role,isPrimary,startDate,source)
                SELECT NEWID(),rootTenantId,owningTenantId,@contact,@address,'residence',1,'2020-01-01','selfReport' FROM dbo.person WHERE personId=@contact;
                INSERT dbo.personRelationship(personRelationshipId,rootTenantId,owningTenantId,personId,relatedPersonId,role,source)
                SELECT @relation,rootTenantId,owningTenantId,@patient,@contact,'legalGuardian','selfReport' FROM dbo.person WHERE personId=@patient;
                """,("@patient",patient),("@contact",contact),("@relation",relationship),("@address",address));
            var today=DateOnly.FromDateTime(DateTime.UtcNow);
            var result=await TestingResult(c,patient,today.AddDays(-7).ToString("yyyy-MM-dd"),4,"venous");
            await Saved(await Plan(patient,task,"early",result,target:today.AddDays(2).ToString("yyyy-MM-dd")));
            var user=await fixture.SignedInAsync("supervisor");
            var csv=await user.Http.GetStringAsync("/testing.csv?planKind=early");
            Assert.DoesNotContain("+15555552446",csv,StringComparison.Ordinal);
            Assert.Contains("Caregiver relationship needs verification",csv,StringComparison.Ordinal);
            await Sql(c,"UPDATE dbo.personRelationship SET verifiedDate='2026-01-01' WHERE personRelationshipId=@id",("@id",relationship));
            csv=await user.Http.GetStringAsync("/testing.csv?planKind=early");
            Assert.Contains("\"'+15555552446\"",csv,StringComparison.Ordinal);
            Assert.Contains("outreach-caregiver@example.test",csv,StringComparison.Ordinal);
            Assert.Contains("2446 Outreach Way",csv,StringComparison.Ordinal);
            Assert.Contains("\"'=FORMULA_SENTINEL ContactSentinel\"",csv,StringComparison.Ordinal);
            var epi=await fixture.SignedInAsync("epi");
            var restricted=await epi.Http.GetStringAsync("/testing.csv?planKind=early");
            Assert.DoesNotContain("ContactSentinel",restricted,StringComparison.Ordinal);
            Assert.DoesNotContain("15555552446",restricted,StringComparison.Ordinal);
            Assert.DoesNotContain("outreach-caregiver@example.test",restricted,StringComparison.Ordinal);
            Assert.DoesNotContain("2446 Outreach Way",restricted,StringComparison.Ordinal);
            var county=await fixture.SignedInAsync("cmbaltco");
            Assert.DoesNotContain(patient.ToString(),await county.Http.GetStringAsync("/testing"),StringComparison.Ordinal);
            await Sql(c,"""
                INSERT dbo.personRelationship(personRelationshipId,rootTenantId,owningTenantId,personId,relatedPersonId,role,source,verifiedDate)
                SELECT @id,rootTenantId,owningTenantId,@patient,@second,'caregiver','selfReport','2026-01-01' FROM dbo.person WHERE personId=@patient;
                """,("@id",other),("@patient",patient),("@second",second));
            csv=await user.Http.GetStringAsync("/testing.csv?planKind=early");
            Assert.Contains("Multiple caregivers: review before contacting",csv,StringComparison.Ordinal);
            Assert.DoesNotContain("15555552446",csv,StringComparison.Ordinal);
            await Sql(c,"UPDATE dbo.personRelationship SET isPrimaryContact=1 WHERE personRelationshipId=@id",("@id",relationship));
            csv=await user.Http.GetStringAsync("/testing.csv?planKind=early");
            Assert.Contains("Primary caregiver relationship verified",csv,StringComparison.Ordinal);
            Assert.Contains("15555552446",csv,StringComparison.Ordinal);
            using var invalid=await user.Http.GetAsync("/testing?dueFilter=bogus"); Assert.Equal(HttpStatusCode.UnprocessableEntity,invalid.StatusCode);
        }
        finally
        {
            await Sql(c,"DELETE dbo.personRelationship WHERE personRelationshipId IN(@a,@b); DELETE dbo.personContactMethod WHERE personId=@contact; DELETE dbo.personLocation WHERE personId=@contact; DELETE dbo.location WHERE locationId=@address",("@a",relationship),("@b",other),("@contact",contact),("@address",address));
            await CleanTesting(c,patient); await CleanTesting(c,contact); await CleanTesting(c,second);
        }
    }
}
