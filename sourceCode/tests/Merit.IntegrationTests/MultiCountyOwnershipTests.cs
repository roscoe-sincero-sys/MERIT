using System.Net;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

public sealed partial class Hl7IntakeTests
{
    /// <summary>Issues #279 and #280: a worker in Baltimore City and Baltimore County imports residents of both. The new
    /// patient belongs to the residence county when the worker can work there, so a single-county colleague in the other
    /// county does not get the record, and the worker's default county is the same every time.</summary>
    [SqlFact]
    public async Task MultiCountyIntakeGivesNewPatientsToTheirResidenceCountyNotTheWorkingCounty()
    {
        _ = fixture.Ids;
        await using var harness = await Harness.OpenAsync();
        Guid cityGrant = Guid.NewGuid(), countyGrant = Guid.NewGuid();
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        // cmboth (case manager in both counties) also takes intake in both for this test.
        foreach (var (grant, code) in new[] { (cityGrant, "MD-BCHD"), (countyGrant, "MD-BCO") })
            await Execute(c, $"""
                INSERT auth.userAssignment (userAssignmentId, appUserId, roleId, tenantId, includeDescendants, accessLevel, startDate)
                SELECT @id, u.appUserId, r.roleId, t.tenantId, 1, 'full', '2026-01-01'
                  FROM auth.appUser u JOIN auth.tenant t ON t.tenantCode = '{code}'
                  JOIN auth.role r ON r.rootTenantId = t.rootTenantId AND r.roleCode = 'intakeClerk' AND r.isDeleted = 0
                 WHERE u.userName = 'cmboth';
                """, grant);
        try
        {
            var city = await SqlAsync<Guid>("SELECT tenantId FROM auth.tenant WHERE tenantCode='MD-BCHD'");
            var county = await SqlAsync<Guid>("SELECT tenantId FROM auth.tenant WHERE tenantCode='MD-BCO'");
            var profile = await harness.ProfileAsync("hl7");
            var suffix = Guid.NewGuid().ToString("N")[..10];

            // Equal-depth tenants: the default working tenant is chosen by name, every time (#280).
            var resident = await harness.RunAsync(profile, Message("Own" + suffix, "OWN-" + suffix, dob: "20240104").Replace("|2.1|", "|6|", StringComparison.Ordinal),
                departmentReceivedAt: new DateTime(2026, 10, 22, 10, 0, 0), asUser: "cmboth");
            Assert.Equal(city, await SqlAsync<Guid>("SELECT owningTenantId FROM dbo.intakeBatch WHERE intakeBatchId=@a", resident.IntakeBatchId));

            // A Baltimore County resident (PID-11 county 005) imported while working in Baltimore City.
            var person = await SqlAsync<Guid>("SELECT personId FROM dbo.labResult WHERE intakeBatchId=@a", resident.IntakeBatchId);
            Assert.Equal(county, await SqlAsync<Guid>("SELECT owningTenantId FROM dbo.person WHERE personId=@a", person));
            Assert.Equal(0, await SqlAsync<int>("SELECT COUNT(*) FROM dbo.personAccess WHERE personId=@a AND tenantId=@b AND isDeleted=0", person, city));
            Assert.Equal(0, await SqlAsync<int>("SELECT COUNT(*) FROM dbo.personAlias WHERE personId=@a AND owningTenantId=@b", person, city));
            Assert.Equal(county, await SqlAsync<Guid>("SELECT owningTenantId FROM dbo.exposureCase WHERE personId=@a", person));
            var cityOnly = await fixture.SignedInAsync("cmbalt");
            var countyOnly = await fixture.SignedInAsync("cmbaltco");
            Assert.NotEqual(HttpStatusCode.OK, (await cityOnly.NoRedirect.GetAsync($"/people/{person}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await countyOnly.NoRedirect.GetAsync($"/people/{person}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await countyOnly.NoRedirect.GetAsync($"/people/{person}/results")).StatusCode);

            // A resident of a county the worker does not serve (033, Prince George's) stays with the working county.
            var outside = await harness.RunAsync(profile, Message("Out" + suffix, "OUT-" + suffix, county: "033", dob: "20240105"),
                departmentReceivedAt: new DateTime(2026, 10, 22, 10, 0, 0), asUser: "cmboth");
            var outsider = await SqlAsync<Guid>("SELECT personId FROM dbo.labResult WHERE intakeBatchId=@a", outside.IntakeBatchId);
            Assert.Equal(city, await SqlAsync<Guid>("SELECT owningTenantId FROM dbo.person WHERE personId=@a", outsider));

            // Existing patients are never re-owned by a later report.
            var again = await harness.RunAsync(profile, Message("Own" + suffix, "OW2-" + suffix, dob: "20240104"),
                departmentReceivedAt: new DateTime(2026, 10, 23, 10, 0, 0), asUser: "cmboth");
            Assert.Equal(person, await SqlAsync<Guid>("SELECT personId FROM dbo.labResult WHERE intakeBatchId=@a", again.IntakeBatchId));
            Assert.Equal(county, await SqlAsync<Guid>("SELECT owningTenantId FROM dbo.person WHERE personId=@a", person));
        }
        finally
        {
            // Retire this test's cases (the HL7 sample address is shared; see ClusterDetectionTests) and its grants.
            await Execute(c, "UPDATE dbo.exposureCase SET isDeleted=1 WHERE personId IN (SELECT r.personId FROM dbo.labResult r JOIN dbo.intakeBatch b ON b.intakeBatchId=r.intakeBatchId WHERE b.submittedByUserId=(SELECT appUserId FROM auth.appUser WHERE userName='cmboth') AND r.labResultId<>@id)", Guid.Empty);
            await Execute(c, "UPDATE auth.userAssignment SET isDeleted=1 WHERE userAssignmentId=@id", cityGrant);
            await Execute(c, "UPDATE auth.userAssignment SET isDeleted=1 WHERE userAssignmentId=@id", countyGrant);
        }
    }
}
