using System.Buffers.Text;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Merit.Application.Data;
using Merit.Application.Email;
using Merit.Application.Errors;
using Merit.Application.Security;
using Merit.Domain.Ids;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Merit.Application.Services;

/// <summary>Limits for forgotten-password links (configuration section PasswordReset).</summary>
public sealed class PasswordResetOptions
{
    public const string Section = "PasswordReset";

    public int LinkLifetimeMinutes { get; set; } = 20;

    /// <summary>Wrong second-factor codes allowed with one link before it stops working.</summary>
    public int MaxCodeAttempts { get; set; } = 5;

    public int CooldownSeconds { get; set; } = 60;

    public int LimitWindowMinutes { get; set; } = 60;

    public int MaxLinksPerAccount { get; set; } = 3;

    public int MaxLinksPerAddress { get; set; } = 10;
}

public enum PasswordResetLinkStatus
{
    Valid,
    Expired,
    Used,
    Superseded,
    None,
    NoSecondFactor,
    AccountLocked,
}

public sealed record PasswordResetLink(PasswordResetLinkStatus Status, string? UserName = null, int PasswordMinLength = PasswordPolicy.MinimumLength, int RemainingAttempts = 0);

public enum PasswordResetOutcome
{
    Reset,
    InvalidLink,
    WrongCode,
    LinkClosed,
}

public sealed record PasswordResetResult(PasswordResetOutcome Outcome, int RemainingAttempts = 0, Guid? AppUserId = null);

/// <summary>
/// Password self-service (#190): a signed-in change, a forgotten-password reset by emailed link, and the change an
/// administrator can require at next sign-in. A reset never bypasses MFA: the link proves the mailbox, and the page also
/// asks for an authenticator code or a recovery code. Links carry a random 256-bit token that reaches the database only as
/// a keyed verifier; a link works once, for 20 minutes, and five wrong codes close it. Every success changes the security
/// stamp and ends the account's sessions. Passwords, tokens, links and addresses never reach the log.
/// </summary>
public sealed partial class PasswordService(IAccountData data, AccountService accounts, IEmailSender email, IOneTimeCodeHasher hasher,
                                            IOptions<PasswordResetOptions> options, ISignInEvents events, ILogger<PasswordService> logger)
{
    private const string Purpose = "passwordReset";

    private PasswordResetOptions Limits => options.Value;

    /// <summary>Forgotten-password links need a mail server; without one the page says to ask an administrator.</summary>
    public bool ResetByEmailAvailable => email.IsConfigured;

    public int LinkLifetimeMinutes => Limits.LinkLifetimeMinutes;

    /// <summary>
    /// Issues and emails a reset link when the account may have one. Runs off the request (the web application queues
    /// it), so the page that asked answers the same way, in the same time, whether or not the account exists.
    /// </summary>
    public async Task RequestResetAsync(string userName, string? clientAddress, string? correlationId, string publicBaseUrl,
                                        CancellationToken ct = default)
    {
        correlationId ??= Activity.Current?.TraceId.ToString();
        var user = string.IsNullOrWhiteSpace(userName) ? null : await data.GetUserForSignInAsync(userName.Trim(), ct).ConfigureAwait(false);
        if (user is null || !user.IsActive || !user.MfaEnabled || !email.IsConfigured)
        {
            LogNotEligible(logger, correlationId);
            // Asking for resets of names that don't exist is a pattern worth seeing (#191); the name is hashed and encrypted.
            await events.RecordAsync(user is null
                ? new SignInEvent(SignInEventTypes.UnknownUser, SignInSteps.PasswordReset, AttemptedUserName: userName, ClientAddress: clientAddress,
                    TraceId: correlationId)
                : new SignInEvent(SignInEventTypes.PasswordResetRequested, SignInSteps.PasswordReset, Reason: "notAvailable", AppUserId: user.AppUserId,
                    ClientAddress: clientAddress, TraceId: correlationId), ct).ConfigureAwait(false);
            return;
        }

        Guid id = TimeOrderedId.New();
        string token = NewToken();
        string reference = OneTimeCodes.NewSupportReference();
        var created = await data.CreatePasswordResetAsync(new PasswordResetRequest(id, user.AppUserId, Verifier(id, token), reference, correlationId,
            clientAddress, Limits.LinkLifetimeMinutes, Limits.MaxCodeAttempts, Limits.CooldownSeconds, Limits.LimitWindowMinutes,
            Limits.MaxLinksPerAccount, Limits.MaxLinksPerAddress), ct).ConfigureAwait(false);
        LogIssue(logger, created.Outcome, user.AppUserId, reference, correlationId);
        if (created.Outcome != "created" || created.Email is null)
        {
            await events.RecordAsync(new SignInEvent(SignInEventTypes.PasswordResetRequested, SignInSteps.PasswordReset, Reason: created.Outcome,
                AppUserId: user.AppUserId, ClientAddress: clientAddress, TraceId: correlationId), ct).ConfigureAwait(false);
            return;
        }

        string link = string.Create(CultureInfo.InvariantCulture, $"{publicBaseUrl.TrimEnd('/')}/account/reset?r={id:N}&t={token}");
        string body = string.Create(CultureInfo.InvariantCulture, $"""
            Someone asked to reset the password for your MERIT account ({user.UserName}).

            To choose a new password, open this link within {Limits.LinkLifetimeMinutes} minutes:
            {link}

            You will also need a code from your authenticator app, or one of your recovery codes. The link works once.

            If you did not ask for this, ignore this email: your password has not changed. If these emails keep arriving,
            tell your MERIT administrator.

            Reference: {reference}
            """);
        EmailSendResult sent;
        try
        {
            sent = await email.SendAsync(new OutgoingEmail(created.Email, "Reset your MERIT password", body, reference, correlationId ?? reference), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            sent = EmailSendResult.Failed(EmailFailureKind.Canceled, false, 1, null, 0);
        }

        await data.RecordPasswordResetDeliveryAsync(id, sent.Submitted, sent.Attempts, sent.Submitted ? null : Camel(sent.Failure), sent.SmtpReplyCode,
            CancellationToken.None).ConfigureAwait(false);
        if (sent.Submitted)
        {
            LogSubmitted(logger, user.AppUserId, reference, sent.Attempts, correlationId);
        }
        else
        {
            LogNotSent(logger, user.AppUserId, reference, sent.Failure, sent.SmtpReplyCode, sent.Attempts, correlationId);
        }

        await events.RecordAsync(new SignInEvent(SignInEventTypes.PasswordResetRequested, SignInSteps.PasswordReset,
            Reason: sent.Submitted ? "linkSent" : "notSent:" + Camel(sent.Failure), AppUserId: user.AppUserId, ClientAddress: clientAddress,
            TraceId: correlationId), ct).ConfigureAwait(false);
    }

    /// <summary>What a reset link may do now. Only a valid link names the account.</summary>
    public async Task<PasswordResetLink> CheckLinkAsync(Guid passwordResetId, string? token, CancellationToken ct = default)
    {
        if (!IsToken(token))
        {
            return new PasswordResetLink(PasswordResetLinkStatus.None);
        }

        var row = await data.GetPasswordResetAsync(passwordResetId, Verifier(passwordResetId, token!), ct).ConfigureAwait(false);
        var status = row.Status switch
        {
            "valid" when !row.MfaEnabled => PasswordResetLinkStatus.NoSecondFactor,
            "valid" when row.LockoutEndUtc is DateTime until && until > DateTime.UtcNow => PasswordResetLinkStatus.AccountLocked,
            "valid" => PasswordResetLinkStatus.Valid,
            "expired" => PasswordResetLinkStatus.Expired,
            "used" => PasswordResetLinkStatus.Used,
            "superseded" => PasswordResetLinkStatus.Superseded,
            _ => PasswordResetLinkStatus.None,
        };
        return new PasswordResetLink(status, status == PasswordResetLinkStatus.Valid ? row.UserName : null, row.PasswordMinLength, row.RemainingAttempts);
    }

    /// <summary>
    /// Sets a new password with a reset link. The new password is checked first, so a recovery code is not used up on a
    /// password that would be refused; then the second factor; then the link is consumed in the same statement that
    /// checks it. A wrong code counts against the link and toward the account lockout.
    /// </summary>
    public async Task<PasswordResetResult> CompleteResetAsync(Guid passwordResetId, string? token, string? code, string? newPassword,
                                                              string? confirmPassword, string? clientAddress, CancellationToken ct = default)
    {
        var link = await CheckLinkAsync(passwordResetId, token, ct).ConfigureAwait(false);
        if (link.Status != PasswordResetLinkStatus.Valid || link.UserName is null)
        {
            return new PasswordResetResult(PasswordResetOutcome.InvalidLink);
        }

        var user = await data.GetUserForSignInAsync(link.UserName, ct).ConfigureAwait(false);
        if (user is null)
        {
            return new PasswordResetResult(PasswordResetOutcome.InvalidLink);
        }

        ValidateNew(user, newPassword, confirmPassword, link.PasswordMinLength);
        byte[] verifier = Verifier(passwordResetId, token!);
        string? method = await accounts.CheckSecondFactorAsync(user, code, ct).ConfigureAwait(false);
        if (method is null)
        {
            await data.RecordSignInResultAsync(user.AppUserId, false, AccountService.MaxFailedAttempts, AccountService.LockoutMinutes, clientAddress, ct)
                .ConfigureAwait(false);
            await accounts.RecordFailureAsync(user, SignInEventTypes.MfaFailed, SignInSteps.PasswordReset,
                (code ?? string.Empty).Trim().Length > 6 ? SignInMethods.RecoveryCode : SignInMethods.Totp, "wrongCode", ct).ConfigureAwait(false);
            int left = await data.RecordPasswordResetFailureAsync(passwordResetId, verifier, ct).ConfigureAwait(false);
            LogWrongCode(logger, user.AppUserId, left);
            return new PasswordResetResult(left > 0 ? PasswordResetOutcome.WrongCode : PasswordResetOutcome.LinkClosed, left);
        }

        var reset = await data.CompletePasswordResetAsync(passwordResetId, verifier, accounts.HashPassword(newPassword!), clientAddress, ct)
            .ConfigureAwait(false);
        if (reset is null)
        {
            return new PasswordResetResult(PasswordResetOutcome.InvalidLink);
        }

        LogReset(logger, user.AppUserId);
        if (method == SignInMethods.RecoveryCode)
        {
            await events.RecordAsync(new SignInEvent(SignInEventTypes.RecoveryCodeUsed, SignInSteps.PasswordReset, method, AppUserId: user.AppUserId), ct)
                .ConfigureAwait(false);
        }

        await events.RecordAsync(new SignInEvent(SignInEventTypes.PasswordResetCompleted, SignInSteps.PasswordReset, method, AppUserId: user.AppUserId), ct)
            .ConfigureAwait(false);
        await events.RecordAsync(new SignInEvent(SignInEventTypes.SessionRevoked, SignInSteps.Session, Reason: "passwordReset", AppUserId: user.AppUserId), ct)
            .ConfigureAwait(false);
        return new PasswordResetResult(PasswordResetOutcome.Reset, AppUserId: reset);
    }

    /// <summary>
    /// The signed-in person's own change: current password, new password and a current authenticator code. A wrong
    /// current password or code counts toward the lockout, as at sign-in. Every session of theirs ends; the caller starts
    /// a fresh one for this browser.
    /// </summary>
    public async Task ChangePasswordAsync(string userName, string? currentPassword, string? newPassword, string? confirmPassword,
                                          string? authenticatorCode, string? clientAddress, CancellationToken ct = default)
    {
        var user = await data.GetUserForSignInAsync(userName, ct).ConfigureAwait(false) ?? throw new NotFoundException();
        var policy = await data.GetMyPasswordPolicyAsync(ct).ConfigureAwait(false);
        new Validator().Required("CurrentPassword", currentPassword, "Current password").ThrowIfInvalid();
        ValidateNew(user, newPassword, confirmPassword, policy.PasswordMinLength);

        if (!accounts.IsCurrentPassword(user, currentPassword))
        {
            await data.RecordSignInResultAsync(user.AppUserId, false, AccountService.MaxFailedAttempts, AccountService.LockoutMinutes, clientAddress, ct)
                .ConfigureAwait(false);
            await accounts.RecordFailureAsync(user, SignInEventTypes.PasswordFailed, SignInSteps.Account, null, "changePassword", ct).ConfigureAwait(false);
            throw new ValidationException("CurrentPassword", "That is not your current password.");
        }

        if (!await accounts.CheckAuthenticatorCodeAsync(userName, authenticatorCode, clientAddress, ct).ConfigureAwait(false))
        {
            throw new ValidationException("PasswordAuthenticatorCode", "That code did not match. Enter the current six-digit code from your authenticator app.");
        }

        await data.ChangeMyPasswordAsync(accounts.HashPassword(newPassword!), clientAddress, ct).ConfigureAwait(false);
        LogChanged(logger, user.AppUserId, "self");
        await RecordChangedAsync(user.AppUserId, "self", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The change an administrator asked for, made right after sign-in (password and second factor already given), so no
    /// current password or code is asked again.
    /// </summary>
    public async Task SetRequiredPasswordAsync(string userName, string? newPassword, string? confirmPassword, string? clientAddress,
                                               CancellationToken ct = default)
    {
        var user = await data.GetUserForSignInAsync(userName, ct).ConfigureAwait(false) ?? throw new NotFoundException();
        var policy = await data.GetMyPasswordPolicyAsync(ct).ConfigureAwait(false);
        ValidateNew(user, newPassword, confirmPassword, policy.PasswordMinLength);
        await data.ChangeMyPasswordAsync(accounts.HashPassword(newPassword!), clientAddress, ct).ConfigureAwait(false);
        LogChanged(logger, user.AppUserId, "required");
        await RecordChangedAsync(user.AppUserId, "required", ct).ConfigureAwait(false);
    }

    public Task<MyPasswordPolicyRow> MyPolicyAsync(CancellationToken ct = default) => data.GetMyPasswordPolicyAsync(ct);

    /// <summary>"Your password was changed", when mail is configured and the account has an address. Best effort.</summary>
    public async Task SendChangedNoticeAsync(Guid appUserId, string? correlationId, CancellationToken ct = default)
    {
        if (!email.IsConfigured)
        {
            return;
        }

        var row = await data.GetEmailCodeOptionAsync(appUserId, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(row?.Email))
        {
            return;
        }

        string reference = OneTimeCodes.NewSupportReference();
        var sent = await email.SendAsync(new OutgoingEmail(row.Email, "Your MERIT password was changed", """
            The password for your MERIT account was just changed, and your other MERIT sessions were signed out.

            If you did this, you don't need to do anything.

            If you did not, contact your MERIT administrator right away.
            """, reference, correlationId ?? reference), ct).ConfigureAwait(false);
        LogNotice(logger, appUserId, sent.Submitted, sent.Failure, reference);
    }

    /// <summary>Password rules: the jurisdiction's minimum length, the breached-password list, a match, and not the current one.</summary>
    private void ValidateNew(SignInUserRow user, string? newPassword, string? confirmPassword, int minLength)
    {
        accounts.ValidateNewPassword(newPassword, minLength, "NewPassword", user.UserName);
        var v = new Validator();
        string? confirmed;
        try { confirmed = confirmPassword is null ? null : PasswordPolicy.Normalize(confirmPassword); }
        catch (ArgumentException) { throw new ValidationException("ConfirmPassword", "Use valid Unicode characters in the password."); }
        v.When(PasswordPolicy.Normalize(newPassword!) != confirmed, "ConfirmPassword", "The two passwords don't match.");
        v.When(accounts.IsCurrentPassword(user, newPassword), "NewPassword", "Choose a password you are not using now.");
        v.ThrowIfInvalid();
    }

    private async Task RecordChangedAsync(Guid appUserId, string how, CancellationToken ct)
    {
        await events.RecordAsync(new SignInEvent(SignInEventTypes.PasswordChanged, SignInSteps.Account, Reason: how, AppUserId: appUserId), ct)
            .ConfigureAwait(false);
        await events.RecordAsync(new SignInEvent(SignInEventTypes.SessionRevoked, SignInSteps.Session, Reason: "passwordChange", AppUserId: appUserId), ct)
            .ConfigureAwait(false);
    }

    private byte[] Verifier(Guid passwordResetId, string token) => hasher.Hash(passwordResetId, Purpose, token);

    /// <summary>32 random bytes, base64url: 43 characters that survive any mail client.</summary>
    private static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url.EncodeToString(bytes);
    }

    private static bool IsToken(string? token)
        => token is { Length: 43 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string Camel(EmailFailureKind kind)
    {
        string name = kind.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Password reset asked for an account that cannot have one (or none), correlation {CorrelationId}")]
    private static partial void LogNotEligible(ILogger logger, string? correlationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Password reset link {Outcome} for user {AppUserId}, reference {SupportReference}, correlation {CorrelationId}")]
    private static partial void LogIssue(ILogger logger, string outcome, Guid appUserId, string supportReference, string? correlationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Password reset link submitted for user {AppUserId}, reference {SupportReference}, after {Attempts} attempt(s), correlation {CorrelationId}")]
    private static partial void LogSubmitted(ILogger logger, Guid appUserId, string supportReference, int attempts, string? correlationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Password reset link not sent for user {AppUserId}, reference {SupportReference}: {FailureKind}, SMTP reply {SmtpReplyCode}, {Attempts} attempt(s), correlation {CorrelationId}")]
    private static partial void LogNotSent(ILogger logger, Guid appUserId, string supportReference, EmailFailureKind failureKind, int? smtpReplyCode, int attempts,
                                           string? correlationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Password reset: wrong second-factor code for user {AppUserId}, {RemainingAttempts} attempt(s) left on the link")]
    private static partial void LogWrongCode(ILogger logger, Guid appUserId, int remainingAttempts);

    [LoggerMessage(Level = LogLevel.Information, Message = "Password reset completed for user {AppUserId}")]
    private static partial void LogReset(ILogger logger, Guid appUserId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Password changed for user {AppUserId} ({How})")]
    private static partial void LogChanged(ILogger logger, Guid appUserId, string how);

    [LoggerMessage(Level = LogLevel.Information, Message = "Password-changed notice for user {AppUserId}: submitted {Submitted} ({FailureKind}), reference {SupportReference}")]
    private static partial void LogNotice(ILogger logger, Guid appUserId, bool submitted, EmailFailureKind failureKind, string supportReference);
}
