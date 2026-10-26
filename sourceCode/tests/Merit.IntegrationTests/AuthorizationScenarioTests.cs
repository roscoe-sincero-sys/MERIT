using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

/// <summary>
/// The hierarchical tenant scenario from the authorization requirements, built on EPA Region 3: a platform administrator;
/// region, state (West Virginia, Pennsylvania, Delaware), tribe and county (Jefferson, Berkeley) tenants; administrators
/// and staff at each level; a staff member working for two counties; reduced profiles; and viewing as another user.
/// Every check calls the stored procedures as the web login under that user's session scope, so it is the database's
/// own answer, not the application's.
/// </summary>
[Collection(MeritCollection.Name)]
public sealed class AuthorizationScenarioTests(MeritFixture fixture)
{
    private static readonly object Gate = new();
    private static bool built;

    // ------------------------------------------------------------------------------------------------ 1-7 visibility

    [SqlFact]
    public async Task RegionAdministratorSeesEveryStateAndCountyInTheRegion()
    {
        Build();
        var seen = await TaskTitlesAsync("r3admin");
        Assert.Superset(new HashSet<string> { "R3 region task", "WV state task", "PA state task", "DE state task", "Jefferson task", "Berkeley task", "Tribe task" }, seen);
    }

    [SqlFact]
    public async Task PennsylvaniaStaffDoNotSeeDelaware()
    {
        Build();
        var seen = await TaskTitlesAsync("pastaff");
        Assert.Contains("PA state task", seen);
        Assert.DoesNotContain("DE state task", seen);
        Assert.DoesNotContain("R3 region task", seen);
        Assert.DoesNotContain("WV state task", seen);
    }

    [SqlFact]
    public async Task StateStaffSeeTheirStateAndItsCountiesOnly()
    {
        Build();
        var seen = await TaskTitlesAsync("wvstaff");
        Assert.Superset(new HashSet<string> { "WV state task", "Jefferson task", "Berkeley task" }, seen);
        Assert.DoesNotContain("PA state task", seen);
        Assert.DoesNotContain("R3 region task", seen);
    }

    [SqlFact]
    public async Task CountyStaffSeeTheirCountyAndTwoCountyStaffSeeBoth()
    {
        Build();
        var jefferson = await TaskTitlesAsync("jefstaff");
        Assert.Contains("Jefferson task", jefferson);
        Assert.DoesNotContain("Berkeley task", jefferson);
        Assert.DoesNotContain("WV state task", jefferson);

        var both = await TaskTitlesAsync("jbstaff");
        Assert.Contains("Jefferson task", both);
        Assert.Contains("Berkeley task", both);
        Assert.DoesNotContain("WV state task", both);
    }

    // ------------------------------------------------------------------------------------------------ 10 platform

    [SqlFact]
    public async Task PlatformAdministratorManagesAccountsButSeesNoRecords()
    {
        Build();
        var platform = await ScopeAsync("platform");
        var ex = await Assert.ThrowsAsync<SqlException>(() => RunAsync(platform, "dbo.uspListWorkTasks", new() { ["taskStatus"] = "open" }));
        Assert.Equal(51403, ex.Number);
        await Assert.ThrowsAsync<SqlException>(() => RunAsync(platform, "dbo.uspGetCaseMap", []));

        var users = await NamesAsync(platform, "auth.uspListUsers", []);
        Assert.Superset(new HashSet<string> { "r3admin", "wvstaff", "jefstaff", "jbstaff", "pastaff" }, users);

        var switchEx = await Assert.ThrowsAsync<SqlException>(() => ScopeAsync("platform", viewAs: "jefstaff"));
        Assert.Equal(51403, switchEx.Number);
    }

    // ------------------------------------------------------------------------------------------------ adding tenants

    [SqlFact]
    public async Task TenantCreationFollowsTheTreeAndTheRole()
    {
        Build();
        string s = Suffix().ToUpperInvariant();

        // Platform administrator: any data root, directly under the platform.
        var platform = await ScopeAsync("platform");
        await RunAsync(platform, "config.uspCreateRootTenant", new() { ["tenantCode"] = "R9-" + s, ["tenantName"] = "Region 9 " + s, ["tenantType"] = "region" });

        // Region administrator: a state under the region, never a top-level tenant.
        var r3 = await ScopeAsync("r3admin");
        await RunAsync(r3, "config.uspCreateRootTenant", new()
        {
            ["tenantCode"] = "R3-MD-" + s, ["tenantName"] = "Maryland " + s, ["tenantType"] = "state", ["statePostalCode"] = "MD",
            ["stateFips"] = "24", ["parentTenantCode"] = "EPA-R3", ["countiesJson"] = """[{"fips":"510","name":"Baltimore city"}]""",
        });
        Assert.Equal(51403, (await Assert.ThrowsAsync<SqlException>(() => RunAsync(r3, "config.uspCreateRootTenant",
            new() { ["tenantCode"] = "TOP-" + s, ["tenantName"] = "Top level", ["tenantType"] = "region" }))).Number);

        // State administrator (administrator role holds tenant.create): a county with a county new to the state.
        var wv = await ScopeAsync("wvadmin");
        await RunAsync(wv, "config.uspCreateChildTenant", new()
        {
            ["tenantCode"] = "R3-WV-MOR-" + s, ["tenantName"] = "Morgan County " + s, ["tenantType"] = "jurisdiction",
            ["parentTenantCode"] = "R3-WV", ["countiesJson"] = """[{"fips":"065","name":"Morgan County"}]""",
        });
        var wvAgain = await ScopeAsync("wvadmin");
        var counties = await RowsAsync(wvAgain, "config.uspListSubJurisdictions", []);
        Assert.Contains(counties, r => (string?)r["countyFips"] == "065" && ((string?)r["servedBy"] ?? "").Contains("Morgan County " + s));
        Assert.Equal(51403, (await Assert.ThrowsAsync<SqlException>(() => RunAsync(wv, "config.uspCreateRootTenant", new()
            { ["tenantCode"] = "R3-VA-" + s, ["tenantName"] = "Virginia", ["tenantType"] = "state", ["statePostalCode"] = "VA", ["parentTenantCode"] = "EPA-R3" }))).Number);
        Assert.Equal(51403, (await Assert.ThrowsAsync<SqlException>(() => RunAsync(wv, "config.uspCreateChildTenant", new()
            { ["tenantCode"] = "R3-PA-X-" + s, ["tenantName"] = "Not mine", ["tenantType"] = "jurisdiction", ["parentTenantCode"] = "R3-PA" }))).Number);

        // A user administrator manages accounts only; and codes stay unique across the tree.
        var jef = await ScopeAsync("jefadmin");
        Assert.Equal(51403, (await Assert.ThrowsAsync<SqlException>(() => RunAsync(jef, "config.uspCreateChildTenant", new()
            { ["tenantCode"] = "R3-WV-JEF-P-" + s, ["tenantName"] = "Jefferson program", ["tenantType"] = "program", ["parentTenantCode"] = "R3-WV-JEF" }))).Number);
        Assert.Equal(51422, (await Assert.ThrowsAsync<SqlException>(() => RunAsync(wv, "config.uspCreateChildTenant", new()
            { ["tenantCode"] = "R3-WV-BER", ["tenantName"] = "Duplicate", ["tenantType"] = "program", ["parentTenantCode"] = "R3-WV-JEF" }))).Number);

        // The core installers are not reachable directly.
        Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => RunAsync(wv, "config.uspInstallChildTenant", new()
            { ["tenantCode"] = "R3-WV-Z-" + s, ["tenantName"] = "Direct", ["tenantType"] = "program", ["parentTenantCode"] = "R3-WV" }))).Number);
    }

    [SqlFact]
    public async Task BelowTheDataRootANewTenantServesOnlyItsParentsCounties()
    {
        // Issue #6: counties give residence access to the people who live there, so a county administrator who may add
        // tenants must not map one to another county, nor add counties to the state. Morgan County is used only here.
        Build();
        string s = Suffix().ToUpperInvariant();
        var morCfg = await ScopeAsync("morcfg");
        var wv = await ScopeAsync("wvadmin");

        // Morgan serves 065: a program under it may serve 065, or no county at all.
        await RunAsync(morCfg, "config.uspCreateChildTenant", new()
            { ["tenantCode"] = "R3-WV-MOR-LEAD-" + s, ["tenantName"] = "Morgan lead program " + s, ["tenantType"] = "program", ["parentTenantCode"] = "R3-WV-MOR", ["countyFipsList"] = "065" });
        await RunAsync(morCfg, "config.uspCreateChildTenant", new()
            { ["tenantCode"] = "R3-WV-MOR-OCC-" + s, ["tenantName"] = "Morgan occupational " + s, ["tenantType"] = "program", ["parentTenantCode"] = "R3-WV-MOR" });

        // Not Jefferson's 037, alone or alongside its own, and not a county new to West Virginia.
        foreach (var counties in new[] { "037", "065,037" })
        {
            Assert.Equal(51403, (await Assert.ThrowsAsync<SqlException>(() => RunAsync(morCfg, "config.uspCreateChildTenant", new()
                { ["tenantCode"] = "R3-WV-MOR-X-" + s, ["tenantName"] = "Reach " + s, ["tenantType"] = "program", ["parentTenantCode"] = "R3-WV-MOR", ["countyFipsList"] = counties }))).Number);
        }

        Assert.Equal(51403, (await Assert.ThrowsAsync<SqlException>(() => RunAsync(morCfg, "config.uspCreateChildTenant", new()
            { ["tenantCode"] = "R3-WV-MOR-Y-" + s, ["tenantName"] = "New county " + s, ["tenantType"] = "program", ["parentTenantCode"] = "R3-WV-MOR", ["countiesJson"] = """[{"fips":"077","name":"Preston County"}]""" }))).Number);

        // The rule belongs to the tree, so the state administrator meets it too under Morgan ...
        Assert.Equal(51403, (await Assert.ThrowsAsync<SqlException>(() => RunAsync(wv, "config.uspCreateChildTenant", new()
            { ["tenantCode"] = "R3-WV-MOR-Z-" + s, ["tenantName"] = "Berkeley via Morgan " + s, ["tenantType"] = "program", ["parentTenantCode"] = "R3-WV-MOR", ["countyFipsList"] = "003" }))).Number);

        // ... while under the state itself any county may be served, and new counties added.
        await RunAsync(wv, "config.uspCreateChildTenant", new()
            { ["tenantCode"] = "R3-WV-EAST-" + s, ["tenantName"] = "Eastern panhandle " + s, ["tenantType"] = "jurisdiction", ["parentTenantCode"] = "R3-WV", ["countyFipsList"] = "037,003" });
        await RunAsync(wv, "config.uspCreateChildTenant", new()
            { ["tenantCode"] = "R3-WV-PRE-" + s, ["tenantName"] = "Preston " + s, ["tenantType"] = "jurisdiction", ["parentTenantCode"] = "R3-WV", ["countiesJson"] = """[{"fips":"077","name":"Preston County"}]""" });

        // Nothing the refused calls asked for was created.
        Assert.DoesNotContain((await RowsAsync(wv, "auth.uspListTenants", [])).Select(r => (string)r["tenantName"]!), n => n.StartsWith("Reach ") || n.StartsWith("New county ") || n.StartsWith("Berkeley via Morgan "));
    }

    // ------------------------------------------------------------------------------------------------ 8 jurisdiction admins

    [SqlFact]
    public async Task CountyAdministratorWorksOnlyInsideTheirCounty()
    {
        Build();
        var jefAdmin = await ScopeAsync("jefadmin");

        // Sees people who work in Jefferson or were given access there, not the rest of the state.
        var users = await NamesAsync(jefAdmin, "auth.uspListUsers", []);
        Assert.Contains("jefstaff", users);
        Assert.Contains("jbstaff", users);
        Assert.DoesNotContain("wvstaff", users);
        Assert.DoesNotContain("pastaff", users);

        // Cannot grant at Berkeley, cannot grant what they do not hold (the state administrator role), cannot change themselves.
        var berkeley = await TenantAsync("R3-WV-BER");
        var jefferson = await TenantAsync("R3-WV-JEF");
        var caseManager = await RoleAsync("R3-WV", "caseManager");
        var administrator = await RoleAsync("R3-WV", "administrator");
        Assert.Equal(51403, (await Assert.ThrowsAsync<SqlException>(() => GrantAsync(jefAdmin, "jefstaff", caseManager, berkeley, "x1"))).Number);
        Assert.Equal(51403, (await Assert.ThrowsAsync<SqlException>(() => GrantAsync(jefAdmin, "jefstaff", administrator, jefferson, "x2"))).Number);
        Assert.Equal(51403, (await Assert.ThrowsAsync<SqlException>(() => GrantAsync(jefAdmin, "jefadmin", caseManager, jefferson, "x3"))).Number);

        // Can add people only to Jefferson.
        Assert.Equal(51403, (await Assert.ThrowsAsync<SqlException>(() => SaveUserAsync(jefAdmin, "newber" + Suffix(), berkeley))).Number);
        await SaveUserAsync(jefAdmin, "newjef" + Suffix(), jefferson);
    }

    [SqlFact]
    public async Task AdministratorsOfTwoCountiesShareAPersonWithoutOverreaching()
    {
        Build();
        // jbstaff works for Jefferson (home) and was given Berkeley by the Berkeley administrator.
        var berAdmin = await ScopeAsync("beradmin");
        var detail = await RowsAsync(berAdmin, "auth.uspGetUser", new() { ["appUserId"] = await UserIdAsync("jbstaff") });
        Assert.Contains(detail, r => r.TryGetValue("canManageAccount", out var v) && v is false);

        // The Berkeley administrator cannot change the account (home Jefferson) or reset its second factor.
        Assert.Equal(51403, (await Assert.ThrowsAsync<SqlException>(() =>
            RunAsync(berAdmin, "auth.uspResetUserMfa", new() { ["appUserId"] = UserIdAsync("jbstaff").Result }))).Number);

        // Nor end the Jefferson grant.
        var jeffersonGrant = Key("grant:jbstaff:JEF:caseManager");
        Assert.Equal(51403, (await Assert.ThrowsAsync<SqlException>(async () => await RunAsync(berAdmin, "auth.uspSaveUserAssignment", new()
        {
            ["userAssignmentId"] = jeffersonGrant, ["appUserId"] = await UserIdAsync("jbstaff"), ["roleId"] = await RoleAsync("R3-WV", "caseManager"),
            ["tenantId"] = await TenantAsync("R3-WV-JEF"), ["includeDescendants"] = true, ["accessLevel"] = "full",
            ["startDate"] = DateTime.Today, ["endDate"] = DateTime.Today,
        }))).Number);
    }

    // ------------------------------------------------------------------------------------------------ 9 switching

    [SqlFact]
    public async Task AdministratorsViewAsTheirStaffLimitedToWhatTheyManage()
    {
        Build();
        // State administrator viewing as the two-county staff member sees both counties.
        Assert.Equal(new[] { "R3-WV-BER", "R3-WV-JEF" }, await ScopeTenantCodesAsync(await ScopeAsync("wvadmin", viewAs: "jbstaff")));

        // The Jefferson administrator viewing as the same person sees Jefferson only.
        Assert.Equal(new[] { "R3-WV-JEF" }, await ScopeTenantCodesAsync(await ScopeAsync("jefadmin", viewAs: "jbstaff")));

        // Region administrator can view as the Jefferson administrator; staff cannot view as anyone.
        Assert.Contains("R3-WV-JEF", await ScopeTenantCodesAsync(await ScopeAsync("r3admin", viewAs: "jefadmin")));
        Assert.Equal(51403, (await Assert.ThrowsAsync<SqlException>(() => ScopeAsync("jefstaff", viewAs: "jbstaff"))).Number);
    }

    [SqlFact]
    public async Task ReducedProfilesNeverExceedTheMaster()
    {
        Build();
        var staff = await ScopeAsync("jbstaff");
        var berkeley = await TenantAsync("R3-WV-BER");
        var caseManager = await RoleAsync("R3-WV", "caseManager");

        // Read-only Berkeley: allowed.
        var readOnly = Key("profile:jbstaff:readBerkeley");
        await SaveProfileAsync(staff, readOnly, "Read only - Berkeley", [new(berkeley, caseManager, true, "read")]);

        // Pennsylvania is outside the master: refused. A bigger role than the master's: refused.
        Assert.Equal(51422, (await Assert.ThrowsAsync<SqlException>(() =>
            SaveProfileAsync(staff, Key("profile:jbstaff:pa"), "PA", [new(TenantAsync("R3-PA").Result, caseManager, true, "read")]))).Number);
        Assert.Equal(51422, (await Assert.ThrowsAsync<SqlException>(() =>
            SaveProfileAsync(staff, Key("profile:jbstaff:sup"), "Sup", [new(berkeley, RoleAsync("R3-WV", "supervisor").Result, true, "read")]))).Number);

        // The master cannot be changed this way.
        var master = await MasterProfileAsync("jbstaff");
        Assert.Equal(51403, (await Assert.ThrowsAsync<SqlException>(() =>
            SaveProfileAsync(staff, master, "Bigger", [new(berkeley, caseManager, true, "full")]))).Number);

        // Using the read-only profile: Berkeley only, read only, and a change is refused by the database.
        var scope = await ScopeAsync("jbstaff", profile: readOnly);
        Assert.Equal(new[] { "R3-WV-BER" }, await ScopeTenantCodesAsync(scope));
        var task = await TaskIdAsync("Berkeley task");
        await Assert.ThrowsAsync<SqlException>(() => RunAsync(scope, "dbo.uspUpdateWorkTasks",
            new() { ["workTaskIdsJson"] = JsonSerializer.Serialize(new[] { task }), ["action"] = "assignToMe" }));
    }

    [SqlFact]
    public async Task WriteWithoutDeleteBlocksOnlyTheSoftDelete()
    {
        Build();
        var staff = await ScopeAsync("jbstaff");
        var jefferson = await TenantAsync("R3-WV-JEF");
        var profile = Key("profile:jbstaff:noDelete");
        await SaveProfileAsync(staff, profile, "Jefferson, no deleting", [new(jefferson, await RoleAsync("R3-WV", "caseManager"), true, "write")]);
        var scope = await ScopeAsync("jbstaff", profile: profile);

        var person = Key("person:jefferson");
        var contact = Guid.NewGuid();
        var save = new Dictionary<string, object?>
        {
            ["personContactMethodId"] = contact, ["personId"] = person, ["contactType"] = "phone", ["contactValue"] = "304-555-0100",
        };
        await RunAsync(scope, "dbo.uspSavePersonContactMethod", save);              // add and change: allowed
        save["isDeleted"] = true;
        await Assert.ThrowsAsync<SqlException>(() => RunAsync(scope, "dbo.uspSavePersonContactMethod", save));   // delete: refused

        var master = await ScopeAsync("jbstaff");                                  // the master profile has full access
        await RunAsync(master, "dbo.uspSavePersonContactMethod", save);
    }

    // ------------------------------------------------------------------------------------------------ the tree

    private void Build()
    {
        // The fixture has deployed the test database (and the demo platform account) before any scenario runs.
        _ = fixture.Ids;
        lock (Gate)
        {
            if (built)
            {
                return;
            }

            using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
            c.Open();
            void Exec(string proc, Dictionary<string, object?> args)
            {
                using var cmd = new SqlCommand(proc, c) { CommandType = CommandType.StoredProcedure };
                foreach (var (k, v) in args)
                {
                    cmd.Parameters.AddWithValue("@" + k, v ?? DBNull.Value);
                }

                using var r = cmd.ExecuteReader();
                while (r.NextResult())
                {
                }
            }

            Exec("config.uspCreateRootTenant", new() { ["tenantCode"] = "EPA-R3", ["tenantName"] = "EPA Region 3", ["tenantType"] = "region" });
            foreach (var (code, name, type, postal, fips) in new[]
                     {
                         ("R3-WV", "West Virginia", "state", "WV", "54"), ("R3-PA", "Pennsylvania", "state", "PA", "42"),
                         ("R3-DE", "Delaware", "state", "DE", "10"), ("R3-PAMUNKEY", "Pamunkey Indian Tribe", "tribe", "VA", "51"),
                     })
            {
                Exec("config.uspCreateRootTenant", new()
                {
                    ["tenantCode"] = code, ["tenantName"] = name, ["tenantType"] = type, ["statePostalCode"] = postal, ["stateFips"] = fips,
                    ["parentTenantCode"] = "EPA-R3",
                    ["countiesJson"] = code == "R3-WV"
                        ? """[{"fips":"037","name":"Jefferson County"},{"fips":"003","name":"Berkeley County"},{"fips":"065","name":"Morgan County"}]"""
                        : "[]",
                });
            }

            Exec("config.uspCreateChildTenant", new() { ["tenantCode"] = "R3-WV-JEF", ["tenantName"] = "Jefferson County Health Department", ["tenantType"] = "jurisdiction", ["parentTenantCode"] = "R3-WV", ["countyFipsList"] = "037" });
            Exec("config.uspCreateChildTenant", new() { ["tenantCode"] = "R3-WV-BER", ["tenantName"] = "Berkeley County Health Department", ["tenantType"] = "jurisdiction", ["parentTenantCode"] = "R3-WV", ["countyFipsList"] = "003" });
            Exec("config.uspCreateChildTenant", new() { ["tenantCode"] = "R3-WV-MOR", ["tenantName"] = "Morgan County Health Department", ["tenantType"] = "jurisdiction", ["parentTenantCode"] = "R3-WV", ["countyFipsList"] = "065" });

            // People: user, home, grants (role root, role, tenant, access).
            var people = new (string User, string Home, (string Root, string Role, string Tenant)[] Grants)[]
            {
                ("r3admin", "EPA-R3", [("EPA-R3", "administrator", "EPA-R3"), ("EPA-R3", "supervisor", "EPA-R3")]),
                ("r3staff", "EPA-R3", [("EPA-R3", "supervisor", "EPA-R3")]),
                ("wvadmin", "R3-WV", [("R3-WV", "administrator", "R3-WV"), ("R3-WV", "supervisor", "R3-WV")]),
                ("wvstaff", "R3-WV", [("R3-WV", "caseManager", "R3-WV")]),
                ("jefadmin", "R3-WV-JEF", [("R3-WV", "userAdministrator", "R3-WV-JEF"), ("R3-WV", "caseManager", "R3-WV-JEF")]),
                ("morcfg", "R3-WV-MOR", [("R3-WV", "administrator", "R3-WV-MOR")]),
                ("beradmin", "R3-WV-BER", [("R3-WV", "userAdministrator", "R3-WV-BER"), ("R3-WV", "caseManager", "R3-WV-BER")]),
                ("jefstaff", "R3-WV-JEF", [("R3-WV", "caseManager", "R3-WV-JEF")]),
                ("jbstaff", "R3-WV-JEF", [("R3-WV", "caseManager", "R3-WV-JEF"), ("R3-WV", "caseManager", "R3-WV-BER")]),
                ("pastaff", "R3-PA", [("R3-PA", "caseManager", "R3-PA")]),
            };
            foreach (var (user, home, grants) in people)
            {
                Exec("auth.uspSaveUser", new()
                {
                    ["appUserId"] = Key("user:" + user), ["userName"] = user, ["displayName"] = user, ["passwordHash"] = "AQAAAAIAAYagAAAAEDisabledForTests==",
                    ["homeTenantId"] = Tenant(c, home),
                });
                foreach (var (root, role, tenant) in grants)
                {
                    Exec("auth.uspSaveUserAssignment", new()
                    {
                        ["userAssignmentId"] = Key($"grant:{user}:{tenant.Split('-')[^1]}:{role}"), ["appUserId"] = Key("user:" + user),
                        ["roleId"] = Role(c, root, role), ["tenantId"] = Tenant(c, tenant), ["includeDescendants"] = true, ["accessLevel"] = "full",
                        ["startDate"] = DateTime.Today.AddDays(-1),
                    });
                }
            }

            // One task per tenant, and a person in Jefferson.
            foreach (var (code, title) in new[]
                     {
                         ("EPA-R3", "R3 region task"), ("R3-WV", "WV state task"), ("R3-PA", "PA state task"), ("R3-DE", "DE state task"),
                         ("R3-WV-JEF", "Jefferson task"), ("R3-WV-BER", "Berkeley task"), ("R3-PAMUNKEY", "Tribe task"),
                     })
            {
                using var cmd = new SqlCommand("""
                    INSERT dbo.workTask (workTaskId, rootTenantId, owningTenantId, queueCode, title)
                    SELECT @id, t.rootTenantId, t.tenantId, 'actionLevel', @title FROM auth.tenant AS t
                     WHERE t.tenantCode = @code AND NOT EXISTS (SELECT 1 FROM dbo.workTask WHERE workTaskId = @id);
                    """, c);
                cmd.Parameters.AddWithValue("@id", Key("task:" + code));
                cmd.Parameters.AddWithValue("@title", title);
                cmd.Parameters.AddWithValue("@code", code);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = new SqlCommand("""
                INSERT dbo.person (personId, rootTenantId, owningTenantId, lastName, firstName, lastNameKey, firstNameKey)
                SELECT @id, t.rootTenantId, t.tenantId, N'Jefferson', N'Pat', N'JEFFERSON', N'PAT' FROM auth.tenant AS t
                 WHERE t.tenantCode = 'R3-WV-JEF' AND NOT EXISTS (SELECT 1 FROM dbo.person WHERE personId = @id);
                """, c))
            {
                cmd.Parameters.AddWithValue("@id", Key("person:jefferson"));
                cmd.ExecuteNonQuery();
            }

            built = true;
        }
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private sealed record Grant(Guid TenantId, Guid RoleId, bool IncludeDescendants, string AccessLevel);

    private sealed record Session(Guid ScopeKey, Guid UserId, Guid? ActingTenantId);

    private static Guid Key(string text) => new(MD5.HashData(Encoding.Unicode.GetBytes(text)));

    private static async Task<Guid> MasterProfileAsync(string userName)
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();
        await using var cmd = new SqlCommand("""
            SELECT p.userProfileId FROM auth.userProfile AS p JOIN auth.appUser AS u ON u.appUserId = p.appUserId
             WHERE u.userName = @u AND p.isMaster = 1 AND p.isDeleted = 0
            """, c);
        cmd.Parameters.AddWithValue("@u", userName);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private static Guid Tenant(SqlConnection c, string code)
    {
        using var cmd = new SqlCommand("SELECT tenantId FROM auth.tenant WHERE tenantCode = @c", c);
        cmd.Parameters.AddWithValue("@c", code);
        return (Guid)cmd.ExecuteScalar()!;
    }

    private static Guid Role(SqlConnection c, string rootCode, string roleCode)
    {
        using var cmd = new SqlCommand("""
            SELECT r.roleId FROM auth.role AS r JOIN auth.tenant AS t ON t.tenantId = r.rootTenantId
             WHERE t.tenantCode = @t AND r.roleCode = @r AND r.isDeleted = 0
            """, c);
        cmd.Parameters.AddWithValue("@t", rootCode);
        cmd.Parameters.AddWithValue("@r", roleCode);
        return (Guid)cmd.ExecuteScalar()!;
    }

    private static async Task<Guid> TenantAsync(string code)
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();
        return Tenant(c, code);
    }

    private static async Task<Guid> RoleAsync(string rootCode, string roleCode)
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();
        return Role(c, rootCode, roleCode);
    }

    private static async Task<Guid> UserIdAsync(string userName)
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();
        await using var cmd = new SqlCommand("SELECT appUserId FROM auth.appUser WHERE userName = @u", c);
        cmd.Parameters.AddWithValue("@u", userName);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<Guid> TaskIdAsync(string title)
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();
        await using var cmd = new SqlCommand("SELECT workTaskId FROM dbo.workTask WHERE title = @t", c);
        cmd.Parameters.AddWithValue("@t", title);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>A session scope as sign-in or a profile switch creates it, through the procedure, as the web login.</summary>
    private static async Task<Session> ScopeAsync(string userName, Guid? profile = null, string? viewAs = null)
    {
        var user = await UserIdAsync(userName);
        var scope = Guid.NewGuid();
        await using var c = new SqlConnection(TestEnvironment.WebConnectionString);
        await c.OpenAsync();
        await using var cmd = new SqlCommand("auth.uspCreateSessionScope", c) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.AddWithValue("@sessionScopeId", scope);
        cmd.Parameters.AddWithValue("@appUserId", user);
        cmd.Parameters.AddWithValue("@expiresUtc", DateTime.UtcNow.AddMinutes(10));
        cmd.Parameters.AddWithValue("@userProfileId", (object?)profile ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@viewAsUserId", viewAs is null ? DBNull.Value : await UserIdAsync(viewAs));
        Guid? acting = null;
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            if (await r.ReadAsync())
            {
                acting = r.GetGuid(r.GetOrdinal("actingTenantId"));
            }

            while (await r.NextResultAsync())
            {
            }
        }

        return new Session(scope, user, acting);
    }

    private static async Task<List<Dictionary<string, object?>>> RowsAsync(Session s, string procedure, Dictionary<string, object?> args)
    {
        await using var c = new SqlConnection(TestEnvironment.WebConnectionString);
        await c.OpenAsync();
        await using (var ctx = new SqlCommand("""
            EXEC sys.sp_set_session_context @key = N'scopeKey', @value = @scope, @read_only = 1;
            EXEC sys.sp_set_session_context @key = N'appUserId', @value = @user, @read_only = 1;
            EXEC sys.sp_set_session_context @key = N'AppUser', @value = N'authorization scenario', @read_only = 1;
            EXEC sys.sp_set_session_context @key = N'actingTenantId', @value = @acting, @read_only = 1;
            """, c))
        {
            ctx.Parameters.AddWithValue("@scope", s.ScopeKey);
            ctx.Parameters.AddWithValue("@user", s.UserId);
            ctx.Parameters.AddWithValue("@acting", (object?)s.ActingTenantId ?? DBNull.Value);
            await ctx.ExecuteNonQueryAsync();
        }

        await using var cmd = new SqlCommand(procedure, c) { CommandType = CommandType.StoredProcedure };
        foreach (var (k, v) in args)
        {
            cmd.Parameters.AddWithValue("@" + k, v ?? DBNull.Value);
        }

        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await cmd.ExecuteReaderAsync();
        do
        {
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }

                rows.Add(row);
            }
        }
        while (await reader.NextResultAsync());
        return rows;
    }

    private static Task RunAsync(Session s, string procedure, Dictionary<string, object?> args) => RowsAsync(s, procedure, args);

    private static async Task<HashSet<string>> NamesAsync(Session s, string procedure, Dictionary<string, object?> args)
        => (await RowsAsync(s, procedure, args)).Where(r => r.ContainsKey("userName")).Select(r => (string)r["userName"]!).ToHashSet();

    private static async Task<HashSet<string>> TaskTitlesAsync(string userName)
        => (await RowsAsync(await ScopeAsync(userName), "dbo.uspListWorkTasks", new() { ["taskStatus"] = "open", ["pageSize"] = 500 }))
            .Where(r => r.ContainsKey("title")).Select(r => (string)r["title"]!).ToHashSet();

    private static async Task<string[]> ScopeTenantCodesAsync(Session s)
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();
        await using var cmd = new SqlCommand("""
            SELECT t.tenantCode FROM auth.sessionScopeTenant AS st JOIN auth.tenant AS t ON t.tenantId = st.tenantId
             WHERE st.sessionScopeId = @s ORDER BY t.tenantCode
            """, c);
        cmd.Parameters.AddWithValue("@s", s.ScopeKey);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(r.GetString(0));
        }

        return [.. list];
    }

    private static Task GrantAsync(Session s, string userName, Guid roleId, Guid tenantId, string salt)
        => RunAsync(s, "auth.uspSaveUserAssignment", new()
        {
            ["userAssignmentId"] = Guid.NewGuid(), ["appUserId"] = UserIdAsync(userName).Result, ["roleId"] = roleId, ["tenantId"] = tenantId,
            ["includeDescendants"] = true, ["accessLevel"] = "full", ["startDate"] = DateTime.Today,
        });

    private static Task SaveUserAsync(Session s, string userName, Guid homeTenantId)
        => RunAsync(s, "auth.uspSaveUser", new()
        {
            ["appUserId"] = Guid.NewGuid(), ["userName"] = userName, ["displayName"] = userName, ["passwordHash"] = "AQAAAAIAAYagAAAAEDisabledForTests==",
            ["homeTenantId"] = homeTenantId,
        });

    private static Task SaveProfileAsync(Session s, Guid id, string name, Grant[] grants)
        => RunAsync(s, "auth.uspSaveMyProfile", new()
        {
            ["userProfileId"] = id, ["profileName"] = name, ["isActive"] = true,
            ["grantsJson"] = JsonSerializer.Serialize(grants.Select(g => new
            {
                tenantId = g.TenantId, roleId = g.RoleId, includeDescendants = g.IncludeDescendants, accessLevel = g.AccessLevel,
            })),
        });
}
