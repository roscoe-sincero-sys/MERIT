using System.Net;
using Merit.Application.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Merit.IntegrationTests;

public sealed partial class Hl7IntakeTests
{
    /// <summary>Issue #268: an intake clerk granted only at a county imports and enters results with a lab-reporting rule
    /// active. Receipt-quality work used to be owned by the data root, which the county session cannot write, so the
    /// row-level-security block predicate aborted the row (and, through #269, the whole batch).</summary>
    [SqlFact]
    public async Task CountyScopedClerkImportsAndEntersResultsWithItsWorkKeptInItsCounty()
    {
        _ = fixture.Ids;
        await using var harness = await Harness.OpenAsync();
        var rule = Guid.NewGuid();
        var grant = Guid.NewGuid();
        var specimen = await SqlAsync<Guid>("SELECT analyteSpecimenId FROM dbo.labResult WHERE labResultId=@a", fixture.Ids.MiaLabResultId);
        var admin = await fixture.SignedInAsync("admin");
        await admin.Http.GetStringAsync("/config/notification-rules?template=mdLabElevated");
        var fields = MarylandPolicyDrafts.Get("mdLabElevated").Select(p => new KeyValuePair<string, string>(p.Key, p.Value)).ToList();
        fields.AddRange(new Dictionary<string, string>
        {
            ["notificationRuleId"] = rule.ToString(), ["analyteSpecimenId"] = specimen.ToString(),
            ["calendarName"] = "Synthetic weekdays-only regression calendar", ["holidays"] = "", ["approved"] = "true",
        });
        using (var activation = await admin.NoRedirect.PostAsync("/config/notification-rules", new FormUrlEncodedContent(fields)))
            Assert.Equal(HttpStatusCode.Redirect, activation.StatusCode);
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        // cmbalt works only in Baltimore City; give them the intake clerk role there (and nowhere else) for this test.
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
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var profile = await harness.ProfileAsync("hl7");

            // A Baltimore County resident (PID-11 county 005) reported to a Baltimore City-only clerk.
            var imported = await harness.RunAsync(profile, Message("County" + suffix, "CTY-" + suffix, dob: "20240102").Replace("|2.1|", "|6|", StringComparison.Ordinal),
                departmentReceivedAt: new DateTime(2026, 10, 22, 10, 0, 0), asUser: "cmbalt");
            Assert.Equal("loaded", imported.BatchStatus);
            Assert.Equal(1, imported.LoadedCount);
            var result = await SqlAsync<Guid>("SELECT labResultId FROM dbo.labResult WHERE intakeBatchId=@a", imported.IntakeBatchId);
            var person = await SqlAsync<Guid>("SELECT personId FROM dbo.labResult WHERE labResultId=@a", result);
            Assert.True(await SqlAsync<int>("SELECT COUNT(*) FROM dbo.workTask WHERE labResultId=@a AND queueCode IN ('receiptFinding','missingInformation','labReportingReview')", result) > 0);
            Assert.Equal(0, await SqlAsync<int>("SELECT COUNT(*) FROM dbo.workTask WHERE labResultId=@a AND queueCode IN ('receiptFinding','missingInformation','labReportingReview') AND owningTenantId<>@b", result, city));
            // The residence jurisdiction is not writable here: the case opens in the clerk's county and says so.
            var caseId = await SqlAsync<Guid>("SELECT exposureCaseId FROM dbo.exposureCase WHERE qualifyingLabResultId=@a", result);
            Assert.Equal(city, await SqlAsync<Guid>("SELECT owningTenantId FROM dbo.exposureCase WHERE exposureCaseId=@a", caseId));
            Assert.Equal(1, await SqlAsync<int>("SELECT COUNT(*) FROM dbo.workTask WHERE exposureCaseId=@a AND title=N'Case opened outside the residence jurisdiction' AND detail LIKE N'%Baltimore County%'", caseId));

            // Manual entry by the same county clerk: saved, evaluated, and its timeline review owned by the county.
            await harness.AsAsync("cmbalt");
            var manual = await harness.Web.GetRequiredService<ResultService>().SaveManualAsync(new ResultEntryInput(person,
                await SqlAsync<Guid>("SELECT analyteId FROM config.analyteSpecimen WHERE analyteSpecimenId=@a", specimen),
                specimen, "5", "ug/dL", "venous", null, null, new DateOnly(2026, 10, 21), null, new DateOnly(2026, 10, 22), null,
                await SqlAsync<Guid>("SELECT labOrganizationId FROM dbo.labResult WHERE labResultId=@a", fixture.Ids.MiaLabResultId),
                null, "CTYM-" + suffix, "final", null, null, DepartmentReceivedAt: new DateTime(2026, 10, 22, 11, 0, 0)));
            Assert.DoesNotContain("did not complete", manual.CaseOutcome, StringComparison.Ordinal);
            Assert.Equal(1, await SqlAsync<int>("SELECT COUNT(*) FROM dbo.workTask WHERE labResultId=@a AND queueCode='labReportingReview' AND owningTenantId=@b", manual.LabResultId, city));
        }
        finally
        {
            // Retire this test's cases: every imported patient shares the HL7 sample address, and open cases at one
            // address are a cluster that ClusterDetectionTests would otherwise count.
            await Execute(c, "UPDATE dbo.exposureCase SET isDeleted=1 WHERE personId IN (SELECT r.personId FROM dbo.labResult r JOIN dbo.intakeBatch b ON b.intakeBatchId=r.intakeBatchId WHERE b.submittedByUserId=(SELECT appUserId FROM auth.appUser WHERE userName='cmbalt') AND r.labResultId<>@id)", Guid.Empty);
            await Execute(c, "UPDATE auth.userAssignment SET isDeleted=1 WHERE userAssignmentId=@id", grant);
            await Execute(c, "UPDATE config.notificationRule SET isActive=0 WHERE notificationRuleId=@id", rule);
        }
    }

    /// <summary>Issue #269: a row the initiating profile may not complete is held as permissionDenied, which the hold
    /// reason constraint must accept (it used to reject it and strand the batch in 'parsing').</summary>
    [SqlFact]
    public async Task PermissionDeniedIsAnAllowedHoldReason()
    {
        _ = fixture.Ids;
        Assert.Equal(1, await SqlAsync<int>("SELECT COUNT(*) FROM sys.check_constraints WHERE name=N'CK_dbo_stagingHoldReason_reasonCode' AND definition LIKE N'%''permissionDenied''%'"));    }
}
