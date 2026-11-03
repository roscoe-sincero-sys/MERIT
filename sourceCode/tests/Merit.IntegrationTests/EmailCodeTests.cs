using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Merit.Application.Email;
using Merit.Application.Security;
using Merit.Application.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Merit.IntegrationTests;

/// <summary>
/// Email one-time codes as an additional second factor: offered only where the jurisdiction allows them and never to
/// administrators; a code works once, before it expires, for its own purpose and account; a newer code supersedes it;
/// cooldowns, limits and attempt counts hold under concurrency; a failed send closes the code and shows a support
/// reference; and neither codes nor addresses reach the log. Every account here is synthetic, with an address at
/// example.test (RFC 2606), and mail goes to an in-memory sender, never to a mail server.
/// </summary>
[Collection(MeritCollection.Name)]
public sealed partial class EmailCodeTests(MeritFixture fixture)
{
    private static readonly string Key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    [SqlFact]
    public async Task EmailCodesAreOfferedOnlyWhereAllowedAndNeverToAdministrators()
    {
        var mail = new CapturingEmailSender();
        using var host = Host(mail);
        await using var plain = await EmailAccount.CreateAsync(host, turnedOn: true);
        await using var administrator = await EmailAccount.CreateAsync(host, turnedOn: true, administrator: true);

        // The jurisdiction has not allowed them (the shipped default).
        await using (await Policy.SetAsync(false))
        {
            using var client = new MeritClient(host);
            Assert.DoesNotContain("Email me a code", await PasswordAsync(client, plain), StringComparison.Ordinal);
            using var refused = await client.NoRedirect.PostAsync("/account/mfa/email/send", MeritClient.Form(await client.Http.GetStringAsync("/account/mfa")));
            Assert.Equal("/account/mfa", refused.Headers.Location?.OriginalString);
        }

        await using (await Policy.SetAsync(true))
        {
            using var client = new MeritClient(host);
            string mfa = Decode(await PasswordAsync(client, plain));
            Assert.Contains("Email me a code", mfa, StringComparison.Ordinal);
            Assert.Contains(plain.MaskedEmail, mfa, StringComparison.Ordinal);
            Assert.DoesNotContain(plain.Email, mfa, StringComparison.Ordinal);

            // An administrator needs phishing-resistant MFA: not offered, and asking anyway sends nothing.
            using var admin = new MeritClient(host);
            string adminMfa = await PasswordAsync(admin, administrator);
            Assert.DoesNotContain("Email me a code", adminMfa, StringComparison.Ordinal);
            using var refused = await admin.NoRedirect.PostAsync("/account/mfa/email/send", MeritClient.Form(adminMfa));
            Assert.Equal("/account/mfa", refused.Headers.Location?.OriginalString);
            Assert.Equal(0, await administrator.ScalarAsync<int>("SELECT COUNT (*) FROM auth.mfaEmailChallenge WHERE appUserId = @id"));

            // No mail server configured: the option stays hidden even where it is allowed.
            using var unconfigured = fixture.Factory.WithWebHostBuilder(b => b.UseSetting("EmailCodes:VerifierKey", Key));
            using var offline = new MeritClient(unconfigured);
            Assert.DoesNotContain("Email me a code", await PasswordAsync(offline, plain), StringComparison.Ordinal);
        }

        Assert.Empty(mail.Messages);
    }

    [SqlFact]
    public async Task ASignInCodeWorksOnceForItsAccountAndOnlyBeforeItExpires()
    {
        var mail = new CapturingEmailSender();
        var logs = new CapturingLoggerProvider();
        using var host = Host(mail, logs, ("EmailCodes:ResendCooldownSeconds", "0"));
        await using var account = await EmailAccount.CreateAsync(host, turnedOn: true);
        await using var policy = await Policy.SetAsync(true);

        using var client = new MeritClient(host);
        var (page, code) = await SendAsync(client, account, mail);
        Assert.Contains($"We sent a six-digit code to <strong>{account.MaskedEmail}</strong>. It expires in 5 minutes.", Decode(page), StringComparison.Ordinal);
        var message = mail.Messages.Last();
        Assert.Equal(account.Email, message.To);
        Assert.Equal("Your MERIT verification code", message.Subject);
        Assert.Matches("^EM-[A-Z2-9]{8}$", message.Reference);

        // A wrong code counts an attempt and toward the account lockout, and says how many are left.
        using var wrong = await client.NoRedirect.PostAsync("/account/mfa/email", MeritClient.Form(page, ("Code", Other(code))));
        Assert.Contains("You can try 4 more times", Decode(await wrong.Content.ReadAsStringAsync()), StringComparison.Ordinal);
        Assert.Equal(1, await account.ScalarAsync<int>("SELECT failedSignInCount FROM auth.appUser WHERE appUserId = @id"));

        // Spaces are fine; the right code signs in and resets the failure count.
        using var right = await client.NoRedirect.PostAsync("/account/mfa/email", MeritClient.Form(page, ("Code", code[..3] + " " + code[3..])));
        Assert.Equal(HttpStatusCode.Redirect, right.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.NoRedirect.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(0, await account.ScalarAsync<int>("SELECT failedSignInCount FROM auth.appUser WHERE appUserId = @id"));

        // Replay: the same code at the next sign-in is refused.
        using var again = new MeritClient(host);
        string mfa = await PasswordAsync(again, account);
        using var replay = await again.NoRedirect.PostAsync("/account/mfa/email", MeritClient.Form(mfa, ("Code", code)));
        Assert.Contains("can no longer be used", Decode(await replay.Content.ReadAsStringAsync()), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, (await again.NoRedirect.GetAsync("/api/v1/me")).StatusCode);

        // Expiry: a code past its five minutes is refused even though it was never used.
        var (page2, code2) = await SendAsync(again, account, mail, signIn: false);
        await account.ScalarAsync<int>("UPDATE auth.mfaEmailChallenge SET expiresUtc = DATEADD (SECOND, -1, SYSUTCDATETIME ()) WHERE appUserId = @id AND consumedUtc IS NULL AND supersededUtc IS NULL; SELECT 0");
        using var late = await again.NoRedirect.PostAsync("/account/mfa/email", MeritClient.Form(page2, ("Code", code2)));
        Assert.Contains("That code has expired. Send a new code.", Decode(await late.Content.ReadAsStringAsync()), StringComparison.Ordinal);

        // Nothing secret in the log: not the codes, not the address. The support reference is there.
        Assert.DoesNotContain(logs.Lines, l => l.Contains(code, StringComparison.Ordinal) || l.Contains(code2, StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Lines, l => l.Contains(account.Email, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logs.Lines, l => l.Contains(message.Reference, StringComparison.Ordinal) && l.Contains("submitted", StringComparison.Ordinal));
        Assert.True(await account.ScalarAsync<int>("SELECT COUNT (*) FROM logs.auditEvent WHERE appUserId = @id AND eventAction = 'mfaCodeSent'") >= 2);
    }

    [SqlFact]
    public async Task ANewCodeSupersedesTheOldOneAndCooldownLimitsAndAttemptsHold()
    {
        var mail = new CapturingEmailSender();
        using var quick = Host(mail, null, ("EmailCodes:ResendCooldownSeconds", "0"), ("EmailCodes:MaxCodesPerAccount", "3"),
            ("EmailCodes:MaxAttempts", "3"));
        await using var account = await EmailAccount.CreateAsync(quick, turnedOn: true);
        await using var policy = await Policy.SetAsync(true);

        // Superseded: only the newest code works.
        using var client = new MeritClient(quick);
        var (_, first) = await SendAsync(client, account, mail);
        var (page, second) = await SendAsync(client, account, mail, signIn: false);
        using var old = await client.NoRedirect.PostAsync("/account/mfa/email", MeritClient.Form(page, ("Code", first)));
        Assert.Contains("That code was replaced by a newer one.", Decode(await old.Content.ReadAsStringAsync()), StringComparison.Ordinal);
        Assert.Equal(0, await account.ScalarAsync<int>("SELECT failedAttempts FROM auth.mfaEmailChallenge WHERE appUserId = @id AND supersededUtc IS NULL"));

        // Attempts: after three wrong entries the code stops working, even the right one.
        for (int i = 0; i < 3; i++)
        {
            using var miss = await client.NoRedirect.PostAsync("/account/mfa/email", MeritClient.Form(page, ("Code", Other(second))));
            Assert.Equal(HttpStatusCode.OK, miss.StatusCode);
        }

        using var locked = await client.NoRedirect.PostAsync("/account/mfa/email", MeritClient.Form(page, ("Code", second)));
        Assert.Contains("entered wrong too many times", Decode(await locked.Content.ReadAsStringAsync()), StringComparison.Ordinal);
        Assert.Equal(1, await account.ScalarAsync<int>("SELECT COUNT (*) FROM logs.auditEvent WHERE appUserId = @id AND eventAction = 'mfaCodeLocked'"));
        await account.ScalarAsync<int>("UPDATE auth.appUser SET failedSignInCount = 0, lockoutEndUtc = NULL WHERE appUserId = @id; SELECT 0");

        // Limit: three codes in the window for this account, then a wait with the authenticator offered instead.
        await SendAsync(client, account, mail, signIn: false);
        using var limited = await client.Http.PostAsync("/account/mfa/email/send", MeritClient.Form(page));
        Assert.Contains("Too many codes have been sent", Decode(await limited.Content.ReadAsStringAsync()), StringComparison.Ordinal);
        Assert.Equal(3, await account.ScalarAsync<int>("SELECT COUNT (*) FROM auth.mfaEmailChallenge WHERE appUserId = @id"));
        Assert.Equal(1, await account.ScalarAsync<int>("SELECT COUNT (*) FROM auth.mfaEmailChallenge WHERE appUserId = @id AND consumedUtc IS NULL AND supersededUtc IS NULL"));

        // Cooldown (the default 30 seconds) on a fresh account: the second request in a row sends nothing new.
        var slowMail = new CapturingEmailSender();
        using var normal = Host(slowMail);
        await using var other = await EmailAccount.CreateAsync(normal, turnedOn: true);
        using var person = new MeritClient(normal);
        var (codePage, _) = await SendAsync(person, other, slowMail);
        using var soon = await person.Http.PostAsync("/account/mfa/email/send", MeritClient.Form(codePage));
        string soonPage = Decode(await soon.Content.ReadAsStringAsync());
        Assert.Contains("A code was sent a moment ago", soonPage, StringComparison.Ordinal);
        int wait = int.Parse(Regex.Match(soonPage, "data-resend-in=\"([0-9]+)\"").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(wait, 20, 31);
        Assert.Single(slowMail.Messages);
    }

    [SqlFact]
    public async Task ConcurrentRequestsSendOneCodeAndConcurrentChecksAcceptItOnce()
    {
        var mail = new CapturingEmailSender { Delay = TimeSpan.FromMilliseconds(200) };
        using var host = Host(mail);
        await using var account = await EmailAccount.CreateAsync(host, turnedOn: true);
        await using var policy = await Policy.SetAsync(true);

        // A double click, two tabs: eight sends at once. The database serializes them; one code goes out.
        using var client = new MeritClient(host);
        string mfa = await PasswordAsync(client, account);
        var sends = Enumerable.Range(0, 8).Select(_ => client.NoRedirect.PostAsync("/account/mfa/email/send", MeritClient.Form(mfa))).ToArray();
        foreach (var response in await Task.WhenAll(sends))
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            response.Dispose();
        }

        Assert.Single(mail.Messages);
        Assert.Equal(1, await account.ScalarAsync<int>("SELECT COUNT (*) FROM auth.mfaEmailChallenge WHERE appUserId = @id"));

        // The same code submitted twice at the same moment: exactly one check succeeds.
        string code = CodeIn(mail.Messages.Single());
        var checks = Enumerable.Range(0, 4).Select(async _ =>
        {
            using var scope = host.Services.CreateScope();
            var codes = scope.ServiceProvider.GetRequiredService<EmailCodeService>();
            return (await codes.VerifySignInAsync(account.Id, account.Name, code, "203.0.113.9")).Outcome;
        }).ToArray();
        var outcomes = await Task.WhenAll(checks);
        Assert.Equal(1, outcomes.Count(o => o == EmailCodeCheck.Verified));
        Assert.All(outcomes.Where(o => o != EmailCodeCheck.Verified), o => Assert.Equal(EmailCodeCheck.Used, o));
    }

    [SqlFact]
    public async Task AFailedSendClosesTheCodeShowsASupportReferenceAndCanBeRetriedAtOnce()
    {
        var mail = new CapturingEmailSender { Fail = EmailFailureKind.Timeout };
        var logs = new CapturingLoggerProvider();
        using var host = Host(mail, logs);
        await using var account = await EmailAccount.CreateAsync(host, turnedOn: true);
        await using var policy = await Policy.SetAsync(true);

        using var client = new MeritClient(host);
        string mfa = await PasswordAsync(client, account);
        using var failed = await client.Http.PostAsync("/account/mfa/email/send", MeritClient.Form(mfa));
        string page = Decode(await failed.Content.ReadAsStringAsync());
        Assert.Contains("We couldn't send your verification code. Please try again. If the problem continues, contact support.", page, StringComparison.Ordinal);
        string reference = SupportReference().Match(page).Groups[1].Value;
        Assert.Matches("^EM-[A-Z2-9]{8}$", reference);
        // No server detail on the page.
        Assert.DoesNotContain("SMTP", page, StringComparison.Ordinal);

        using (var c = new SqlConnection(TestEnvironment.AdminConnectionString()))
        {
            await c.OpenAsync();
            await using var cmd = new SqlCommand("""
                SELECT deliveryStatus, failureKind, deliveryAttempts, CASE WHEN supersededUtc IS NULL THEN 0 ELSE 1 END
                  FROM auth.mfaEmailChallenge WHERE supportReference = @ref
                """, c);
            cmd.Parameters.AddWithValue("@ref", reference);
            await using var r = await cmd.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync());
            Assert.Equal("failed", r.GetString(0));
            Assert.Equal("timeout", r.GetString(1));
            Assert.Equal(3, r.GetInt32(2));
            Assert.Equal(1, r.GetInt32(3));
        }

        Assert.Contains(logs.Lines, l => l.Contains(reference, StringComparison.Ordinal) && l.Contains("Timeout", StringComparison.Ordinal));
        Assert.Equal(1, await account.ScalarAsync<int>("SELECT COUNT (*) FROM logs.auditEvent WHERE appUserId = @id AND eventAction = 'mfaCodeNotSent'"));

        // The mail server recovers. The failed code never works, and trying again needs no wait.
        string lost = CodeIn(mail.Messages.Single());
        mail.Fail = null;
        using var retry = await client.Http.PostAsync("/account/mfa/email/send", MeritClient.Form(page));
        string retried = Decode(await retry.Content.ReadAsStringAsync());
        Assert.Contains("We sent a six-digit code", retried, StringComparison.Ordinal);
        Assert.Equal(2, mail.Messages.Count);
        string fresh = CodeIn(mail.Messages.Last());
        if (fresh != lost)
        {
            using var stale = await client.NoRedirect.PostAsync("/account/mfa/email", MeritClient.Form(retried, ("Code", lost)));
            Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        }

        using var ok = await client.NoRedirect.PostAsync("/account/mfa/email", MeritClient.Form(retried, ("Code", fresh)));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
    }

    [SqlFact]
    public async Task TurningOnEmailCodesNeedsTheAuthenticatorAndAConfirmedCode()
    {
        var mail = new CapturingEmailSender();
        using var host = Host(mail, null, ("EmailCodes:ResendCooldownSeconds", "0"));
        await using var account = await EmailAccount.CreateAsync(host, turnedOn: false);
        await using var policy = await Policy.SetAsync(true);

        using var client = new MeritClient(host);
        string mfa = await PasswordAsync(client, account);
        Assert.DoesNotContain("Email me a code", mfa, StringComparison.Ordinal);
        using (var signedIn = await client.NoRedirect.PostAsync("/account/mfa", MeritClient.Form(mfa, ("Code", account.TotpCode()))))
        {
            Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        }

        string methods = Decode(await client.Http.GetStringAsync("/account/security"));
        Assert.Contains("NIST SP 800-63B-4 does not accept email", methods, StringComparison.Ordinal);
        Assert.Contains($"Send a code to {account.MaskedEmail}", methods, StringComparison.Ordinal);

        // A session alone is not enough: a wrong authenticator code sends nothing.
        using var noStepUp = await client.Http.PostAsync("/account/security/email/start", MeritClient.Form(methods, ("AuthenticatorCode", "000000")));
        Assert.Contains("That code did not match", Decode(await noStepUp.Content.ReadAsStringAsync()), StringComparison.Ordinal);
        Assert.Empty(mail.Messages);
        await account.ScalarAsync<int>("UPDATE auth.appUser SET failedSignInCount = 0 WHERE appUserId = @id; SELECT 0");

        using var started = await client.Http.PostAsync("/account/security/email/start", MeritClient.Form(methods, ("AuthenticatorCode", account.TotpCode())));
        string confirm = Decode(await started.Content.ReadAsStringAsync());
        Assert.Equal("/account/security/email", started.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Contains("Confirm your email address", confirm, StringComparison.Ordinal);
        string enrollCode = CodeIn(mail.Messages.Single());
        Assert.Contains("Sign-in methods page", mail.Messages.Single().TextBody, StringComparison.Ordinal);

        using var wrong = await client.NoRedirect.PostAsync("/account/security/email/confirm", MeritClient.Form(confirm, ("Code", Other(enrollCode))));
        Assert.Contains("did not match", Decode(await wrong.Content.ReadAsStringAsync()), StringComparison.Ordinal);
        Assert.Equal(0, await account.ScalarAsync<int>("SELECT COUNT (*) FROM auth.appUser WHERE appUserId = @id AND mfaEmailVerifiedUtc IS NOT NULL"));

        using var confirmed = await client.Http.PostAsync("/account/security/email/confirm", MeritClient.Form(confirm, ("Code", enrollCode)));
        string on = Decode(await confirmed.Content.ReadAsStringAsync());
        Assert.Contains("Email codes are on.", on, StringComparison.Ordinal);
        Assert.Equal(1, await account.ScalarAsync<int>("SELECT COUNT (*) FROM auth.appUser WHERE appUserId = @id AND mfaEmailVerifiedUtc IS NOT NULL AND mfaEnabled = 1"));
        Assert.Equal(1, await account.ScalarAsync<int>("SELECT COUNT (*) FROM logs.auditEvent WHERE appUserId = @id AND eventAction = 'mfaEmailEnabled'"));

        // Preferring email puts it first at the next sign-in.
        using (await client.Http.PostAsync("/account/security/preference", MeritClient.Form(on, ("preferredMethod", "email"))))
        {
        }

        using var next = new MeritClient(host);
        string nextMfa = await PasswordAsync(next, account);
        int emailAt = nextMfa.IndexOf("Get a code by email", StringComparison.Ordinal);
        Assert.True(emailAt >= 0 && emailAt < nextMfa.IndexOf("Or enter a code from your authenticator app", StringComparison.Ordinal));

        // A code for one purpose never works for the other: an enroll-style verifier does not sign in.
        var (codePage, signInCode) = await SendAsync(next, account, mail, signIn: false);
        if (enrollCode != signInCode)
        {
            using var crossed = await next.NoRedirect.PostAsync("/account/mfa/email", MeritClient.Form(codePage, ("Code", enrollCode)));
            Assert.Equal(HttpStatusCode.OK, crossed.StatusCode);
        }

        // Turning them off is one step and leaves the authenticator as the only way in.
        string current = await client.Http.GetStringAsync("/account/security");
        using (await client.Http.PostAsync("/account/security/email/off", MeritClient.Form(current)))
        {
        }

        Assert.Equal(1, await account.ScalarAsync<int>("SELECT COUNT (*) FROM auth.appUser WHERE appUserId = @id AND mfaEmailVerifiedUtc IS NULL AND mfaPreferredMethod = 'totp'"));
        Assert.Equal(0, await account.ScalarAsync<int>("SELECT COUNT (*) FROM auth.mfaEmailChallenge WHERE appUserId = @id AND consumedUtc IS NULL AND supersededUtc IS NULL"));
    }

    [SqlFact]
    public async Task AnEmailChangeOrMfaResetTurnsEmailCodesOff()
    {
        using var host = Host(new CapturingEmailSender());
        await using var account = await EmailAccount.CreateAsync(host, turnedOn: true);
        var admin = await fixture.SignedInAsync("admin");

        string form = await admin.Http.GetStringAsync($"/admin/users/{account.Id}");
        string home = (await account.ScalarAsync<Guid>("SELECT homeTenantId FROM auth.appUser WHERE appUserId = @id")).ToString();
        using var saved = await admin.NoRedirect.PostAsync("/admin/users", MeritClient.Form(form,
            ("AppUserId", account.Id.ToString()), ("UserName", account.Name), ("DisplayName", "Email code test"),
            ("Email", "changed-" + account.Email), ("IsActive", "true"), ("HomeTenantId", home)));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Equal(1, await account.ScalarAsync<int>("SELECT COUNT (*) FROM auth.appUser WHERE appUserId = @id AND mfaEmailVerifiedUtc IS NULL"));
        Assert.Equal(1, await account.ScalarAsync<int>("SELECT COUNT (*) FROM logs.auditEvent WHERE appUserId = @id AND eventAction = 'mfaEmailDisabled'"));

        await account.ScalarAsync<int>("UPDATE auth.appUser SET mfaEmailVerifiedUtc = SYSUTCDATETIME (), mfaPreferredMethod = 'email' WHERE appUserId = @id; SELECT 0");
        string page = await admin.Http.GetStringAsync($"/admin/users/{account.Id}");
        using var reset = await admin.NoRedirect.PostAsync($"/admin/users/{account.Id}/reset-mfa", MeritClient.Form(page));
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        Assert.Equal(1, await account.ScalarAsync<int>("SELECT COUNT (*) FROM auth.appUser WHERE appUserId = @id AND mfaEmailVerifiedUtc IS NULL AND mfaPreferredMethod = 'totp' AND mfaEnabled = 0"));
    }

    [SqlFact]
    public async Task TheSettingsScreenStatesNistsPositionBeforeEmailCodesCanBeTurnedOn()
    {
        var admin = await fixture.SignedInAsync("admin");
        await using var policy = await Policy.SetAsync(false);
        string settings = Decode(await admin.Http.GetStringAsync("/config/settings"));
        Assert.Contains("name=\"acknowledgeEmailCodeRisk\"", settings, StringComparison.Ordinal);
        Assert.Contains("NIST SP 800-63B-4 does not accept email for out-of-band authentication", settings, StringComparison.Ordinal);

        using var refused = await admin.NoRedirect.PostAsync("/config/settings/emailCodesEnabled", MeritClient.Form(settings, ("settingValue", "true")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("false", await Policy.ValueAsync());

        using var accepted = await admin.NoRedirect.PostAsync("/config/settings/emailCodesEnabled",
            MeritClient.Form(settings, ("settingValue", "true"), ("acknowledgeEmailCodeRisk", "true")));
        Assert.True(accepted.IsSuccessStatusCode, accepted.StatusCode.ToString());
        Assert.Equal("true", await Policy.ValueAsync());

        using var off = await admin.NoRedirect.PostAsync("/config/settings/emailCodesEnabled", MeritClient.Form(settings, ("settingValue", "false")));
        Assert.True(off.IsSuccessStatusCode, off.StatusCode.ToString());
        Assert.Equal("false", await Policy.ValueAsync());
    }

    // ---------------------------------------------------------------------------------------------- helpers

    private WebApplicationFactory<Program> Host(CapturingEmailSender mail, CapturingLoggerProvider? logs = null, params (string Key, string Value)[] settings)
        => fixture.Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("EmailCodes:VerifierKey", Key);
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

    private static async Task<string> PasswordAsync(MeritClient client, EmailAccount account)
    {
        var page = await client.Http.GetStringAsync("/account/signin");
        using var response = await client.NoRedirect.PostAsync("/account/signin",
            MeritClient.Form(page, ("UserName", account.Name), ("Password", MeritFixture.Password)));
        Assert.Equal("/account/mfa", response.Headers.Location?.OriginalString);
        return await client.Http.GetStringAsync("/account/mfa");
    }

    /// <summary>Signs in with the password (unless already at the code step), asks for a code, and returns the code page and the code.</summary>
    private static async Task<(string Page, string Code)> SendAsync(MeritClient client, EmailAccount account, CapturingEmailSender mail, bool signIn = true)
    {
        string from = signIn ? await PasswordAsync(client, account) : await client.Http.GetStringAsync("/account/mfa");
        int before = mail.Messages.Count;
        using var sent = await client.Http.PostAsync("/account/mfa/email/send", MeritClient.Form(from));
        Assert.Equal("/account/mfa/email", sent.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Equal(before + 1, mail.Messages.Count);
        return (await sent.Content.ReadAsStringAsync(), CodeIn(mail.Messages.Last()));
    }

    private static string CodeIn(OutgoingEmail message) => CodePattern().Match(message.TextBody).Groups[1].Value;

    /// <summary>A six-digit code that is not <paramref name="code"/>.</summary>
    private static string Other(string code) => ((int.Parse(code, System.Globalization.CultureInfo.InvariantCulture) + 1) % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    [GeneratedRegex(@"verification code is (\d{6})")]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"data-support-reference>(EM-[A-Z2-9]{8})<")]
    private static partial Regex SupportReference();

    /// <summary>The demo state's emailCodesEnabled setting, restored to its shipped value (false) afterwards.</summary>
    private sealed class Policy : IAsyncDisposable
    {
        private const string InForce = """
            FROM config.jurisdictionSetting
             WHERE settingKey = 'emailCodesEnabled' AND isDeleted = 0 AND effectiveTo IS NULL
               AND rootTenantId = (SELECT rootTenantId FROM auth.appUser WHERE userName = N'admin')
            """;

        public static async Task<Policy> SetAsync(bool on)
        {
            await ExecAsync($"UPDATE config.jurisdictionSetting SET settingValue = @v WHERE jurisdictionSettingId IN (SELECT jurisdictionSettingId {InForce})", on ? "true" : "false");
            return new Policy();
        }

        public static async Task<string> ValueAsync()
        {
            await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
            await c.OpenAsync();
            await using var cmd = new SqlCommand($"SELECT TOP (1) settingValue {InForce} ORDER BY effectiveFrom DESC", c);
            return (string)(await cmd.ExecuteScalarAsync())!;
        }

        public async ValueTask DisposeAsync() => await ExecAsync($"UPDATE config.jurisdictionSetting SET settingValue = @v WHERE jurisdictionSettingId IN (SELECT jurisdictionSettingId {InForce})", "false");

        private static async Task ExecAsync(string sql, string value)
        {
            await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
            await c.OpenAsync();
            await using var cmd = new SqlCommand(sql, c);
            cmd.Parameters.AddWithValue("@v", value);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// A synthetic account in the demo state, with the demo password, an authenticator secret this test knows and an
    /// address at example.test. No assignments unless it is to be an administrator.
    /// </summary>
    private sealed class EmailAccount : IAsyncDisposable
    {
        private readonly string secret = Totp.ToBase32(Totp.NewSecret());

        public Guid Id { get; } = Guid.NewGuid();

        public string Name => "otp-" + Id.ToString("N")[..20];

        public string Email => $"{Name}@example.test";

        public string MaskedEmail => "o***@example.test";

        public string TotpCode() => Totp.Code(Totp.FromBase32(secret), DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);

        public static async Task<EmailAccount> CreateAsync(WebApplicationFactory<Program> host, bool turnedOn, bool administrator = false)
        {
            var account = new EmailAccount();
            string protectedSecret = host.Services.GetRequiredService<ISecretProtector>().Protect(account.secret);
            await account.ScalarAsync<int>("""
                INSERT auth.appUser (appUserId, rootTenantId, homeTenantId, userName, displayName, email, passwordHash, securityStamp,
                                     mfaSecretProtected, mfaEnabled, mfaEmailVerifiedUtc)
                SELECT @id, rootTenantId, homeTenantId, @name, N'Email code test', @email, passwordHash, NEWID (), @secret, 1,
                       CASE WHEN @on = 1 THEN SYSUTCDATETIME () END
                  FROM auth.appUser WHERE userName = N'admin';
                IF @admin = 1
                    INSERT auth.userAssignment (userAssignmentId, appUserId, roleId, tenantId, includeDescendants, accessLevel, startDate)
                    SELECT TOP (1) NEWID (), @id, ua.roleId, ua.tenantId, ua.includeDescendants, ua.accessLevel, ua.startDate
                      FROM auth.userAssignment AS ua JOIN auth.role AS r ON r.roleId = ua.roleId
                     WHERE ua.appUserId = (SELECT appUserId FROM auth.appUser WHERE userName = N'admin') AND r.isAdministrator = 1 AND ua.isDeleted = 0;
                SELECT 0;
                """, ("@email", account.Email), ("@secret", protectedSecret), ("@on", turnedOn), ("@admin", administrator));
            return account;
        }

        public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] args)
        {
            await using var connection = new SqlConnection(TestEnvironment.AdminConnectionString());
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@id", Id);
            command.Parameters.AddWithValue("@name", Name);
            foreach (var (name, value) in args)
            {
                command.Parameters.AddWithValue(name, value);
            }

            return (T)(await command.ExecuteScalarAsync())!;
        }

        public async ValueTask DisposeAsync() => await ScalarAsync<int>("""
            DECLARE @scopes TABLE (id UNIQUEIDENTIFIER); INSERT @scopes SELECT sessionScopeId FROM auth.sessionScope WHERE appUserId = @id;
            DELETE auth.sessionScopePermission WHERE sessionScopeId IN (SELECT id FROM @scopes);
            DELETE auth.sessionScopeAdminTenant WHERE sessionScopeId IN (SELECT id FROM @scopes);
            DELETE auth.sessionScopeRoot WHERE sessionScopeId IN (SELECT id FROM @scopes);
            DELETE auth.sessionScopeTenant WHERE sessionScopeId IN (SELECT id FROM @scopes);
            DELETE auth.sessionScope WHERE appUserId = @id;
            DELETE auth.mfaEmailChallenge WHERE appUserId = @id;
            DELETE auth.mfaRecoveryCode WHERE appUserId = @id;
            DELETE auth.userAssignment WHERE appUserId = @id;
            DELETE auth.userProfile WHERE appUserId = @id;
            DELETE auth.appUser WHERE appUserId = @id;
            SELECT 0;
            """);
    }
}

/// <summary>An in-memory mail server: keeps every message (even one it pretends could not be sent) and can fail or be slow on demand.</summary>
public sealed class CapturingEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<OutgoingEmail> messages = new();

    public IReadOnlyCollection<OutgoingEmail> Messages => messages.ToArray();

    public EmailFailureKind? Fail { get; set; }

    public TimeSpan Delay { get; set; }

    public bool IsConfigured => true;

    public async Task<EmailSendResult> SendAsync(OutgoingEmail message, CancellationToken ct = default)
    {
        messages.Enqueue(message);
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, ct);
        }

        return Fail is { } kind ? EmailSendResult.Failed(kind, false, 3, null, 25_000) : EmailSendResult.Accepted(1, 5);
    }
}

/// <summary>Every log line the application writes, message and structured values, for checking what never appears there.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> lines = new();

    public IReadOnlyCollection<string> Lines => lines.ToArray();

    public ILogger CreateLogger(string categoryName) => new Logger(lines);

    public void Dispose()
    {
    }

    private sealed class Logger(ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string values = state is IEnumerable<KeyValuePair<string, object?>> pairs ? string.Join(" ", pairs.Select(p => $"{p.Key}={p.Value}")) : string.Empty;
            lines.Enqueue($"{formatter(state, exception)} | {values} | {exception}");
        }
    }
}
