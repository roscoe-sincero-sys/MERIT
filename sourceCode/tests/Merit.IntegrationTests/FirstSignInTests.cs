using System.Text.RegularExpressions;
using Merit.Application.Security;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

/// <summary>
/// Issue #44: a person's first sign-in ends with MFA enrollment, and it was never recorded, so "Last sign-in" stayed blank
/// and the audit log had no sign-in for it.
/// </summary>
[Collection(MeritCollection.Name)]
public sealed partial class FirstSignInTests(MeritFixture fixture)
{
    // No other test signs in as the intake clerk, so this test owns its enrollment.
    private const string User = "intake";

    [SqlFact]
    public async Task EnrollmentIsRecordedAsASignIn()
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();
        await Exec(c, """
            UPDATE auth.appUser SET mfaEnabled = 0, mfaSecretProtected = NULL, failedSignInCount = 0, lockoutEndUtc = NULL
             WHERE userName = @u;
            """);
        var started = (DateTime)(await Scalar(c, "SELECT SYSUTCDATETIME ()"))!;

        using var client = new MeritClient(fixture.Factory);
        string page = await client.Http.GetStringAsync("/account/signin");
        var enroll = await client.Http.PostAsync("/account/signin", MeritClient.Form(page, ("UserName", User), ("Password", MeritFixture.Password)));
        Assert.EndsWith("/account/enroll", enroll.RequestMessage!.RequestUri!.AbsolutePath, StringComparison.OrdinalIgnoreCase);
        string enrollPage = await enroll.Content.ReadAsStringAsync();
        string secret = SecretPattern().Match(enrollPage).Groups[1].Value.Replace(" ", string.Empty, StringComparison.Ordinal);

        // A wrong first code counts toward the lockout, like a wrong code at any later sign-in.
        var wrong = await client.Http.PostAsync("/account/enroll", MeritClient.Form(enrollPage, ("Code", "000000")));
        Assert.Equal(1, (int)(await Scalar(c, "SELECT failedSignInCount FROM auth.appUser WHERE userName = @u"))!);

        // The right code signs the person in: the last sign-in is set and the sign-in is in the audit log.
        string again = await wrong.Content.ReadAsStringAsync();
        var done = await client.Http.PostAsync("/account/enroll",
            MeritClient.Form(again, ("Code", Totp.Code(Totp.FromBase32(secret), DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30))));
        Assert.True(done.IsSuccessStatusCode);
        Assert.Equal(0, (int)(await Scalar(c, "SELECT failedSignInCount FROM auth.appUser WHERE userName = @u"))!);
        Assert.True((DateTime)(await Scalar(c, "SELECT lastSignInUtc FROM auth.appUser WHERE userName = @u"))! >= started);
        var events = (int)(await Scalar(c, """
            SELECT COUNT (*) FROM logs.auditEvent AS e JOIN auth.appUser AS u ON u.appUserId = e.appUserId
             WHERE u.userName = @u AND e.eventAction IN ('signIn', 'signInFailed') AND e.eventUtc >= @started
            """, started))!;
        Assert.Equal(2, events);
    }

    private static async Task Exec(SqlConnection c, string sql)
    {
        await using var cmd = new SqlCommand(sql, c);
        cmd.Parameters.AddWithValue("@u", User);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> Scalar(SqlConnection c, string sql, DateTime? started = null)
    {
        await using var cmd = new SqlCommand(sql, c);
        cmd.Parameters.AddWithValue("@u", User);
        if (started is { } s)
        {
            cmd.Parameters.AddWithValue("@started", s);
        }

        return await cmd.ExecuteScalarAsync();
    }

    [GeneratedRegex("<code>([A-Z2-7 ]+)</code>")]
    private static partial Regex SecretPattern();
}
