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

    [SqlFact]
    public async Task Scenario243FreshMarylandTenantBaselineAndSupervisorParity()
    {
        _ = fixture.Ids;
        string suffix = Suffix().ToUpperInvariant(), regionCode = "S243-R3G-" + suffix, mdCode = "S243-MDG-" + suffix, lhdCode = "S243-BALTCO-" + suffix;
        string regionUser = "s243r3g" + suffix, mdUser = "s243mdg" + suffix, lhdUser = "s243lhdg" + suffix;
        var platform = await ScopeAsync("platform");
        await RunAsync(platform, "config.uspCreateRootTenant", new()
        {
            ["tenantCode"] = regionCode, ["tenantName"] = "Scenario 243 Region 3 Gaps", ["tenantType"] = "region",
        });
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
        await RunAsync(regional, "config.uspCreateRootTenant", new()
        {
            ["tenantCode"] = mdCode, ["tenantName"] = "Scenario 243 Maryland Gaps", ["tenantType"] = "state",
            ["statePostalCode"] = "MD", ["stateFips"] = "24", ["parentTenantCode"] = regionCode,
            ["timeZone"] = "Eastern Standard Time", ["effectiveFrom"] = new DateTime(2020,1,1), ["seedThresholds"] = false,
            ["countiesJson"] = """[{"fips":"005","name":"Baltimore County"},{"fips":"510","name":"Baltimore city"}]""",
        });
        var md = await TenantAsync(mdCode);

        // Raw procedure calls retain immutable scopes; HTTP creation refreshes them (covered by replay).
        Assert.Equal(51403, (await Assert.ThrowsAsync<SqlException>(() => SaveUserAsync(regional, mdUser, md))).Number);
        regional = await ScopeAsync(regionUser);
        await SaveUserAsync(regional, mdUser, md);
        await GrantAsync(regional, mdUser, await Role(md, "administrator"), md, suffix);

        var maryland = await ScopeAsync(mdUser);
        await RunAsync(maryland, "config.uspCreateChildTenant", new()
        {
            ["tenantCode"] = lhdCode, ["tenantName"] = "Scenario 243 Baltimore County LHD", ["tenantType"] = "jurisdiction",
            ["parentTenantCode"] = mdCode, ["countyFipsList"] = "005",
        });
        var baltCo = await TenantAsync(lhdCode);
        Assert.Equal(51403, (await Assert.ThrowsAsync<SqlException>(() => SaveUserAsync(maryland, lhdUser, baltCo))).Number);
        maryland = await ScopeAsync(mdUser);
        await SaveUserAsync(maryland, lhdUser, baltCo);

        // Fresh supervisors have the same waiver permission as upgraded supervisors.
        await using (var c = new SqlConnection(TestEnvironment.AdminConnectionString()))
        {
            await c.OpenAsync();
            await using var cmd = new SqlCommand("""
                SELECT COUNT(*) FROM auth.rolePermission rp
                JOIN auth.permission p ON p.permissionId = rp.permissionId
                WHERE rp.roleId = @role AND p.permissionCode = 'workflow.waive' AND rp.isDeleted = 0
                """, c);
            cmd.Parameters.AddWithValue("@role", await Role(md, "supervisor"));
            Assert.Equal(1, (int)(await cmd.ExecuteScalarAsync())!);
        }

        // Gap 2: Fresh root tenant has zero laboratory intake profiles configured and no UI/service path to create them.
        await using (var c = new SqlConnection(TestEnvironment.AdminConnectionString()))
        {
            await c.OpenAsync();
            await using var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.intakeProfile WHERE rootTenantId=@root", c);
            cmd.Parameters.AddWithValue("@root", md);
            Assert.Equal(0, (int)(await cmd.ExecuteScalarAsync())!);
        }

        // Baseline settings apply to historical setup; thresholds are explicitly opted out.
        Assert.NotEmpty(await RowsAsync(maryland, "config.uspGetSettings", new() { ["asOfDate"] = new DateTime(2020, 8, 1) }));
        Assert.Empty(await RowsAsync(maryland, "config.uspGetThresholds", new() { ["asOfDate"] = new DateTime(2026, 1, 1) }));
    }
}
