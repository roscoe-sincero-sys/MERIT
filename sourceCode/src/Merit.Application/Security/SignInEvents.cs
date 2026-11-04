namespace Merit.Application.Security;

/// <summary>The kinds of sign-in and account-security event (#191). The values are what logs.signInEvent stores.</summary>
public static class SignInEventTypes
{
    public const string PasswordSucceeded = "passwordSucceeded";
    public const string PasswordFailed = "passwordFailed";
    public const string UnknownUser = "unknownUser";
    public const string AccountInactive = "accountInactive";
    public const string AttemptWhileLocked = "attemptWhileLocked";
    public const string AccountLocked = "accountLocked";
    public const string MfaSucceeded = "mfaSucceeded";
    public const string MfaFailed = "mfaFailed";
    public const string MfaEnrolled = "mfaEnrolled";
    public const string RecoveryCodeUsed = "recoveryCodeUsed";
    public const string SessionStarted = "sessionStarted";
    public const string RateLimited = "rateLimited";
    public const string SignedOut = "signedOut";
    public const string SessionExpired = "sessionExpired";
    public const string SessionRevoked = "sessionRevoked";
    public const string PasswordChanged = "passwordChanged";
    public const string PasswordResetRequested = "passwordResetRequested";
    public const string PasswordResetCompleted = "passwordResetCompleted";
    public const string PasswordSetByAdmin = "passwordSetByAdmin";
    public const string UnlockedByAdmin = "unlockedByAdmin";
    public const string MfaResetByAdmin = "mfaResetByAdmin";
    public const string AccountDeactivated = "accountDeactivated";
    public const string ViewAsStarted = "viewAsStarted";
    public const string ViewAsEnded = "viewAsEnded";
    public const string EmailCodeSent = "emailCodeSent";
    public const string EmailCodeNotSent = "emailCodeNotSent";
    public const string EmailCodeRefused = "emailCodeRefused";
    public const string EmailCodesEnabled = "emailCodesEnabled";
    public const string EmailCodesDisabled = "emailCodesDisabled";

    /// <summary>The events that count as a failed attempt in the reports.</summary>
    public static readonly IReadOnlySet<string> Failures = new HashSet<string>(StringComparer.Ordinal)
        { PasswordFailed, UnknownUser, MfaFailed, AttemptWhileLocked, AccountInactive };
}

/// <summary>Where an event happened.</summary>
public static class SignInSteps
{
    public const string Password = "password";
    public const string SecondFactor = "secondFactor";
    public const string Enrollment = "enrollment";
    public const string Session = "session";
    public const string PasswordReset = "passwordReset";
    public const string Account = "account";
    public const string Request = "request";
}

/// <summary>The second factor used or tried.</summary>
public static class SignInMethods
{
    public const string Totp = "totp";
    public const string RecoveryCode = "recoveryCode";
    public const string EmailCode = "emailCode";
}

/// <summary>
/// One event as the code that saw it describes it. The recorder adds what the request knows: client address, forwarded
/// chain, user agent, trace id, the sign-in attempt and the session. <see cref="AttemptedUserName"/> is only for a name
/// that matched no account; it is stored hashed and encrypted, never in plain text.
/// </summary>
public sealed record SignInEvent(string Type, string? Step = null, string? Method = null, string? Reason = null, Guid? AppUserId = null,
                                 Guid? ActorUserId = null, string? AttemptedUserName = null, Guid? SessionScopeId = null,
                                 string? ClientAddress = null, string? TraceId = null);

/// <summary>Records sign-in and account-security events (#191). Recording never fails the action it describes.</summary>
public interface ISignInEvents
{
    Task RecordAsync(SignInEvent signInEvent, CancellationToken ct = default);
}

/// <summary>For hosts without a sign-in page (the worker): nothing to record.</summary>
public sealed class NoSignInEvents : ISignInEvents
{
    public Task RecordAsync(SignInEvent signInEvent, CancellationToken ct = default) => Task.CompletedTask;
}
