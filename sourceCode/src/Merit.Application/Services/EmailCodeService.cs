using System.Diagnostics;
using System.Globalization;
using Merit.Application.Data;
using Merit.Application.Email;
using Merit.Application.Security;
using Merit.Domain.Ids;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Merit.Application.Services;

/// <summary>Limits for email one-time codes (configuration section EmailCodes). The defaults follow NIST SP 800-63B-4 guidance for short-lived codes.</summary>
public sealed class EmailCodeOptions
{
    public const string Section = "EmailCodes";

    /// <summary>How long a code works.</summary>
    public int LifetimeSeconds { get; set; } = 300;

    /// <summary>Wrong codes allowed against one emailed code before it stops working.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Seconds between sends for one account. A send that failed does not start the wait.</summary>
    public int ResendCooldownSeconds { get; set; } = 30;

    /// <summary>The window the per-account and per-address limits count over.</summary>
    public int LimitWindowMinutes { get; set; } = 15;

    public int MaxCodesPerAccount { get; set; } = 5;

    public int MaxCodesPerAddress { get; set; } = 20;

    /// <summary>Base64 key of at least 32 bytes for the code verifier; from user secrets or the environment, never appsettings.</summary>
    public string? VerifierKey { get; set; }
}

public enum EmailCodePurpose
{
    SignIn,
    Enroll,
}

public enum EmailCodeSendStatus
{
    Sent,
    Cooldown,
    Limited,
    NotAvailable,
    DeliveryFailed,
}

/// <summary>Sent means the mail server accepted the message; MaskedEmail and ExpiresUtc are set then.</summary>
public sealed record EmailCodeSendResult(EmailCodeSendStatus Status, string? MaskedEmail = null, DateTime? ExpiresUtc = null, int RetryAfterSeconds = 0,
                                         string? SupportReference = null);

public enum EmailCodeCheck
{
    Verified,
    Wrong,
    Locked,
    Expired,
    Used,
    Superseded,
    None,
    Malformed,
    AccountLocked,
}

public sealed record EmailCodeVerifyResult(EmailCodeCheck Outcome, int RemainingAttempts = 0, DateTime? LockedUntilUtc = null);

/// <summary>Email codes for one account. Available: offered at sign-in now. Allowed: the account may turn them on.</summary>
public sealed record EmailCodeOption(bool Available, bool TurnedOn, bool Allowed, string? MaskedEmail, string PreferredMethod);

/// <summary>
/// Email one-time codes: an additional second factor next to the authenticator app, which stays required. A
/// jurisdiction allows them with its emailCodesEnabled setting (off by default; NIST SP 800-63B-4 does not accept email
/// for out-of-band authentication), administrators never get them, and a person turns them on only after a current
/// authenticator code and a code sent to the address. Codes come from the operating system's CSPRNG, live five
/// minutes, work once, and reach the database only as a keyed verifier. The database issues, supersedes, limits and
/// consumes them atomically. Codes, addresses and server replies are never logged; the support reference and the
/// request's trace id are.
/// </summary>
public sealed partial class EmailCodeService(IAccountData data, IEmailSender email, IOneTimeCodeHasher hasher, IOptions<EmailCodeOptions> options,
                                             ILogger<EmailCodeService> logger)
{
    private EmailCodeOptions Limits => options.Value;

    public async Task<EmailCodeOption> GetOptionAsync(Guid appUserId, CancellationToken ct = default)
    {
        var row = await data.GetEmailCodeOptionAsync(appUserId, ct).ConfigureAwait(false);
        bool allowed = row is not null && row.EmailCodesAllowed && row.MfaEnabled && email.IsConfigured && !string.IsNullOrWhiteSpace(row.Email);
        bool on = row?.MfaEmailVerifiedUtc is not null;
        bool available = allowed && on;
        return new EmailCodeOption(available, on, allowed, OneTimeCodes.MaskEmail(row?.Email),
            available && row!.MfaPreferredMethod == "email" ? "email" : "totp");
    }

    /// <summary>
    /// Issues a new code (superseding the open one) and emails it. The database decides whether the account may have
    /// one now; the send is bounded by the mail server timeouts and retries, and its outcome is recorded either way.
    /// </summary>
    public async Task<EmailCodeSendResult> SendAsync(Guid appUserId, EmailCodePurpose purpose, string? clientAddress, string? correlationId,
                                                     CancellationToken ct = default)
    {
        string purposeCode = Code(purpose);
        correlationId ??= Activity.Current?.TraceId.ToString();
        if (!email.IsConfigured)
        {
            LogNotConfigured(logger, appUserId, purposeCode);
            return new EmailCodeSendResult(EmailCodeSendStatus.NotAvailable);
        }

        string code = OneTimeCodes.NewCode();
        string reference = OneTimeCodes.NewSupportReference();
        Guid challengeId = TimeOrderedId.New();
        var created = await data.CreateEmailChallengeAsync(new EmailChallengeRequest(challengeId, appUserId, purposeCode,
            hasher.Hash(appUserId, purposeCode, code), reference, correlationId, clientAddress, Limits.LifetimeSeconds, Limits.MaxAttempts,
            Limits.ResendCooldownSeconds, Limits.LimitWindowMinutes, Limits.MaxCodesPerAccount, Limits.MaxCodesPerAddress), ct).ConfigureAwait(false);

        LogIssue(logger, created.Outcome, appUserId, purposeCode, reference, correlationId);
        switch (created.Outcome)
        {
            case "cooldown":
                return new EmailCodeSendResult(EmailCodeSendStatus.Cooldown, RetryAfterSeconds: created.RetryAfterSeconds);
            case "limit":
                return new EmailCodeSendResult(EmailCodeSendStatus.Limited, RetryAfterSeconds: created.RetryAfterSeconds);
            case "created" when created.Email is not null && created.ExpiresUtc is not null:
                break;
            default:
                return new EmailCodeSendResult(EmailCodeSendStatus.NotAvailable);
        }

        EmailSendResult sent;
        try
        {
            sent = await email.SendAsync(Compose(created.Email, code, purpose, reference, correlationId ?? reference), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            sent = EmailSendResult.Failed(EmailFailureKind.Canceled, false, 1, null, 0);
        }

        try
        {
            // The browser may have gone; the outcome is still recorded so the challenge never stays half-open.
            await data.RecordEmailChallengeDeliveryAsync(challengeId, sent.Submitted, sent.Attempts,
                sent.Submitted ? null : FailureCode(sent.Failure), sent.SmtpReplyCode, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (sent.Submitted)
        {
            // The message is out and a pending challenge still verifies, so the person can carry on; support sees why.
            LogRecordFailed(logger, ex, appUserId, reference, correlationId);
        }

        if (sent.Submitted)
        {
            LogSubmitted(logger, appUserId, purposeCode, reference, sent.Attempts, sent.ElapsedMilliseconds, correlationId);
            return new EmailCodeSendResult(EmailCodeSendStatus.Sent, OneTimeCodes.MaskEmail(created.Email), created.ExpiresUtc,
                Limits.ResendCooldownSeconds, reference);
        }

        LogNotSent(logger, appUserId, purposeCode, reference, sent.Failure, sent.SmtpReplyCode, sent.Attempts, sent.Retryable,
            sent.ElapsedMilliseconds, correlationId);
        return new EmailCodeSendResult(EmailCodeSendStatus.DeliveryFailed, SupportReference: reference);
    }

    /// <summary>The sign-in step: the account's lockout applies, and a wrong code counts toward it like a wrong authenticator code.</summary>
    public async Task<EmailCodeVerifyResult> VerifySignInAsync(Guid appUserId, string userName, string? typed, string? clientAddress, CancellationToken ct = default)
    {
        var user = await data.GetUserForSignInAsync(userName, ct).ConfigureAwait(false);
        if (user is null || user.AppUserId != appUserId || !user.IsActive)
        {
            return new EmailCodeVerifyResult(EmailCodeCheck.None);
        }

        if (user.LockoutEndUtc is DateTime until && until > DateTime.UtcNow)
        {
            return new EmailCodeVerifyResult(EmailCodeCheck.AccountLocked, LockedUntilUtc: until);
        }

        // The jurisdiction may have turned email codes off, or the person become an administrator, since the code went out.
        if (!(await GetOptionAsync(appUserId, ct).ConfigureAwait(false)).Available)
        {
            return new EmailCodeVerifyResult(EmailCodeCheck.None);
        }

        var result = await VerifyAsync(appUserId, EmailCodePurpose.SignIn, typed, clientAddress, ct).ConfigureAwait(false);
        if (result.Outcome is EmailCodeCheck.Verified or EmailCodeCheck.Wrong or EmailCodeCheck.Locked)
        {
            bool ok = result.Outcome == EmailCodeCheck.Verified;
            await data.RecordSignInResultAsync(appUserId, ok, AccountService.MaxFailedAttempts, AccountService.LockoutMinutes, clientAddress, ct)
                .ConfigureAwait(false);
            if (!ok && user.FailedSignInCount + 1 >= AccountService.MaxFailedAttempts)
            {
                return new EmailCodeVerifyResult(EmailCodeCheck.AccountLocked, LockedUntilUtc: DateTime.UtcNow.AddMinutes(AccountService.LockoutMinutes));
            }
        }

        return result;
    }

    /// <summary>Confirms the address: a verified enroll code turns email codes on for the account, in the same transaction.</summary>
    public Task<EmailCodeVerifyResult> VerifyEnrollmentAsync(Guid appUserId, string? typed, string? clientAddress, CancellationToken ct = default)
        => VerifyAsync(appUserId, EmailCodePurpose.Enroll, typed, clientAddress, ct);

    /// <summary>Turns email codes off for the signed-in user, or sets which second factor sign-in offers first.</summary>
    public Task SaveMyMethodsAsync(bool emailCodesOn, bool preferEmail, CancellationToken ct = default)
        => data.SaveMyMfaMethodsAsync(emailCodesOn, emailCodesOn && preferEmail ? "email" : "totp", ct);

    private async Task<EmailCodeVerifyResult> VerifyAsync(Guid appUserId, EmailCodePurpose purpose, string? typed, string? clientAddress, CancellationToken ct)
    {
        string? code = OneTimeCodes.Normalize(typed);
        if (code is null)
        {
            return new EmailCodeVerifyResult(EmailCodeCheck.Malformed);
        }

        string purposeCode = Code(purpose);
        var row = await data.VerifyEmailChallengeAsync(appUserId, purposeCode, hasher.Hash(appUserId, purposeCode, code), clientAddress, ct)
            .ConfigureAwait(false);
        var outcome = row.Outcome switch
        {
            "verified" => EmailCodeCheck.Verified,
            "wrong" => EmailCodeCheck.Wrong,
            "locked" => EmailCodeCheck.Locked,
            "expired" => EmailCodeCheck.Expired,
            "used" => EmailCodeCheck.Used,
            "superseded" => EmailCodeCheck.Superseded,
            _ => EmailCodeCheck.None,
        };
        LogVerify(logger, outcome, appUserId, purposeCode, row.RemainingAttempts);
        return new EmailCodeVerifyResult(outcome, row.RemainingAttempts);
    }

    private OutgoingEmail Compose(string to, string code, EmailCodePurpose purpose, string reference, string correlationId)
    {
        int minutes = Math.Max(1, Limits.LifetimeSeconds / 60);
        string why = purpose == EmailCodePurpose.SignIn
            ? "Enter it on the MERIT sign-in page to finish signing in."
            : "Enter it on the MERIT Sign-in methods page to turn on email codes for your account.";
        string notYou = purpose == EmailCodePurpose.SignIn
            ? "If you are not signing in to MERIT right now, someone may know your password. Change it, and tell your MERIT administrator."
            : "If you did not ask to turn on email codes, tell your MERIT administrator.";
        string body = string.Create(CultureInfo.InvariantCulture, $"""
            Your MERIT verification code is {code}

            {why} The code works once and expires in {minutes} minutes.

            {notYou}

            Never share this code. MERIT staff will never ask you for it.

            Reference: {reference}
            """);
        return new OutgoingEmail(to, "Your MERIT verification code", body, reference, correlationId);
    }

    private static string Code(EmailCodePurpose purpose) => purpose == EmailCodePurpose.SignIn ? "signIn" : "enroll";

    /// <summary>The stored category: camelCase, at most 30 characters.</summary>
    private static string FailureCode(EmailFailureKind kind)
    {
        string name = kind.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Email code not available for user {AppUserId} ({Purpose}): no mail server is configured")]
    private static partial void LogNotConfigured(ILogger logger, Guid appUserId, string purpose);

    [LoggerMessage(Level = LogLevel.Information, Message = "Email code request {Outcome} for user {AppUserId} ({Purpose}), reference {SupportReference}, correlation {CorrelationId}")]
    private static partial void LogIssue(ILogger logger, string outcome, Guid appUserId, string purpose, string supportReference, string? correlationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Email code submitted to the mail server for user {AppUserId} ({Purpose}), reference {SupportReference}, after {Attempts} attempt(s) in {ElapsedMs} ms, correlation {CorrelationId}")]
    private static partial void LogSubmitted(ILogger logger, Guid appUserId, string purpose, string supportReference, int attempts, long elapsedMs, string? correlationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Email code not sent for user {AppUserId} ({Purpose}), reference {SupportReference}: {FailureKind}, SMTP reply {SmtpReplyCode}, {Attempts} attempt(s), retryable {Retryable}, {ElapsedMs} ms, correlation {CorrelationId}")]
    private static partial void LogNotSent(ILogger logger, Guid appUserId, string purpose, string supportReference, EmailFailureKind failureKind, int? smtpReplyCode,
                                           int attempts, bool retryable, long elapsedMs, string? correlationId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Email code for user {AppUserId} was submitted but its delivery could not be recorded, reference {SupportReference}, correlation {CorrelationId}")]
    private static partial void LogRecordFailed(ILogger logger, Exception exception, Guid appUserId, string supportReference, string? correlationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Email code check {Outcome} for user {AppUserId} ({Purpose}), {RemainingAttempts} attempt(s) left")]
    private static partial void LogVerify(ILogger logger, EmailCodeCheck outcome, Guid appUserId, string purpose, int remainingAttempts);
}
