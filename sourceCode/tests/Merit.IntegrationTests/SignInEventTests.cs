using System.Net;
using Merit.Application.Email;
using Merit.Application.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Merit.IntegrationTests;

/// <summary>
/// The sign-in event log (#191). Every outcome is recorded with its reason, step and method, the client address, user
/// agent, attempt and session reference (never the scope key), and the trace id. A name that matched no account is kept
/// only hashed and encrypted. Rate-limit refusals, account changes (with the administrator as actor) and the ways a
/// session ends are recorded too. Reports are scoped: tenant administrators see their accounts, and only holders of
/// security.investigate see unknown names, revealed after a current authenticator code, with each reveal, lookup and
/// export audited. Retention strips the personal data. Accounts and addresses are synthetic (example.test, TEST-NET-3).
/// </summary>
[Collection(MeritCollection.Name)]
public sealed class SignInEventTests(MeritFixture fixture)
{
    private static readonly string Key = Convert.ToBase64String(Enumerable.Range(90, 32).Select(i => (byte)i).ToArray());

    [SqlFact]
    public async Task EachStepIsRecordedWithItsAttemptAddressAgentAndSession()
    {
        var logs = new CapturingLoggerProvider();
        using var host = Host(logs: logs);
        await using var account = await SyntheticAccount.CreateAsync(host, "sie");
        string unknown = "MeritAdmin-" + Guid.NewGuid().ToString("N")[..8];

        // A name that matches no account: hashed and encrypted, never stored in plain text.
        using var stranger = Client(host, "203.0.113.10");
        await PasswordAsync(stranger, unknown, "Guess-123-guess");
        var row = await SingleAsync($"SELECT TOP (1) appUserId, attemptedNameHash, attemptedNameProtected, clientAddress, userAgent, traceId, attemptId FROM logs.signInEvent WHERE eventType = 'unknownUser' AND clientAddress = '203.0.113.10' ORDER BY eventUtc DESC");
        Assert.Equal(DBNull.Value, row[0]);
        var names = host.Services.GetRequiredService<IAttemptedNameProtection>();
        Assert.Equal(names.Hash(unknown), (byte[])row[1]);
        Assert.Equal(names.Hash(unknown.ToUpperInvariant() + "  "), (byte[])row[1]);   // the same name however it was typed
        Assert.NotEqual(unknown, (string)row[2]);
        Assert.Equal(unknown, names.Unprotect((string)row[2]));
        Assert.Equal("MeritTest/1.0 (synthetic)", row[4]);
        Assert.NotEqual(DBNull.Value, row[5]);
        Assert.NotEqual(DBNull.Value, row[6]);
        Assert.Equal(0, await ScalarAsync<int>($"SELECT COUNT (*) FROM logs.signInEvent WHERE attemptedNameProtected = N'{unknown}' OR reason = N'{unknown}'"));

        // A full sign-in: one attempt id from the password to the session, which is stored by its reference only.
        using var person = Client(host, "203.0.113.11");
        await PasswordAsync(person, account.Name, "Wrong-password-1");
        string mfa = await PasswordAsync(person, account.Name, MeritFixture.Password);
        using (await person.NoRedirect.PostAsync("/account/mfa", MeritClient.Form(mfa, ("Code", "000000"))))
        {
        }

        string page = await person.Http.GetStringAsync("/account/mfa");
        using (var done = await person.NoRedirect.PostAsync("/account/mfa", MeritClient.Form(page, ("Code", account.TotpCode()))))
        {
            Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
        }

        var events = await EventsAsync(account.Id);
        Assert.Equal(["passwordFailed", "passwordSucceeded", "mfaFailed", "mfaSucceeded", "sessionStarted"], events.Select(e => e.Type).ToArray());
        Assert.Equal("wrongPassword", events[0].Reason);
        Assert.Equal(1, events[0].FailedCount);
        Assert.Equal(("secondFactor", "totp", "wrongCode"), (events[2].Step, events[2].Method, events[2].Reason));
        Assert.All(events, e => Assert.Equal("203.0.113.11", e.Address));
        var attempt = events[1].AttemptId;
        Assert.NotNull(attempt);
        Assert.All(events.Skip(1), e => Assert.Equal(attempt, e.AttemptId));
        Assert.NotEqual(events[0].AttemptId, attempt);
        var scope = await account.ScalarAsync<Guid>("SELECT TOP (1) sessionScopeId FROM auth.sessionScope WHERE appUserId = @id ORDER BY createdDateUtc DESC");
        var reference = await account.ScalarAsync<Guid>("SELECT TOP (1) sessionReference FROM auth.sessionScope WHERE appUserId = @id ORDER BY createdDateUtc DESC");
        Assert.Equal(reference, events[4].SessionReference);
        Assert.NotEqual(scope, reference);
        Assert.Equal(0, await ScalarAsync<int>($"SELECT COUNT (*) FROM logs.signInEvent WHERE sessionReference = '{scope}' OR attemptId = '{scope}'"));

        // Signing out names the same session.
        string home = await person.Http.GetStringAsync("/account/profiles");
        using (await person.Http.PostAsync("/account/signout", MeritClient.Form(home)))
        {
        }

        Assert.Equal(reference, (await EventsAsync(account.Id)).Last().SessionReference);
        Assert.Equal("signedOut", (await EventsAsync(account.Id)).Last().Type);

        // A recovery code is a method of its own, and its use is flagged.
        await account.AddRecoveryCodeAsync(host, "RECOVERY45");
        using var lostPhone = Client(host, "203.0.113.11");
        string mfa2 = await PasswordAsync(lostPhone, account.Name, MeritFixture.Password);
        using (await lostPhone.NoRedirect.PostAsync("/account/mfa", MeritClient.Form(mfa2, ("Code", "RECOVERY45"))))
        {
        }

        var latest = (await EventsAsync(account.Id)).TakeLast(3).ToArray();
        Assert.Equal(["mfaSucceeded", "recoveryCodeUsed", "sessionStarted"], latest.Select(e => e.Type).ToArray());
        Assert.Equal("recoveryCode", latest[0].Method);

        // The application log has the same line, with the name only as the start of its hash.
        Assert.Contains(logs.Lines, l => l.Contains("Sign-in event unknownUser", StringComparison.Ordinal)
                                         && l.Contains(Convert.ToHexString(names.Hash(unknown))[..16], StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Lines, l => l.Contains(unknown, StringComparison.OrdinalIgnoreCase));
    }

    [SqlFact]
    public async Task LockoutsInactiveAccountsAndRateLimitRefusalsAreRecorded()
    {
        using var host = Host(settings: ("Merit:SignInAttemptsPerMinute", "6"));
        await using var account = await SyntheticAccount.CreateAsync(host, "sie");
        await using var inactive = await SyntheticAccount.CreateAsync(host, "sie");
        await inactive.ScalarAsync<int>("UPDATE auth.appUser SET isActive = 0 WHERE appUserId = @id; SELECT 0");

        using var client = Client(host, "203.0.113.20");
        for (int i = 0; i < 5; i++)
        {
            await PasswordAsync(client, account.Name, "Wrong-password-" + i);
        }

        await PasswordAsync(client, account.Name, MeritFixture.Password);   // locked now, even with the right password
        var types = (await EventsAsync(account.Id)).Select(e => e.Type).ToArray();
        Assert.Equal(5, types.Count(t => t == "passwordFailed"));
        Assert.Equal(1, types.Count(t => t == "accountLocked"));
        Assert.Equal("attemptWhileLocked", types.Last());
        Assert.NotNull((await EventsAsync(account.Id)).Last(e => e.Type == "accountLocked").LockoutEndUtc);

        using var other = Client(host, "203.0.113.21");
        await PasswordAsync(other, inactive.Name, MeritFixture.Password);
        Assert.Equal("accountInactive", (await EventsAsync(inactive.Id)).Single().Type);

        // Six password posts a minute from one address, then the seventh is refused and recorded.
        using var flood = Client(host, "203.0.113.22");
        string page = await flood.Http.GetStringAsync("/account/signin");
        HttpStatusCode last = HttpStatusCode.OK;
        for (int i = 0; i < 8; i++)
        {
            using var response = await flood.NoRedirect.PostAsync("/account/signin", MeritClient.Form(page, ("UserName", "nobody-" + i), ("Password", "x-x-x-x-x")));
            last = response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last);
        Assert.True(await ScalarAsync<int>("SELECT COUNT (*) FROM logs.signInEvent WHERE eventType = 'rateLimited' AND clientAddress = '203.0.113.22' AND reason = 'signin' AND step = 'request'") >= 1);
    }

    [SqlFact]
    public async Task AccountChangesAreRecordedWithTheAdministratorAsActor()
    {
        using var host = Host();
        await using var account = await SyntheticAccount.CreateAsync(host, "sie");
        var admin = (await fixture.SignedInAsync("admin")).On(host);
        var adminId = await ScalarAsync<Guid>("SELECT appUserId FROM auth.appUser WHERE userName = N'admin'");

        await account.ScalarAsync<int>("UPDATE auth.appUser SET failedSignInCount = 5, lockoutEndUtc = DATEADD (MINUTE, 15, SYSUTCDATETIME ()) WHERE appUserId = @id; SELECT 0");
        string page = await admin.Http.GetStringAsync($"/admin/users/{account.Id}");
        using (await admin.Http.PostAsync($"/admin/users/{account.Id}/unlock", MeritClient.Form(page)))
        {
        }

        string home = (await account.ScalarAsync<Guid>("SELECT homeTenantId FROM auth.appUser WHERE appUserId = @id")).ToString();
        page = await admin.Http.GetStringAsync($"/admin/users/{account.Id}");
        using (await admin.NoRedirect.PostAsync("/admin/users", MeritClient.Form(page, ("AppUserId", account.Id.ToString()), ("UserName", account.Name),
                   ("DisplayName", "Synthetic"), ("Email", account.Email), ("IsActive", "true"), ("HomeTenantId", home), ("NewPassword", "Admin-Set-Temporary-88"))))
        {
        }

        page = await admin.Http.GetStringAsync($"/admin/users/{account.Id}");
        using (await admin.NoRedirect.PostAsync($"/admin/users/{account.Id}/reset-mfa", MeritClient.Form(page)))
        {
        }

        page = await admin.Http.GetStringAsync($"/admin/users/{account.Id}");
        using (await admin.NoRedirect.PostAsync("/admin/users", MeritClient.Form(page, ("AppUserId", account.Id.ToString()), ("UserName", account.Name),
                   ("DisplayName", "Synthetic"), ("Email", account.Email), ("IsActive", "false"), ("HomeTenantId", home))))
        {
        }

        var events = await EventsAsync(account.Id);
        Assert.Equal(["unlockedByAdmin", "passwordSetByAdmin", "mfaResetByAdmin", "sessionRevoked", "accountDeactivated", "sessionRevoked"],
            events.Select(e => e.Type).ToArray());
        Assert.All(events, e => Assert.Equal(adminId, e.ActorUserId));
        Assert.Equal(new[] { "mfaReset", "deactivated" }, events.Where(e => e.Type == "sessionRevoked").Select(e => e.Reason ?? string.Empty).ToArray());

        // The account's page shows its history to the administrator.
        string history = WebUtility.HtmlDecode(await admin.Http.GetStringAsync($"/admin/users/{account.Id}"));
        Assert.Contains("Sign-in history", history, StringComparison.Ordinal);
        Assert.Contains("Unlocked by an administrator", history, StringComparison.Ordinal);
        Assert.Contains("by Avery Admin", history, StringComparison.Ordinal);

        // Viewing as someone is recorded on the viewed account, with the administrator as actor.
        var viewed = await ScalarAsync<Guid>("SELECT appUserId FROM auth.appUser WHERE userName = N'cmbalt'");
        var since = await ScalarAsync<DateTime>("SELECT DATEADD (MILLISECOND, -1, SYSUTCDATETIME ())");
        var adminSession = (await fixture.SignedInAsync("admin")).On(host);
        string profiles = await adminSession.Http.GetStringAsync("/account/profiles");
        using (await adminSession.Http.PostAsync($"/account/view-as/{viewed}", MeritClient.Form(profiles)))
        {
        }

        profiles = await adminSession.Http.GetStringAsync("/account/profiles");
        using (await adminSession.Http.PostAsync("/account/view-as/end", MeritClient.Form(profiles)))
        {
        }

        var viewEvents = await ScalarAsync<int>($"SELECT COUNT (*) FROM logs.signInEvent WHERE appUserId = '{viewed}' AND actorUserId = '{adminId}' AND eventType IN ('viewAsStarted', 'viewAsEnded') AND eventUtc >= '{since:yyyy-MM-ddTHH:mm:ss.fff}'");
        Assert.Equal(2, viewEvents);
    }

    [SqlFact]
    public async Task IdleLifetimeAndStampEndsOfASessionAreRecorded()
    {
        var clock = new TestClock();
        using var host = Host(clock: clock);
        await using var account = await SyntheticAccount.CreateAsync(host, "sie");

        // Idle: the cookie's ticket expires in the browser; the next request records it once and ends the scope.
        using var idle = Client(host, "203.0.113.30");
        await account.SignInAsync(idle, MeritFixture.Password);
        var scope = await account.ScalarAsync<Guid>("SELECT TOP (1) sessionScopeId FROM auth.sessionScope WHERE appUserId = @id ORDER BY createdDateUtc DESC");
        clock.Advance(TimeSpan.FromMinutes(21));
        using (await idle.NoRedirect.GetAsync("/"))
        {
        }

        using (await idle.NoRedirect.GetAsync("/"))
        {
        }

        Assert.Equal(1, await ScalarAsync<int>($"SELECT COUNT (*) FROM logs.signInEvent WHERE eventType = 'sessionExpired' AND reason = 'idle' AND sessionReference = (SELECT sessionReference FROM auth.sessionScope WHERE sessionScopeId = '{scope}')"));
        Assert.Equal(1, await ScalarAsync<int>($"SELECT COUNT (*) FROM auth.sessionScope WHERE sessionScopeId = '{scope}' AND revokedUtc IS NOT NULL"));

        // Lifetime: the scope outlived its expiry; the five-minute re-check finds it.
        using var aged = Client(host, "203.0.113.31");
        await account.SignInAsync(aged, MeritFixture.Password);
        await account.ScalarAsync<int>("UPDATE auth.sessionScope SET expiresUtc = DATEADD (MINUTE, -1, SYSUTCDATETIME ()) WHERE appUserId = @id AND revokedUtc IS NULL; SELECT 0");
        clock.Advance(TimeSpan.FromMinutes(6));
        using (await aged.NoRedirect.GetAsync("/"))
        {
        }

        Assert.Contains(await EventsAsync(account.Id), e => e is { Type: "sessionExpired", Reason: "lifetime" });

        // A changed security stamp (a password set elsewhere) ends the session too.
        using var stamped = Client(host, "203.0.113.32");
        await account.SignInAsync(stamped, MeritFixture.Password);
        await account.ScalarAsync<int>("UPDATE auth.appUser SET securityStamp = NEWID () WHERE appUserId = @id; SELECT 0");
        clock.Advance(TimeSpan.FromMinutes(6));
        using (await stamped.NoRedirect.GetAsync("/"))
        {
        }

        Assert.Contains(await EventsAsync(account.Id), e => e is { Type: "sessionRevoked", Reason: "securityStampChanged" });
    }

    [SqlFact]
    public async Task ReportsAreScopedAndUnknownNamesNeedTheStepUp()
    {
        using var host = Host();
        await using var account = await SyntheticAccount.CreateAsync(host, "sie");
        var names = host.Services.GetRequiredService<IAttemptedNameProtection>();
        string suspect = "MeritAdmin-" + Guid.NewGuid().ToString("N")[..6];
        string spray = "203.0.113." + Random.Shared.Next(100, 200);
        try
        {
            // Spraying: six different unknown names from one address. Stuffing: the account from four addresses, then a sign-in.
            for (int i = 0; i < 6; i++)
            {
                string name = i == 0 ? suspect : $"guess-{i}-{Guid.NewGuid():N}"[..20];
                await InsertEventAsync("unknownUser", null, spray, names.Hash(name), names.Protect(name), -30 + i);
            }

            for (int i = 0; i < 4; i++)
            {
                await InsertEventAsync("passwordFailed", account.Id, $"198.51.100.{10 + i}", null, null, -20 + i);
            }

            await InsertEventAsync("sessionStarted", account.Id, "198.51.100.10", null, null, -5);

            // A tenant administrator sees their account's patterns, but not the unknown names or their address.
            var admin = (await fixture.SignedInAsync("admin")).On(host);
            string report = WebUtility.HtmlDecode(await admin.Http.GetStringAsync("/admin/sign-in-activity"));
            Assert.Contains("One account tried from many addresses", report, StringComparison.Ordinal);
            Assert.Contains($"/admin/users/{account.Id}", report, StringComparison.Ordinal);
            Assert.Contains("Signed in after repeated failures", report, StringComparison.Ordinal);
            Assert.DoesNotContain(spray, report, StringComparison.Ordinal);
            Assert.DoesNotContain("/admin/sign-in-activity/unknown-names", report, StringComparison.Ordinal);
            using (var denied = await admin.NoRedirect.GetAsync("/admin/sign-in-activity/unknown-names"))
            {
                Assert.NotEqual(HttpStatusCode.OK, denied.StatusCode);
            }

            // The platform level sees everything, with unknown names hashed.
            var platform = (await fixture.SignedInAsync("platform")).On(host);
            string all = WebUtility.HtmlDecode(await platform.Http.GetStringAsync("/admin/sign-in-activity?hours=24"));
            Assert.Contains(spray, all, StringComparison.Ordinal);
            string hashed = WebUtility.HtmlDecode(await platform.Http.GetStringAsync("/admin/sign-in-activity/unknown-names"));
            Assert.Contains(Convert.ToHexString(names.Hash(suspect))[..12].ToLowerInvariant(), hashed, StringComparison.Ordinal);
            Assert.DoesNotContain(suspect, hashed, StringComparison.Ordinal);

            // Revealing needs a current authenticator code; the reveal is audited.
            var platformId = await ScalarAsync<Guid>("SELECT appUserId FROM auth.appUser WHERE userName = N'platform'");
            int viewsBefore = await AuditCountAsync(platformId, "view");
            using (var wrong = await platform.Http.PostAsync("/admin/sign-in-activity/unknown-names/reveal",
                       MeritClient.Form(hashed, ("Hours", "168"), ("AuthenticatorCode", "000000"))))
            {
                string body = WebUtility.HtmlDecode(await wrong.Content.ReadAsStringAsync());
                Assert.Contains("That code did not match", body, StringComparison.Ordinal);
                Assert.DoesNotContain(suspect, body, StringComparison.Ordinal);
            }

            await ScalarAsync<int>($"UPDATE auth.appUser SET failedSignInCount = 0 WHERE appUserId = '{platformId}'; SELECT 0");
            using (var shown = await platform.Http.PostAsync("/admin/sign-in-activity/unknown-names/reveal",
                       MeritClient.Form(hashed, ("Hours", "168"), ("AuthenticatorCode", platform.CurrentTotpCode()))))
            {
                Assert.Contains(suspect, WebUtility.HtmlDecode(await shown.Content.ReadAsStringAsync()), StringComparison.Ordinal);
            }

            Assert.Equal(viewsBefore + 1, await AuditCountAsync(platformId, "view"));

            // The lookup hashes a typed name and finds its attempts without decrypting; it and the export are audited.
            using (var lookup = await platform.Http.PostAsync("/admin/sign-in-activity/unknown-names/lookup",
                       MeritClient.Form(hashed, ("Hours", "168"), ("LookupName", "  " + suspect.ToUpperInvariant()))))
            {
                string body = WebUtility.HtmlDecode(await lookup.Content.ReadAsStringAsync());
                Assert.Contains("1 attempt in the last 90 days.", body, StringComparison.Ordinal);
                Assert.Contains(spray, body, StringComparison.Ordinal);
            }

            Assert.True(await AuditCountAsync(platformId, "search") >= 1);
            string csv = await platform.Http.GetStringAsync("/admin/sign-in-activity/unknown-names.csv");
            Assert.Contains(Convert.ToHexString(names.Hash(suspect)), csv, StringComparison.Ordinal);
            Assert.DoesNotContain(suspect, csv, StringComparison.Ordinal);
            Assert.True(await AuditCountAsync(platformId, "export") >= 1);
        }
        finally
        {
            await ScalarAsync<int>($"DELETE logs.signInEvent WHERE clientAddress = '{spray}' OR clientAddress LIKE '198.51.100.%'; SELECT 0");
        }
    }

    [SqlFact]
    public async Task TheAuditLogFiltersSignInEvents()
    {
        var admin = await fixture.SignedInAsync("admin");
        string page = WebUtility.HtmlDecode(await admin.Http.GetStringAsync("/admin/audit?action=signInEvents"));
        Assert.Contains("Sign-in and account security (all)", page, StringComparison.Ordinal);
        Assert.Contains("<td>Signed in</td>", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<td>Viewed</td>", page, StringComparison.Ordinal);   // only sign-in and account-security rows
    }

    [SqlFact]
    public async Task RetentionStripsThePersonalDataOfOldEvents()
    {
        using var host = Host();
        var names = host.Services.GetRequiredService<IAttemptedNameProtection>();
        var old = Guid.NewGuid();
        var recent = Guid.NewGuid();
        try
        {
            await ScalarAsync<int>($"""
                INSERT logs.signInEvent (signInEventId, eventUtc, eventType, clientAddress, userAgent, attemptedNameHash, attemptedNameProtected)
                VALUES ('{old}', DATEADD (DAY, -400, SYSUTCDATETIME ()), 'unknownUser', '203.0.113.90', N'Old agent', 0x{Convert.ToHexString(names.Hash("old-name"))}, N'{names.Protect("old-name")}'),
                       ('{recent}', DATEADD (DAY, -10, SYSUTCDATETIME ()), 'unknownUser', '203.0.113.91', N'Recent agent', 0x{Convert.ToHexString(names.Hash("new-name"))}, N'{names.Protect("new-name")}');
                SELECT 0;
                """);

            // As the worker runs it: its own login, no session.
            await using (var c = new SqlConnection(TestEnvironment.WorkerConnectionString))
            {
                await c.OpenAsync();
                await using var tooShort = new SqlCommand("EXEC logs.uspPurgeSignInEvents @retentionDays = 10", c);
                Assert.Equal(50000, (await Assert.ThrowsAsync<SqlException>(() => tooShort.ExecuteNonQueryAsync())).Number);
                await using var purge = new SqlCommand("EXEC logs.uspPurgeSignInEvents @retentionDays = 365", c);
                Assert.True((int)(await purge.ExecuteScalarAsync())! >= 1);
            }

            var gone = await SingleAsync($"SELECT isDeleted, clientAddress, userAgent, attemptedNameHash, attemptedNameProtected, eventType FROM logs.signInEvent WHERE signInEventId = '{old}'");
            Assert.Equal(true, gone[0]);
            Assert.All(gone.Skip(1).Take(4), v => Assert.Equal(DBNull.Value, v));
            Assert.Equal("unknownUser", gone[5]);   // an anonymous count remains
            var kept = await SingleAsync($"SELECT isDeleted, clientAddress FROM logs.signInEvent WHERE signInEventId = '{recent}'");
            Assert.Equal(false, kept[0]);
            Assert.Equal("203.0.113.91", kept[1]);
        }
        finally
        {
            await ScalarAsync<int>($"DELETE logs.signInEvent WHERE signInEventId IN ('{old}', '{recent}'); SELECT 0");
        }
    }

    // ---------------------------------------------------------------------------------------------- helpers

    private sealed record Event(string Type, string? Step, string? Method, string? Reason, string? Address, Guid? AttemptId, Guid? SessionReference,
                                int? FailedCount, DateTime? LockoutEndUtc, Guid? ActorUserId);

    private WebApplicationFactory<Program> Host(CapturingLoggerProvider? logs = null, TestClock? clock = null, params (string Key, string Value)[] settings)
        => fixture.Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("SignInLog:NameHashKey", Key);
            foreach (var (key, value) in settings)
            {
                b.UseSetting(key, value);
            }

            b.ConfigureTestServices(s =>
            {
                s.AddSingleton<IStartupFilter, TestClientAddressFilter>();
                s.AddSingleton<IEmailSender>(new CapturingEmailSender());
                if (logs is not null)
                {
                    s.AddSingleton<ILoggerProvider>(logs);
                }

                if (clock is not null)
                {
                    s.AddSingleton<TimeProvider>(clock);
                }
            });
        });

    private static MeritClient Client(WebApplicationFactory<Program> host, string address)
    {
        var client = new MeritClient(host);
        foreach (var http in new[] { client.Http, client.NoRedirect })
        {
            http.DefaultRequestHeaders.Add(TestClientAddressFilter.Header, address);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MeritTest/1.0 (synthetic)");
        }

        return client;
    }

    /// <summary>The password step; returns the page it leads to.</summary>
    private static async Task<string> PasswordAsync(MeritClient client, string userName, string password)
    {
        string page = await client.Http.GetStringAsync("/account/signin");
        using var response = await client.Http.PostAsync("/account/signin", MeritClient.Form(page, ("UserName", userName), ("Password", password)));
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<List<Event>> EventsAsync(Guid appUserId)
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();
        await using var cmd = new SqlCommand("""
            SELECT eventType, step, method, reason, clientAddress, attemptId, sessionReference, failedSignInCount, lockoutEndUtc, actorUserId
              FROM logs.signInEvent WHERE appUserId = @id AND isDeleted = 0
             -- Events from one request can share a millisecond; break ties in the order the code records them.
             ORDER BY eventUtc, CASE eventType WHEN 'recoveryCodeUsed' THEN 2 WHEN 'accountLocked' THEN 2 WHEN 'sessionStarted' THEN 3
                                               WHEN 'sessionRevoked' THEN 3 ELSE 1 END
            """, c);
        cmd.Parameters.AddWithValue("@id", appUserId);
        var list = new List<Event>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            T? N<T>(int i) where T : struct => r.IsDBNull(i) ? null : r.GetFieldValue<T>(i);
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            list.Add(new Event(r.GetString(0), S(1), S(2), S(3), S(4), N<Guid>(5), N<Guid>(6), N<int>(7), N<DateTime>(8), N<Guid>(9)));
        }

        return list;
    }

    private static async Task InsertEventAsync(string type, Guid? appUserId, string address, byte[]? hash, string? protectedName, int minutesAgo)
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();
        await using var cmd = new SqlCommand("""
            INSERT logs.signInEvent (signInEventId, eventUtc, eventType, appUserId, rootTenantId, clientAddress, attemptedNameHash, attemptedNameProtected)
            VALUES (NEWID (), DATEADD (MINUTE, @ago, SYSUTCDATETIME ()), @type, @user,
                    (SELECT rootTenantId FROM auth.appUser WHERE appUserId = @user), @address, @hash, @protected);
            """, c);
        cmd.Parameters.AddWithValue("@ago", minutesAgo);
        cmd.Parameters.AddWithValue("@type", type);
        cmd.Parameters.AddWithValue("@user", (object?)appUserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@address", address);
        cmd.Parameters.Add("@hash", System.Data.SqlDbType.VarBinary, 32).Value = (object?)hash ?? DBNull.Value;
        cmd.Parameters.AddWithValue("@protected", (object?)protectedName ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static Task<int> AuditCountAsync(Guid appUserId, string action)
        => ScalarAsync<int>($"SELECT COUNT (*) FROM logs.auditEvent WHERE appUserId = '{appUserId}' AND eventAction = '{action}' AND entityType = 'unknownUserNames'");

    private static async Task<T> ScalarAsync<T>(string sql)
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<object[]> SingleAsync(string sql)
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync(), "No row for: " + sql);
        var values = new object[r.FieldCount];
        r.GetValues(values);
        return values;
    }
}
