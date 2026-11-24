using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Security;
using Merit.Application.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
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

        // Results line list returns de-identified projection (zero patient IDs, names, or DOBs)
        // with explicit isIdentified=false so UI labels matched masked rows "De-identified" instead of "(unmatched)".
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
            Assert.False(item.GetProperty("isIdentified").GetBoolean());
        }
        Assert.Contains(items, i => i.GetProperty("isMatched").GetBoolean());

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

        // Cluster list & detail (if demo cluster exists) mask titles, sharedKey, member names, addresses, coordinates,
        // AND generated building-cluster open task titles/details (B1).
        if (fixture.Ids.ClusterId is Guid clusterId)
        {
            Guid tempTaskId = Guid.NewGuid();
            string secretAddress = "742 Evergreen Terrace Apt 4B";
            await ExecAsync("""
                INSERT dbo.workTask (workTaskId, rootTenantId, owningTenantId, queueCode, title, detail, clusterId, isUrgent, dueDate)
                SELECT @tid, rootTenantId, owningTenantId, 'clusterReview', CONCAT (N'Building cluster: ', @addr),
                       CONCAT (N'Multiple cases at ', @addr), clusterId, 1, '2026-12-31'
                  FROM dbo.cluster WHERE clusterId = @cid;
                """, ("@tid", tempTaskId), ("@addr", secretAddress), ("@cid", clusterId));
            try
            {
                using var clusterResp = await epi.Http.GetAsync($"/api/v1/clusters/{clusterId}");
                Assert.Equal(HttpStatusCode.OK, clusterResp.StatusCode);
                string clusterJson = await clusterResp.Content.ReadAsStringAsync();
                Assert.DoesNotContain("Rivera", clusterJson, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(secretAddress, clusterJson, StringComparison.OrdinalIgnoreCase);
                using var clusterDoc = JsonDocument.Parse(clusterJson);
                Assert.Equal("De-identified cluster", clusterDoc.RootElement.GetProperty("title").GetString());
                Assert.Empty(clusterDoc.RootElement.GetProperty("tasks").EnumerateArray().ToList());
                foreach (var member in clusterDoc.RootElement.GetProperty("members").EnumerateArray())
                {
                    Assert.Equal(Guid.Empty, member.GetProperty("personId").GetGuid());
                    Assert.Equal(JsonValueKind.Null, member.GetProperty("addressLabel").ValueKind);
                    Assert.Equal(JsonValueKind.Null, member.GetProperty("latitude").ValueKind);
                    Assert.Equal(JsonValueKind.Null, member.GetProperty("longitude").ValueKind);
                }
            }
            finally
            {
                await ExecAsync("DELETE FROM dbo.workTask WHERE workTaskId = @tid", ("@tid", tempTaskId));
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

            // Also simulate a pre-upgrade open clinician session whose new canReadIdentified* columns defaulted to 0 (I4).
            var clinician = await fixture.SignedInAsync("cmbalt");
            Guid clinicianScope = await ScalarAsync<Guid>(
                "SELECT TOP (1) sessionScopeId FROM auth.sessionScope AS ss JOIN auth.appUser AS u ON u.appUserId = ss.appUserId WHERE u.userName = N'cmbalt' AND ss.revokedUtc IS NULL ORDER BY ss.createdDateUtc DESC");
            await ExecAsync("UPDATE auth.sessionScopeTenant SET canReadIdentifiedResults = 0, canReadIdentifiedCases = 0 WHERE sessionScopeId = @s", ("@s", clinicianScope));

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

            // Legacy person.view/task.view/extract.run must be removed and open epidemiologist sessions revoked.
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

            // Pre-upgrade open clinician session must have its identified clinical flags backfilled and keep chart access.
            int clinicianBackfilled = await ScalarAsync<int>(
                "SELECT COUNT (*) FROM auth.sessionScopeTenant WHERE sessionScopeId = @s AND canReadIdentifiedResults = 1 AND canReadIdentifiedCases = 1 AND isDeleted = 0",
                ("@s", clinicianScope));
            Assert.True(clinicianBackfilled >= 1, "Pre-upgrade clinician session should have identified flags backfilled.");
            using (var clinCase = await clinician.Http.GetAsync($"/cases/{fixture.Ids.MiaCaseId}"))
                Assert.Equal(HttpStatusCode.OK, clinCase.StatusCode);
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

            // I3: Transferred patient test — add a Baltimore County personAccess row for Mia Rivera (whose historical
            // case is owned by Baltimore City). Case visibility is owner-based, so Mia's historical Baltimore City case
            // MUST remain masked/403, while lab results follow Result RLS (personAccess).
            int hadCountyAccess = await ScalarAsync<int>(
                "SELECT COUNT (*) FROM dbo.personAccess WHERE personId = @p AND tenantId = @t AND isDeleted = 0",
                ("@p", fixture.Ids.MiaPersonId), ("@t", fixture.Ids.BaltimoreCountyTenantId));
            Guid? addedAccessId = null;
            if (hadCountyAccess == 0)
            {
                addedAccessId = Guid.NewGuid();
                await ExecAsync("""
                    INSERT dbo.personAccess (personAccessId, rootTenantId, personId, tenantId, accessReason)
                    SELECT @aid, rootTenantId, personId, @t, 'residence'
                      FROM dbo.person WHERE personId = @p;
                    """, ("@aid", addedAccessId.Value), ("@t", fixture.Ids.BaltimoreCountyTenantId), ("@p", fixture.Ids.MiaPersonId));
            }
            try
            {
                // Historical case owned by Baltimore City stays 403 and masked on /cases and /api/v1/cases/map.
                using var historicalCase = await client.Http.GetAsync($"/cases/{fixture.Ids.MiaCaseId}");
                Assert.Equal(HttpStatusCode.Forbidden, historicalCase.StatusCode);
                Assert.DoesNotContain("Rivera", await client.Http.GetStringAsync("/cases"), StringComparison.OrdinalIgnoreCase);

                using var mapResp = await client.Http.GetAsync("/api/v1/cases/map");
                Assert.Equal(HttpStatusCode.OK, mapResp.StatusCode);
                using var mapDoc = JsonDocument.Parse(await mapResp.Content.ReadAsStringAsync());
                var miaMap = mapDoc.RootElement.EnumerateArray().FirstOrDefault(e => e.GetProperty("exposureCaseId").GetGuid() == fixture.Ids.MiaCaseId);
                Assert.Equal(JsonValueKind.Object, miaMap.ValueKind);
                Assert.Equal(Guid.Empty, miaMap.GetProperty("personId").GetGuid());
                Assert.Equal(JsonValueKind.Null, miaMap.GetProperty("addressLabel").ValueKind);

                // Lab result follows Result RLS (residence personAccess grants identified result access in Baltimore County).
                using var miaRes = await client.Http.GetAsync($"/results/{fixture.Ids.MiaLabResultId}");
                Assert.Equal(HttpStatusCode.OK, miaRes.StatusCode);
            }
            finally
            {
                if (addedAccessId is Guid aid)
                {
                    await ExecAsync("DELETE FROM dbo.personAccess WHERE personAccessId = @aid", ("@aid", aid));
                }
            }
        }
    }

    [SqlFact]
    public async Task ViewAsPreservesClinicalIdentifiedAccessAndKeepsDeidentifiedTargetMasked()
    {
        // B2: cityadmin (administers Baltimore City + holds clinical access) views as cmbalt (clinician WITHOUT tenant.administer).
        // Identified clinical access MUST be preserved in View As.
        Guid cityAdminId = await ScalarAsync<Guid>("SELECT appUserId FROM auth.appUser WHERE userName = N'cityadmin'");
        Guid cmBaltId = await ScalarAsync<Guid>("SELECT appUserId FROM auth.appUser WHERE userName = N'cmbalt'");

        var cityAdmin = await fixture.SignedInAsync("cityadmin");
        string profilesHtml = await cityAdmin.Http.GetStringAsync("/account/profiles");
        using (var startViewAs = await cityAdmin.Http.PostAsync($"/account/view-as/{cmBaltId}", MeritClient.Form(profilesHtml)))
            Assert.Equal(HttpStatusCode.OK, startViewAs.StatusCode);

        try
        {
            using var miaCase = await cityAdmin.Http.GetAsync($"/cases/{fixture.Ids.MiaCaseId}");
            Assert.Equal(HttpStatusCode.OK, miaCase.StatusCode);
            using var miaResult = await cityAdmin.Http.GetAsync($"/results/{fixture.Ids.MiaLabResultId}");
            Assert.Equal(HttpStatusCode.OK, miaResult.StatusCode);

            using var resList = await cityAdmin.Http.GetAsync("/api/v1/results?pageSize=25");
            Assert.Equal(HttpStatusCode.OK, resList.StatusCode);
            using var resDoc = JsonDocument.Parse(await resList.Content.ReadAsStringAsync());
            var rows = resDoc.RootElement.GetProperty("items").EnumerateArray().ToList();
            Assert.NotEmpty(rows);
            Assert.Contains(rows, r => r.GetProperty("isIdentified").GetBoolean() && r.GetProperty("personName").ValueKind == JsonValueKind.String);
        }
        finally
        {
            string page = await cityAdmin.Http.GetStringAsync("/");
            await cityAdmin.Http.PostAsync("/account/view-as/end", MeritClient.Form(page));
        }

        // Negative B2: cityadmin views as a Baltimore City de-identified epidemiologist -> MUST stay masked & 403 on details.
        await using var cityEpi = await SyntheticAccount.CreateAsync(fixture.Factory, "cityepi");
        await cityEpi.ScalarAsync<int>("""
            UPDATE auth.appUser SET homeTenantId = @city WHERE appUserId = @id;
            INSERT auth.userAssignment (userAssignmentId, appUserId, roleId, tenantId, includeDescendants, accessLevel, startDate)
            SELECT NEWID (), @id, r.roleId, @city, 1, 'read', '2020-01-01'
              FROM auth.role AS r JOIN auth.appUser AS u ON u.rootTenantId = r.rootTenantId AND u.appUserId = @id
             WHERE r.roleCode = 'epidemiologist' AND r.isDeleted = 0;
            SELECT 0;
            """, ("@city", fixture.Ids.BaltimoreCityTenantId));

        string profiles2 = await cityAdmin.Http.GetStringAsync("/account/profiles");
        using (var startDeidViewAs = await cityAdmin.Http.PostAsync($"/account/view-as/{cityEpi.Id}", MeritClient.Form(profiles2)))
            Assert.Equal(HttpStatusCode.OK, startDeidViewAs.StatusCode);

        try
        {
            using var deniedCase = await cityAdmin.Http.GetAsync($"/cases/{fixture.Ids.MiaCaseId}");
            Assert.Equal(HttpStatusCode.Forbidden, deniedCase.StatusCode);
            using var resList = await cityAdmin.Http.GetAsync("/api/v1/results?pageSize=25");
            Assert.Equal(HttpStatusCode.OK, resList.StatusCode);
            using var resDoc = JsonDocument.Parse(await resList.Content.ReadAsStringAsync());
            Assert.All(resDoc.RootElement.GetProperty("items").EnumerateArray(),
                r => Assert.False(r.GetProperty("isIdentified").GetBoolean()));
        }
        finally
        {
            string page = await cityAdmin.Http.GetStringAsync("/");
            await cityAdmin.Http.PostAsync("/account/view-as/end", MeritClient.Form(page));
            _ = cityAdminId;
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
        var enrollResults = await Task.WhenAll(enrollAttempts.Select(async a =>
        {
            using var s = fixture.Factory.Services.CreateScope();
            var svc = s.ServiceProvider.GetRequiredService<AccountService>();
            try
            {
                return await svc.CompleteEnrollmentAsync(unenrolled.Id, a.Secret, a.Code, "127.0.0.1", default, unenrolled.Name, initialStamp);
            }
            catch (ValidationException)
            {
                return null;
            }
        }));
        var winner = Assert.Single(enrollResults, r => r is not null)!;
        Assert.NotEqual(initialStamp, winner.SecurityStamp);
        Assert.Equal(winner.SecurityStamp, await unenrolled.ScalarAsync<Guid>("SELECT securityStamp FROM auth.appUser WHERE appUserId = @id"));

        // Fresh scope creation bound to expectedSecurityStamp rejects the pre-enrollment stamp and accepts the new stamp (I5).
        using (var s = fixture.Factory.Services.CreateScope())
        {
            var svc = s.ServiceProvider.GetRequiredService<AccountService>();
            await Assert.ThrowsAsync<SessionEndedException>(() =>
                svc.CreateSessionAsync(unenrolled.Id, TimeSpan.FromHours(1), expectedSecurityStamp: initialStamp));
            var created = await svc.CreateSessionAsync(unenrolled.Id, TimeSpan.FromHours(1), expectedSecurityStamp: winner.SecurityStamp);
            Assert.Equal(winner.SecurityStamp, created.Scope.Summary.SecurityStamp);
        }

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
        // exact-scope expiry (revokeLogicalSession=false) on stale old scope MUST NOT revoke the valid successor;
        // explicit logical sign-out (revokeLogicalSession=true) MUST revoke the successor while leaving otherSignIn active.
        var second = await accounts.CreateSessionAsync(account.Id, TimeSpan.FromHours(2));
        Guid secondOldId = second.Scope.Summary.SessionScopeId;
        var renewed = await accounts.RenewSessionAsync(account.Id, secondOldId, stamp, TimeSpan.FromHours(12));
        Guid successorId = renewed.Scope.Summary.SessionScopeId;

        await accounts.SignOutAsync(secondOldId, revokeLogicalSession: false);
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT (*) FROM auth.sessionScope WHERE sessionScopeId = @s AND revokedUtc IS NULL", ("@s", successorId)));

        await accounts.SignOutAsync(secondOldId, revokeLogicalSession: true);
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT (*) FROM auth.sessionScope WHERE sessionScopeId = @s AND revokedUtc IS NOT NULL", ("@s", successorId)));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT (*) FROM auth.sessionScope WHERE sessionScopeId = @s AND revokedUtc IS NULL", ("@s", otherScopeId)));

        // Overlapping concurrent renewal and explicit logical sign-out serialize cleanly with zero surviving scopes.
        var third = await accounts.CreateSessionAsync(account.Id, TimeSpan.FromHours(2));
        Guid thirdScopeId = third.Scope.Summary.SessionScopeId;
        Guid thirdRef = await ScalarAsync<Guid>("SELECT sessionReference FROM auth.sessionScope WHERE sessionScopeId = @s", ("@s", thirdScopeId));
        await Task.WhenAll(
            Task.Run(async () =>
            {
                using var s = fixture.Factory.Services.CreateScope();
                try { await s.ServiceProvider.GetRequiredService<AccountService>().RenewSessionAsync(account.Id, thirdScopeId, stamp, TimeSpan.FromHours(12)); }
                catch (SessionEndedException) { }
            }),
            Task.Run(async () =>
            {
                using var s = fixture.Factory.Services.CreateScope();
                await s.ServiceProvider.GetRequiredService<AccountService>().SignOutAsync(thirdScopeId, revokeLogicalSession: true);
            }));
        int activeAfterRace = await ScalarAsync<int>(
            "SELECT COUNT (*) FROM auth.sessionScope WHERE appUserId = @u AND sessionReference = @r AND revokedUtc IS NULL AND isDeleted = 0",
            ("@u", account.Id), ("@r", thirdRef));
        Assert.Equal(0, activeAfterRace);

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

    [Fact]
    public void ProductionStartupFailsFastWhenSignInLogNameHashKeyMissingAndMailDisabled()
    {
        using var failFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:Merit", TestEnvironment.WebConnectionString);
            builder.UseSetting("DataProtection:AllowUnencryptedKeys", "true");
            builder.UseSetting("Email:Provider", "Disabled");
            builder.UseSetting("EmailCodes:VerifierKey", "");
            builder.UseSetting("SignInLog:NameHashKey", "");
        });

        var ex = Assert.ThrowsAny<Exception>(() => _ = failFactory.CreateClient());
        Assert.Contains("SignInLog:NameHashKey", ex.ToString(), StringComparison.Ordinal);

        string validKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var okFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:Merit", TestEnvironment.WebConnectionString);
            builder.UseSetting("DataProtection:AllowUnencryptedKeys", "true");
            builder.UseSetting("Email:Provider", "Disabled");
            builder.UseSetting("EmailCodes:VerifierKey", "");
            builder.UseSetting("SignInLog:NameHashKey", validKey);
        });

        using var client = okFactory.CreateClient();
        Assert.NotNull(client);
    }

    [SqlFact]
    public async Task ReconcileDeidentifiedRoleBackfillUsesScopeJurisdictionDate()
    {
        // Use a non-Eastern root tenant (Pacific Standard Time) and verify 566_reconcileDeidentifiedRole.sql
        // evaluates grant effective dates against auth.udfLocalDate(rt.timeZone) rather than the default Eastern date.
        Guid rootId = Guid.NewGuid();
        Guid futureTenantId = Guid.NewGuid();
        Guid endingTodayTenantId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        Guid scopeId = Guid.NewGuid();
        Guid clinicianRoleId = Guid.NewGuid();
        Guid futureAssignId = Guid.NewGuid();
        Guid endingTodayAssignId = Guid.NewGuid();
        string suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        try
        {
            await ExecAsync("""
                DECLARE @platform UNIQUEIDENTIFIER = CAST (HASHBYTES ('MD5', N'tenant:PLATFORM') AS UNIQUEIDENTIFIER);
                INSERT auth.tenant (tenantId, parentTenantId, rootTenantId, tenantCode, tenantName, tenantType, statePostalCode, timeZone, isActive)
                VALUES (@rootId, @platform, @rootId, @rootCode, N'Pacific Test Root', 'state', 'WA', 'Pacific Standard Time', 1),
                       (@futureTenantId, @rootId, @rootId, @futureCode, N'Pacific Future County', 'jurisdiction', 'WA', NULL, 1),
                       (@endingTodayTenantId, @rootId, @rootId, @endingCode, N'Pacific Today County', 'jurisdiction', 'WA', NULL, 1);

                INSERT auth.tenantClosure (tenantClosureId, ancestorTenantId, descendantTenantId, depth)
                VALUES (NEWID (), @rootId, @rootId, 0),
                       (NEWID (), @futureTenantId, @futureTenantId, 0),
                       (NEWID (), @endingTodayTenantId, @endingTodayTenantId, 0),
                       (NEWID (), @rootId, @futureTenantId, 1),
                       (NEWID (), @rootId, @endingTodayTenantId, 1);

                INSERT auth.role (roleId, rootTenantId, roleCode, roleName, isAdministrator, isDeidentified)
                VALUES (@roleId, @rootId, 'caseManager', N'Case manager', 0, 0);

                INSERT auth.rolePermission (rolePermissionId, roleId, permissionId)
                SELECT NEWID (), @roleId, permissionId
                  FROM auth.permission
                 WHERE permissionCode IN ('result.view', 'case.view', 'person.view') AND isDeleted = 0;

                INSERT auth.appUser (appUserId, rootTenantId, homeTenantId, userName, displayName, passwordHash, securityStamp, mfaEnabled, isActive)
                VALUES (@userId, @rootId, @rootId, @userName, N'Pacific Clinician', N'hash', NEWID (), 1, 1);

                DECLARE @localToday DATE = auth.udfLocalDate ('Pacific Standard Time');
                INSERT auth.userAssignment (userAssignmentId, appUserId, roleId, tenantId, includeDescendants, accessLevel, startDate, endDate)
                VALUES (@futureAssignId, @userId, @roleId, @futureTenantId, 0, 'read', DATEADD (DAY, 1, @localToday), NULL),
                       (@endingTodayAssignId, @userId, @roleId, @endingTodayTenantId, 0, 'read', DATEADD (DAY, -7, @localToday), @localToday);

                INSERT auth.sessionScope (sessionScopeId, appUserId, rootTenantId, actingTenantId, isPlatformAdmin, isDeidentified, expiresUtc)
                VALUES (@scopeId, @userId, @rootId, @endingTodayTenantId, 0, 0, DATEADD (HOUR, 2, SYSUTCDATETIME ()));

                INSERT auth.sessionScopeTenant (sessionScopeTenantId, sessionScopeId, tenantId, accessLevel, canReadRelatedContacts, canReadIdentifiedResults, canReadIdentifiedCases)
                VALUES (NEWID (), @scopeId, @futureTenantId, 'read', 0, 0, 0),
                       (NEWID (), @scopeId, @endingTodayTenantId, 'read', 0, 0, 0);

                INSERT auth.sessionScopePermission (sessionScopePermissionId, sessionScopeId, permissionCode)
                VALUES (NEWID (), @scopeId, 'result.view'),
                       (NEWID (), @scopeId, 'case.view'),
                       (NEWID (), @scopeId, 'person.view');
                """,
                ("@rootId", rootId),
                ("@rootCode", $"TST-WA-{suffix}"),
                ("@futureTenantId", futureTenantId),
                ("@futureCode", $"TST-WAF-{suffix}"),
                ("@endingTodayTenantId", endingTodayTenantId),
                ("@endingCode", $"TST-WAT-{suffix}"),
                ("@roleId", clinicianRoleId),
                ("@userId", userId),
                ("@userName", $"pac_{suffix}"),
                ("@futureAssignId", futureAssignId),
                ("@endingTodayAssignId", endingTodayAssignId),
                ("@scopeId", scopeId));

            // Directly confirm tvfUserGrantPermissionsAsOf flips accurately across the date boundary.
            int activeTodayOnEnding = await ScalarAsync<int>(
                "SELECT COUNT (*) FROM auth.tvfUserGrantPermissionsAsOf (@u, auth.udfLocalDate ('Pacific Standard Time')) WHERE tenantId = @t AND permissionCode = 'case.view' AND hasIdentified = 1",
                ("@u", userId), ("@t", endingTodayTenantId));
            int activeTomorrowOnEnding = await ScalarAsync<int>(
                "SELECT COUNT (*) FROM auth.tvfUserGrantPermissionsAsOf (@u, DATEADD (DAY, 1, auth.udfLocalDate ('Pacific Standard Time'))) WHERE tenantId = @t AND permissionCode = 'case.view' AND hasIdentified = 1",
                ("@u", userId), ("@t", endingTodayTenantId));
            int activeTodayOnFuture = await ScalarAsync<int>(
                "SELECT COUNT (*) FROM auth.tvfUserGrantPermissionsAsOf (@u, auth.udfLocalDate ('Pacific Standard Time')) WHERE tenantId = @t AND permissionCode = 'case.view' AND hasIdentified = 1",
                ("@u", userId), ("@t", futureTenantId));
            int activeTomorrowOnFuture = await ScalarAsync<int>(
                "SELECT COUNT (*) FROM auth.tvfUserGrantPermissionsAsOf (@u, DATEADD (DAY, 1, auth.udfLocalDate ('Pacific Standard Time'))) WHERE tenantId = @t AND permissionCode = 'case.view' AND hasIdentified = 1",
                ("@u", userId), ("@t", futureTenantId));
            Assert.Equal(1, activeTodayOnEnding);
            Assert.Equal(0, activeTomorrowOnEnding);
            Assert.Equal(0, activeTodayOnFuture);
            Assert.Equal(1, activeTomorrowOnFuture);

            // Run the real 566_reconcileDeidentifiedRole.sql migration script.
            string scriptPath = Path.Combine(TestEnvironment.SourceRoot, "database", "scripts", "500_seed", "566_reconcileDeidentifiedRole.sql");
            string scriptSql = await File.ReadAllTextAsync(scriptPath);
            foreach (string batch in System.Text.RegularExpressions.Regex.Split(scriptSql, @"^\s*GO\s*$", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(batch))
                {
                    await ExecAsync(batch);
                }
            }

            // Scope must remain unrevoked, ending-today tenant must backfill to (1,1), future tenant must stay (0,0).
            bool revoked = await ScalarAsync<int>("SELECT CASE WHEN revokedUtc IS NULL THEN 0 ELSE 1 END FROM auth.sessionScope WHERE sessionScopeId = @s", ("@s", scopeId)) == 1;
            Assert.False(revoked);

            int endingResults = await ScalarAsync<int>("SELECT CAST (canReadIdentifiedResults AS INT) FROM auth.sessionScopeTenant WHERE sessionScopeId = @s AND tenantId = @t", ("@s", scopeId), ("@t", endingTodayTenantId));
            int endingCases = await ScalarAsync<int>("SELECT CAST (canReadIdentifiedCases AS INT) FROM auth.sessionScopeTenant WHERE sessionScopeId = @s AND tenantId = @t", ("@s", scopeId), ("@t", endingTodayTenantId));
            Assert.Equal(1, endingResults);
            Assert.Equal(1, endingCases);

            int futureResults = await ScalarAsync<int>("SELECT CAST (canReadIdentifiedResults AS INT) FROM auth.sessionScopeTenant WHERE sessionScopeId = @s AND tenantId = @t", ("@s", scopeId), ("@t", futureTenantId));
            int futureCases = await ScalarAsync<int>("SELECT CAST (canReadIdentifiedCases AS INT) FROM auth.sessionScopeTenant WHERE sessionScopeId = @s AND tenantId = @t", ("@s", scopeId), ("@t", futureTenantId));
            Assert.Equal(0, futureResults);
            Assert.Equal(0, futureCases);
        }
        finally
        {
            await ExecAsync("""
                DELETE FROM auth.sessionScopePermission WHERE sessionScopeId = @scopeId;
                DELETE FROM auth.sessionScopeTenant WHERE sessionScopeId = @scopeId;
                DELETE FROM auth.sessionScope WHERE sessionScopeId = @scopeId;
                DELETE FROM auth.userAssignment WHERE appUserId = @userId;
                DELETE FROM auth.appUser WHERE appUserId = @userId;
                DELETE FROM auth.rolePermission WHERE roleId = @roleId;
                DELETE FROM auth.role WHERE roleId = @roleId;
                DELETE FROM auth.tenantClosure WHERE ancestorTenantId IN (@rootId, @futureTenantId, @endingTodayTenantId) OR descendantTenantId IN (@rootId, @futureTenantId, @endingTodayTenantId);
                DELETE FROM auth.tenant WHERE tenantId IN (@futureTenantId, @endingTodayTenantId);
                DELETE FROM auth.tenant WHERE tenantId = @rootId;
                """,
                ("@scopeId", scopeId),
                ("@userId", userId),
                ("@roleId", clinicianRoleId),
                ("@rootId", rootId),
                ("@futureTenantId", futureTenantId),
                ("@endingTodayTenantId", endingTodayTenantId));
        }
    }
}
