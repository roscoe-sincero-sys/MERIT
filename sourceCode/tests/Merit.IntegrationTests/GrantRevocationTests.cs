using System.Net;

namespace Merit.IntegrationTests;

/// <summary>
/// Issue #295: pressing End on a grant removes it at once. Its end date is the last day it applies, so the date alone
/// let a fresh sign-in use the grant for the rest of the day.
/// </summary>
public sealed partial class AdministrationAndTestingPolicyTests
{
    private static async Task<HttpStatusCode> Status(MeritClient client, string url)
    {
        using var response = await client.NoRedirect.GetAsync(url);
        return response.StatusCode;
    }

    private async Task<Guid> Grant(Guid user, string role)
        => await Sql<Guid>("SELECT ua.userAssignmentId FROM auth.userAssignment ua JOIN auth.role r ON r.roleId=ua.roleId WHERE ua.appUserId=@u AND r.roleCode=@r", ("@u", user), ("@r", role));

    private async Task End(MeritClient admin, Guid user, Guid grant)
    {
        await admin.Http.GetStringAsync($"/admin/users/{user}");
        await Saved(await admin.NoRedirect.PostAsync($"/admin/users/{user}/assignments/{grant}/end", Form()));
    }

    [SqlTheory]
    [InlineData(true)]  // granted today: the end date cannot be moved to yesterday
    [InlineData(false)] // granted years ago
    public async Task EndingTheOnlyGrantRemovesAccessForTheOldSessionAndForAFreshSignIn(bool grantedToday)
    {
        var ids = fixture.Ids;
        string patient = $"/people/{ids.MiaPersonId}";
        // The statewide administrator, not the platform: administration is audited at Baltimore City, and other tests
        // expect the platform account to have left no trace there.
        var stateAdmin = await fixture.SignedInAsync("admin");
        var (account, oldSession) = await User(ids.BaltimoreCityTenantId, "caseManager");
        await using (account)
        using (oldSession)
        {
            var grant = await Grant(account.Id, "caseManager");
            if (grantedToday)
            {
                // The grant was resolved at sign-in; a same-day start date does not change what the session holds.
                await Sql<int>("UPDATE auth.userAssignment SET startDate=CAST(SYSUTCDATETIME() AT TIME ZONE 'UTC' AT TIME ZONE 'Eastern Standard Time' AS DATE) WHERE userAssignmentId=@g; SELECT 0", ("@g", grant));
            }

            Assert.Equal(HttpStatusCode.OK, await Status(oldSession, patient));
            await End(stateAdmin, account.Id, grant);

            Assert.NotNull(await Sql<object>("SELECT revokedUtc FROM auth.userAssignment WHERE userAssignmentId=@g", ("@g", grant)));
            Assert.Equal(1, await Sql<int>("SELECT COUNT(*) FROM auth.userAssignment WHERE userAssignmentId=@g AND isDeleted=0 AND endDate>=startDate", ("@g", grant)));
            Assert.NotEqual(HttpStatusCode.OK, await Status(oldSession, patient));

            // A new browser, password and a fresh authenticator code: the ended grant gives nothing.
            using var fresh = fixture.Anonymous();
            await account.SignInAsync(fresh, MeritFixture.Password);
            Assert.NotEqual(HttpStatusCode.OK, await Status(fresh, patient));
            Assert.NotEqual(HttpStatusCode.OK, await Status(fresh, "/cases"));
            Assert.Equal(0, await Sql<int>("SELECT COUNT(*) FROM auth.tvfUserGrantPermissions(@u)", ("@u", account.Id)));
            // Not offered for assignment either (jobs and assignment lists use the same resolvers).
            Assert.Equal(0, await Sql<int>("SELECT COUNT(*) FROM auth.tvfAssignableUserTenants('case.edit') WHERE appUserId=@u", ("@u", account.Id)));

            // The history is kept: the grant row, its original grant event and one revocation event that records the time.
            Assert.Equal(1, await Sql<int>("SELECT COUNT(*) FROM logs.auditEvent WHERE entityId=@g AND eventAction='revoke' AND JSON_VALUE(administrationJson,'$.revokedUtc') IS NOT NULL", ("@g", grant)));
            string page = WebUtility.HtmlDecode(await stateAdmin.Http.GetStringAsync($"/admin/users/{account.Id}"));
            Assert.Contains("(ended by an administrator)", page, StringComparison.Ordinal);
            Assert.DoesNotContain($"/assignments/{grant}/end", page, StringComparison.Ordinal);

            // An ended grant cannot be ended again or brought back by changing its end date.
            using var again = await stateAdmin.NoRedirect.PostAsync($"/admin/users/{account.Id}/assignments/{grant}/end", Form());
            Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
            Assert.Equal(1, await Sql<int>("SELECT COUNT(*) FROM logs.auditEvent WHERE entityId=@g AND eventAction='revoke'", ("@g", grant)));
        }
    }

    [SqlFact]
    public async Task EndingOneOfSeveralGrantsKeepsTheOthersAndAScheduledEndDateStillRunsThroughItsLastDay()
    {
        var ids = fixture.Ids;
        string patient = $"/people/{ids.MiaPersonId}";
        // The statewide administrator, not the platform: administration is audited at Baltimore City, and other tests
        // expect the platform account to have left no trace there.
        var stateAdmin = await fixture.SignedInAsync("admin");
        var (account, first) = await User(ids.BaltimoreCityTenantId, "caseManager", "intakeClerk");
        await using (account)
        using (first)
        {
            Assert.Equal(HttpStatusCode.OK, await Status(first, patient));
            Assert.True(await Sql<int>("SELECT COUNT(*) FROM auth.tvfUserGrantPermissions(@u) WHERE permissionCode='case.edit'", ("@u", account.Id)) > 0);
            await End(stateAdmin, account.Id, await Grant(account.Id, "caseManager"));

            using var fresh = fixture.Anonymous();
            await account.SignInAsync(fresh, MeritFixture.Password);
            // The intake clerk grant still works; the case manager's right to change cases is gone.
            Assert.Equal(HttpStatusCode.OK, await Status(fresh, "/intake"));
            Assert.Equal(0, await Sql<int>("SELECT COUNT(*) FROM auth.tvfUserGrantPermissions(@u) WHERE permissionCode='case.edit'", ("@u", account.Id)));
            Assert.True(await Sql<int>("SELECT COUNT(*) FROM auth.tvfUserGrantPermissions(@u) WHERE permissionCode='intake.upload'", ("@u", account.Id)) > 0);

            // A scheduled end date of today is not a revocation: the grant applies through its last day.
            var clerk = await Grant(account.Id, "intakeClerk");
            await Sql<int>("UPDATE auth.userAssignment SET endDate=CAST(SYSUTCDATETIME() AT TIME ZONE 'UTC' AT TIME ZONE 'Eastern Standard Time' AS DATE) WHERE userAssignmentId=@g; SELECT 0", ("@g", clerk));
            using var lastDay = fixture.Anonymous();
            await account.SignInAsync(lastDay, MeritFixture.Password);
            Assert.Equal(HttpStatusCode.OK, await Status(lastDay, "/intake"));

            // The administrator can still end it early; then it is gone.
            string page = await stateAdmin.Http.GetStringAsync($"/admin/users/{account.Id}");
            Assert.Contains($"/assignments/{clerk}/end", page, StringComparison.Ordinal);
            await End(stateAdmin, account.Id, clerk);
            using var after = fixture.Anonymous();
            await account.SignInAsync(after, MeritFixture.Password);
            Assert.NotEqual(HttpStatusCode.OK, await Status(after, "/intake"));
            Assert.Equal(0, await Sql<int>("SELECT COUNT(*) FROM auth.tvfUserGrantPermissions(@u)", ("@u", account.Id)));
        }
    }
}
