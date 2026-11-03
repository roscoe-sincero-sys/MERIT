namespace Merit.Application.Email;

/// <summary>
/// A plain-text message to one recipient. The body can hold a one-time code: nothing that receives an
/// <see cref="OutgoingEmail"/> may log its body, and only the reference and correlation id identify it in logs.
/// </summary>
public sealed record OutgoingEmail(string To, string Subject, string TextBody, string Reference, string CorrelationId);

/// <summary>Why a message was not submitted, in categories that are safe to log and store. Never the server's own text.</summary>
public enum EmailFailureKind
{
    None,
    NotConfigured,
    Dns,
    ConnectionRefused,
    Unreachable,
    Timeout,
    ServiceUnavailable,
    TlsHandshake,
    CertificateExpired,
    CertificateUntrusted,
    CertificateNameMismatch,
    CertificateInvalid,
    StartTlsUnavailable,
    AuthenticationFailed,
    AuthenticationUnavailable,
    SenderRejected,
    RecipientRejected,
    MessageRejected,
    TemporaryRejection,
    Protocol,
    Canceled,
}

/// <summary>
/// The outcome of one send. Submitted means the mail server accepted the message for delivery; it does not mean the
/// message reached the mailbox, which SMTP cannot confirm. Attempts counts connections, retries included.
/// </summary>
public sealed record EmailSendResult(bool Submitted, EmailFailureKind Failure, bool Retryable, int Attempts, int? SmtpReplyCode, long ElapsedMilliseconds)
{
    public static EmailSendResult Accepted(int attempts, long elapsedMilliseconds) => new(true, EmailFailureKind.None, false, attempts, null, elapsedMilliseconds);

    public static EmailSendResult Failed(EmailFailureKind failure, bool retryable, int attempts, int? smtpReplyCode, long elapsedMilliseconds)
        => new(false, failure, retryable, attempts, smtpReplyCode, elapsedMilliseconds);
}

/// <summary>
/// Sends email. The web application registers the SMTP implementation when a mail server is configured, and a
/// disabled one otherwise, so features that need email can hide themselves instead of failing.
/// </summary>
public interface IEmailSender
{
    /// <summary>False when no mail server is configured.</summary>
    bool IsConfigured { get; }

    Task<EmailSendResult> SendAsync(OutgoingEmail message, CancellationToken ct = default);
}

/// <summary>No mail server configured: nothing is sent and email-based features stay hidden.</summary>
public sealed class DisabledEmailSender : IEmailSender
{
    public bool IsConfigured => false;

    public Task<EmailSendResult> SendAsync(OutgoingEmail message, CancellationToken ct = default)
        => Task.FromResult(EmailSendResult.Failed(EmailFailureKind.NotConfigured, false, 0, null, 0));
}
