using System.Net;
using Microsoft.Data.SqlClient;
using Merit.Domain.Text;

namespace Merit.IntegrationTests;

// Regression for #260: explicit unit changes are part of address completion evidence.
public sealed partial class PatientCareTests
{
    [SqlTheory]
    [InlineData("set", "3F", "3F", "2F")]
    [InlineData("set", " Apt. 3f ", "3F", "2F")]
    [InlineData("remove", null, null, "2F")]
    [InlineData("unspecified", null, "2F", "2F")]
    [InlineData("remove", null, null, null)]
    [InlineData("unspecified", null, null, null)]
    public async Task ApartmentCorrectionRequiresRequestedUnitAndPreservesUnspecifiedRequests(string action, string? proposedUnit, string? appliedUnit, string? originalUnit)
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        var patient = await TestingPatient(c); var caregiver = await TestingPatient(c, "1990-01-01"); var place = Guid.NewGuid();
        try
        {
            await Sql(c, """
                UPDATE dbo.person SET owningTenantId=rootTenantId WHERE personId IN(@patient,@caregiver);
                INSERT dbo.personAccess(personAccessId,rootTenantId,personId,tenantId,accessReason)
                SELECT NEWID(),rootTenantId,personId,@county,'share' FROM dbo.person WHERE personId=@patient;
                INSERT dbo.personRelationship(personRelationshipId,rootTenantId,owningTenantId,personId,relatedPersonId,role,source)
                SELECT NEWID(),rootTenantId,owningTenantId,personId,@caregiver,'caregiver','labReport' FROM dbo.person WHERE personId=@patient;
                INSERT dbo.location(locationId,rootTenantId,owningTenantId,addressLine1,unit,city,state,zip5,addressKey)
                SELECT @place,rootTenantId,owningTenantId,N'260 Audit Street',@originalUnit,N'Baltimore','MD','21223',@key FROM dbo.person WHERE personId=@caregiver;
                INSERT dbo.personLocation(personLocationId,rootTenantId,owningTenantId,personId,locationId,role,isPrimary,startDate,source)
                SELECT NEWID(),rootTenantId,owningTenantId,personId,@place,'residence',1,'2020-01-01','interview' FROM dbo.person WHERE personId=@caregiver;
                """, ("@patient", patient), ("@caregiver", caregiver), ("@county", fixture.Ids.BaltimoreCountyTenantId), ("@place", place),
                ("@originalUnit", originalUnit), ("@key", MatchKeys.AddressKey("260 Audit Street", originalUnit, "Baltimore", "MD", "21223")));
            var county = await fixture.SignedInAsync("cmbaltco");
            var form = await county.Http.GetStringAsync($"/contact-corrections?personId={patient}&caregiverId={caregiver}");
            Assert.Contains("name=\"Request.Unit\"", form, StringComparison.Ordinal);
            using var request = await county.NoRedirect.PostAsync("/contact-corrections", Form(("Request.PersonId", patient), ("Request.CaregiverId", caregiver),
                ("Request.ContactType", "address"), ("Request.ProposedValue", "260 Audit Street"), ("Request.City", "Baltimore"), ("Request.State", "MD"), ("Request.Zip5", "21223"),
                ("Request.SourceNote", "Verified apartment/unit instruction; other address fields unchanged."), ("Request.UnitAction", action), ("Request.Unit", proposedUnit), ("Request.ProposedAddressKey", "Client value must not determine the verification key")));
            Assert.Equal(HttpStatusCode.Redirect, request.StatusCode);
            var id = (Guid)(await Sql(c, "SELECT contactCorrectionId FROM dbo.contactCorrection WHERE personId=@id", ("@id", patient)))!;
            var version = Convert.ToHexString((byte[])(await Sql(c, "SELECT rowVersion FROM dbo.contactCorrection WHERE contactCorrectionId=@id", ("@id", id)))!);
            var owner = await fixture.SignedInAsync("supervisor"); await owner.Http.GetStringAsync("/contact-corrections");
            Task<HttpResponseMessage> Review() => owner.NoRedirect.PostAsync($"/contact-corrections/{id}/review", Form(("rowVersion", version), ("requestStatus", "applied"),
                ("reviewNote", "Synthetic owner review of verified address and apartment/unit.")));
            if (action != "unspecified" && MatchKeys.NormalizeUnit(originalUnit) != MatchKeys.NormalizeUnit(appliedUnit))
            {
                using var unchanged = await Review();
                Assert.Equal(HttpStatusCode.UnprocessableEntity, unchanged.StatusCode);
                Assert.Equal("pending", await Sql(c, "SELECT requestStatus FROM dbo.contactCorrection WHERE contactCorrectionId=@id", ("@id", id)));
                var link = (Guid)(await Sql(c, "SELECT personLocationId FROM dbo.personLocation WHERE personId=@id", ("@id", caregiver)))!;
                var stamp = Convert.ToHexString((byte[])(await Sql(c, "SELECT rowVersion FROM dbo.personLocation WHERE personLocationId=@id", ("@id", link)))!);
                await owner.Http.GetStringAsync($"/people/{caregiver}/places/{link}/edit");
                using var corrected = await owner.NoRedirect.PostAsync($"/people/{caregiver}/places/{link}/edit", Form(("Unit", appliedUnit), ("RowVersion", stamp)));
                Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
            }
            using var reviewed = await Review();
            Assert.Equal(HttpStatusCode.Redirect, reviewed.StatusCode);
            Assert.Equal("applied", await Sql(c, "SELECT requestStatus FROM dbo.contactCorrection WHERE contactCorrectionId=@id", ("@id", id)));
            Assert.Equal(action, await Sql(c, "SELECT unitAction FROM dbo.contactCorrection WHERE contactCorrectionId=@id", ("@id", id)));
            var key = await Sql(c, "SELECT proposedAddressKey FROM dbo.contactCorrection WHERE contactCorrectionId=@id", ("@id", id));
            Assert.Equal(action == "unspecified" ? null : MatchKeys.AddressKey("260 Audit Street", proposedUnit, "Baltimore", "MD", "21223"), key is DBNull ? null : key as string);
            var actualUnit = await Sql(c, "SELECT l.unit FROM dbo.personLocation pl JOIN dbo.location l ON l.locationId=pl.locationId WHERE pl.personId=@id AND pl.isPrimary=1 AND pl.isDeleted=0", ("@id", caregiver));
            Assert.Equal(appliedUnit, actualUnit is DBNull ? null : actualUnit as string);
            if (originalUnit is not null) Assert.Contains(originalUnit, (string)(await Sql(c, "SELECT beforeJson FROM dbo.contactCorrection WHERE contactCorrectionId=@id", ("@id", id)))!, StringComparison.Ordinal);
            var relationship = (Guid)(await Sql(c, "SELECT personRelationshipId FROM dbo.personRelationship WHERE personId=@id", ("@id", patient)))!;
            var relationshipVersion = Convert.ToHexString((byte[])(await Sql(c, "SELECT rowVersion FROM dbo.personRelationship WHERE personRelationshipId=@id", ("@id", relationship)))!);
            await owner.Http.GetStringAsync($"/people/{patient}/relationships");
            using var verified = await owner.NoRedirect.PostAsync($"/people/{patient}/relationships/{relationship}/verify", Form(("confirmed", "true"), ("primary", "true"), ("role", "caregiver"), ("rowVersion", relationshipVersion)));
            Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
            var today = DateOnly.FromDateTime(DateTime.Today);
            var source = await TestingResult(c, patient, today.AddDays(-14).ToString("yyyy-MM-dd"), 4, "venous"); var task = Guid.NewGuid();
            await Saved(await Plan(patient, task, "later", source, target: today.AddDays(3).ToString("yyyy-MM-dd")));
            var csv = await owner.Http.GetStringAsync("/testing.csv?planKind=later&dueFilter=next7");
            var row = Assert.Single(csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries), line => line.Contains(patient.ToString(), StringComparison.Ordinal));
            Assert.Contains("260 Audit Street" + (appliedUnit is null ? "" : ", " + appliedUnit) + ", Baltimore", row, StringComparison.Ordinal);
        }
        finally
        {
            await Sql(c, "DELETE dbo.contactCorrection WHERE personId=@patient; DELETE dbo.personRelationship WHERE personId=@patient; DELETE dbo.personLocation WHERE personId=@caregiver; DELETE dbo.location WHERE locationId=@place",
                ("@patient", patient), ("@caregiver", caregiver), ("@place", place));
            await CleanTesting(c, patient); await CleanTesting(c, caregiver);
        }
    }
}
