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

        // Upgrade path: simulate a pre-upgrade epidemiologist role that still holds person.view and task.view,
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
                 WHERE pm.permissionCode IN ('person.view', 'task.view')
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

            // Legacy person.view/task.view must be removed and open sessions revoked.
            int remainingDisallowed = await ScalarAsync<int>("""
                SELECT COUNT (*)
                  FROM auth.rolePermission AS rp
                  JOIN auth.permission AS pm ON pm.permissionId = rp.permissionId
                 WHERE rp.roleId = @roleId AND rp.isDeleted = 0 AND pm.permissionCode IN ('person.view', 'task.view')
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
                UPDATE rp SET isDeleted = 1
                  FROM auth.rolePermission AS rp
                  JOIN auth.permission AS pm ON pm.permissionId = rp.permissionId
                 WHERE rp.roleId = @roleId AND rp.isDeleted = 0 AND pm.permissionCode IN ('person.view', 'task.view')
                """, ("@roleId", epiRoleId));
        }
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

        // Wrong security stamp is rejected atomically without revoking the valid scope.
        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
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
            catch (PermissionDeniedException)
            {
                return null;
            }
        }).ToArray();

        var results = await Task.WhenAll(tasks);
        var winner = Assert.Single(results, r => r is not null);
        Assert.True(winner!.Scope.Summary.ExpiresUtc <= initialExpiry.AddSeconds(1),
            $"Renewed expiry {winner.Scope.Summary.ExpiresUtc:O} exceeded original {initialExpiry:O}.");

        // Reusing the already-replaced scope is rejected.
        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
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
