using System.Net;
using Merit.Application.Data;
using Merit.Application.Security;
using Merit.Web.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Merit.IntegrationTests;

[Collection(MeritCollection.Name)]
public sealed class PasswordPolicyTests(MeritFixture fixture)
{
    [SqlFact]
    public async Task LegacyVerificationUpgradesTheHashWithoutChangingCredentialOrSessionState()
    {
        var host = fixture.Factory;
        await using var account = await SyntheticAccount.CreateAsync(host, "policy");
        const string original = "cafe\u0301 lake stone";
        var oldHasher = new PasswordHasher<object>(Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions { IterationCount = 100_000 }));
        string legacy = oldHasher.HashPassword(new object(), original);
        await account.ScalarAsync<int>("UPDATE auth.appUser SET passwordHash = @hash WHERE appUserId = @id; SELECT 0", ("@hash", legacy));
        using var existing = new MeritClient(host);
        await account.SignInAsync(existing, original);
        Guid stamp = await account.ScalarAsync<Guid>("SELECT securityStamp FROM auth.appUser WHERE appUserId = @id");
        // Model an older credential with an existing session, and a required-change flag waiting for its next sign-in.
        await account.ScalarAsync<int>("""
            UPDATE auth.appUser SET passwordHash = @hash, passwordChangedUtc = '2026-01-01', passwordChangeRequired = 1 WHERE appUserId = @id;
            SELECT 0;
            """, ("@hash", legacy));
        using var returning = new MeritClient(host);
        string form = await returning.Http.GetStringAsync("/account/signin");
        using var result = await returning.NoRedirect.PostAsync("/account/signin", MeritClient.Form(form, ("UserName", account.Name), ("Password", original)));
        Assert.Equal("/account/mfa", result.Headers.Location?.OriginalString);
        string upgraded = await account.ScalarAsync<string>("SELECT passwordHash FROM auth.appUser WHERE appUserId = @id");
        Assert.StartsWith(IdentityPasswordHasher.FormatPrefix, upgraded, StringComparison.Ordinal);
        Assert.Equal(PasswordCheck.Success, host.Services.GetRequiredService<IPasswordHasher>().Verify(upgraded, "café lake stone"));
        Assert.Equal(1, await account.ScalarAsync<int>("""
            SELECT COUNT(*) FROM auth.appUser WHERE appUserId = @id AND securityStamp = @stamp
              AND passwordChangedUtc = '2026-01-01' AND passwordChangeRequired = 1
            """, ("@stamp", stamp)));
        Assert.Equal(HttpStatusCode.OK, (await existing.NoRedirect.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(0, await account.ScalarAsync<int>("SELECT COUNT(*) FROM auth.sessionScope WHERE appUserId = @id AND revokedUtc IS NOT NULL"));
    }

    [SqlFact]
    public async Task HashUpgradeCannotRestoreAResetCredentialOrIgnoreCaseDifferencesInBase64()
    {
        var host = fixture.Factory;
        await using var account = await SyntheticAccount.CreateAsync(host, "policy");
        var hasher = host.Services.GetRequiredService<IPasswordHasher>();
        string old = await account.ScalarAsync<string>("SELECT passwordHash FROM auth.appUser WHERE appUserId = @id");
        Guid stamp = await account.ScalarAsync<Guid>("SELECT securityStamp FROM auth.appUser WHERE appUserId = @id");
        using var scope = host.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<IAccountData>();
        string upgraded = hasher.Hash(MeritFixture.Password);
        Assert.False(await data.UpgradePasswordHashAsync(account.Id, stamp, old.ToUpperInvariant(), upgraded));
        Assert.True(await data.UpgradePasswordHashAsync(account.Id, stamp, old, upgraded));
        Assert.False(await data.UpgradePasswordHashAsync(account.Id, stamp, old, hasher.Hash(MeritFixture.Password)));
        string reset = hasher.Hash("Another orchard constellation");
        await account.ScalarAsync<int>("UPDATE auth.appUser SET passwordHash = @hash, securityStamp = NEWID() WHERE appUserId = @id; SELECT 0", ("@hash", reset));
        Assert.False(await data.UpgradePasswordHashAsync(account.Id, stamp, upgraded, hasher.Hash(MeritFixture.Password)));
        Assert.Equal(reset, await account.ScalarAsync<string>("SELECT passwordHash FROM auth.appUser WHERE appUserId = @id"));
    }

    [SqlFact]
    public async Task AdminAndSelfServiceShowTheSameEffectiveMinimumIncludingLegacySettingsAndAnotherRoot()
    {
        var host = fixture.Factory;
        await using var account = await SyntheticAccount.CreateAsync(host, "policy");
        using var person = new MeritClient(host);
        await account.SignInAsync(person, MeritFixture.Password);
        using var platform = (await fixture.SignedInAsync("platform")).On(host);
        string original = await account.ScalarAsync<string>("""
            SELECT TOP(1) settingValue FROM config.jurisdictionSetting WHERE settingKey = 'passwordMinLength'
              AND rootTenantId = (SELECT rootTenantId FROM auth.appUser WHERE appUserId = @id)
              AND isDeleted = 0 AND effectiveTo IS NULL ORDER BY effectiveFrom DESC
            """);
        async Task Set(string value) => await account.ScalarAsync<int>("""
            UPDATE config.jurisdictionSetting SET settingValue = @value WHERE settingKey = 'passwordMinLength'
              AND rootTenantId = (SELECT rootTenantId FROM auth.appUser WHERE appUserId = @id) AND isDeleted = 0 AND effectiveTo IS NULL;
            SELECT 0;
            """, ("@value", value));
        try
        {
            foreach (var (value, minimum) in new[] { ("0", 15), ("8", 15), ("invalid", 15), ("30", 30), ("200", 128) })
            {
                await Set(value);
                string mine = WebUtility.HtmlDecode(await person.Http.GetStringAsync("/account/security"));
                string admin = WebUtility.HtmlDecode(await platform.Http.GetStringAsync($"/admin/users/{account.Id}"));
                Assert.Contains($"At least {minimum} characters", mine, StringComparison.Ordinal);
                Assert.Contains($"At least {minimum} characters", admin, StringComparison.Ordinal);
                int resetMinimum = await account.ScalarAsync<int>("DECLARE @n INT; EXEC auth.uspGetPasswordPolicy @appUserId = @id, @passwordMinLength = @n OUTPUT; SELECT @n;");
                Assert.Equal(minimum, resetMinimum);
            }
            await Set("30");
            string hash = await account.ScalarAsync<string>("SELECT passwordHash FROM auth.appUser WHERE appUserId = @id");
            Guid home = await account.ScalarAsync<Guid>("SELECT homeTenantId FROM auth.appUser WHERE appUserId = @id");
            string edit = await platform.Http.GetStringAsync($"/admin/users/{account.Id}");
            using var refused = await platform.NoRedirect.PostAsync("/admin/users", MeritClient.Form(edit,
                ("AppUserId", account.Id.ToString()), ("UserName", account.Name), ("DisplayName", "Policy test"), ("Email", account.Email),
                ("HomeTenantId", home.ToString()), ("IsActive", "true"), ("NewPassword", "oak river cloud")));
            Assert.Contains("Use at least 30 characters", WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync()), StringComparison.Ordinal);
            Assert.Equal(hash, await account.ScalarAsync<string>("SELECT passwordHash FROM auth.appUser WHERE appUserId = @id"));
        }
        finally { await Set(original); }
    }

    [SqlFact]
    public async Task SettingsRejectOutOfRangePoliciesAndUnicodeConfirmationsMatchAfterNormalization()
    {
        using var admin = (await fixture.SignedInAsync("admin")).On(fixture.Factory);
        string settings = await admin.Http.GetStringAsync("/config/settings");
        foreach (string value in new[] { "0", "8", "14", "129", "invalid" })
        {
            using var response = await admin.NoRedirect.PostAsync("/config/settings/passwordMinLength", MeritClient.Form(settings, ("settingValue", value)));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Contains("15 to 128", WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()), StringComparison.Ordinal);
        }
        await using var account = await SyntheticAccount.CreateAsync(fixture.Factory, "policy");
        using var person = new MeritClient(fixture.Factory);
        await account.SignInAsync(person, MeritFixture.Password);
        string page = await person.Http.GetStringAsync("/account/security");
        using var changed = await person.NoRedirect.PostAsync("/account/security/password", MeritClient.Form(page,
            ("CurrentPassword", MeritFixture.Password), ("NewPassword", "café lake stone"),
            ("ConfirmPassword", "cafe\u0301 lake stone"), ("PasswordAuthenticatorCode", account.TotpCode())));
        Assert.Equal(HttpStatusCode.Redirect, changed.StatusCode);
        string hash = await account.ScalarAsync<string>("SELECT passwordHash FROM auth.appUser WHERE appUserId = @id");
        Assert.Equal(PasswordCheck.Success, fixture.Factory.Services.GetRequiredService<IPasswordHasher>().Verify(hash, "cafe\u0301 lake stone"));
    }
}
