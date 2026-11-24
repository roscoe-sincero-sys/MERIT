using System.Net;
using System.Text.Json;
using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Security;
using Merit.Application.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Merit.IntegrationTests;

/// <summary>
/// Targeted regression tests for PR #308 security hardening findings (#298, #299, #300, #302, #305).
/// </summary>
[Collection(MeritCollection.Name)]
public sealed class SecurityHardeningRegressionTests(MeritFixture fixture)
{
    private static async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] args)
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ExecAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    [SqlFact]
    public async Task DeidentifiedEpidemiologistSeesOnlyDeidentifiedProjectionsAndUpgradeReconcilesLegacyGrants()
    {
        var epi = await fixture.SignedInAsync("epi");

        // Fresh epidemiologist cannot open person charts, individual results, or individual case charts.
        using (var personResp = await epi.Http.GetAsync($"/people/{fixture.Ids.MiaPersonId}"))
            Assert.Equal(HttpStatusCode.Forbidden, personResp.StatusCode);
        using (var resultResp = await epi.Http.GetAsync($"/results/{fixture.Ids.MiaLabResultId}"))
            Assert.Equal(HttpStatusCode.Forbidden, resultResp.StatusCode);
        using (var caseResp = await epi.Http.GetAsync($"/cases/{fixture.Ids.MiaCaseId}"))
            Assert.Equal(HttpStatusCode.Forbidden, caseResp.StatusCode);

        // Results line list returns de-identified projection (zero patient IDs, names, or DOBs).
        using var resultsResp = await epi.Http.GetAsync("/api/v1/results?pageSize=50");
        Assert.Equal(HttpStatusCode.OK, resultsResp.StatusCode);
        using var resultsDoc = JsonDocument.Parse(await resultsResp.Content.ReadAsStringAsync());
        var items = resultsDoc.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.NotEmpty(items);
        foreach (var item in items)
        {
            Assert.Equal(JsonValueKind.Null, item.GetProperty("personId").ValueKind);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("personName").ValueKind);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("dob").ValueKind);
        }

        // Cases list returns de-identified projection without patient names or DOBs.
        string casesHtml = await epi.Http.GetStringAsync("/cases");
        Assert.DoesNotContain("Rivera", casesHtml, StringComparison.OrdinalIgnoreCase);

        // Case map returns de-identified projection (zero names, addresses, or precise coordinates).
        using (var mapResp = await epi.Http.GetAsync("/api/v1/cases/map"))
        {
            Assert.Equal(HttpStatusCode.OK, mapResp.StatusCode);
            using var mapDoc = JsonDocument.Parse(await mapResp.Content.ReadAsStringAsync());
            var mapItems = mapDoc.RootElement.EnumerateArray().ToList();
            Assert.NotEmpty(mapItems);
            foreach (var pt in mapItems)
            {
                Assert.Equal(Guid.Empty, pt.GetProperty("personId").GetGuid());
                Assert.DoesNotContain("Rivera", pt.GetProperty("personName").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(JsonValueKind.Null, pt.GetProperty("addressLabel").ValueKind);
                Assert.Equal(JsonValueKind.Null, pt.GetProperty("latitude").ValueKind);
                Assert.Equal(JsonValueKind.Null, pt.GetProperty("longitude").ValueKind);
            }
        }

        // Cluster list & detail (if demo cluster exists) mask titles, sharedKey, member names, addresses, and coordinates.
        if (fixture.Ids.ClusterId is Guid clusterId)
        {
            using var clusterResp = await epi.Http.GetAsync($"/api/v1/clusters/{clusterId}");
            Assert.Equal(HttpStatusCode.OK, clusterResp.StatusCode);
            string clusterJson = await clusterResp.Content.ReadAsStringAsync();
            Assert.DoesNotContain("Rivera", clusterJson, StringComparison.OrdinalIgnoreCase);
            using var clusterDoc = JsonDocument.Parse(clusterJson);
            Assert.Equal("De-identified cluster", clusterDoc.RootElement.GetProperty("title").GetString());
            foreach (var member in clusterDoc.RootElement.GetProperty("members").EnumerateArray())
            {
                Assert.Equal(Guid.Empty, member.GetProperty("personId").GetGuid());
                Assert.Equal(JsonValueKind.Null, member.GetProperty("addressLabel").ValueKind);
                Assert.Equal(JsonValueKind.Null, member.GetProperty("latitude").ValueKind);
                Assert.Equal(JsonValueKind.Null, member.GetProperty("longitude").ValueKind);
            }
        }

        // Identifiable regulatory extracts (CBLS/ABLES containing full DOBs) are withheld from de-identified roles.
        using (var extractsResp = await epi.Http.GetAsync("/extracts"))
            Assert.Equal(HttpStatusCode.Forbidden, extractsResp.StatusCode);

        // Upgrade path: simulate a pre-upgrade epidemiologist role that still holds person.view, task.view, and extract.run,
        // verify reconciliation removes them and revokes active sessions for affected users.
        await using var upgraded = await SyntheticAccount.CreateAsync(fixture.Factory, "epireconcile");
        Guid epiRoleId = await ScalarAsync<Guid>(
            "SELECT r.roleId FROM auth.role AS r JOIN auth.tenant AS t ON t.tenantId = r.rootTenantId WHERE t.tenantCode = 'MD-LEAD' AND r.roleCode = 'epidemiologist' AND r.isDeleted = 0");
        try
        {
            await ExecAsync("""
                INSERT auth.userAssignment (userAssignmentId, appUserId, roleId, tenantId, includeDescendants, accessLevel, startDate)
                SELECT NEWID (), @userId, @roleId, rootTenantId, 1, 'read', '2020-01-01'
                  FROM auth.appUser WHERE appUserId = @userId;

                INSERT auth.rolePermission (rolePermissionId, roleId, permissionId)
                SELECT NEWID (), @roleId, pm.permissionId
                  FROM auth.permission AS pm
                 WHERE pm.permissionCode IN ('person.view', 'task.view', 'extract.run')
                   AND pm.isDeleted = 0
                   AND NOT EXISTS (SELECT 1 FROM auth.rolePermission AS x WHERE x.roleId = @roleId AND x.permissionId = pm.permissionId AND x.isDeleted = 0);
                """, ("@userId", upgraded.Id), ("@roleId", epiRoleId));

            using var client = fixture.Anonymous();
            await upgraded.SignInAsync(client, MeritFixture.Password);
            Guid openScope = await upgraded.ScalarAsync<Guid>(
                "SELECT TOP (1) sessionScopeId FROM auth.sessionScope WHERE appUserId = @id AND revokedUtc IS NULL ORDER BY createdDateUtc DESC");

            // Run the idempotent upgrade reconciliation script against the upgraded database.
            string reconcileSql = await File.ReadAllTextAsync(
                Path.Combine(TestEnvironment.SourceRoot, "database", "scripts", "500_seed", "566_reconcileDeidentifiedRole.sql"));
            foreach (string batch in reconcileSql.Split("\nGO", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string trimmed = batch.Trim();
                if (trimmed.Length > 0 && !trimmed.Equals("GO", StringComparison.OrdinalIgnoreCase))
                {
                    await ExecAsync(trimmed);
                }
            }

            // Legacy person.view/task.view/extract.run must be removed and open sessions revoked.
            int remainingDisallowed = await ScalarAsync<int>("""
                SELECT COUNT (*)
                  FROM auth.rolePermission AS rp
                  JOIN auth.permission AS pm ON pm.permissionId = rp.permissionId
                 WHERE rp.roleId = @roleId AND rp.isDeleted = 0 AND pm.permissionCode IN ('person.view', 'task.view', 'extract.run')
                """, ("@roleId", epiRoleId));
            Assert.Equal(0, remainingDisallowed);

            int revoked = await ScalarAsync<int>(
                "SELECT COUNT (*) FROM auth.sessionScope WHERE sessionScopeId = @s AND revokedUtc IS NOT NULL", ("@s", openScope));
            Assert.Equal(1, revoked);
        }
        finally
        {
            // Ensure no disallowed permissions remain even if an assertion failed mid-test.
            await ExecAsync("""
                UPDATE rp SET isDeleted = 1, modifiedBy = ORIGINAL_LOGIN (), modifiedDateUtc = SYSUTCDATETIME ()
                  FROM auth.rolePermission AS rp
                  JOIN auth.permission AS pm ON pm.permissionId = rp.permissionId
                 WHERE rp.roleId = @roleId AND rp.isDeleted = 0 AND pm.permissionCode IN ('person.view', 'task.view', 'extract.run')
                """, ("@roleId", epiRoleId));
        }
    }

    [SqlFact]
    public async Task MixedDeidentifiedAndNonClinicalOrCrossTenantRolesEnforcePerTenantMasking()
    {
        // Case A: epidemiologist + userAdministrator (non-de-identified account-only role) at MD-LEAD.
        // The non-clinical role must NOT unmask results, cases, or map rows.
        await using var epiPlusAdmin = await SyntheticAccount.CreateAsync(fixture.Factory, "epiuseradm");
        await epiPlusAdmin.ScalarAsync<int>("""
            INSERT auth.userAssignment (userAssignmentId, appUserId, roleId, tenantId, includeDescendants, accessLevel, startDate)
            SELECT NEWID (), @id, r.roleId, u.rootTenantId, 1, 'full', '2020-01-01'
              FROM auth.role AS r JOIN auth.appUser AS u ON u.rootTenantId = r.rootTenantId AND u.appUserId = @id
             WHERE r.roleCode IN ('epidemiologist', 'userAdministrator') AND r.isDeleted = 0;
            SELECT 0;
            """);
        using (var client = fixture.Anonymous())
        {
            await epiPlusAdmin.SignInAsync(client, MeritFixture.Password);
            using var resResp = await client.Http.GetAsync("/api/v1/results?pageSize=25");
            Assert.Equal(HttpStatusCode.OK, resResp.StatusCode);
            using var resDoc = JsonDocument.Parse(await resResp.Content.ReadAsStringAsync());
            var rows = resDoc.RootElement.GetProperty("items").EnumerateArray().ToList();
            Assert.NotEmpty(rows);
            Assert.All(rows, item => Assert.Equal(JsonValueKind.Null, item.GetProperty("personName").ValueKind));
            Assert.DoesNotContain("Rivera", await client.Http.GetStringAsync("/cases"), StringComparison.OrdinalIgnoreCase);
            using var miaCase = await client.Http.GetAsync($"/cases/{fixture.Ids.MiaCaseId}");
            Assert.Equal(HttpStatusCode.Forbidden, miaCase.StatusCode);
        }

        // Case B: epidemiologist at MD-LEAD (covers both Baltimore City and Baltimore County)
        // PLUS investigator (identified) at Baltimore County ONLY.
        // Baltimore City (Mia Rivera) must stay masked while Baltimore County records are identified.
        await using var mixedTenant = await SyntheticAccount.CreateAsync(fixture.Factory, "epimixed");
        await mixedTenant.ScalarAsync<int>("""
            INSERT auth.userAssignment (userAssignmentId, appUserId, roleId, tenantId, includeDescendants, accessLevel, startDate)
            SELECT NEWID (), @id, r.roleId, u.rootTenantId, 1, 'read', '2020-01-01'
              FROM auth.role AS r JOIN auth.appUser AS u ON u.rootTenantId = r.rootTenantId AND u.appUserId = @id
             WHERE r.roleCode = 'epidemiologist' AND r.isDeleted = 0;
            INSERT auth.userAssignment (userAssignmentId, appUserId, roleId, tenantId, includeDescendants, accessLevel, startDate)
            SELECT NEWID (), @id, r.roleId, @county, 1, 'full', '2020-01-01'
              FROM auth.role AS r JOIN auth.appUser AS u ON u.rootTenantId = r.rootTenantId AND u.appUserId = @id
             WHERE r.roleCode = 'investigator' AND r.isDeleted = 0;
            SELECT 0;
            """, ("@county", fixture.Ids.BaltimoreCountyTenantId));

        Guid? baltCoCaseId = await ScalarAsync<Guid?>(
            "SELECT TOP (1) exposureCaseId FROM dbo.exposureCase WHERE owningTenantId = @t AND isDeleted = 0",
            ("@t", fixture.Ids.BaltimoreCountyTenantId));
        using (var client = fixture.Anonymous())
        {
            await mixedTenant.SignInAsync(client, MeritFixture.Password);

            // Mia Rivera (Baltimore City) cannot be opened and stays masked on the case list.
            using var miaCase = await client.Http.GetAsync($"/cases/{fixture.Ids.MiaCaseId}");
            Assert.Equal(HttpStatusCode.Forbidden, miaCase.StatusCode);
            string casesHtml = await client.Http.GetStringAsync("/cases");
            Assert.DoesNotContain("Rivera", casesHtml, StringComparison.OrdinalIgnoreCase);

            // A Baltimore County case CAN be opened with full identified clinical access.
            if (baltCoCaseId is Guid countyCase)
            {
                using var countyResp = await client.Http.GetAsync($"/cases/{countyCase}");
                Assert.Equal(HttpStatusCode.OK, countyResp.StatusCode);
            }
        }
    }

    [SqlFact]
    public async Task EnrollmentAndTotpConsumptionRejectConcurrentEnrollmentAndMidFlightReset()
    {
        // Concurrent MFA enrollment for the same unenrolled account: exactly one succeeds.
        await using var unenrolled = await SyntheticAccount.CreateAsync(fixture.Factory, "concenroll", mfa: false);
        Guid initialStamp = await unenrolled.ScalarAsync<Guid>("SELECT securityStamp FROM auth.appUser WHERE appUserId = @id");
        string secret1 = Totp.ToBase32(Totp.NewSecret());
        string secret2 = Totp.ToBase32(Totp.NewSecret());
        long step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        string code1 = Totp.Code(Totp.FromBase32(secret1), step);
        string code2 = Totp.Code(Totp.FromBase32(secret2), step);

        var enrollAttempts = new (string Secret, string Code)[] { (secret1, code1), (secret2, code2) };
        bool[] enrollResults = await Task.WhenAll(enrollAttempts.Select(async a =>
        {
            using var s = fixture.Factory.Services.CreateScope();
            var svc = s.ServiceProvider.GetRequiredService<AccountService>();
            try
            {
                await svc.CompleteEnrollmentAsync(unenrolled.Id, a.Secret, a.Code, "127.0.0.1", default, unenrolled.Name, initialStamp);
                return true;
            }
            catch (ValidationException)
            {
                return false;
            }
        }));
        Assert.Equal(1, enrollResults.Count(ok => ok));

        // Mid-flight MFA reset: consuming a TOTP step with a stale securityStamp fails.
        await using var resetAccount = await SyntheticAccount.CreateAsync(fixture.Factory, "totpreset");
        Guid staleStamp = await resetAccount.ScalarAsync<Guid>("SELECT securityStamp FROM auth.appUser WHERE appUserId = @id");
        await ExecAsync("UPDATE auth.appUser SET securityStamp = NEWID () WHERE appUserId = @id", ("@id", resetAccount.Id));

        using var scope = fixture.Factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<IAccountData>();
        Assert.False(await data.ConsumeTotpStepAsync(resetAccount.Id, step + 1, staleStamp));
    }

    [SqlFact]
    public async Task SignOutRevokesLogicalSessionAcrossRenewalRaceAndDeniedViewAsPreservesSession()
    {
        await using var account = await SyntheticAccount.CreateAsync(fixture.Factory, "signoutrace");
        await account.ScalarAsync<int>("""
            INSERT auth.userAssignment (userAssignmentId, appUserId, roleId, tenantId, includeDescendants, accessLevel, startDate)
            SELECT NEWID (), @id, r.roleId, u.homeTenantId, 1, 'full', '2020-01-01'
              FROM auth.role AS r JOIN auth.appUser AS u ON u.rootTenantId = r.rootTenantId AND u.appUserId = @id
             WHERE r.roleCode = 'supervisor' AND r.isDeleted = 0;
            SELECT 0;
            """);

        using var scope = fixture.Factory.Services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
        Guid stamp = await account.ScalarAsync<Guid>("SELECT securityStamp FROM auth.appUser WHERE appUserId = @id");

        // Independent sign-in (different sessionReference) must survive revoking another logical session.
        var otherSignIn = await accounts.CreateSessionAsync(account.Id, TimeSpan.FromHours(2));
        Guid otherScopeId = otherSignIn.Scope.Summary.SessionScopeId;

        // Commit order 1: sign-out commits first -> renewal fails with SessionEndedException (51401).
        var first = await accounts.CreateSessionAsync(account.Id, TimeSpan.FromHours(2));
        Guid firstScopeId = first.Scope.Summary.SessionScopeId;
        await accounts.SignOutAsync(firstScopeId);
        await Assert.ThrowsAsync<SessionEndedException>(() =>
            accounts.RenewSessionAsync(account.Id, firstScopeId, stamp, TimeSpan.FromHours(12)));

        // Commit order 2: renewal commits first (producing successor with same sessionReference) ->
        // sign-out using the stale old scope ID revokes the successor too, while leaving otherSignIn active.
        var second = await accounts.CreateSessionAsync(account.Id, TimeSpan.FromHours(2));
        Guid secondOldId = second.Scope.Summary.SessionScopeId;
        var renewed = await accounts.RenewSessionAsync(account.Id, secondOldId, stamp, TimeSpan.FromHours(12));
        Guid successorId = renewed.Scope.Summary.SessionScopeId;
        await accounts.SignOutAsync(secondOldId);

        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT (*) FROM auth.sessionScope WHERE sessionScopeId = @s AND revokedUtc IS NOT NULL", ("@s", successorId)));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT (*) FROM auth.sessionScope WHERE sessionScopeId = @s AND revokedUtc IS NULL", ("@s", otherScopeId)));

        // Denied view-as (unauthorized target) returns to /account/profiles with the denial message
        // and preserves the caller's existing valid session cookie.
        var cityAdmin = await fixture.SignedInAsync("cityadmin");
        Guid countyUserId = await ScalarAsync<Guid>("SELECT appUserId FROM auth.appUser WHERE userName = N'cmbaltco'");
        string profilesPage = await cityAdmin.Http.GetStringAsync("/account/profiles");
        using var deniedViewAs = await cityAdmin.Http.PostAsync($"/account/view-as/{countyUserId}",
            MeritClient.Form(profilesPage));
        Assert.Equal(HttpStatusCode.OK, deniedViewAs.StatusCode);
        Assert.Equal("/account/profiles", deniedViewAs.RequestMessage!.RequestUri!.AbsolutePath);
        string deniedHtml = await deniedViewAs.Content.ReadAsStringAsync();
        Assert.Contains("administer", deniedHtml, StringComparison.OrdinalIgnoreCase);
        using var stillSignedIn = await cityAdmin.NoRedirect.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, stillSignedIn.StatusCode);
    }

    [SqlFact]
    public async Task GlobalAnalytesAreEditableByPlatformAdminAndReadOnlyForJurisdictionAdmin()
    {
        var admin = await fixture.SignedInAsync("admin");
        var platform = await fixture.SignedInAsync("platform");

        Guid analyteId = await ScalarAsync<Guid>("SELECT TOP (1) analyteId FROM config.analyte WHERE isDeleted = 0 ORDER BY sortOrder");
        int originalOrder = await ScalarAsync<int>("SELECT sortOrder FROM config.analyte WHERE analyteId = @id", ("@id", analyteId));
        bool originalActive = await ScalarAsync<bool>("SELECT isActive FROM config.analyte WHERE analyteId = @id", ("@id", analyteId));

        try
        {
            // Jurisdiction admin sees read-only analytes page and cannot POST edits.
            string adminPage = await admin.Http.GetStringAsync("/config/analytes");
            Assert.DoesNotContain($"hx-post=\"/config/analytes/{analyteId}\"", adminPage, StringComparison.OrdinalIgnoreCase);
            using var denied = await admin.Http.PostAsync($"/config/analytes/{analyteId}",
                MeritClient.Form(adminPage, ("isActive", originalActive ? "true" : "false"), ("sortOrder", originalOrder.ToString())));
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

            // Platform administrator sees edit controls and can save global analyte changes.
            string platformPage = await platform.Http.GetStringAsync("/config/analytes");
            Assert.Contains($"hx-post=\"/config/analytes/{analyteId}\"", platformPage, StringComparison.OrdinalIgnoreCase);
            int updatedOrder = originalOrder + 1;
            using var saved = await platform.Http.PostAsync($"/config/analytes/{analyteId}",
                MeritClient.Form(platformPage, ("isActive", originalActive ? "true" : "false"), ("sortOrder", updatedOrder.ToString())));
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.Equal(updatedOrder, await ScalarAsync<int>("SELECT sortOrder FROM config.analyte WHERE analyteId = @id", ("@id", analyteId)));
        }
        finally
        {
            await ExecAsync("UPDATE config.analyte SET isActive = @a, sortOrder = @o WHERE analyteId = @id",
                ("@a", originalActive), ("@o", originalOrder), ("@id", analyteId));
        }
    }

    [SqlFact]
    public async Task AtomicSessionRenewalRejectsWrongStampRevokedScopeAndConcurrentRenewals()
    {
        await using var account = await SyntheticAccount.CreateAsync(fixture.Factory, "atomicrenew");
        await account.ScalarAsync<int>("""
            INSERT auth.userAssignment (userAssignmentId, appUserId, roleId, tenantId, includeDescendants, accessLevel, startDate)
            SELECT NEWID (), @id, r.roleId, u.homeTenantId, 1, 'full', '2020-01-01'
              FROM auth.role AS r JOIN auth.appUser AS u ON u.rootTenantId = r.rootTenantId AND u.appUserId = @id
             WHERE r.roleCode = 'supervisor' AND r.isDeleted = 0;
            SELECT 0;
            """);

        using var scope = fixture.Factory.Services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
        Guid stamp = await account.ScalarAsync<Guid>("SELECT securityStamp FROM auth.appUser WHERE appUserId = @id");
        var initial = await accounts.CreateSessionAsync(account.Id, TimeSpan.FromHours(2));
        Guid initialScopeId = initial.Scope.Summary.SessionScopeId;
        DateTime initialExpiry = initial.Scope.Summary.ExpiresUtc;

        // Wrong security stamp is rejected atomically with SessionEndedException without revoking the valid scope.
        await Assert.ThrowsAsync<SessionEndedException>(() =>
            accounts.RenewSessionAsync(account.Id, initialScopeId, Guid.NewGuid(), TimeSpan.FromHours(12)));

        // Concurrent renewals of the same scope: exactly one succeeds and carries forward the shorter expiry.
        var tasks = Enumerable.Range(0, 2).Select(async _ =>
        {
            using var s = fixture.Factory.Services.CreateScope();
            var svc = s.ServiceProvider.GetRequiredService<AccountService>();
            try
            {
                return await svc.RenewSessionAsync(account.Id, initialScopeId, stamp, TimeSpan.FromHours(12));
            }
            catch (SessionEndedException)
            {
                return null;
            }
        }).ToArray();

        var results = await Task.WhenAll(tasks);
        var winner = Assert.Single(results, r => r is not null);
        Assert.True(winner!.Scope.Summary.ExpiresUtc <= initialExpiry.AddSeconds(1),
            $"Renewed expiry {winner.Scope.Summary.ExpiresUtc:O} exceeded original {initialExpiry:O}.");

        // Reusing the already-replaced scope is rejected with SessionEndedException.
        await Assert.ThrowsAsync<SessionEndedException>(() =>
            accounts.RenewSessionAsync(account.Id, initialScopeId, stamp, TimeSpan.FromHours(12)));
    }

    [SqlFact]
    public async Task TotpCodeCannotBeReplayedAcrossVerificationPathsOrConcurrently()
    {
        await using var account = await SyntheticAccount.CreateAsync(fixture.Factory, "totpreplay");
        long step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        string code = account.TotpCodeForStep(step);

        // Concurrent submissions across sign-in MFA and step-up/password-reset paths: at most one succeeds.
        var attempts = new Func<Task<bool>>[]
        {
            async () =>
            {
                using var s = fixture.Factory.Services.CreateScope();
                return await s.ServiceProvider.GetRequiredService<AccountService>().VerifyMfaAsync(account.Name, code, "127.0.0.1");
            },
            async () =>
            {
                using var s = fixture.Factory.Services.CreateScope();
                return await s.ServiceProvider.GetRequiredService<AccountService>().CheckAuthenticatorCodeAsync(account.Name, code, "127.0.0.1");
            },
            async () =>
            {
                using var s = fixture.Factory.Services.CreateScope();
                var data = s.ServiceProvider.GetRequiredService<IAccountData>();
                var user = (await data.GetUserForSignInAsync(account.Name))!;
                return await s.ServiceProvider.GetRequiredService<AccountService>().CheckSecondFactorAsync(user, code) is not null;
            },
        };

        bool[] outcomes = await Task.WhenAll(attempts.Select(a => a()));
        Assert.Equal(1, outcomes.Count(ok => ok));

        // Sequential replay of the exact same code on every path fails.
        using (var s = fixture.Factory.Services.CreateScope())
        {
            var svc = s.ServiceProvider.GetRequiredService<AccountService>();
            var user = (await s.ServiceProvider.GetRequiredService<IAccountData>().GetUserForSignInAsync(account.Name))!;
            Assert.False(await svc.VerifyMfaAsync(account.Name, code, "127.0.0.1"));
            Assert.False(await svc.CheckAuthenticatorCodeAsync(account.Name, code, "127.0.0.1"));
            Assert.Null(await svc.CheckSecondFactorAsync(user, code));
        }
    }

    [SqlFact]
    public async Task QueueDepthUpgradeRevokesWebRoleAndRetainsWorkerRole()
    {
        // Simulate a pre-upgrade database where meritWebRole held EXECUTE on dbo.uspGetQueueDepth.
        await ExecAsync("GRANT EXECUTE ON dbo.uspGetQueueDepth TO meritWebRole;");

        // Re-apply the idempotent revoke/grant tail from dbo.uspGetQueueDepth.sql.
        await ExecAsync("""
            IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
                REVOKE EXECUTE ON dbo.uspGetQueueDepth FROM meritWebRole;
            IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
                GRANT EXECUTE ON dbo.uspGetQueueDepth TO meritWorkerRole;
            """);

        // Verify effective permissions as both logins.
        await using (var webConn = new SqlConnection(TestEnvironment.WebConnectionString))
        {
            await webConn.OpenAsync();
            await using var cmd = new SqlCommand("SELECT HAS_PERMS_BY_NAME (N'dbo.uspGetQueueDepth', N'OBJECT', N'EXECUTE')", webConn);
            Assert.Equal(0, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
        }

        await using (var workerConn = new SqlConnection(TestEnvironment.WorkerConnectionString))
        {
            await workerConn.OpenAsync();
            await using var cmd = new SqlCommand("SELECT HAS_PERMS_BY_NAME (N'dbo.uspGetQueueDepth', N'OBJECT', N'EXECUTE')", workerConn);
            Assert.Equal(1, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
        }
    }
}
