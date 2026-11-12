using System.Net;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

public sealed partial class Hl7IntakeTests
{
    /// <summary>Issue #274: a case opened outside the residence jurisdiction (the #268 fallback) is transferred there with
    /// its history, tasks and own investigation, by someone who works in both jurisdictions, and only by them.</summary>
    [SqlFact]
    public async Task CaseOpenedOutsideTheResidenceIsTransferredWithItsWorkByAWorkerInBothJurisdictions()
    {
        _ = fixture.Ids;
        await using var harness = await Harness.OpenAsync();
        var grant = Guid.NewGuid();
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        await Execute(c, """
            INSERT auth.userAssignment (userAssignmentId, appUserId, roleId, tenantId, includeDescendants, accessLevel, startDate)
            SELECT @id, u.appUserId, r.roleId, t.tenantId, 1, 'full', '2026-01-01'
              FROM auth.appUser u JOIN auth.tenant t ON t.tenantCode = 'MD-BCHD'
              JOIN auth.role r ON r.rootTenantId = t.rootTenantId AND r.roleCode = 'intakeClerk' AND r.isDeleted = 0
             WHERE u.userName = 'cmbalt';
            """, grant);
        try
        {
            var city = await SqlAsync<Guid>("SELECT tenantId FROM auth.tenant WHERE tenantCode='MD-BCHD'");
            var county = await SqlAsync<Guid>("SELECT tenantId FROM auth.tenant WHERE tenantCode='MD-BCO'");
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var imported = await harness.RunAsync(await harness.ProfileAsync("hl7"),
                Message("Move" + suffix, "MOV-" + suffix, dob: "20240103").Replace("|2.1|", "|7|", StringComparison.Ordinal),
                departmentReceivedAt: new DateTime(2026, 10, 22, 10, 0, 0), asUser: "cmbalt");
            var result = await SqlAsync<Guid>("SELECT labResultId FROM dbo.labResult WHERE intakeBatchId=@a", imported.IntakeBatchId);
            var caseId = await SqlAsync<Guid>("SELECT exposureCaseId FROM dbo.exposureCase WHERE qualifyingLabResultId=@a", result);
            Assert.Equal(city, await SqlAsync<Guid>("SELECT owningTenantId FROM dbo.exposureCase WHERE exposureCaseId=@a", caseId));
            var routing = await SqlAsync<Guid>("SELECT workTaskId FROM dbo.workTask WHERE exposureCaseId=@a AND title=N'Case opened outside the residence jurisdiction'", caseId);
            Assert.Contains("Transfer case", await SqlAsync<string>("SELECT detail FROM dbo.workTask WHERE workTaskId=@a", routing), StringComparison.Ordinal);
            string Version() => Convert.ToHexString(SqlAsync<byte[]>("SELECT rowVersion FROM dbo.exposureCase WHERE exposureCaseId=@a", caseId).GetAwaiter().GetResult());
            FormUrlEncodedContent Transfer(Guid to, bool confirmed = true) => new(new Dictionary<string, string>
            {
                ["toTenantId"] = to.ToString(), ["transferReason"] = "Family lives in Baltimore County; county agreed by phone",
                ["transferConfirmed"] = confirmed ? "true" : "false", ["rowVersion"] = Version(),
            });

            // A city-only worker is not offered a transfer and cannot force one.
            var cityOnly = await fixture.SignedInAsync("cmbalt");
            Assert.DoesNotContain("case-transfer-form", await cityOnly.Http.GetStringAsync($"/cases/{caseId}"), StringComparison.Ordinal);
            using (var refused = await cityOnly.NoRedirect.PostAsync($"/cases/{caseId}/transfer", Transfer(county)))
                Assert.NotEqual(HttpStatusCode.OK, refused.StatusCode);
            Assert.Equal(city, await SqlAsync<Guid>("SELECT owningTenantId FROM dbo.exposureCase WHERE exposureCaseId=@a", caseId));

            // A worker in both jurisdictions sees the residence jurisdiction first; an unconfirmed transfer changes nothing.
            var both = await fixture.SignedInAsync("cmboth");
            string page = await both.Http.GetStringAsync($"/cases/{caseId}");
            Assert.Contains("case-transfer-form", page, StringComparison.Ordinal);
            Assert.Contains("(patient&#x27;s residence)", page, StringComparison.Ordinal);
            using (var unconfirmed = await both.NoRedirect.PostAsync($"/cases/{caseId}/transfer", Transfer(county, confirmed: false)))
                Assert.Equal(HttpStatusCode.UnprocessableEntity, unconfirmed.StatusCode);
            Assert.Equal(city, await SqlAsync<Guid>("SELECT owningTenantId FROM dbo.exposureCase WHERE exposureCaseId=@a", caseId));

            // Its own investigation, requested before the transfer, moves with it.
            var location = await SqlAsync<Guid>("SELECT TOP (1) locationId FROM dbo.personLocation WHERE personId=(SELECT personId FROM dbo.exposureCase WHERE exposureCaseId=@a) AND isDeleted=0", caseId);
            await both.Http.GetStringAsync($"/cases/{caseId}/investigations/new");
            using (var requested = await both.NoRedirect.PostAsync($"/cases/{caseId}/investigations", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["ExposureCaseId"] = caseId.ToString(), ["InvestigationType"] = "residential", ["Target"] = "loc:" + location, ["Checked"] = "true",
            })))
                Assert.True(requested.IsSuccessStatusCode, requested.StatusCode.ToString());
            var investigation = await SqlAsync<Guid>("SELECT investigationId FROM dbo.investigationCase WHERE exposureCaseId=@a AND isDeleted=0", caseId);

            using (var moved = await both.NoRedirect.PostAsync($"/cases/{caseId}/transfer", Transfer(county)))
                Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
            Assert.Equal(county, await SqlAsync<Guid>("SELECT owningTenantId FROM dbo.exposureCase WHERE exposureCaseId=@a", caseId));
            Assert.Equal(0, await SqlAsync<int>("SELECT COUNT(*) FROM dbo.workTask WHERE exposureCaseId=@a AND owningTenantId<>@b", caseId, county));
            Assert.Equal(0, await SqlAsync<int>("SELECT COUNT(*) FROM dbo.caseActivity WHERE exposureCaseId=@a AND owningTenantId<>@b", caseId, county));
            Assert.Equal(county, await SqlAsync<Guid>("SELECT owningTenantId FROM dbo.investigation WHERE investigationId=@a", investigation));
            Assert.Equal("done", await SqlAsync<string>("SELECT taskStatus FROM dbo.workTask WHERE workTaskId=@a", routing));
            Assert.Equal(1, await SqlAsync<int>("SELECT COUNT(*) FROM dbo.caseActivity WHERE exposureCaseId=@a AND activityType='statusChange' AND note LIKE N'Case transferred from %Reason: Family lives%'", caseId));
            Assert.True(await SqlAsync<int>("SELECT COUNT(*) FROM logs.auditEvent WHERE entityType='exposureCase' AND entityId=@a AND eventAction='update' AND owningTenantId=@b", caseId, county) > 0);

            // The receiving county now works the case; the city no longer sees it.
            var countyOnly = await fixture.SignedInAsync("cmbaltco");
            Assert.Equal(HttpStatusCode.OK, (await countyOnly.NoRedirect.GetAsync($"/cases/{caseId}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await countyOnly.NoRedirect.GetAsync($"/investigations/{investigation}")).StatusCode);
            Assert.NotEqual(HttpStatusCode.OK, (await cityOnly.NoRedirect.GetAsync($"/cases/{caseId}")).StatusCode);
        }
        finally
        {
            // Retire this test's cases: every imported patient shares the HL7 sample address, and open cases at one
            // address are a cluster that ClusterDetectionTests would otherwise count.
            await Execute(c, "UPDATE dbo.exposureCase SET isDeleted=1 WHERE personId IN (SELECT r.personId FROM dbo.labResult r JOIN dbo.intakeBatch b ON b.intakeBatchId=r.intakeBatchId WHERE b.submittedByUserId=(SELECT appUserId FROM auth.appUser WHERE userName='cmbalt') AND r.labResultId<>@id)", Guid.Empty);
            await Execute(c, "UPDATE auth.userAssignment SET isDeleted=1 WHERE userAssignmentId=@id", grant);
        }
    }
}
