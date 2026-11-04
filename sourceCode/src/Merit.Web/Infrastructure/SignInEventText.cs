using Merit.Application.Reference;
using Merit.Application.Security;

namespace Merit.Web.Infrastructure;

/// <summary>Sign-in events in words (#191), as the audit log's actions are (AuditText).</summary>
public static class SignInEventText
{
    private static readonly Dictionary<string, string> Events = new(StringComparer.Ordinal)
    {
        [SignInEventTypes.PasswordSucceeded] = "Password accepted",
        [SignInEventTypes.PasswordFailed] = "Wrong password",
        [SignInEventTypes.UnknownUser] = "Unknown user name",
        [SignInEventTypes.AccountInactive] = "Inactive account",
        [SignInEventTypes.AttemptWhileLocked] = "Attempt while locked",
        [SignInEventTypes.AccountLocked] = "Account locked",
        [SignInEventTypes.MfaSucceeded] = "Second factor accepted",
        [SignInEventTypes.MfaFailed] = "Second factor failed",
        [SignInEventTypes.MfaEnrolled] = "Authenticator set up",
        [SignInEventTypes.RecoveryCodeUsed] = "Recovery code used",
        [SignInEventTypes.SessionStarted] = "Signed in",
        [SignInEventTypes.RateLimited] = "Too many attempts (refused)",
        [SignInEventTypes.SignedOut] = "Signed out",
        [SignInEventTypes.SessionExpired] = "Session expired",
        [SignInEventTypes.SessionRevoked] = "Session ended",
        [SignInEventTypes.PasswordChanged] = "Password changed",
        [SignInEventTypes.PasswordResetRequested] = "Password reset asked for",
        [SignInEventTypes.PasswordResetCompleted] = "Password reset with a link",
        [SignInEventTypes.PasswordSetByAdmin] = "Password set by an administrator",
        [SignInEventTypes.UnlockedByAdmin] = "Unlocked by an administrator",
        [SignInEventTypes.MfaResetByAdmin] = "MFA reset by an administrator",
        [SignInEventTypes.AccountDeactivated] = "Account deactivated",
        [SignInEventTypes.ViewAsStarted] = "Viewed as (started)",
        [SignInEventTypes.ViewAsEnded] = "Viewed as (ended)",
        [SignInEventTypes.EmailCodeSent] = "Email code sent",
        [SignInEventTypes.EmailCodeNotSent] = "Email code not sent",
        [SignInEventTypes.EmailCodeRefused] = "Email code refused (wait or limit)",
        [SignInEventTypes.EmailCodesEnabled] = "Email codes turned on",
        [SignInEventTypes.EmailCodesDisabled] = "Email codes turned off",
    };

    private static readonly Dictionary<string, string> Methods = new(StringComparer.Ordinal)
    {
        [SignInMethods.Totp] = "authenticator app",
        [SignInMethods.RecoveryCode] = "recovery code",
        [SignInMethods.EmailCode] = "email code",
    };

    public static string Event(string code) => Events.TryGetValue(code, out var label) ? label : DisplayNames.Split(code);

    public static string Method(string? code) => code is null ? string.Empty : Methods.TryGetValue(code, out var label) ? label : DisplayNames.Split(code);

    public static string Reason(string? code) => code is null ? string.Empty : DisplayNames.Split(code);

    public static bool IsFailure(string code) => SignInEventTypes.Failures.Contains(code) || code is SignInEventTypes.AccountLocked or SignInEventTypes.RateLimited;

    /// <summary>The first characters of a name hash: enough to tell groups apart on screen.</summary>
    public static string ShortHash(string? hex) => string.IsNullOrEmpty(hex) ? string.Empty : hex[..Math.Min(12, hex.Length)].ToLowerInvariant();

    public static string ShortReference(Guid? id) => id is Guid g ? g.ToString("N")[..8] : string.Empty;
}
