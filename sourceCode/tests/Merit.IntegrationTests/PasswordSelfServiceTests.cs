using System.Net;
using System.Text.RegularExpressions;
using Merit.Application.Email;
using Merit.Application.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Merit.IntegrationTests;

/// <summary>
/// Password self-service (#190). A forgotten-password request answers the same way for any user name. A reset link
/// works once, only with the second factor, and closes after wrong codes. A signed-in change needs the current password
/// and the authenticator. An administrator can require a new password at next sign-in. Every success ends the
/// account's sessions and is audited. Tokens, passwords and addresses never reach the log. Accounts are synthetic
/// (example.test), and mail goes to an in-memory sender.
/// </summary>
[Collection(MeritCollection.Name)]
public sealed partial class PasswordSelfServiceTests(MeritFixture fixture)
{
    private const string PublicBase = "https://merit.example.test";
    private const string NewPassword = "Granite-Lantern-Orchard-42";
    private static readonly string Key = Convert.ToBase64String(Enumerable.Range(40, 32).Select(i => (byte)i).ToArray());

    [SqlFact]
    public async Task AForgottenPasswordIsResetOnlyWithTheLinkAndTheSecondFactor()
    {
        var mail = new CapturingEmailSender();
        var logs = new CapturingLoggerProvider();
        using var host = Host(mail, logs);
        await using var account = await SyntheticAccount.CreateAsync(host, "pw");
        await using var other = await SyntheticAccount.CreateAsync(host, "pw");

        // A session the reset must end.
        using var earlier = new MeritClient(host);
        await account.SignInAsync(earlier, MeritFixture.Password);

        // The same answer, and the same page, whether or not the account exists.
        using var visitor = new MeritClient(host);
        string known = await ForgotAsync(visitor, account.Name);
        string unknown = await ForgotAsync(visitor, "no-such-user-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.Equal(Card(known), Card(unknown));
        Assert.Contains("If MERIT has an account with that user name and an email address", Decode(known), StringComparison.Ordinal);

        var message = await mail.WaitForAsync(1);
        await Task.Delay(500);
        Assert.Single(mail.Messages);
        Assert.Equal(account.Email, message.To);
        Assert.Equal("Reset your MERIT password", message.Subject);
        var (resetId, token) = Link(message);   // built from Merit:PublicBaseUrl, never from the request's host

        // The link moves into a cookie; the page is clean and names the account.
        using var person = new MeritClient(host);
        using var opened = await person.Http.GetAsync($"/account/reset?r={resetId}&t={token}");
        Assert.Equal("/account/reset", opened.RequestMessage!.RequestUri!.PathAndQuery);
        Assert.Equal("no-referrer", opened.Headers.GetValues("Referrer-Policy").Single());
        string form = Decode(await opened.Content.ReadAsStringAsync());
        Assert.Contains($"For the MERIT account <strong>{account.Name}</strong>", form, StringComparison.Ordinal);

        // Without the second factor nothing changes, and the wrong code counts.
        using var wrong = await person.NoRedirect.PostAsync("/account/reset", MeritClient.Form(form,
            ("Code", "000000"), ("NewPassword", NewPassword), ("ConfirmPassword", NewPassword)));
        Assert.Contains("You can try 4 more times with this link", Decode(await wrong.Content.ReadAsStringAsync()), StringComparison.Ordinal);
        Assert.Equal(1, await account.ScalarAsync<int>("SELECT failedSignInCount FROM auth.appUser WHERE appUserId = @id"));

        // The rules come first: a breached or mismatched password is refused before the code is checked.
        using var weak = await person.NoRedirect.PostAsync("/account/reset", MeritClient.Form(form,
            ("Code", account.TotpCode()), ("NewPassword", "password123"), ("ConfirmPassword", "password123")));
        Assert.Contains("breached passwords", Decode(await weak.Content.ReadAsStringAsync()), StringComparison.Ordinal);
        using var mismatch = await person.NoRedirect.PostAsync("/account/reset", MeritClient.Form(form,
            ("Code", account.TotpCode()), ("NewPassword", NewPassword), ("ConfirmPassword", NewPassword + "x")));
        Assert.Contains("The two passwords don't match.", Decode(await mismatch.Content.ReadAsStringAsync()), StringComparison.Ordinal);

        using var done = await person.NoRedirect.PostAsync("/account/reset", MeritClient.Form(form,
            ("Code", account.TotpCode()), ("NewPassword", NewPassword), ("ConfirmPassword", NewPassword)));
        Assert.Contains("Your password has been reset", Decode(await done.Content.ReadAsStringAsync()), StringComparison.Ordinal);
        Assert.Equal(0, await account.ScalarAsync<int>("SELECT failedSignInCount FROM auth.appUser WHERE appUserId = @id AND passwordChangedUtc IS NOT NULL"));
        Assert.Equal(0, await account.ScalarAsync<int>("SELECT COUNT (*) FROM auth.sessionScope WHERE appUserId = @id AND revokedUtc IS NULL"));
        Assert.Equal(1, await account.ScalarAsync<int>("SELECT COUNT (*) FROM logs.auditEvent WHERE appUserId = @id AND eventAction = 'passwordResetRequested'"));
        Assert.Equal(1, await account.ScalarAsync<int>("SELECT COUNT (*) FROM logs.auditEvent WHERE appUserId = @id AND eventAction = 'passwordResetCompleted'"));
        Assert.Equal("Your MERIT password was changed", (await mail.WaitForAsync(2)).Subject);

        // The old password no longer works; the new one does.
        using (var old = new MeritClient(host))
        {
            Assert.Equal("/account/signin", await PasswordStepAsync(old, account.Name, MeritFixture.Password));
        }

        using (var fresh = new MeritClient(host))
        {
            Assert.Equal("/account/mfa", await PasswordStepAsync(fresh, account.Name, NewPassword));
        }

        // A used link never works again, and the other account was never touched.
        using var again = new MeritClient(host);
        string reused = Decode(await again.Http.GetStringAsync($"/account/reset?r={resetId}&t={token}"));
        Assert.Contains("This link was already used.", reused, StringComparison.Ordinal);
        Assert.Equal(0, await other.ScalarAsync<int>("SELECT COUNT (*) FROM auth.passwordReset WHERE appUserId = @id"));

        // Neither the token, the password nor the address reached the log.
        Assert.DoesNotContain(logs.Lines, l => l.Contains(token, StringComparison.Ordinal) || l.Contains(NewPassword, StringComparison.Ordinal)
                                               || l.Contains(account.Email, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logs.Lines, l => l.Contains("Password reset completed", StringComparison.Ordinal));
    }

    [SqlFact]
    public async Task ResetLinksCloseAfterWrongCodesExpireAndGiveWayToNewerOnes()
    {
        var mail = new CapturingEmailSender();
        using var host = Host(mail, null, ("PasswordReset:MaxCodeAttempts", "2"), ("PasswordReset:CooldownSeconds", "0"),
            ("PasswordReset:MaxLinksPerAccount", "4"));
        await using var account = await SyntheticAccount.CreateAsync(host, "pw");
        await using var noAuthenticator = await SyntheticAccount.CreateAsync(host, "pw", mfa: false);
        using var visitor = new MeritClient(host);

        // Two wrong codes close the link.
        await ForgotAsync(visitor, account.Name);
        var first = Link(await mail.WaitForAsync(1));
        using var person = new MeritClient(host);
        string form = await person.Http.GetStringAsync($"/account/reset?r={first.Id}&t={first.Token}");
        using (await person.NoRedirect.PostAsync("/account/reset", MeritClient.Form(form, ("Code", "000000"), ("NewPassword", NewPassword), ("ConfirmPassword", NewPassword))))
        {
        }

        using var closed = await person.NoRedirect.PostAsync("/account/reset", MeritClient.Form(form, ("Code", "000000"), ("NewPassword", NewPassword), ("ConfirmPassword", NewPassword)));
        Assert.Contains("This link no longer works", Decode(await closed.Content.ReadAsStringAsync()), StringComparison.Ordinal);
        await account.ScalarAsync<int>("UPDATE auth.appUser SET failedSignInCount = 0, lockoutEndUtc = NULL WHERE appUserId = @id; SELECT 0");

        // A newer link supersedes the open one.
        await ForgotAsync(visitor, account.Name);
        var second = Link(await mail.WaitForAsync(2));
        await ForgotAsync(visitor, account.Name);
        var third = Link(await mail.WaitForAsync(3));
        using var superseded = new MeritClient(host);
        Assert.Contains("A newer link was sent", Decode(await superseded.Http.GetStringAsync($"/account/reset?r={second.Id}&t={second.Token}")), StringComparison.Ordinal);

        // An expired link says so.
        await account.ScalarAsync<int>($"UPDATE auth.passwordReset SET expiresUtc = DATEADD (SECOND, -1, SYSUTCDATETIME ()) WHERE passwordResetId = '{Guid.Parse(third.Id)}'; SELECT 0");
        using var late = new MeritClient(host);
        Assert.Contains("This link has expired.", Decode(await late.Http.GetStringAsync($"/account/reset?r={third.Id}&t={third.Token}")), StringComparison.Ordinal);

        // A tampered token is just "not valid".
        using var forged = new MeritClient(host);
        Assert.Contains("This link isn't valid.", Decode(await forged.Http.GetStringAsync($"/account/reset?r={third.Id}&t={new string('A', 43)}")), StringComparison.Ordinal);

        // A recovery code is a second factor too, and is used up.
        await ForgotAsync(visitor, account.Name);
        var fourth = Link(await mail.WaitForAsync(4));
        await account.AddRecoveryCodeAsync(host, "RECOVERY23");
        using var recovering = new MeritClient(host);
        string page = await recovering.Http.GetStringAsync($"/account/reset?r={fourth.Id}&t={fourth.Token}");
        using var recovered = await recovering.NoRedirect.PostAsync("/account/reset", MeritClient.Form(page,
            ("Code", "RECOVERY23"), ("NewPassword", NewPassword), ("ConfirmPassword", NewPassword)));
        Assert.Contains("Your password has been reset", Decode(await recovered.Content.ReadAsStringAsync()), StringComparison.Ordinal);
        Assert.Equal(1, await account.ScalarAsync<int>("SELECT COUNT (*) FROM auth.mfaRecoveryCode WHERE appUserId = @id AND usedUtc IS NOT NULL"));

        // The per-account limit: a fifth link within the hour is not sent, and the page still says the same thing.
        int Links() => mail.Messages.Count(m => m.Subject == "Reset your MERIT password");
        int sent = Links();
        await ForgotAsync(visitor, account.Name);
        await Task.Delay(1000);
        Assert.Equal(sent, Links());
        Assert.Equal(4, await account.ScalarAsync<int>("SELECT COUNT (*) FROM auth.passwordReset WHERE appUserId = @id"));

        // An account with no authenticator gets no link at all: email alone would be the only factor.
        await ForgotAsync(visitor, noAuthenticator.Name);
        await Task.Delay(1000);
        Assert.DoesNotContain(mail.Messages, m => m.To == noAuthenticator.Email);
        Assert.Equal(0, await noAuthenticator.ScalarAsync<int>("SELECT COUNT (*) FROM auth.passwordReset WHERE appUserId = @id"));
    }

    [SqlFact]
    public async Task ChangingYourPasswordNeedsTheCurrentOneAndTheAuthenticatorAndEndsOtherSessions()
    {
        var mail = new CapturingEmailSender();
        using var host = Host(mail);
        await using var account = await SyntheticAccount.CreateAsync(host, "pw");
        using var client = new MeritClient(host);
        using var elsewhere = new MeritClient(host);
        await account.SignInAsync(client, MeritFixture.Password);
        await account.SignInAsync(elsewhere, MeritFixture.Password);
        var elsewhereScope = await account.ScalarAsync<Guid>("SELECT TOP (1) sessionScopeId FROM auth.sessionScope WHERE appUserId = @id ORDER BY createdDateUtc DESC");

        string page = await client.Http.GetStringAsync("/account/security");
        Assert.Contains("Change password", page, StringComparison.Ordinal);

        async Task<string> ChangeAsync(string current, string next, string confirm, string code)
        {
            using var response = await client.Http.PostAsync("/account/security/password", MeritClient.Form(page,
                ("CurrentPassword", current), ("NewPassword", next), ("ConfirmPassword", confirm), ("PasswordAuthenticatorCode", code)));
            return Decode(await response.Content.ReadAsStringAsync());
        }

        Assert.Contains("That is not your current password.", await ChangeAsync("Not-The-Password-9", NewPassword, NewPassword, account.TotpCode()), StringComparison.Ordinal);
        Assert.Equal(1, await account.ScalarAsync<int>("SELECT failedSignInCount FROM auth.appUser WHERE appUserId = @id"));
        Assert.Contains("That code did not match", await ChangeAsync(MeritFixture.Password, NewPassword, NewPassword, "000000"), StringComparison.Ordinal);
        // A legacy 13-character credential remains valid for sign-in, but is too short to choose again.
        Assert.Contains("Use at least 15 characters.", await ChangeAsync(MeritFixture.Password, MeritFixture.Password, MeritFixture.Password, account.TotpCode()), StringComparison.Ordinal);
        Assert.Contains("breached passwords", await ChangeAsync(MeritFixture.Password, "12345678", "12345678", account.TotpCode()), StringComparison.Ordinal);
        await account.ScalarAsync<int>("UPDATE auth.appUser SET failedSignInCount = 0 WHERE appUserId = @id; SELECT 0");

        string changed = await ChangeAsync(MeritFixture.Password, NewPassword, NewPassword, account.TotpCode());
        Assert.Contains("Your password was changed. Your other MERIT sessions were signed out.", changed, StringComparison.Ordinal);
        Assert.Contains("Choose a password you are not using now.", await ChangeAsync(NewPassword, NewPassword, NewPassword, account.TotpCode()), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await client.NoRedirect.GetAsync("/api/v1/me")).StatusCode);   // this browser carries on
        Assert.Equal(1, await account.ScalarAsync<int>($"SELECT COUNT (*) FROM auth.sessionScope WHERE sessionScopeId = '{elsewhereScope}' AND revokedUtc IS NOT NULL"));
        Assert.Equal(1, await account.ScalarAsync<int>("SELECT COUNT (*) FROM logs.auditEvent WHERE appUserId = @id AND eventAction = 'passwordChanged'"));
        Assert.Equal("Your MERIT password was changed", (await mail.WaitForAsync(1)).Subject);

        using var fresh = new MeritClient(host);
        Assert.Equal("/account/mfa", await PasswordStepAsync(fresh, account.Name, NewPassword));
    }

    [SqlFact]
    public async Task AnAdministratorCanRequireANewPasswordAtNextSignIn()
    {
        using var host = Host(new CapturingEmailSender());
        await using var account = await SyntheticAccount.CreateAsync(host, "pw");
        var admin = (await fixture.SignedInAsync("admin")).On(host);
        const string adminSet = "Admin-Set-Temporary-77";

        string form = await admin.Http.GetStringAsync($"/admin/users/{account.Id}");
        Assert.Contains("Ask them to choose a new password at their next sign-in", form, StringComparison.Ordinal);
        string home = (await account.ScalarAsync<Guid>("SELECT homeTenantId FROM auth.appUser WHERE appUserId = @id")).ToString();
        using var saved = await admin.NoRedirect.PostAsync("/admin/users", MeritClient.Form(form,
            ("AppUserId", account.Id.ToString()), ("UserName", account.Name), ("DisplayName", "Password test"), ("Email", account.Email),
            ("IsActive", "true"), ("HomeTenantId", home), ("NewPassword", adminSet), ("RequirePasswordChange", "true")));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Equal(1, await account.ScalarAsync<int>("SELECT COUNT (*) FROM auth.appUser WHERE appUserId = @id AND passwordChangeRequired = 1"));
        Assert.Equal(1, await account.ScalarAsync<int>("""
            SELECT COUNT (*) FROM logs.auditEvent WHERE entityId = @id AND eventAction = 'passwordSetByAdmin'
               AND appUserId = (SELECT appUserId FROM auth.appUser WHERE userName = N'admin')
            """));

        // After the password and the code, every page leads to the new-password step; the API refuses meanwhile.
        using var person = new MeritClient(host);
        await account.SignInAsync(person, adminSet, "/cases");
        using var home1 = await person.Http.GetAsync("/");
        Assert.Equal("/account/password/required", home1.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpStatusCode.Conflict, (await person.NoRedirect.GetAsync("/api/v1/me")).StatusCode);
        string step = await home1.Content.ReadAsStringAsync();

        using var same = await person.Http.PostAsync("/account/password/required", MeritClient.Form(step,
            ("NewPassword", adminSet), ("ConfirmPassword", adminSet), ("ReturnUrl", "/")));
        Assert.Contains("Choose a password you are not using now.", Decode(await same.Content.ReadAsStringAsync()), StringComparison.Ordinal);

        using var chosen = await person.NoRedirect.PostAsync("/account/password/required", MeritClient.Form(step,
            ("NewPassword", NewPassword), ("ConfirmPassword", NewPassword), ("ReturnUrl", "/")));
        Assert.Equal("/", chosen.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await person.NoRedirect.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(1, await account.ScalarAsync<int>("SELECT COUNT (*) FROM auth.appUser WHERE appUserId = @id AND passwordChangeRequired = 0"));
        Assert.Equal(1, await account.ScalarAsync<int>("SELECT COUNT (*) FROM logs.auditEvent WHERE appUserId = @id AND eventAction = 'passwordChanged'"));
    }

    // ---------------------------------------------------------------------------------------------- helpers

    private WebApplicationFactory<Program> Host(CapturingEmailSender mail, CapturingLoggerProvider? logs = null, params (string Key, string Value)[] settings)
        => fixture.Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("EmailCodes:VerifierKey", Key);
            b.UseSetting("Merit:PublicBaseUrl", PublicBase);
            b.UseSetting("Merit:EmailCodeSendsPerMinute", "10000");
            foreach (var (key, value) in settings)
            {
                b.UseSetting(key, value);
            }

            b.ConfigureTestServices(s =>
            {
                s.AddSingleton<IEmailSender>(mail);
                if (logs is not null)
                {
                    s.AddSingleton<ILoggerProvider>(logs);
                }
            });
        });

    private static async Task<string> ForgotAsync(MeritClient client, string userName)
    {
        string page = await client.Http.GetStringAsync("/account/forgot");
        using var response = await client.Http.PostAsync("/account/forgot", MeritClient.Form(page, ("UserName", userName)));
        Assert.Equal("/account/forgot/sent", response.RequestMessage!.RequestUri!.AbsolutePath);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>The password step alone: where it leads (the MFA page, or back to sign-in on failure).</summary>
    private static async Task<string> PasswordStepAsync(MeritClient client, string userName, string password)
    {
        string page = await client.Http.GetStringAsync("/account/signin");
        using var response = await client.Http.PostAsync("/account/signin", MeritClient.Form(page, ("UserName", userName), ("Password", password)));
        return response.RequestMessage!.RequestUri!.AbsolutePath;
    }

    private static (string Id, string Token) Link(OutgoingEmail message)
    {
        var m = LinkPattern().Match(message.TextBody);
        Assert.True(m.Success, "No reset link at " + PublicBase + " in the message.");
        return (m.Groups[1].Value, m.Groups[2].Value);
    }

    private static string Card(string html) => Regex.Match(html, "<section class=\"merit-signin__card\".*?</section>", RegexOptions.Singleline).Value;

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    [GeneratedRegex(@"https://merit\.example\.test/account/reset\?r=([0-9a-f]{32})&t=([A-Za-z0-9_-]{43})")]
    private static partial Regex LinkPattern();
}
