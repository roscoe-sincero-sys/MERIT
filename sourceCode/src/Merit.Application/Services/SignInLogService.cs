using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Security;

namespace Merit.Application.Services;

/// <summary>
/// Keyed hash and encryption for a user name typed at sign-in that matched no account (#191). The hash groups repeated
/// guesses and answers a lookup; the encrypted copy can be read back only through <see cref="SignInLogService.RevealUnknownNamesAsync"/>.
/// The web application implements it with its own key and ASP.NET Core data protection.
/// </summary>
public interface IAttemptedNameProtection
{
    /// <summary>HMAC-SHA256 of the name, trimmed and lower-cased.</summary>
    byte[] Hash(string userName);

    string Protect(string userName);

    /// <summary>The name, or null when the key that encrypted it is gone.</summary>
    string? Unprotect(string protectedName);
}

/// <summary>
/// For hosts that never record or read attempted names (the worker), so their containers validate. Hashing works with a
/// throwaway key; encryption is refused.
/// </summary>
public sealed class UnavailableNameProtection : IAttemptedNameProtection
{
    private readonly byte[] key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    public byte[] Hash(string userName)
        => System.Security.Cryptography.HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes(userName.Trim().ToLowerInvariant()));

    public string Protect(string userName) => throw new InvalidOperationException("Attempted user names are not recorded by this host.");

    public string? Unprotect(string protectedName) => throw new InvalidOperationException("Attempted user names are not read by this host.");
}

/// <summary>An unknown name's attempts, with the name itself only after a reveal.</summary>
public sealed record UnknownUserName(string Hash, int Attempts, int Addresses, DateTime FirstUtc, DateTime LastUtc, string? TopAddresses, string? Name);

/// <summary>
/// The sign-in event log's screens (#191): an account's history, the suspicious-activity report, and the investigation of
/// unknown user names. Every query is checked again by its stored procedure. Names are decrypted only for holders of
/// security.investigate, after a current authenticator code, and each reveal, lookup and export is in the audit ledger.
/// </summary>
public sealed class SignInLogService(IAccountData data, AccountService accounts, IAttemptedNameProtection names, AuditService audit,
                                     ICurrentSession session)
{
    public const string UnknownNamesEntity = "unknownUserNames";

    public const string ActivityEntity = "signInActivity";

    public Task<SignInActivity> ActivityAsync(int hours, CancellationToken ct = default)
    {
        session.RequireAny(Permissions.TenantAdminister, Permissions.SecurityReview);
        return data.GetSignInActivityAsync(Hours(hours), 5, 3, 3, ct);
    }

    /// <summary>The suspicious-activity report as a file (#272); the export is audited with its row count.</summary>
    public async Task<SignInActivity> ExportActivityAsync(int hours, CancellationToken ct = default)
    {
        var report = await ActivityAsync(hours, ct).ConfigureAwait(false);
        int rows = report.ByAddress.Count + report.ByAccount.Count + report.Spraying.Count + report.Stuffing.Count
                   + report.SuccessAfterFailures.Count + report.LockoutsAndLimits.Count;
        await audit.ExportedAsync(ActivityEntity, null, rows, ct).ConfigureAwait(false);
        return report;
    }

    public async Task<IReadOnlyList<UnknownUserName>> UnknownNamesAsync(int hours, CancellationToken ct = default)
    {
        session.Require(Permissions.SecurityInvestigate);
        return (await data.ListUnknownUserNamesAsync(Hours(hours), false, ct).ConfigureAwait(false)).Select(r => Map(r, null)).ToList();
    }

    /// <summary>The names themselves: a current authenticator code first, then one audited reveal for this report run.</summary>
    public async Task<IReadOnlyList<UnknownUserName>> RevealUnknownNamesAsync(int hours, string? authenticatorCode, string? clientAddress,
                                                                            CancellationToken ct = default)
    {
        session.Require(Permissions.SecurityInvestigate);
        if (!await accounts.CheckAuthenticatorCodeAsync(session.UserName!, authenticatorCode, clientAddress, ct).ConfigureAwait(false))
        {
            throw new ValidationException("AuthenticatorCode", "That code did not match. Enter the current six-digit code from your authenticator app.");
        }

        var rows = await data.ListUnknownUserNamesAsync(Hours(hours), true, ct).ConfigureAwait(false);
        await audit.ListedAsync(UnknownNamesEntity, rows.Count, ct).ConfigureAwait(false);
        return rows.Select(r => Map(r, r.AttemptedNameProtected is null ? null : names.Unprotect(r.AttemptedNameProtected) ?? "(no longer readable)")).ToList();
    }

    /// <summary>Every attempt at a typed name, found by its hash. Nothing is decrypted; the lookup itself is audited.</summary>
    public async Task<IReadOnlyList<SignInAttemptRow>> FindAttemptsAsync(string? userName, CancellationToken ct = default)
    {
        session.Require(Permissions.SecurityInvestigate);
        new Validator().Required("LookupName", userName, "User name").ThrowIfInvalid();
        var rows = await data.FindSignInAttemptsByNameAsync(names.Hash(userName!), 2160, ct).ConfigureAwait(false);
        await audit.SearchedAsync(UnknownNamesEntity, rows.Count, ct).ConfigureAwait(false);
        return rows;
    }

    /// <summary>The hashed report as a file; the export is audited. Names are never exported.</summary>
    public async Task<IReadOnlyList<UnknownUserName>> ExportUnknownNamesAsync(int hours, CancellationToken ct = default)
    {
        var rows = await UnknownNamesAsync(hours, ct).ConfigureAwait(false);
        await audit.ExportedAsync(UnknownNamesEntity, null, rows.Count, ct).ConfigureAwait(false);
        return rows;
    }

    private static UnknownUserName Map(UnknownUserNameRow r, string? name)
        => new(r.AttemptedNameHash, r.Attempts, r.Addresses, r.FirstUtc, r.LastUtc, r.TopAddresses, name);

    private static int Hours(int hours) => Math.Clamp(hours, 1, 2160);
}
