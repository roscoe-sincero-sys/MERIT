using System.Net;
using System.Text.Json;

namespace Merit.IntegrationTests;

public sealed partial class AdministrationAndTestingPolicyTests
{
    private static Task<Guid> ConfigurationSpecimen() => Sql<Guid>("SELECT s.analyteSpecimenId FROM config.analyteSpecimen s JOIN config.analyte a ON a.analyteId=s.analyteId WHERE a.code='PB' AND s.specimen='wholeBlood'");
    private static async Task<JsonDocument> ConfigurationEvent(Guid id)
        => JsonDocument.Parse(await Sql<string>("SELECT TOP(1) administrationJson FROM logs.auditEvent WHERE entityId=@id AND entityType IN('jurisdictionSetting','threshold','notificationRule') ORDER BY auditEventId DESC", ("@id", id)));
    private static Task<int> ConfigurationEventCount(Guid actor)
        => Sql<int>("SELECT COUNT(*) FROM logs.auditEvent WHERE appUserId=@actor AND entityType IN('jurisdictionSetting','threshold','notificationRule')", ("@actor", actor));
    private static async Task SetConfiguration(MeritClient client, string key, string value)
    {
        await client.Http.GetStringAsync("/config/settings");
        await Saved(await client.NoRedirect.PostAsync("/config/settings/" + key, Form(("settingValue", value), ("effectiveFrom", "2026-01-01"))));
    }
    private async Task<Guid> ConfigurationCounty(Guid parent, string fips)
    {
        var platform = await fixture.SignedInAsync("platform");
        string code = "AUD-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        await platform.Http.GetStringAsync("/admin/tenants");
        await Saved(await platform.NoRedirect.PostAsync("/admin/tenants", Form(("ParentTenantId", parent), ("TenantCode", code),
            ("TenantName", code), ("TenantType", "jurisdiction"), ("CountyFipsList", fips))));
        return await Sql<Guid>("SELECT tenantId FROM auth.tenant WHERE tenantCode=@code", ("@code", code));
    }

    [SqlFact]
    public async Task ConfigurationSettingsKeepSameDayHistoryRedactFreeTextAndReachOnlyTheirRootReviewers()
    {
        _ = fixture.Ids;
        var root = await NewRoot("MD", "001 Audit County\n003 Sibling County");
        var (account, admin) = await User(root, "administrator", "supervisor");
        await using (account)
        using (admin)
        {
            await SetConfiguration(admin, "sessionTimeoutMinutes", "30");
            var first = await Sql<Guid>("SELECT jurisdictionSettingId FROM config.jurisdictionSetting WHERE rootTenantId=@r AND settingKey='sessionTimeoutMinutes' AND isDeleted=0 AND effectiveFrom='2026-01-01'", ("@r", root));
            await SetConfiguration(admin, "sessionTimeoutMinutes", "60");
            var second = await Sql<Guid>("SELECT jurisdictionSettingId FROM config.jurisdictionSetting WHERE rootTenantId=@r AND settingKey='sessionTimeoutMinutes' AND isDeleted=0 AND effectiveFrom='2026-01-01'", ("@r", root));
            using var before = await ConfigurationEvent(first); using var after = await ConfigurationEvent(second);
            Assert.Equal("20", before.RootElement.GetProperty("oldValue").GetString());
            Assert.Equal("30", before.RootElement.GetProperty("newValue").GetString());
            Assert.Equal("30", after.RootElement.GetProperty("oldValue").GetString());
            Assert.Equal("60", after.RootElement.GetProperty("newValue").GetString());
            Assert.Equal(2, await ConfigurationEventCount(account.Id));
            Assert.Equal(2, await Sql<int>("SELECT COUNT(*) FROM logs.auditEvent WHERE entityId IN(@a,@b) AND appUserId=@u AND rootTenantId=@r AND owningTenantId=@r", ("@a", first), ("@b", second), ("@u", account.Id), ("@r", root)));

            const string sensitive = "Private configuration sentinel";
            await SetConfiguration(admin, "agencyName", sensitive);
            // Existing permissive/extension-setting behavior remains; unvalidated values and keys never become ledger text.
            await SetConfiguration(admin, "sessionTimeoutMinutes", sensitive);
            await SetConfiguration(admin, "PrivateExtensionKey", sensitive);
            Assert.Equal(5, await ConfigurationEventCount(account.Id));
            Assert.Equal(0, await Sql<int>("SELECT COUNT(*) FROM logs.auditEvent WHERE appUserId=@u AND (administrationJson LIKE '%Private%' OR administrationJson LIKE '%sentinel%')", ("@u", account.Id)));
            Assert.Equal(3, await Sql<int>("SELECT COUNT(*) FROM logs.auditEvent WHERE appUserId=@u AND entityType='jurisdictionSetting' AND JSON_VALUE(administrationJson,'$.newValueRedacted')='true'", ("@u", account.Id)));
            await SetConfiguration(admin, "sessionTimeoutMinutes", "30");

            var county = await ConfigurationCounty(root, "001");
            var (officer, reviewer) = await User(county, "securityOfficer");
            await using (officer)
            using (reviewer)
            {
                string csv = await reviewer.Http.GetStringAsync("/admin/audit.csv?recordType=jurisdictionSetting");
                Assert.Contains(first.ToString(), csv, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("sessionTimeoutMinutes", csv, StringComparison.Ordinal);
                Assert.DoesNotContain(sensitive, csv, StringComparison.Ordinal);
                string page = await reviewer.Http.GetStringAsync("/admin/audit?recordType=jurisdictionSetting");
                Assert.Contains("value=\"jurisdictionSetting\"", page, StringComparison.Ordinal);
                Assert.Contains(account.Id.ToString(), page, StringComparison.OrdinalIgnoreCase); // actor is offered in the filter
                Assert.Contains(account.Name, await reviewer.Http.GetStringAsync("/admin/users.csv"), StringComparison.Ordinal);
                using var refused = await reviewer.NoRedirect.PostAsync("/config/settings/sessionTimeoutMinutes", Form(("settingValue", "120")));
                Assert.True(refused.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect);
                Assert.Equal(0, await ConfigurationEventCount(officer.Id));
                Assert.Equal("30", await Sql<string>("SELECT settingValue FROM config.jurisdictionSetting WHERE rootTenantId=@r AND settingKey='sessionTimeoutMinutes' AND isDeleted=0 AND effectiveTo IS NULL", ("@r", root)));
            }
            var otherRoot = await NewRoot("VA");
            var (otherOfficer, other) = await User(otherRoot, "securityOfficer");
            await using (otherOfficer)
            using (other)
                Assert.DoesNotContain(first.ToString(), await other.Http.GetStringAsync("/admin/audit.csv?recordType=jurisdictionSetting"), StringComparison.OrdinalIgnoreCase);
            Assert.Contains(first.ToString(), await admin.Http.GetStringAsync("/admin/audit.csv?recordType=jurisdictionSetting"), StringComparison.OrdinalIgnoreCase);
            var platform = await fixture.SignedInAsync("platform");
            Assert.Contains(first.ToString(), await platform.Http.GetStringAsync($"/admin/audit.csv?entityId={first}"), StringComparison.OrdinalIgnoreCase);
            int count = await ConfigurationEventCount(account.Id);
            using var invalid = await admin.NoRedirect.PostAsync("/config/settings/passwordMinLength", Form(("settingValue", "1")));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
            Assert.Equal(count, await ConfigurationEventCount(account.Id));
        }
    }

    private static async Task<HttpResponseMessage> ConfigurationThreshold(MeritClient client, Guid id, Guid specimen, string value, bool active, string? version = null)
    {
        await client.Http.GetStringAsync("/config/thresholds/new");
        return await client.NoRedirect.PostAsync("/config/thresholds", Form(("ThresholdId", id), ("AnalyteSpecimenId", specimen),
            ("Population", "child"), ("AgeMinMonths", "0"), ("AgeMaxMonths", "71"), ("ThresholdType", "case"),
            ("LowerValue", value), ("Comparator", ">="), ("EffectiveFrom", "2026-01-01"), ("IsActive", active ? "true" : "false"),
            ("Authority", "Private authority sentinel"), ("RowVersion", version)));
    }

    [SqlFact]
    public async Task ConfigurationThresholdAuditTracksValuesActivationAndRefusedWrites()
    {
        _ = fixture.Ids; var root = await NewRoot("MD");
        var (account, client) = await User(root, "administrator", "supervisor");
        await using (account)
        using (client)
        {
            var specimen = await ConfigurationSpecimen(); var id = Guid.NewGuid();
            await Saved(await ConfigurationThreshold(client, id, specimen, "3.5", true));
            async Task<string> Version() => "0x" + Convert.ToHexString(await Sql<byte[]>("SELECT rowVersion FROM config.threshold WHERE thresholdId=@id", ("@id", id)));
            string stale = await Version();
            await Saved(await ConfigurationThreshold(client, id, specimen, "5", true, stale));
            using var snapshot = await ConfigurationEvent(id);
            Assert.Equal(3.5m, snapshot.RootElement.GetProperty("oldValue").GetProperty("lowerValue").GetDecimal());
            Assert.Equal(5m, snapshot.RootElement.GetProperty("newValue").GetProperty("lowerValue").GetDecimal());
            using (var conflict = await ConfigurationThreshold(client, id, specimen, "6", true, stale))
                Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal(2, await ConfigurationEventCount(account.Id));
            await Saved(await ConfigurationThreshold(client, id, specimen, "5", false, await Version()));
            await Saved(await ConfigurationThreshold(client, id, specimen, "5", true, await Version()));
            Assert.Equal("create,update,deactivate,reactivate", await Sql<string>("SELECT STRING_AGG(eventAction,',') WITHIN GROUP(ORDER BY auditEventId) FROM logs.auditEvent WHERE entityType='threshold' AND entityId=@id", ("@id", id)));
            Assert.Equal(0, await Sql<int>("SELECT COUNT(*) FROM logs.auditEvent WHERE entityId=@id AND administrationJson LIKE '%Private%'", ("@id", id)));
            string csv = await client.Http.GetStringAsync("/admin/audit.csv?recordType=threshold");
            Assert.Contains(id.ToString(), csv, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Threshold", csv, StringComparison.Ordinal);
        }
    }

    private static async Task<HttpResponseMessage> ConfigurationRule(MeritClient client, Guid id, Guid specimen, string name, bool approved = true)
    {
        await client.Http.GetStringAsync("/config/notification-rules");
        return await client.NoRedirect.PostAsync("/config/notification-rules", Form(("notificationRuleId", id), ("ruleName", name),
            ("analyteSpecimenId", specimen), ("population", "child"), ("minimumValue", "3.5"), ("recipientKind", "caregiver"),
            ("dueInDays", "10"), ("dayBasis", "business"), ("timeZoneId", "Eastern Standard Time"), ("calendarName", "Private calendar sentinel"),
            ("effectiveFrom", "2026-01-01"), ("approvalReference", "Private approval sentinel"), ("approved", approved ? "true" : "false"),
            ("rulePurpose", "notification"), ("eligibilityMode", "threshold")));
    }

    [SqlFact]
    public async Task ConfigurationRuleApprovalActivationAndRetirementAreAtomicSingleEventsAndSiblingPrivate()
    {
        _ = fixture.Ids; var root = await NewRoot("MD", "001 Audit County\n003 Sibling County");
        var county = await ConfigurationCounty(root, "001"); var sibling = await ConfigurationCounty(root, "003");
        var (account, client) = await User(county, "administrator", "supervisor");
        await using (account)
        using (client)
        {
            var id = Guid.NewGuid(); var specimen = await ConfigurationSpecimen();
            using (var refused = await ConfigurationRule(client, id, specimen, "Private rule sentinel", false))
                Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Equal(0, await ConfigurationEventCount(account.Id));
            await Saved(await ConfigurationRule(client, id, specimen, "Private rule sentinel"));
            Assert.Equal(1, await ConfigurationEventCount(account.Id));
            using var created = await ConfigurationEvent(id);
            Assert.True(created.RootElement.GetProperty("approvedAndActivated").GetBoolean());
            Assert.True(created.RootElement.GetProperty("newValue").GetProperty("isActive").GetBoolean());
            await client.Http.GetStringAsync("/config/notification-rules");
            await Saved(await client.NoRedirect.PostAsync($"/config/notification-rules/{id}/retire", Form()));
            Assert.Equal(2, await ConfigurationEventCount(account.Id));
            using var ended = await ConfigurationEvent(id);
            Assert.True(ended.RootElement.GetProperty("oldValue").GetProperty("isActive").GetBoolean());
            Assert.False(ended.RootElement.GetProperty("newValue").GetProperty("isActive").GetBoolean());
            Assert.Equal(0, await Sql<int>("SELECT COUNT(*) FROM logs.auditEvent WHERE entityId=@id AND administrationJson LIKE '%Private%'", ("@id", id)));
            var (officer, reviewer) = await User(sibling, "securityOfficer");
            await using (officer)
            using (reviewer)
                Assert.DoesNotContain(id.ToString(), await reviewer.Http.GetStringAsync($"/admin/audit.csv?entityId={id}"), StringComparison.OrdinalIgnoreCase);
        }
    }

    [SqlFact]
    public async Task ConfigurationRegionalSettingChangeCannotRewriteDescendantRoots()
    {
        _ = fixture.Ids;
        var platform = await fixture.SignedInAsync("platform");
        var parent = await Sql<Guid>("SELECT tenantId FROM auth.tenant WHERE tenantCode='PLATFORM'");
        string code = "REG-AUD-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await platform.Http.GetStringAsync("/admin/tenants");
        await Saved(await platform.NoRedirect.PostAsync("/admin/tenants", Form(("ParentTenantId", parent), ("TenantCode", code),
            ("TenantName", code), ("TenantType", "region"), ("TimeZone", "Eastern Standard Time"), ("EffectiveFrom", "2020-01-01"), ("SeedThresholds", "false"))));
        var region = await Sql<Guid>("SELECT tenantId FROM auth.tenant WHERE tenantCode=@code", ("@code", code));
        var maryland = await NewRoot("MD", parentTenant: region); var virginia = await NewRoot("VA", parentTenant: region);
        var (account, client) = await User(region, "administrator", "supervisor");
        await using (account)
        using (client)
        {
            await SetConfiguration(client, "sessionTimeoutMinutes", "45");
            Assert.Equal("45", await Sql<string>("SELECT settingValue FROM config.jurisdictionSetting WHERE rootTenantId=@r AND settingKey='sessionTimeoutMinutes' AND isDeleted=0 AND effectiveTo IS NULL", ("@r", region)));
            Assert.Equal(2, await Sql<int>("SELECT COUNT(*) FROM config.jurisdictionSetting WHERE rootTenantId IN(@md,@va) AND settingKey='sessionTimeoutMinutes' AND isDeleted=0 AND effectiveTo IS NULL AND settingValue=N'20'", ("@md", maryland), ("@va", virginia)));
            Assert.Equal(1, await Sql<int>("SELECT COUNT(*) FROM logs.auditEvent WHERE appUserId=@u AND entityType='jurisdictionSetting' AND rootTenantId=@r AND owningTenantId=@r", ("@u", account.Id), ("@r", region)));
        }
    }

    [SqlFact]
    public async Task ConfigurationAuditRollsBackWithEveryMutation()
    {
        _ = fixture.Ids; var root = await NewRoot("MD");
        var (account, client) = await User(root, "administrator", "supervisor");
        await using (account)
        using (client)
        {
            var threshold = Guid.NewGuid(); var rule = Guid.NewGuid(); var specimen = await ConfigurationSpecimen();
            int count = await ConfigurationEventCount(account.Id);
            int inside = await Sql<int>("""
                DECLARE @scope UNIQUEIDENTIFIER=(SELECT TOP(1) sessionScopeId FROM auth.sessionScope WHERE appUserId=@u AND revokedUtc IS NULL ORDER BY createdDateUtc DESC);
                EXEC sys.sp_set_session_context @key=N'scopeKey',@value=@scope;
                EXEC sys.sp_set_session_context @key=N'appUserId',@value=@u;
                DECLARE @before BIGINT=(SELECT COALESCE(MAX(auditEventId),0) FROM logs.auditEvent),@inside INT;
                BEGIN TRANSACTION;
                EXEC config.uspSaveSetting @settingKey='sessionTimeoutMinutes',@settingValue=N'31',@effectiveFrom='2026-01-01';
                EXEC config.uspSaveThreshold @thresholdId=@t,@analyteSpecimenId=@s,@population='child',@thresholdType='case',@lowerValue=99,@unit=N'ug/dL',@effectiveFrom='2026-01-01',@isActive=0;
                EXEC config.uspSaveNotificationRule @notificationRuleId=@rule,@ruleName=N'Rollback rule',@analyteSpecimenId=@s,@population='child',@minimumValue=99,@recipientKind='caregiver',@dueInDays=10,@dayBasis='business',@timeZoneId='Eastern Standard Time',@calendarName=N'Synthetic calendar',@holidayDatesJson=N'[]',@effectiveFrom='2026-01-01',@approvalReference=N'Synthetic approval';
                EXEC config.uspRetireNotificationRule @notificationRuleId=@rule;
                SELECT @inside=COUNT(*) FROM logs.auditEvent WHERE auditEventId>@before AND appUserId=@u AND entityType IN('jurisdictionSetting','threshold','notificationRule');
                ROLLBACK TRANSACTION;
                SELECT @inside;
                """, ("@u", account.Id), ("@t", threshold), ("@s", specimen), ("@rule", rule));
            Assert.Equal(4, inside);
            Assert.Equal(count, await ConfigurationEventCount(account.Id));
            Assert.Equal(0, await Sql<int>("SELECT COUNT(*) FROM config.threshold WHERE thresholdId=@t", ("@t", threshold)));
            Assert.Equal(0, await Sql<int>("SELECT COUNT(*) FROM config.notificationRule WHERE notificationRuleId=@id", ("@id", rule)));
            Assert.Equal("20", await Sql<string>("SELECT settingValue FROM config.jurisdictionSetting WHERE rootTenantId=@r AND settingKey='sessionTimeoutMinutes' AND isDeleted=0", ("@r", root)));
        }
    }
}
