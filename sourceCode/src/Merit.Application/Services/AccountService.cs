using System.Security.Cryptography;
using System.Runtime.CompilerServices;
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
/// Built-in accounts with required multi-factor authentication: new passwords have at least 15 Unicode code points,
/// are checked against common/compromised values, and have no composition rules or forced rotation; then an
/// authenticator app (TOTP) code. MFA cannot be turned off. Sign-in only establishes who the user is; the session scope
/// created afterwards decides what they can see and do.
/// </summary>
public sealed class AccountService(IAccountData data, IPasswordHasher hasher, ISecretProtector protector,
                                   IBreachedPasswordList breached, ISignInEvents events)
{
    public const int MaxFailedAttempts = 5;
    public const int LockoutMinutes = 15;
    public const string Issuer = "MERIT";

    public async Task<SignInStep> CheckPasswordAsync(string userName, string password, string? clientAddress, CancellationToken ct = default)
    {
        var user = await data.GetUserForSignInAsync(userName.Trim(), ct).ConfigureAwait(false);
        if (user is null || !user.IsActive)
        {
            // Use a valid cached hash so missing accounts do not skip password-hashing work.
            hasher.Verify(DummyHash.For(hasher), password);
            await data.RecordSignInResultAsync(null, false, MaxFailedAttempts, LockoutMinutes, clientAddress, ct).ConfigureAwait(false);
            await events.RecordAsync(user is null
                ? new SignInEvent(SignInEventTypes.UnknownUser, SignInSteps.Password, AttemptedUserName: userName)
                : new SignInEvent(SignInEventTypes.AccountInactive, SignInSteps.Password, AppUserId: user.AppUserId), ct).ConfigureAwait(false);
            return new SignInStep(SignInStatus.Failed);
        }

        if (user.LockoutEndUtc is DateTime until && until > DateTime.UtcNow)
        {
            await events.RecordAsync(new SignInEvent(SignInEventTypes.AttemptWhileLocked, SignInSteps.Password, AppUserId: user.AppUserId), ct)
                .ConfigureAwait(false);
            return new SignInStep(SignInStatus.LockedOut, LockedUntilUtc: until);
        }

        var check = hasher.Verify(user.PasswordHash, password);
        if (check == PasswordCheck.Failed)
        {
            await data.RecordSignInResultAsync(user.AppUserId, false, MaxFailedAttempts, LockoutMinutes, clientAddress, ct).ConfigureAwait(false);
            await RecordFailureAsync(user, SignInEventTypes.PasswordFailed, SignInSteps.Password, null, "wrongPassword", ct).ConfigureAwait(false);
            return user.FailedSignInCount + 1 >= MaxFailedAttempts
                ? new SignInStep(SignInStatus.LockedOut, LockedUntilUtc: DateTime.UtcNow.AddMinutes(LockoutMinutes))
                : new SignInStep(SignInStatus.Failed);
        }

        if (check == PasswordCheck.SuccessRehashNeeded
            && !await data.UpgradePasswordHashAsync(user.AppUserId, user.SecurityStamp, user.PasswordHash, hasher.Hash(password), ct).ConfigureAwait(false))
        {
            // Another sign-in may have upgraded the same hash. A reset/deactivation must still win.
            var current = await data.GetUserForSignInAsync(user.UserName, ct).ConfigureAwait(false);
            if (current is null || !current.IsActive || current.SecurityStamp != user.SecurityStamp
                || current.LockoutEndUtc > DateTime.UtcNow || hasher.Verify(current.PasswordHash, password) == PasswordCheck.Failed)
            {
                await events.RecordAsync(new SignInEvent(SignInEventTypes.PasswordFailed, SignInSteps.Password,
                    Reason: "credentialsChangedDuringVerification", AppUserId: user.AppUserId), ct).ConfigureAwait(false);
                return new SignInStep(SignInStatus.Failed);
            }
            user = current;
        }

        await events.RecordAsync(new SignInEvent(SignInEventTypes.PasswordSucceeded, SignInSteps.Password, AppUserId: user.AppUserId), ct)
            .ConfigureAwait(false);
        return new SignInStep(user.MfaEnabled ? SignInStatus.NeedsMfaCode : SignInStatus.NeedsMfaEnrollment,
            user.AppUserId, user.UserName, user.SecurityStamp, user.IsAdministrator);
    }

    public static MfaEnrollmentStart BeginEnrollment(string userName)
    {
        var secret = Totp.NewSecret();
        return new MfaEnrollmentStart(Totp.ToBase32(secret), Totp.ProvisioningUri(Issuer, userName, secret));
    }

    /// <summary>
    /// Verifies the first code from the new authenticator and stores the secret; returns one-time recovery codes. This is
    /// the person's first sign-in, so its outcome is recorded like any code step (issue #44): a good code sets the last
    /// sign-in and writes the sign-in audit event, a wrong one counts toward the lockout.
    /// </summary>
    public async Task<IReadOnlyList<string>> CompleteEnrollmentAsync(Guid appUserId, string secretBase32, string code, string? clientAddress = null,
                                                                     CancellationToken ct = default)
    {
        byte[] secret = Totp.FromBase32(secretBase32);
        if (!Totp.Verify(secret, code, DateTimeOffset.UtcNow))
        {
            await data.RecordSignInResultAsync(appUserId, false, MaxFailedAttempts, LockoutMinutes, clientAddress, ct).ConfigureAwait(false);
            await events.RecordAsync(new SignInEvent(SignInEventTypes.MfaFailed, SignInSteps.Enrollment, SignInMethods.Totp, "wrongCode", appUserId), ct)
                .ConfigureAwait(false);
            throw new ValidationException("Code", "That code did not match. Check the time on your device and try the current code.");
        }

        var codes = Enumerable.Range(0, 10).Select(_ => NewRecoveryCode()).ToList();
        await data.SaveMfaEnrollmentAsync(appUserId, protector.Protect(secretBase32), codes.Select(hasher.Hash).ToList(), ct)
            .ConfigureAwait(false);
        await events.RecordAsync(new SignInEvent(SignInEventTypes.MfaEnrolled, SignInSteps.Enrollment, SignInMethods.Totp, AppUserId: appUserId), ct)
            .ConfigureAwait(false);
        await data.RecordSignInResultAsync(appUserId, true, MaxFailedAttempts, LockoutMinutes, clientAddress, ct).ConfigureAwait(false);
        return codes;
    }

    public async Task<bool> VerifyMfaAsync(string userName, string code, string? clientAddress, CancellationToken ct = default)
    {
        var user = await data.GetUserForSignInAsync(userName, ct).ConfigureAwait(false);
        if (user?.MfaSecretProtected is null || !user.IsActive)
        {
            await events.RecordAsync(new SignInEvent(SignInEventTypes.MfaFailed, SignInSteps.SecondFactor, Reason: "accountUnavailable",
                AppUserId: user?.AppUserId), ct).ConfigureAwait(false);
            return false;
        }

        if (user.LockoutEndUtc is DateTime until && until > DateTime.UtcNow)
        {
            await events.RecordAsync(new SignInEvent(SignInEventTypes.AttemptWhileLocked, SignInSteps.SecondFactor, AppUserId: user.AppUserId), ct)
                .ConfigureAwait(false);
            return false;
        }

        byte[] secret = Totp.FromBase32(protector.Unprotect(user.MfaSecretProtected));
        bool ok = Totp.Verify(secret, code, DateTimeOffset.UtcNow);
        string method = SignInMethods.Totp;
        if (!ok && code.Trim().Length > 6)
        {
            method = SignInMethods.RecoveryCode;
            ok = await TryRecoveryCodeAsync(user.AppUserId, code.Trim(), ct).ConfigureAwait(false);
        }

        await data.RecordSignInResultAsync(user.AppUserId, ok, MaxFailedAttempts, LockoutMinutes, clientAddress, ct).ConfigureAwait(false);
        if (ok)
        {
            await events.RecordAsync(new SignInEvent(SignInEventTypes.MfaSucceeded, SignInSteps.SecondFactor, method, AppUserId: user.AppUserId), ct)
                .ConfigureAwait(false);
            if (method == SignInMethods.RecoveryCode)
            {
                await events.RecordAsync(new SignInEvent(SignInEventTypes.RecoveryCodeUsed, SignInSteps.SecondFactor, method, AppUserId: user.AppUserId), ct)
                    .ConfigureAwait(false);
            }
        }
        else
        {
            await RecordFailureAsync(user, SignInEventTypes.MfaFailed, SignInSteps.SecondFactor, method, "wrongCode", ct).ConfigureAwait(false);
        }

        return ok;
    }

    /// <summary>
    /// A current authenticator code from a signed-in person, before a change to their sign-in methods (turning on email
    /// codes). Recovery codes do not count here. A wrong code counts toward the account lockout like one at sign-in.
    /// </summary>
    public async Task<bool> CheckAuthenticatorCodeAsync(string userName, string? code, string? clientAddress, CancellationToken ct = default)
    {
        var user = await data.GetUserForSignInAsync(userName, ct).ConfigureAwait(false);
        if (user?.MfaSecretProtected is null || !user.IsActive || (user.LockoutEndUtc is DateTime until && until > DateTime.UtcNow))
        {
            return false;
        }

        byte[] secret = Totp.FromBase32(protector.Unprotect(user.MfaSecretProtected));
        if (Totp.Verify(secret, (code ?? string.Empty).Trim(), DateTimeOffset.UtcNow))
        {
            return true;
        }

        await data.RecordSignInResultAsync(user.AppUserId, false, MaxFailedAttempts, LockoutMinutes, clientAddress, ct).ConfigureAwait(false);
        await RecordFailureAsync(user, SignInEventTypes.MfaFailed, SignInSteps.Account, SignInMethods.Totp, "stepUp", ct).ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// A failure that counted toward the lockout, recorded after <see cref="IAccountData.RecordSignInResultAsync"/>; when it
    /// was the one that locked the account, accountLocked follows.
    /// </summary>
    public async Task RecordFailureAsync(SignInUserRow user, string type, string step, string? method, string? reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        await events.RecordAsync(new SignInEvent(type, step, method, reason, user.AppUserId), ct).ConfigureAwait(false);
        if (user.FailedSignInCount + 1 >= MaxFailedAttempts)
        {
            await events.RecordAsync(new SignInEvent(SignInEventTypes.AccountLocked, step, method, $"after{MaxFailedAttempts}Failures", user.AppUserId), ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The second factor on its own, for a password reset (#190): a current authenticator code, or an unused recovery code,
    /// which is then used up. Nothing is recorded here; the caller counts a failure against the link and the lockout.
    /// </summary>
    /// <returns>The method that matched (totp or recoveryCode), or null.</returns>
    public async Task<string?> CheckSecondFactorAsync(SignInUserRow user, string? code, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        string typed = (code ?? string.Empty).Trim();
        if (user.MfaSecretProtected is null || typed.Length == 0)
        {
            return null;
        }

        byte[] secret = Totp.FromBase32(protector.Unprotect(user.MfaSecretProtected));
        if (Totp.Verify(secret, typed, DateTimeOffset.UtcNow))
        {
            return SignInMethods.Totp;
        }

        return typed.Length > 6 && await TryRecoveryCodeAsync(user.AppUserId, typed, ct).ConfigureAwait(false) ? SignInMethods.RecoveryCode : null;
    }

    /// <summary>Whether a password is the account's current one.</summary>
    public bool IsCurrentPassword(SignInUserRow user, string? password)
    {
        ArgumentNullException.ThrowIfNull(user);
        return !string.IsNullOrEmpty(password) && hasher.Verify(user.PasswordHash, password) != PasswordCheck.Failed;
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

    /// <summary>Why a session check failed: expired, revoked or a changed security stamp (#191).</summary>
    public Task<SessionScopeStateRow?> SessionStateAsync(Guid scopeKey, CancellationToken ct = default) => data.GetSessionScopeStateAsync(scopeKey, ct);

    /// <summary>NIST SP 800-63B-4 password rules: a minimum length and a breached-password check; no composition rules.</summary>
    public void ValidateNewPassword(string? password, int minLength, string field = "Password", string? userName = null)
    {
        var v = new Validator();
        v.Required(field, password, "Password");
        if (password is not null)
        {
            string normalized;
            try { normalized = PasswordPolicy.Normalize(password); }
            catch (ArgumentException) { throw new ValidationException(field, "Use valid Unicode characters in the password."); }
            int effective = PasswordPolicy.EffectiveMinimum(minLength);
            int length = PasswordPolicy.Length(normalized);
            v.When(length < effective, field, $"Use at least {effective} characters.");
            v.When(length > PasswordPolicy.MaximumLength, field, $"Use {PasswordPolicy.MaximumLength} characters or fewer.");
            v.When(breached.IsBreached(normalized), field, "That password appears in lists of breached passwords. Choose another.");
            v.When(PasswordPolicy.IsExpected(normalized, userName), field,
                "Choose a password that is not your user name or MERIT with a simple number or symbol added.");
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
        public static readonly ConditionalWeakTable<IPasswordHasher, Lazy<string>> Values = new();

        public static string For(IPasswordHasher hasher)
            => Values.GetValue(hasher, h => new Lazy<string>(() => h.Hash(Convert.ToHexString(RandomNumberGenerator.GetBytes(32))))).Value;
    }

    /// <summary>A fresh id for a new account.</summary>
    public static Guid NewUserId() => TimeOrderedId.New();
}
