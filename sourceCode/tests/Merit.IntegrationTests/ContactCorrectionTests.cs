using System.Net;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

public sealed partial class PatientCareTests
{
    [SqlTheory]
    [InlineData("phone", "4105550258")]
    [InlineData("email", "corrected258@example.test")]
    [InlineData("address", "258 Verified Street")]
    public async Task CountyRequestsOwnerAppliesAndHistoryRetainsContactEvidence(string kind, string value)
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        var patient = await TestingPatient(c); var caregiver = await TestingPatient(c, "1990-01-01");
        try
        {
            await Sql(c, """
                UPDATE dbo.person SET owningTenantId=rootTenantId WHERE personId IN(@patient,@caregiver);
                INSERT dbo.personAccess(personAccessId,rootTenantId,personId,tenantId,accessReason)
                SELECT NEWID(),rootTenantId,personId,@county,'share' FROM dbo.person WHERE personId=@patient;
                INSERT dbo.personRelationship(personRelationshipId,rootTenantId,owningTenantId,personId,relatedPersonId,role,source)
                SELECT NEWID(),rootTenantId,owningTenantId,personId,@caregiver,'caregiver','labReport' FROM dbo.person WHERE personId=@patient;
                INSERT dbo.personContactMethod(personContactMethodId,rootTenantId,owningTenantId,personId,contactType,contactValue)
                SELECT NEWID(),rootTenantId,owningTenantId,personId,'phone','4105550000' FROM dbo.person WHERE personId=@caregiver;
                """, ("@patient", patient), ("@caregiver", caregiver), ("@county", fixture.Ids.BaltimoreCountyTenantId));
            var county = await fixture.SignedInAsync("cmbaltco"); var owner = await fixture.SignedInAsync("supervisor");
            await county.Http.GetStringAsync($"/contact-corrections?personId={patient}&caregiverId={caregiver}");
            var fields = new[] { ("Request.PersonId", (object?)patient), ("Request.CaregiverId", caregiver), ("Request.ContactType", kind), ("Request.ProposedValue", value),
                ("Request.SourceNote", "Verified by telephone; replace the outdated contact if appropriate"), ("Request.City", "Towson"), ("Request.State", "MD"), ("Request.Zip5", "21204") };
            using (var request = await county.NoRedirect.PostAsync("/contact-corrections", Form(fields))) Assert.Equal(HttpStatusCode.Redirect, request.StatusCode);
            var id = (Guid)(await Sql(c, "SELECT contactCorrectionId FROM dbo.contactCorrection WHERE personId=@id", ("@id", patient)))!;
            var version = Convert.ToHexString((byte[])(await Sql(c, "SELECT rowVersion FROM dbo.contactCorrection WHERE contactCorrectionId=@id", ("@id", id)))!);
            if (kind == "phone")
            {
                await using var mixed = await SyntheticAccount.CreateAsync(fixture.Factory, "mixedcorrection");
                await mixed.ScalarAsync<int>("""
                    INSERT auth.userAssignment(userAssignmentId,appUserId,roleId,tenantId,includeDescendants,accessLevel,startDate)
                    SELECT NEWID(),@id,r.roleId,CASE WHEN r.roleCode='caseManager' THEN @city ELSE @county END,1,'full','2020-01-01'
                    FROM auth.role r JOIN auth.appUser u ON u.rootTenantId=r.rootTenantId AND u.appUserId=@id
                    WHERE r.roleCode IN('caseManager','epidemiologist') AND r.isDeleted=0; SELECT 0;
                    """, ("@city", fixture.Ids.BaltimoreCityTenantId), ("@county", fixture.Ids.BaltimoreCountyTenantId));
                using var mixedClient = new MeritClient(fixture.Factory); await mixed.SignInAsync(mixedClient, "MeritDemo!2026");
                Assert.DoesNotContain(id.ToString(), await mixedClient.Http.GetStringAsync("/contact-corrections"), StringComparison.Ordinal);
                using var deniedRequest = await mixedClient.NoRedirect.PostAsync("/contact-corrections", Form(fields));
                Assert.Equal(HttpStatusCode.Forbidden, deniedRequest.StatusCode);
                using var deniedWork = await mixedClient.NoRedirect.PostAsync($"/people/{patient}/work", Form(("workTaskId", Guid.NewGuid()), ("title", "Unauthorized mixed work"), ("description", "Must remain denied"), ("completionKind", "internal")));
                Assert.Equal(HttpStatusCode.Forbidden, deniedWork.StatusCode);
            }
            Task<HttpResponseMessage> Review(MeritClient user, string stamp, string status = "applied") => user.NoRedirect.PostAsync($"/contact-corrections/{id}/review",
                Form(("rowVersion", stamp), ("requestStatus", status), ("reviewNote", "Verified source and current caregiver contacts"), ("personId", patient)));
            using (var denied = await Review(county, version)) Assert.Equal(HttpStatusCode.UnprocessableEntity, denied.StatusCode);
            Assert.Contains("Pending", await county.Http.GetStringAsync($"/contact-corrections?personId={patient}"), StringComparison.Ordinal);
            await owner.Http.GetStringAsync("/contact-corrections");
            using (var absent = await Review(owner, version)) Assert.Equal(HttpStatusCode.UnprocessableEntity, absent.StatusCode);
            // Apply through existing owner-authorized contact/address handlers, not a new chart grant.
            await owner.Http.GetStringAsync($"/people/{caregiver}");
            if (kind != "address")
            {
                using var saved = await owner.NoRedirect.PostAsync($"/people/{caregiver}/contacts", Form(("ContactType", kind), ("ContactValue", value), ("IsPreferred", "true")));
                Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            }
            else
            {
                await owner.Http.GetStringAsync($"/people/{caregiver}/places/add");
                using var saved = await owner.NoRedirect.PostAsync($"/people/{caregiver}/places/add", Form(("Role", "residence"), ("AddressLine1", value),
                    ("City", "Towson"), ("State", "MD"), ("Zip5", "21204"), ("CountyFips", "005"), ("StartDate", "2020-01-01"), ("Source", "interview"), ("PrimaryChoice", "new"), ("PrimaryReason", "mostOvernights")));
                Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            }
            using (var applied = await Review(owner, version)) Assert.Equal(HttpStatusCode.Redirect, applied.StatusCode);
            using (var stale = await Review(owner, version, "rejected")) Assert.Equal(HttpStatusCode.UnprocessableEntity, stale.StatusCode);
            Assert.Equal("applied", await Sql(c, "SELECT requestStatus FROM dbo.contactCorrection WHERE contactCorrectionId=@id", ("@id", id)));
            Assert.Contains("4105550000", (string)(await Sql(c, "SELECT beforeJson FROM dbo.contactCorrection WHERE contactCorrectionId=@id", ("@id", id)))!, StringComparison.Ordinal);
            Assert.NotNull(await Sql(c, "SELECT afterJson FROM dbo.contactCorrection WHERE contactCorrectionId=@id", ("@id", id)));
            Assert.Contains("Applied", await county.Http.GetStringAsync($"/contact-corrections?personId={patient}"), StringComparison.Ordinal);
            await Sql(c, "UPDATE dbo.personRelationship SET endDate=auth.udfSessionToday() WHERE personId=@id", ("@id", patient));
            Assert.Contains("Applied", await county.Http.GetStringAsync($"/contact-corrections?personId={patient}"), StringComparison.Ordinal);
            using (var ended = await county.NoRedirect.PostAsync("/contact-corrections", Form(fields))) Assert.Equal(HttpStatusCode.Forbidden, ended.StatusCode);
        }
        finally
        {
            await Sql(c, """
                DELETE dbo.contactCorrection WHERE personId=@patient;
                DELETE dbo.personRelationship WHERE personId=@patient;
                DELETE dbo.personContactMethod WHERE personId=@caregiver;
                DELETE dbo.personLocation WHERE personId=@caregiver;
                """, ("@patient", patient), ("@caregiver", caregiver));
            await CleanTesting(c, patient); await CleanTesting(c, caregiver);
        }
    }
}
