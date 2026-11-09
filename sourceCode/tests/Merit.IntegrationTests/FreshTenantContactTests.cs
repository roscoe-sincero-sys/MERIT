using System.Net;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

public sealed partial class PatientCareTests
{
    [SqlFact]
    public async Task CountyCanReadCaregiverContactButCannotReadOrWriteCaregiverClinicalChart()
    {
        await using var c=new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        var patient=await TestingPatient(c,DateTime.Today.AddYears(-2).ToString("yyyy-MM-dd"));
        var caregiver=await TestingPatient(c,"1990-01-01");
        try
        {
            await TestingResult(c,caregiver,"2026-01-01",42);
            await Sql(c,"""
                INSERT dbo.personAccess(personAccessId,rootTenantId,personId,tenantId,accessReason)
                SELECT NEWID(),rootTenantId,personId,@county,'residence' FROM dbo.person WHERE personId=@patient;
                INSERT dbo.personRelationship(personRelationshipId,rootTenantId,owningTenantId,personId,relatedPersonId,role,source)
                SELECT NEWID(),rootTenantId,owningTenantId,@patient,@contact,'caregiver','labReport' FROM dbo.person WHERE personId=@patient;
                INSERT dbo.personContactMethod(personContactMethodId,rootTenantId,owningTenantId,personId,contactType,contactValue)
                SELECT NEWID(),rootTenantId,owningTenantId,@contact,'phone',N'4105550249' FROM dbo.person WHERE personId=@contact;
                """,("@patient",patient),("@contact",caregiver),("@county",fixture.Ids.BaltimoreCountyTenantId));
            var user=await fixture.SignedInAsync("cmbaltco");
            var page=await user.Http.GetStringAsync($"/people/{caregiver}");
            Assert.Contains("4105550249",page,StringComparison.Ordinal);
            Assert.Contains("Caregiver contact details",page,StringComparison.Ordinal);
            // A clinical role in another county must not combine with epidemiology here to expose the caregiver.
            await using(var mixed=await SyntheticAccount.CreateAsync(fixture.Factory,"mixedcontact"))
            {
                await mixed.ScalarAsync<int>("""
                    INSERT auth.userAssignment(userAssignmentId,appUserId,roleId,tenantId,includeDescendants,accessLevel,startDate)
                    SELECT NEWID(),@id,r.roleId,CASE WHEN r.roleCode='caseManager' THEN @city ELSE @county END,1,'full','2020-01-01'
                    FROM auth.role r JOIN auth.appUser u ON u.rootTenantId=r.rootTenantId AND u.appUserId=@id
                    WHERE r.roleCode IN('caseManager','epidemiologist') AND r.isDeleted=0; SELECT 0;
                    """,("@city",fixture.Ids.BaltimoreCityTenantId),("@county",fixture.Ids.BaltimoreCountyTenantId));
                using var mixedClient=new MeritClient(fixture.Factory); await mixed.SignInAsync(mixedClient,"MeritDemo!2026");
                using var noProjection=await mixedClient.Http.GetAsync($"/people/{caregiver}"); Assert.Equal(HttpStatusCode.NotFound,noProjection.StatusCode);
            }
            using(var care=await user.Http.GetAsync($"/people/{caregiver}/care/testing")) Assert.Equal(HttpStatusCode.Forbidden,care.StatusCode);
            using(var forbidden=await user.NoRedirect.PostAsync($"/people/{caregiver}/contacts",Form(("ContactType","phone"),("ContactValue","4105550000"))))
                Assert.Equal(HttpStatusCode.Forbidden,forbidden.StatusCode);
            var plan=Guid.NewGuid(); await user.Http.GetStringAsync($"/people/{patient}/care/testing");
            await Saved(await user.NoRedirect.PostAsync($"/people/{patient}/care/testing",Form(("workTaskId",plan),("planKind","screen24"),("evidence","Synthetic reviewed Maryland screening history"),("clinicianConfirmed","true"))));
            Assert.Equal(fixture.Ids.BaltimoreCountyTenantId,await Sql(c,"SELECT owningTenantId FROM dbo.workTask WHERE workTaskId=@id",("@id",plan)));
            // An ended relationship removes even the narrow contact projection immediately.
            await Sql(c,"UPDATE dbo.personRelationship SET endDate=auth.udfSessionToday() WHERE personId=@patient",("@patient",patient));
            using var ended=await user.Http.GetAsync($"/people/{caregiver}"); Assert.Equal(HttpStatusCode.NotFound,ended.StatusCode);
        }
        finally
        {
            await Sql(c,"DELETE dbo.personRelationship WHERE personId=@patient; DELETE dbo.personContactMethod WHERE personId=@contact",("@patient",patient),("@contact",caregiver));
            await CleanTesting(c,patient); await CleanTesting(c,caregiver);
        }
    }
}
