using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Merit.Application.Reference;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

/// <summary>Issues #289 and #290: settings are validated against one catalogue, have an effect, and never lose versions silently.</summary>
public sealed partial class AdministrationAndTestingPolicyTests
{
    private static Task<string> CurrentSetting(Guid root, string key)
        => Sql<string>("SELECT COALESCE((SELECT TOP(1) settingValue FROM config.jurisdictionSetting WHERE rootTenantId=@r AND settingKey=@k AND isDeleted=0 AND effectiveFrom<=CONVERT(DATE,GETDATE()) AND (effectiveTo IS NULL OR effectiveTo>=CONVERT(DATE,GETDATE())) ORDER BY effectiveFrom DESC),N'(none)')", ("@r", root), ("@k", key));

    private static async Task<(HttpStatusCode Status, string Body)> PostSetting(MeritClient client, string key, string value, string effectiveFrom = "2026-01-01", bool replaceLater = false)
    {
        await client.Http.GetStringAsync("/config/settings");
        using var response = await client.NoRedirect.PostAsync("/config/settings/" + key,
            Form(("settingValue", value), ("effectiveFrom", effectiveFrom), ("replaceLaterVersions", replaceLater ? "true" : "false")));
        return (response.StatusCode, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
    }

    [SqlFact]
    public async Task SettingCatalogueIsTheSameInTheDatabaseAndTheApplication()
    {
        _ = fixture.Ids;
        var rows = new List<string>();
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        await using var cmd = new SqlCommand("SELECT settingKey,valueKind,minValue,maxValue,allowedCodes,allowNotSet,inUse FROM config.tvfSettingCatalogue() ORDER BY settingKey", c);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(string.Join("|", reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? "" : Convert.ToDecimal(reader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                reader.IsDBNull(3) ? "" : Convert.ToDecimal(reader.GetValue(3), System.Globalization.CultureInfo.InvariantCulture).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? "" : reader.GetString(4), Convert.ToInt32(reader.GetValue(5), System.Globalization.CultureInfo.InvariantCulture), Convert.ToInt32(reader.GetValue(6), System.Globalization.CultureInfo.InvariantCulture)));
        }

        var expected = SettingCatalogue.All.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => string.Join("|", d.Key, d.KindCode,
            d.Min?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "", d.Max?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "",
            d.Codes is null ? "" : string.Join(",", d.Codes), d.AllowNotSet ? 1 : 0, d.InUse ? 1 : 0)).ToList();
        Assert.Equal(expected, rows.OrderBy(r => r.Split('|')[0], StringComparer.Ordinal).ToList());
        // Every key a new root is seeded with is catalogued, so nothing on the page is an unknown setting.
        Assert.Equal(0, await Sql<int>("SELECT COUNT(*) FROM (SELECT DISTINCT settingKey FROM config.jurisdictionSetting WHERE isDeleted=0 AND settingKey NOT LIKE 'Private%') s WHERE NOT EXISTS(SELECT 1 FROM config.tvfSettingCatalogue() c WHERE c.settingKey=s.settingKey COLLATE Latin1_General_100_BIN2)"));
    }

    [SqlFact]
    public async Task SettingsRefuseUnknownKeysInvalidValuesAndUnusedSettingsInBothLayers()
    {
        _ = fixture.Ids;
        var root = await NewRoot("MD");
        string rootCode = await Sql<string>("SELECT tenantCode FROM auth.tenant WHERE tenantId=@r", ("@r", root));
        var (account, admin) = await User(root, "administrator", "supervisor");
        await using (account)
        using (admin)
        {
            int events = await ConfigurationEventCount(account.Id);
            int rowCount = await Sql<int>("SELECT COUNT(*) FROM config.jurisdictionSetting WHERE rootTenantId=@r", ("@r", root));
            foreach (var (key, value, message) in new[]
            {
                ("sessionTimeoutMinutes", "1", "5 to 60"), ("sessionTimeoutMinutes", "999999", "5 to 60"), ("sessionTimeoutMinutes", "-5", "5 to 60"),
                ("sessionTimeoutMinutes", "abc", "5 to 60"), ("sessionTimeoutMinutes", "NOTSET", "5 to 60"), ("sessionTimeoutMinutes", "3٠", "5 to 60"),
                ("adultAgeCutoffYears", "-3", "12 to 21"), ("adultAgeCutoffYears", "banana", "12 to 21"), ("pregnancyMaxWeeks", "0", "20 to 52"),
                ("siteInvestigationMinHours", "1.234", "0.5 to 168"), ("siteInvestigationMinHours", "500", "0.5 to 168"),
                ("unreportedMethodConfirms", "maybe", "true or false"), ("primaryResidencePolicy", "anything", "review, move"),
                ("confirmingMethods", "icpMs,madeUp", "separated by commas"), ("confirmingMethods", "icpMs,icpMs", "separated by commas"),
                ("statePostalCode", "md", "two-letter"), ("stateFips", "2", "two-digit"), ("adultCaseOwnerTenantCode", "NO-SUCH-TENANT", "active jurisdiction"),
                ("madeUpKey", "free text value", "not a MERIT setting"),
                ("smsCodesEnabled", "true", "not in use"), ("matchingPreset", "lenient", "not in use"), ("localNotification", "true", "not in use"),
                ("providerLookups", "file", "not in use"), ("occupationalReferralAgency", "State plan", "not in use"), ("agencyName", "Any agency", "not in use"),
            })
            {
                string before = await CurrentSetting(root, key);
                var (status, body) = await PostSetting(admin, key, value);
                Assert.True(status == HttpStatusCode.UnprocessableEntity, $"{key}={value}: HTTP {(int)status}");
                Assert.Contains(message, body, StringComparison.Ordinal);
                Assert.Equal(before, await CurrentSetting(root, key));
            }

            Assert.Equal(events, await ConfigurationEventCount(account.Id));
            Assert.Equal(rowCount, await Sql<int>("SELECT COUNT(*) FROM config.jurisdictionSetting WHERE rootTenantId=@r", ("@r", root)));

            // The procedure refuses the same things when the application check is bypassed.
            foreach (var (key, value) in new[] { ("sessionTimeoutMinutes", "abc"), ("sessionTimeoutMinutes", "61"), ("madeUpKey", "x"), ("agencyName", "Any agency"), ("adultAgeCutoffYears", "-3"), ("adultCaseOwnerTenantCode", "NO-SUCH-TENANT") })
            {
                var refused = await Assert.ThrowsAsync<SqlException>(() => Sql<int>("""
                    DECLARE @scope UNIQUEIDENTIFIER=(SELECT TOP(1) sessionScopeId FROM auth.sessionScope WHERE appUserId=@u AND revokedUtc IS NULL ORDER BY createdDateUtc DESC);
                    EXEC sys.sp_set_session_context @key=N'scopeKey',@value=@scope;
                    EXEC sys.sp_set_session_context @key=N'appUserId',@value=@u;
                    EXEC config.uspSaveSetting @settingKey=@k,@settingValue=@v,@effectiveFrom='2026-01-01';
                    SELECT 0;
                    """, ("@u", account.Id), ("@k", key), ("@v", value)));
                Assert.Equal(51422, refused.Number);
            }

            Assert.Equal(events, await ConfigurationEventCount(account.Id));

            // Values inside the definitions still save, one audit event each.
            foreach (var (key, value) in new[]
            {
                ("sessionTimeoutMinutes", "45"), ("adultAgeCutoffYears", "18"), ("siteInvestigationMinHours", "7.5"), ("clusterMinCount", "3"), ("clusterMinCount", "NOTSET"),
                ("confirmingMethods", "icpMs,graphiteFurnaceAa,anodicStrippingVoltammetry"), ("primaryResidencePolicy", "move"), ("unreportedMethodConfirms", "FALSE"),
                ("adultCaseOwnerTenantCode", rootCode), ("statePostalCode", "MD"), ("stateFips", "24"),
            })
            {
                var (status, body) = await PostSetting(admin, key, value);
                Assert.True(status == HttpStatusCode.OK, $"{key}={value}: HTTP {(int)status} {body[..Math.Min(300, body.Length)]}");
                Assert.Equal(key == "unreportedMethodConfirms" ? "false" : value, await CurrentSetting(root, key));
            }

            Assert.Equal(events + 11, await ConfigurationEventCount(account.Id));
            Assert.Equal("7.50", await Sql<string>("SELECT TOP(1) JSON_VALUE(administrationJson,'$.newValue') FROM logs.auditEvent WHERE appUserId=@u AND JSON_VALUE(administrationJson,'$.settingKey')='siteInvestigationMinHours' ORDER BY auditEventId DESC", ("@u", account.Id)));

            // The page offers no control for a setting nothing reads, and says so.
            string page = WebUtility.HtmlDecode(await admin.Http.GetStringAsync("/config/settings"));
            foreach (var unused in SettingCatalogue.All.Where(d => !d.InUse))
                Assert.DoesNotContain($"hx-post=\"/config/settings/{unused.Key}\"", page, StringComparison.Ordinal);
            Assert.Equal(SettingCatalogue.All.Count(d => !d.InUse), Regex.Matches(page, "<span class=\"usa-tag\">Not in use</span>").Count);
            Assert.Contains("hx-post=\"/config/settings/sessionTimeoutMinutes\"", page, StringComparison.Ordinal);
            Assert.Contains("Whole number, 5 to 60", page, StringComparison.Ordinal);
        }
    }

    [SqlFact]
    public async Task SessionTimeoutSettingIsTheIdleTimeoutOfSessionsStartedAfterTheChange()
    {
        _ = fixture.Ids;
        var root = await NewRoot("MD");
        var (account, admin) = await User(root, "administrator", "supervisor");
        await using (account)
        using (admin)
        {
            static string Minutes(string html) => Regex.Match(html, "data-session-minutes=\"([0-9]+)\"").Groups[1].Value;
            Assert.Equal("20", Minutes(await admin.Http.GetStringAsync("/config/settings")));
            Assert.Equal(HttpStatusCode.OK, (await PostSetting(admin, "sessionTimeoutMinutes", "5")).Status);
            // The session that made the change keeps the lifetime it started with.
            Assert.Equal("20", Minutes(await admin.Http.GetStringAsync("/config/settings")));

            var (second, later) = await User(root, "caseManager");
            await using (second)
            using (later)
            {
                using var response = await later.Http.GetAsync("/account/security");
                response.EnsureSuccessStatusCode();
                Assert.Equal("5", Minutes(await response.Content.ReadAsStringAsync()));
                long expires = long.Parse(response.Headers.GetValues("X-Merit-Session-Expires").Single(), System.Globalization.CultureInfo.InvariantCulture);
                double remaining = (DateTimeOffset.FromUnixTimeMilliseconds(expires) - DateTimeOffset.UtcNow).TotalMinutes;
                Assert.InRange(remaining, 3.5, 5.1);
            }

            // Another data root is unaffected.
            var other = await NewRoot("VA");
            var (third, elsewhere) = await User(other, "caseManager");
            await using (third)
            using (elsewhere)
                Assert.Equal("20", Minutes(await elsewhere.Http.GetStringAsync("/account/security")));
        }
    }

    [SqlFact]
    public async Task ExtractSwitchesDecideWhetherAnExtractCanBeRequested()
    {
        _ = fixture.Ids;
        var root = await NewRoot("MD");
        var (account, admin) = await User(root, "administrator", "supervisor", "epidemiologist");
        await using (account)
        using (admin)
        {
            Assert.Equal(HttpStatusCode.OK, (await PostSetting(admin, "cblsEnabled", "false")).Status);
            await admin.Http.GetStringAsync("/extracts");
            using (var refused = await admin.NoRedirect.PostAsync("/extracts", Form(("extractType", "CBLS"), ("year", "2025"), ("quarter", "1"))))
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
                Assert.Contains("turned off", WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync()), StringComparison.Ordinal);
            }

            Assert.Equal(0, await Sql<int>("SELECT COUNT(*) FROM dbo.extractRun WHERE rootTenantId=@r", ("@r", root)));
            await Saved(await admin.NoRedirect.PostAsync("/extracts", Form(("extractType", "ABLES"), ("year", "2025"))));
            Assert.Equal(1, await Sql<int>("SELECT COUNT(*) FROM dbo.extractRun WHERE rootTenantId=@r AND extractType='ABLES'", ("@r", root)));
            Assert.Equal(HttpStatusCode.OK, (await PostSetting(admin, "cblsEnabled", "true")).Status);
            await Saved(await admin.NoRedirect.PostAsync("/extracts", Form(("extractType", "CBLS"), ("year", "2025"), ("quarter", "1"))));
            Assert.Equal(1, await Sql<int>("SELECT COUNT(*) FROM dbo.extractRun WHERE rootTenantId=@r AND extractType='CBLS'", ("@r", root)));
            // Test cleanup only: the synthetic account cannot be removed while its queued extract jobs reference it.
            await Sql<int>("DELETE dbo.extractRun WHERE requestedByUserId=@u; DELETE dbo.jobQueue WHERE requestedByUserId=@u; SELECT 0;", ("@u", account.Id));
        }
    }

    [SqlFact]
    public async Task LaterDatedSettingVersionsAreReplacedOnlyWhenConfirmedAndTheAuditNamesThem()
    {
        _ = fixture.Ids;
        var root = await NewRoot("MD"); // seeded settings start 2020-01-01
        var (account, admin) = await User(root, "administrator", "supervisor");
        await using (account)
        using (admin)
        {
            static Task<string> Versions(Guid root) => Sql<string>("SELECT STRING_AGG(CONCAT(settingValue,'@',CONVERT(CHAR(10),effectiveFrom,23),'..',COALESCE(CONVERT(CHAR(10),effectiveTo,23),'open')),';') WITHIN GROUP(ORDER BY effectiveFrom) FROM config.jurisdictionSetting WHERE rootTenantId=@r AND settingKey='clusterMinCount' AND isDeleted=0", ("@r", root));
            async Task<JsonDocument> LastEvent() => JsonDocument.Parse(await Sql<string>("SELECT TOP(1) CONCAT('{\"action\":\"',eventAction,'\",\"detail\":',administrationJson,'}') FROM logs.auditEvent WHERE appUserId=@u AND entityType='jurisdictionSetting' ORDER BY auditEventId DESC", ("@u", account.Id)));

            // Scheduling a future value needs no confirmation: nothing is dated after it.
            Assert.Equal(HttpStatusCode.OK, (await PostSetting(admin, "clusterMinCount", "5", "2031-01-01")).Status);
            Assert.Equal("NOTSET@2020-01-01..2030-12-31;5@2031-01-01..open", await Versions(root));
            int events = await ConfigurationEventCount(account.Id);

            // A change in the meantime would cancel the scheduled value: refused, nothing changed, nothing audited.
            var (status, body) = await PostSetting(admin, "clusterMinCount", "3", "2026-06-01");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
            Assert.Contains("1 version dated after 2026-06-01 (effective 2031-01-01)", body, StringComparison.Ordinal);
            Assert.Contains("Replace later-dated versions", body, StringComparison.Ordinal);
            Assert.Equal("NOTSET@2020-01-01..2030-12-31;5@2031-01-01..open", await Versions(root));
            Assert.Equal(events, await ConfigurationEventCount(account.Id));

            // Confirmed: the change is saved and the audit event names the version it removed.
            Assert.Equal(HttpStatusCode.OK, (await PostSetting(admin, "clusterMinCount", "3", "2026-06-01", replaceLater: true)).Status);
            Assert.Equal("NOTSET@2020-01-01..2026-05-31;3@2026-06-01..open", await Versions(root));
            using (var confirmed = await LastEvent())
            {
                Assert.Equal("update", confirmed.RootElement.GetProperty("action").GetString());
                var detail = confirmed.RootElement.GetProperty("detail");
                Assert.Equal("NOTSET", detail.GetProperty("oldValue").GetString());
                Assert.Equal("3", detail.GetProperty("newValue").GetString());
                Assert.Equal(1, detail.GetProperty("removedVersionCount").GetInt32());
                var removed = detail.GetProperty("removedVersions")[0];
                Assert.Equal("5", removed.GetProperty("value").GetString());
                Assert.Equal("2031-01-01", removed.GetProperty("effectiveFrom").GetString());
            }

            // Backdating before every version is a change, never "Created", and is refused until confirmed.
            (status, body) = await PostSetting(admin, "clusterMinCount", "9", "2019-01-01");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
            Assert.Contains("2 versions dated after 2019-01-01 (effective 2020-01-01 to 2026-06-01)", body, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.OK, (await PostSetting(admin, "clusterMinCount", "9", "2019-01-01", replaceLater: true)).Status);
            Assert.Equal("9@2019-01-01..open", await Versions(root));
            using (var backdated = await LastEvent())
            {
                Assert.Equal("update", backdated.RootElement.GetProperty("action").GetString());
                var detail = backdated.RootElement.GetProperty("detail");
                Assert.Equal(JsonValueKind.Null, detail.GetProperty("oldValue").ValueKind);
                Assert.Equal(2, detail.GetProperty("removedVersionCount").GetInt32());
                Assert.Equal(new[] { "NOTSET", "3" }, detail.GetProperty("removedVersions").EnumerateArray().Select(v => v.GetProperty("value").GetString()).ToArray());
            }

            // A same-day correction replaces only that day's version and needs no confirmation.
            Assert.Equal(HttpStatusCode.OK, (await PostSetting(admin, "clusterMinCount", "4", "2019-01-01")).Status);
            using (var sameDay = await LastEvent())
            {
                Assert.Equal("9", sameDay.RootElement.GetProperty("detail").GetProperty("oldValue").GetString());
                Assert.Equal(0, sameDay.RootElement.GetProperty("detail").GetProperty("removedVersionCount").GetInt32());
            }

            // The removed versions are kept as soft-deleted rows; the reviewer's export shows the removal.
            Assert.Equal(4, await Sql<int>("SELECT COUNT(*) FROM config.jurisdictionSetting WHERE rootTenantId=@r AND settingKey='clusterMinCount' AND isDeleted=1", ("@r", root)));
            Assert.Contains("removedVersions", await admin.Http.GetStringAsync("/admin/audit.csv?recordType=jurisdictionSetting"), StringComparison.Ordinal);
            (status, _) = await PostSetting(admin, "clusterMinCount", "4", "2099-01-01");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        }
    }
}
