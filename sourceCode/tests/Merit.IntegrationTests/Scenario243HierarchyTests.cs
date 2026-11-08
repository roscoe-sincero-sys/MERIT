using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

public sealed partial class AuthorizationScenarioTests
{
    // Diagnostic scenario, using real web-login procedures and RLS scopes. Password/MFA UI is covered separately.
    [SqlFact]
    public async Task Scenario243DelegatedMarylandBootstrapAndWorkflowAccounts()
    {
        _ = fixture.Ids;
        string suffix = Suffix().ToUpperInvariant(), regionCode = "S243-R3-" + suffix, mdCode = "S243-MD-" + suffix;
        string regionUser = "s243region" + suffix, mdUser = "s243md" + suffix, supervisorUser = "s243sup" + suffix;
        var platform = await ScopeAsync("platform");
        await RunAsync(platform, "config.uspCreateRootTenant", new() {
            ["tenantCode"] = regionCode, ["tenantName"] = "Scenario 243 Region 3", ["tenantType"] = "region" });
        var region = await TenantAsync(regionCode);
        async Task<Guid> Role(Guid root, string code)
        {
            await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
            await using var cmd = new SqlCommand("SELECT roleId FROM auth.role WHERE rootTenantId=@root AND roleCode=@code", c);
            cmd.Parameters.AddWithValue("@root", root); cmd.Parameters.AddWithValue("@code", code);
            return (Guid)(await cmd.ExecuteScalarAsync())!;
        }
        await SaveUserAsync(platform, regionUser, region);
        await GrantAsync(platform, regionUser, await Role(region, "administrator"), region, suffix);
        var regional = await ScopeAsync(regionUser);
        await RunAsync(regional, "config.uspCreateRootTenant", new() {
            ["tenantCode"] = mdCode, ["tenantName"] = "Scenario 243 Maryland", ["tenantType"] = "state",
            ["statePostalCode"] = "MD", ["stateFips"] = "24", ["parentTenantCode"] = regionCode,
            ["timeZone"] = "Eastern Standard Time" });
        var md = await TenantAsync(mdCode);
        regional = await ScopeAsync(regionUser); // Newly created descendant enters the refreshed scope.
        await SaveUserAsync(regional, mdUser, md);
        await GrantAsync(regional, mdUser, await Role(md, "administrator"), md, suffix);
        var maryland = await ScopeAsync(mdUser);
        await SaveUserAsync(maryland, supervisorUser, md);
        // Administrator does not possess clinical rights and cannot delegate them. Supply those rights explicitly
        // from the platform down the same hierarchy; do not weaken the delegation guard to make the scenario pass.
        var supervisorRole = await Role(md, "supervisor");
        Assert.Equal(51403, (await Assert.ThrowsAsync<SqlException>(() =>
            GrantAsync(maryland, supervisorUser, supervisorRole, md, suffix))).Number);
        await GrantAsync(platform, regionUser, await Role(region, "supervisor"), region, suffix);
        regional = await ScopeAsync(regionUser);
        await GrantAsync(regional, mdUser, await Role(md, "supervisor"), md, suffix);
        maryland = await ScopeAsync(mdUser);
        await GrantAsync(maryland, supervisorUser, await Role(md, "supervisor"), md, suffix);
        var supervisor = await ScopeAsync(supervisorUser);
        Assert.Equal(51403, (await Assert.ThrowsAsync<SqlException>(() => SaveUserAsync(supervisor, "s243deny" + suffix, md))).Number);
        // Explicit account-administration authority is required; supervisor alone must not escalate itself.
        await GrantAsync(maryland, supervisorUser, await Role(md, "userAdministrator"), md, suffix);
        supervisor = await ScopeAsync(supervisorUser);
        foreach (var code in new[] { "intakeClerk", "caseManager", "investigator", "epidemiologist" })
        {
            string name = "s243" + code + suffix;
            await SaveUserAsync(supervisor, name, md);
            await GrantAsync(supervisor, name, await Role(md, code), md, suffix);
            Assert.NotNull(await ScopeAsync(name));
        }
        // Like the existing authorization scenarios, this retains labelled synthetic tenants in MERIT_IT for inspection.
    }
}
