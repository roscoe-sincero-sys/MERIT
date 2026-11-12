using System.Net;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

[Collection(MeritCollection.Name)]
public sealed class AdministrationAndTestingPolicyTests(MeritFixture fixture)
{
    private static async Task<T> Sql<T>(string text, params (string Name, object Value)[] args)
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        await using var cmd = new SqlCommand(text, c);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
    private static FormUrlEncodedContent Form(params (string Key, object? Value)[] args)
        => new(args.Select(a => new KeyValuePair<string, string>(a.Key, a.Value?.ToString() ?? "")));
    private static async Task Saved(HttpResponseMessage response)
    {
        using (response) Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }
    private async Task<(SyntheticAccount Account, MeritClient Client)> User(Guid tenant, params string[] roles)
    {
        var account = await SyntheticAccount.CreateAsync(fixture.Factory, "policyreview");
        await account.ScalarAsync<int>("""
            UPDATE auth.appUser SET homeTenantId=@tenant,rootTenantId=(SELECT rootTenantId FROM auth.tenant WHERE tenantId=@tenant) WHERE appUserId=@id;
            SELECT 0;
            """, ("@tenant", tenant));
        foreach (var role in roles)
            await account.ScalarAsync<int>("""
                INSERT auth.userAssignment(userAssignmentId,appUserId,roleId,tenantId,includeDescendants,accessLevel,startDate)
                SELECT NEWID(),@id,r.roleId,@tenant,1,'full','2020-01-01'
                FROM auth.role r JOIN auth.tenant t ON t.rootTenantId=r.rootTenantId WHERE t.tenantId=@tenant AND r.roleCode=@role;
                SELECT 0;
                """, ("@tenant", tenant), ("@role", role));
        var client = fixture.Anonymous(); await account.SignInAsync(client, MeritFixture.Password);
        return (account, client);
    }
    private async Task<Guid> NewRoot(string state)
    {
        var platform = await fixture.SignedInAsync("platform");
        string code = "POL-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var parent = await Sql<Guid>("SELECT tenantId FROM auth.tenant WHERE tenantCode='PLATFORM'");
        await platform.Http.GetStringAsync("/admin/tenants");
        await Saved(await platform.NoRedirect.PostAsync("/admin/tenants", Form(("ParentTenantId", parent), ("TenantCode", code),
            ("TenantName", code), ("TenantType", "state"), ("StatePostalCode", state), ("StateFips", state == "MD" ? "24" : "51"),
            ("TimeZone", "Eastern Standard Time"), ("EffectiveFrom", "2020-01-01"), ("SeedThresholds", "false"))));
        return await Sql<Guid>("SELECT tenantId FROM auth.tenant WHERE tenantCode=@code", ("@code", code));
    }

    [SqlTheory]
    [InlineData("VA")]
    [InlineData("MD")]
    public async Task TestingPolicyStartsOffAndOnlyMarylandCanAdoptItsPolicy(string state)
    {
        _ = fixture.Ids;
        var tenant = await NewRoot(state);
        var (account, client) = await User(tenant, "administrator", "supervisor");
        await using (account)
        using (client)
        {
            var person = Guid.NewGuid();
            await Sql<int>("INSERT dbo.person(personId,rootTenantId,owningTenantId,lastName,lastNameKey,dob) VALUES(@p,@t,@t,N'Policy patient',N'POLICYPATIENT','2025-01-01'); SELECT 0", ("@p", person), ("@t", tenant));
            try
            {
                string url = $"/people/{person}/care/testing";
                string page = await client.Http.GetStringAsync(url);
                Assert.Contains("No testing policy is active", page, StringComparison.Ordinal);
                Assert.DoesNotContain("mde.maryland.gov", page, StringComparison.Ordinal);
                async Task<HttpResponseMessage> Plan() => await client.NoRedirect.PostAsync(url, Form(("workTaskId", Guid.NewGuid()),
                    ("planKind", "screen12"), ("evidence", "Synthetic source and clinician review"), ("clinicianConfirmed", "true")));
                using (var refused = await Plan())
                {
                    Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
                    Assert.Contains("No testing policy is active", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
                }
                Assert.Equal(0, await Sql<int>("SELECT COUNT(*) FROM dbo.workTask WHERE personId=@p", ("@p", person)));
                await client.Http.GetStringAsync("/config/settings");
                using var adopted = await client.NoRedirect.PostAsync("/config/settings/testingPolicyVersion", Form(("settingValue", "MDH-2025-03"), ("effectiveFrom", "2026-01-01")));
                if (state == "VA")
                {
                    Assert.Equal(HttpStatusCode.UnprocessableEntity, adopted.StatusCode);
                    Assert.Contains("No testing policy is active", await client.Http.GetStringAsync(url), StringComparison.Ordinal);
                }
                else
                {
                    Assert.Equal(HttpStatusCode.OK, adopted.StatusCode);
                    Assert.Contains("Maryland childhood guidance", await client.Http.GetStringAsync(url), StringComparison.Ordinal);
                    await Saved(await Plan());
                    Assert.Equal("MDH-2025-03", await Sql<string>("SELECT JSON_VALUE(testingPlanJson,'$.policyVersion') FROM dbo.workTask WHERE personId=@p AND queueCode='testingFollowUp'", ("@p", person)));
                    // Turning the feature off must override an earlier adoption.
                    await client.Http.GetStringAsync("/config/settings");
                    await Saved(await client.NoRedirect.PostAsync("/config/settings/testingPolicyVersion", Form(("settingValue", "none"), ("effectiveFrom", "2026-01-02"))));
                    Assert.Contains("No testing policy is active", await client.Http.GetStringAsync(url), StringComparison.Ordinal);
                }
            }
            finally
            {
                await Sql<int>("DELETE dbo.workTask WHERE personId=@p; DELETE dbo.person WHERE personId=@p; SELECT 0", ("@p", person));
            }
        }
    }

    [SqlFact]
    public async Task CountyConfigurationGrantCannotAdoptAStatewideTestingPolicy()
    {
        _ = fixture.Ids;
        var root = await NewRoot("MD");
        string code = "PROG-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var platform = await fixture.SignedInAsync("platform");
        await platform.Http.GetStringAsync("/admin/tenants");
        await Saved(await platform.NoRedirect.PostAsync("/admin/tenants", Form(("ParentTenantId", root), ("TenantCode", code),
            ("TenantName", code), ("TenantType", "program"))));
        var child = await Sql<Guid>("SELECT tenantId FROM auth.tenant WHERE tenantCode=@code", ("@code", code));
        var (account, client) = await User(child, "administrator");
        await using (account)
        using (client)
        {
            await client.Http.GetStringAsync("/config/settings");
            using var refused = await client.NoRedirect.PostAsync("/config/settings/testingPolicyVersion", Form(("settingValue", "MDH-2025-03"), ("effectiveFrom", "2026-01-01")));
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal(0, await Sql<int>("SELECT COUNT(*) FROM config.jurisdictionSetting WHERE rootTenantId=@r AND settingKey='testingPolicyVersion' AND settingValue=N'MDH-2025-03' AND isDeleted=0", ("@r", root)));
        }
    }

    [SqlFact]
    public async Task LegacyMarylandPlanNeverAppliesMarylandClosureRulesToVirginia()
    {
        _ = fixture.Ids;
        var tenant = await NewRoot("VA");
        var (account, client) = await User(tenant, "supervisor");
        await using (account)
        using (client)
        {
            var person = Guid.NewGuid(); var result = Guid.NewGuid(); var caseId = Guid.NewGuid();
            await Sql<int>("""
                INSERT dbo.person(personId,rootTenantId,owningTenantId,lastName,lastNameKey,dob) VALUES(@p,@t,@t,N'Virginia legacy',N'VIRGINIALEGACY','2024-01-01');
                INSERT dbo.intakeBatch(intakeBatchId,rootTenantId,owningTenantId,sourceChannel,fileName,receivedUtc,batchStatus)
                VALUES(@r,@t,@t,'manual',N'Legacy Virginia test',SYSUTCDATETIME(),'loaded');
                INSERT dbo.labResult(labResultId,rootTenantId,owningTenantId,personId,analyteSpecimenId,collectedDate,receivedUtc,resultStatus,unitReported,unitCanonical,valueCanonical,valueText,sampleType,labOrganizationId,ageAtCollectionMonths,reportedSnapshotJson,intakeBatchId)
                SELECT @r,@t,@t,@p,analyteSpecimenId,'2026-01-01',SYSUTCDATETIME(),'final',N'ug/dL',N'ug/dL',4,N'4','venous',labOrganizationId,24,N'{"state":"MD"}',@r
                FROM dbo.labResult WHERE labResultId=@originalResult;
                INSERT dbo.exposureCase(exposureCaseId,rootTenantId,owningTenantId,caseNumber,personId,analyteId,population,classification,computedClassification,caseStatus,qualifyingLabResultId,workflowTemplate,openedDate)
                SELECT @c,@t,@t,CONCAT('VA-',LEFT(CONVERT(VARCHAR(36),@c),8)),@p,analyteId,'child',classification,computedClassification,'open',@r,workflowTemplate,'2026-01-01'
                FROM dbo.exposureCase WHERE exposureCaseId=@originalCase;
                INSERT dbo.workTask(workTaskId,rootTenantId,owningTenantId,personId,exposureCaseId,queueCode,title,taskStatus,completedUtc,testingPlanJson)
                VALUES(NEWID(),@t,@t,@p,@c,'testingFollowUp',N'Legacy imported plan','open',NULL,N'{"policyVersion":"MDH-2025-03","kind":"confirmation"}');
                SELECT 0;
                """, ("@p", person), ("@t", tenant), ("@r", result), ("@c", caseId), ("@originalResult", fixture.Ids.MiaLabResultId), ("@originalCase", fixture.Ids.MiaCaseId));
            try
            {
                await client.Http.GetStringAsync($"/cases/{caseId}");
                var version = await Sql<byte[]>("SELECT rowVersion FROM dbo.exposureCase WHERE exposureCaseId=@c", ("@c", caseId));
                var stamp = await Sql<string>("SELECT dbo.udfCaseClosureReviewStamp(@c)", ("@c", caseId));
                using var closed = await client.NoRedirect.PostAsync($"/cases/{caseId}/close", Form(("closureReason", "belowLevelOnRetest"),
                    ("closedDate", "2026-10-01"), ("note", "Synthetic Virginia closure review"), ("rowVersion", "0x" + Convert.ToHexString(version)), ("closureReviewToken", stamp), ("closureReviewConfirmed", "true")));
                Assert.True(closed.StatusCode is HttpStatusCode.OK or HttpStatusCode.Redirect, await closed.Content.ReadAsStringAsync());
                Assert.Equal("closed", await Sql<string>("SELECT caseStatus FROM dbo.exposureCase WHERE exposureCaseId=@c", ("@c", caseId)));
            }
            finally
            {
                await Sql<int>("DELETE dbo.workTask WHERE personId=@p; DELETE dbo.caseActivity WHERE exposureCaseId=@c; DELETE dbo.exposureCase WHERE personId=@p; DELETE dbo.labResult WHERE personId=@p; DELETE dbo.intakeBatch WHERE intakeBatchId=@r; DELETE dbo.person WHERE personId=@p; SELECT 0", ("@p", person), ("@c", caseId), ("@r", result));
            }
        }
    }

    [SqlFact]
    public async Task AdministrationIsAuditedWithActorTenantAndGrantHistoryWithoutSensitiveFormValues()
    {
        _ = fixture.Ids;
        var county = await Sql<Guid>("SELECT tenantId FROM auth.tenant WHERE tenantCode='MD-BCO'");
        var city = await Sql<Guid>("SELECT tenantId FROM auth.tenant WHERE tenantCode='MD-BCHD'");
        var platform = await fixture.SignedInAsync("platform");
        var countyRole = await Sql<Guid>("SELECT r.roleId FROM auth.role r JOIN auth.tenant t ON t.rootTenantId=r.rootTenantId WHERE t.tenantId=@t AND r.roleCode='caseManager'", ("@t", county));
        string name = "audit284" + Guid.NewGuid().ToString("N")[..8];
        await platform.Http.GetStringAsync("/admin/users/new");
        await Saved(await platform.NoRedirect.PostAsync("/admin/users", Form(("UserName", name), ("DisplayName", "Sensitive display sentinel"),
            ("Email", name + "@example.test"), ("NewPassword", "Audit284!SyntheticPassphrase"), ("HomeTenantId", county), ("IsActive", "true"),
            ("FirstRoleId", countyRole), ("FirstTenantId", county), ("FirstIncludeDescendants", "true"), ("FirstAccessLevel", "full"))));
        var id = await Sql<Guid>("SELECT appUserId FROM auth.appUser WHERE userName=@n", ("@n", name));
        var grant = await Sql<Guid>("SELECT userAssignmentId FROM auth.userAssignment WHERE appUserId=@u", ("@u", id));
        var actor = await Sql<Guid>("SELECT appUserId FROM auth.appUser WHERE userName='platform'");
        Assert.Equal(1, await Sql<int>("SELECT COUNT(*) FROM logs.auditEvent WHERE entityId=@u AND eventAction='create' AND entityType='appUser' AND appUserId=@actor AND owningTenantId=@t", ("@u", id), ("@actor", actor), ("@t", county)));
        foreach (string action in new[] { "unlock", "reset-mfa" })
        {
            await platform.Http.GetStringAsync($"/admin/users/{id}");
            await Saved(await platform.NoRedirect.PostAsync($"/admin/users/{id}/{action}", Form()));
        }
        await platform.Http.GetStringAsync($"/admin/users/{id}");
        await Saved(await platform.NoRedirect.PostAsync($"/admin/users/{id}/assignments/{grant}/end", Form()));
        Assert.Equal(2, await Sql<int>("SELECT COUNT(*) FROM logs.auditEvent WHERE entityId=@g AND eventAction IN('grant','revoke') AND appUserId=@actor AND owningTenantId=@t", ("@g", grant), ("@actor", actor), ("@t", county)));
        Assert.Equal(2, await Sql<int>("SELECT COUNT(*) FROM logs.auditEvent WHERE entityId=@u AND eventAction IN('unlock','mfaResetByAdmin')", ("@u", id)));
        string json = await Sql<string>("SELECT administrationJson FROM logs.auditEvent WHERE entityId=@g AND eventAction='grant'", ("@g", grant));
        Assert.Contains(countyRole.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sensitive", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Passphrase", json, StringComparison.Ordinal);
        // A caller rolling back the mutation also rolls back the append-only ledger insert.
        var rolledBack = Guid.NewGuid();
        Assert.Equal(0, await Sql<int>("""
            DECLARE @scope UNIQUEIDENTIFIER=(SELECT TOP(1) sessionScopeId FROM auth.sessionScope WHERE appUserId=@actor AND revokedUtc IS NULL ORDER BY createdDateUtc DESC);
            EXEC sys.sp_set_session_context @key=N'scopeKey',@value=@scope;
            BEGIN TRANSACTION;
            EXEC auth.uspSaveUser @appUserId=@u,@userName=N'rollback284',@displayName=N'Rollback sentinel',@homeTenantId=@t,@passwordHash=N'synthetic-unused-hash';
            ROLLBACK TRANSACTION;
            SELECT COUNT(*) FROM logs.auditEvent WHERE entityId=@u;
            """, ("@actor", actor), ("@u", rolledBack), ("@t", county)));
        Assert.Equal(0, await Sql<int>("SELECT COUNT(*) FROM auth.appUser WHERE appUserId=@u", ("@u", rolledBack)));
        var (officer, reviewer) = await User(county, "securityOfficer");
        await using (officer)
        using (reviewer)
        {
            string csv = await reviewer.Http.GetStringAsync($"/admin/audit.csv?entityId={grant}");
            Assert.Contains("Granted access", csv, StringComparison.Ordinal);
            Assert.Contains("Ended grant", csv, StringComparison.Ordinal);
            Assert.Contains(countyRole.ToString(), csv, StringComparison.OrdinalIgnoreCase);
        }
        var (sibling, other) = await User(city, "securityOfficer");
        await using (sibling)
        using (other)
            Assert.DoesNotContain(grant.ToString(), await other.Http.GetStringAsync($"/admin/audit.csv?entityId={grant}"), StringComparison.OrdinalIgnoreCase);
    }
}
