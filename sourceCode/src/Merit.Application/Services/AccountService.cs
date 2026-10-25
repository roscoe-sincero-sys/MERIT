using System.Security.Cryptography;
using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Security;
using Merit.Domain.Ids;

namespace Merit.Application.Services;

public enum SignInStatus
{
    Failed,
    LockedOut,
    NeedsMfaEnrollment,
    NeedsMfaCode,
}

public sealed record SignInStep(SignInStatus Status, Guid? AppUserId = null, string? UserName = null,
                                Guid? SecurityStamp = null, bool IsAdministrator = false, DateTime? LockedUntilUtc = null);

public sealed record MfaEnrollmentStart(string SecretBase32, string ProvisioningUri);

public sealed record SignedInSession(SessionScopeResult Scope);

/// <summary>
/// Built-in accounts with required multi-factor authentication (NIST SP 800-63B-4 defaults): a password of at least 8
/// characters checked against known breached passwords, no composition rules, no forced rotation; then an
/// authenticator app (TOTP) code. MFA cannot be turned off. Sign-in only establishes who the user is; the session scope
/// created afterwards decides what they can see and do.
/// </summary>
public sealed class AccountService(IAccountData data, IPasswordHasher hasher, ISecretProtector protector,
                                   IBreachedPasswordList breached)
{
    public const int MaxFailedAttempts = 5;
    public const int LockoutMinutes = 15;
    public const string Issuer = "MERIT";

    public async Task<SignInStep> CheckPasswordAsync(string userName, string password, string? clientAddress, CancellationToken ct = default)
    {
        var user = await data.GetUserForSignInAsync(userName.Trim(), ct).ConfigureAwait(false);
        if (user is null || !user.IsActive)
        {
            // Same work and the same answer whether or not the account exists.
            hasher.Verify(DummyHash.Value, password);
            await data.RecordSignInResultAsync(null, false, MaxFailedAttempts, LockoutMinutes, clientAddress, ct).ConfigureAwait(false);
            return new SignInStep(SignInStatus.Failed);
        }

        if (user.LockoutEndUtc is DateTime until && until > DateTime.UtcNow)
        {
            return new SignInStep(SignInStatus.LockedOut, LockedUntilUtc: until);
        }

        if (hasher.Verify(user.PasswordHash, password) == PasswordCheck.Failed)
        {
            await data.RecordSignInResultAsync(user.AppUserId, false, MaxFailedAttempts, LockoutMinutes, clientAddress, ct).ConfigureAwait(false);
            return user.FailedSignInCount + 1 >= MaxFailedAttempts
                ? new SignInStep(SignInStatus.LockedOut, LockedUntilUtc: DateTime.UtcNow.AddMinutes(LockoutMinutes))
                : new SignInStep(SignInStatus.Failed);
        }

        return new SignInStep(user.MfaEnabled ? SignInStatus.NeedsMfaCode : SignInStatus.NeedsMfaEnrollment,
            user.AppUserId, user.UserName, user.SecurityStamp, user.IsAdministrator);
    }

    public static MfaEnrollmentStart BeginEnrollment(string userName)
    {
        var secret = Totp.NewSecret();
        return new MfaEnrollmentStart(Totp.ToBase32(secret), Totp.ProvisioningUri(Issuer, userName, secret));
    }

    /// <summary>Verifies the first code from the new authenticator and stores the secret; returns one-time recovery codes.</summary>
    public async Task<IReadOnlyList<string>> CompleteEnrollmentAsync(Guid appUserId, string secretBase32, string code, CancellationToken ct = default)
    {
        byte[] secret = Totp.FromBase32(secretBase32);
        if (!Totp.Verify(secret, code, DateTimeOffset.UtcNow))
        {
            throw new ValidationException("Code", "That code did not match. Check the time on your device and try the current code.");
        }

        var codes = Enumerable.Range(0, 10).Select(_ => NewRecoveryCode()).ToList();
        await data.SaveMfaEnrollmentAsync(appUserId, protector.Protect(secretBase32), codes.Select(hasher.Hash).ToList(), ct)
            .ConfigureAwait(false);
        return codes;
    }

    public async Task<bool> VerifyMfaAsync(string userName, string code, string? clientAddress, CancellationToken ct = default)
    {
        var user = await data.GetUserForSignInAsync(userName, ct).ConfigureAwait(false);
        if (user?.MfaSecretProtected is null || !user.IsActive)
        {
            return false;
        }

        if (user.LockoutEndUtc is DateTime until && until > DateTime.UtcNow)
        {
            return false;
        }

        byte[] secret = Totp.FromBase32(protector.Unprotect(user.MfaSecretProtected));
        bool ok = Totp.Verify(secret, code, DateTimeOffset.UtcNow);
        if (!ok && code.Trim().Length > 6)
        {
            ok = await TryRecoveryCodeAsync(user.AppUserId, code.Trim(), ct).ConfigureAwait(false);
        }

        await data.RecordSignInResultAsync(user.AppUserId, ok, MaxFailedAttempts, LockoutMinutes, clientAddress, ct).ConfigureAwait(false);
        return ok;
    }

    /// <summary>Resolves the session scope: tenants and permissions the user holds right now.</summary>
    /// <summary>
    /// A new session scope: the master profile by default, one of the user's own profiles, or (for an administrator) another
    /// user's access. The database decides what each may see; a refusal comes back as PermissionDeniedException.
    /// </summary>
    public async Task<SignedInSession> CreateSessionAsync(Guid appUserId, TimeSpan lifetime, Guid? userProfileId = null, Guid? viewAsUserId = null,
                                                          Guid? actingTenantId = null, CancellationToken ct = default)
    {
        // The scope key travels in the cookie and is the row-level security key: random, not time-ordered.
        var scope = await data.CreateSessionScopeAsync(Guid.NewGuid(), appUserId, DateTime.UtcNow.Add(lifetime), userProfileId, viewAsUserId,
            actingTenantId, ct).ConfigureAwait(false);
        return new SignedInSession(scope);
    }

    public async Task<bool> IsSessionValidAsync(Guid scopeKey, Guid securityStamp, CancellationToken ct = default)
    {
        var row = await data.GetSessionScopeAsync(scopeKey, ct).ConfigureAwait(false);
        return row is not null && row.SecurityStamp == securityStamp;
    }

    public Task SignOutAsync(Guid scopeKey, CancellationToken ct = default) => data.RevokeSessionScopeAsync(scopeKey, ct);

    /// <summary>NIST SP 800-63B-4 password rules: a minimum length and a breached-password check; no composition rules.</summary>
    public void ValidateNewPassword(string? password, int minLength)
    {
        var v = new Validator();
        v.Required("Password", password, "Password");
        if (password is not null)
        {
            v.When(password.Length < minLength, "Password", $"Use at least {minLength} characters.");
            v.When(password.Length > 128, "Password", "Use 128 characters or fewer.");
            v.When(breached.IsBreached(password), "Password", "That password appears in lists of breached passwords. Choose another.");
        }

        v.ThrowIfInvalid();
    }

    public string HashPassword(string password) => hasher.Hash(password);

    private async Task<bool> TryRecoveryCodeAsync(Guid appUserId, string code, CancellationToken ct)
    {
        string normalized = code.Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        foreach (var row in await data.GetRecoveryCodesAsync(appUserId, ct).ConfigureAwait(false))
        {
            if (hasher.Verify(row.CodeHash, normalized) != PasswordCheck.Failed)
            {
                await data.UseRecoveryCodeAsync(row.MfaRecoveryCodeId, ct).ConfigureAwait(false);
                return true;
            }
        }

        return false;
    }

    private static string NewRecoveryCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> chars = stackalloc char[10];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return new string(chars);
    }

    private static class DummyHash
    {
        // Verified against a throwaway hash so a missing account costs the same time as a wrong password.
        public static readonly string Value = "AQAAAAIAAYagAAAAEDummyDummyDummyDummyDummyDummyDummyDummyDummyDummyDummyDu==";
    }

    /// <summary>A fresh id for a new account.</summary>
    public static Guid NewUserId() => TimeOrderedId.New();
}
