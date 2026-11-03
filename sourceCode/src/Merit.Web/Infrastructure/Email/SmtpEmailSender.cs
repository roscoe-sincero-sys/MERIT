using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Merit.Application.Email;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Merit.Web.Infrastructure.Email;

/// <summary>
/// Sends mail through an SMTP relay with MailKit. Every send has a connect timeout, a per-command timeout and an overall
/// budget, so a slow or silent server cannot hold a sign-in page open. A failure is classified (DNS, refused, timeout,
/// TLS, certificate, authentication, rejected sender or recipient, temporary or permanent reply) and retried, with
/// growing waits, only when it cannot have delivered the message: a 4xx reply, or a connection, TLS or sign-in
/// failure before the message was handed over. A failure once the DATA command has gone out is never retried,
/// because the server may already have accepted the message. Certificates are always validated; StartTls refuses a server that
/// does not offer STARTTLS. Logs carry the stage, category and numeric reply only: never the message, the
/// recipient, the credentials or the server's text.
/// </summary>
public sealed partial class SmtpEmailSender : IEmailSender
{
    private readonly SmtpOptions smtp;
    private readonly ILogger<SmtpEmailSender> logger;
    private readonly X509Certificate2? trustedRoot;

    public SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger)
    {
        smtp = options.Value.Smtp;
        this.logger = logger;
        trustedRoot = string.IsNullOrWhiteSpace(smtp.TrustedRootCertificatePath) ? null : LoadCertificate(smtp.TrustedRootCertificatePath);
    }

    private enum Stage
    {
        Connect,
        Secure,
        Authenticate,
        Transfer,
    }

    public bool IsConfigured => true;

    public async Task<EmailSendResult> SendAsync(OutgoingEmail message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var clock = Stopwatch.StartNew();
        MimeMessage mime;
        try
        {
            mime = Build(message);
        }
        catch (ParseException)
        {
            LogAttempt(logger, message.Reference, 0, Stage.Connect, EmailFailureKind.RecipientRejected, null, false, message.CorrelationId);
            return EmailSendResult.Failed(EmailFailureKind.RecipientRejected, false, 0, null, clock.ElapsedMilliseconds);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(smtp.TotalTimeoutSeconds));
        EmailSendResult last = EmailSendResult.Failed(EmailFailureKind.Timeout, true, 0, null, 0);
        for (int attempt = 1; attempt <= smtp.MaxAttempts; attempt++)
        {
            var outcome = await AttemptAsync(mime, attempt, message, budget.Token, ct).ConfigureAwait(false);
            last = outcome with { Attempts = attempt, ElapsedMilliseconds = clock.ElapsedMilliseconds };
            if (last.Submitted || !last.Retryable || attempt == smtp.MaxAttempts)
            {
                break;
            }

            try
            {
                int wait = (smtp.RetryDelayMilliseconds << (attempt - 1)) + RandomNumberGenerator.GetInt32(0, Math.Max(1, smtp.RetryDelayMilliseconds / 2));
                await Task.Delay(wait, budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                last = EmailSendResult.Failed(ct.IsCancellationRequested ? EmailFailureKind.Canceled : EmailFailureKind.Timeout, false, attempt, last.SmtpReplyCode,
                    clock.ElapsedMilliseconds);
                break;
            }
        }

        return last;
    }

    private async Task<EmailSendResult> AttemptAsync(MimeMessage mime, int attempt, OutgoingEmail message, CancellationToken budget, CancellationToken caller)
    {
        var stage = Stage.Connect;
        var tls = new TlsObservation();
        var data = new DataCommandObserver();
        using var client = new SmtpClient(data)
        {
            Timeout = (int)TimeSpan.FromSeconds(smtp.OperationTimeoutSeconds).TotalMilliseconds,
            CheckCertificateRevocation = smtp.CheckCertificateRevocation,
            ServerCertificateValidationCallback = (_, certificate, chain, errors) => Validate(certificate, chain, errors, tls),
        };
        if (!string.IsNullOrWhiteSpace(smtp.ClientName))
        {
            client.LocalDomain = smtp.ClientName;
        }

        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(budget))
            {
                connect.CancelAfter(TimeSpan.FromSeconds(smtp.ConnectTimeoutSeconds));
                var socketOptions = smtp.Security switch
                {
                    SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
                    // Detected below rather than by MailKit, so a missing STARTTLS is reported as exactly that.
                    SmtpSecurity.StartTls => SecureSocketOptions.StartTlsWhenAvailable,
                    _ => SecureSocketOptions.None,
                };
                await client.ConnectAsync(smtp.Host!, smtp.Port, socketOptions, connect.Token).ConfigureAwait(false);
            }

            stage = Stage.Secure;
            if (smtp.Security != SmtpSecurity.None && !client.IsSecure)
            {
                // Nothing but EHLO has been said in plain text; leave before the credentials or the message.
                await client.DisconnectAsync(true, budget).ConfigureAwait(false);
                return Fail(message, attempt, stage, EmailFailureKind.StartTlsUnavailable, false, null);
            }

            stage = Stage.Authenticate;
            if (!string.IsNullOrEmpty(smtp.UserName))
            {
                if (!client.Capabilities.HasFlag(SmtpCapabilities.Authentication))
                {
                    await client.DisconnectAsync(true, budget).ConfigureAwait(false);
                    return Fail(message, attempt, stage, EmailFailureKind.AuthenticationUnavailable, false, null);
                }

                await client.AuthenticateAsync(smtp.UserName, smtp.Password ?? string.Empty, budget).ConfigureAwait(false);
            }

            stage = Stage.Transfer;
            await client.SendAsync(mime, budget).ConfigureAwait(false);
            try
            {
                await client.DisconnectAsync(true, budget).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or SmtpProtocolException or OperationCanceledException or TimeoutException)
            {
                // The message was accepted before QUIT; a lost goodbye changes nothing.
            }

            return EmailSendResult.Accepted(attempt, 0);
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested)
        {
            return Fail(message, attempt, stage, EmailFailureKind.Canceled, false, null);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // A timeout once DATA was sent may have delivered the message: never send it twice.
            return Fail(message, attempt, stage, EmailFailureKind.Timeout, !data.Sent, null);
        }
        catch (SocketException ex)
        {
            var kind = ex.SocketErrorCode switch
            {
                SocketError.HostNotFound or SocketError.NoData => EmailFailureKind.Dns,
                SocketError.TryAgain => EmailFailureKind.Dns,
                SocketError.ConnectionRefused => EmailFailureKind.ConnectionRefused,
                SocketError.TimedOut => EmailFailureKind.Timeout,
                _ => EmailFailureKind.Unreachable,
            };
            bool retry = !data.Sent && (kind != EmailFailureKind.Dns || ex.SocketErrorCode == SocketError.TryAgain);
            return Fail(message, attempt, stage, kind, retry, null);
        }
        catch (SslHandshakeException)
        {
            return Fail(message, attempt, stage, tls.Classify(), false, null);
        }
        catch (AuthenticationException)
        {
            return Fail(message, attempt, stage, EmailFailureKind.AuthenticationFailed, false, 535);
        }
        catch (SmtpCommandException ex)
        {
            int status = (int)ex.StatusCode;
            bool temporary = status is >= 400 and < 500;
            var kind = temporary
                ? (status == 421 ? EmailFailureKind.ServiceUnavailable : EmailFailureKind.TemporaryRejection)
                : ex.ErrorCode switch
                {
                    SmtpErrorCode.SenderNotAccepted => EmailFailureKind.SenderRejected,
                    SmtpErrorCode.RecipientNotAccepted => EmailFailureKind.RecipientRejected,
                    SmtpErrorCode.MessageNotAccepted => EmailFailureKind.MessageRejected,
                    _ => stage == Stage.Connect ? EmailFailureKind.ServiceUnavailable : EmailFailureKind.MessageRejected,
                };
            return Fail(message, attempt, stage, kind, temporary, status);
        }
        catch (Exception ex) when (ex is SmtpProtocolException or IOException or ServiceNotConnectedException)
        {
            // The connection dropped. Before DATA nothing can have been delivered, so another attempt is safe.
            return Fail(message, attempt, stage, EmailFailureKind.Protocol, !data.Sent, null);
        }
        catch (NotSupportedException)
        {
            return Fail(message, attempt, stage, stage == Stage.Authenticate ? EmailFailureKind.AuthenticationUnavailable : EmailFailureKind.Protocol, false, null);
        }
    }

    private EmailSendResult Fail(OutgoingEmail message, int attempt, Stage stage, EmailFailureKind kind, bool retryable, int? status)
    {
        LogAttempt(logger, message.Reference, attempt, stage, kind, status, retryable, message.CorrelationId);
        return EmailSendResult.Failed(kind, retryable, attempt, status, 0);
    }

    private MimeMessage Build(OutgoingEmail message)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(smtp.FromName, smtp.FromAddress!));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new TextPart(MimeKit.Text.TextFormat.Plain) { Text = message.TextBody };
        // Out-of-office replies and mailing-list software leave automatic mail alone (RFC 3834).
        mime.Headers.Add("Auto-Submitted", "auto-generated");
        mime.Headers.Add("X-Merit-Reference", message.Reference);
        return mime;
    }

    /// <summary>The platform's verdict, or the private root's when one is configured. A host name mismatch is never accepted.</summary>
    private bool Validate(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors, TlsObservation tls)
    {
        tls.Errors = errors;
        tls.ChainStatus = chain?.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (all, s) => all | s.Status) ?? X509ChainStatusFlags.NoError;
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        if (trustedRoot is null || errors != SslPolicyErrors.RemoteCertificateChainErrors || certificate is null)
        {
            return false;
        }

        using var leaf = new X509Certificate2(certificate);
        using var custom = new X509Chain();
        custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        custom.ChainPolicy.CustomTrustStore.Add(trustedRoot);
        // A private test authority publishes no revocation list.
        custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (chain is not null)
        {
            foreach (var element in chain.ChainElements)
            {
                custom.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
        }

        bool ok = custom.Build(leaf);
        tls.ChainStatus = custom.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (all, s) => all | s.Status);
        return ok;
    }

    private static X509Certificate2 LoadCertificate(string path)
    {
        string text = File.ReadAllText(path);
        return text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)
            ? X509Certificate2.CreateFromPem(text)
            : X509CertificateLoader.LoadCertificateFromFile(path);
    }

    /// <summary>
    /// Watches the client's side of the conversation for one thing only: whether the DATA (or BDAT) command has gone out.
    /// Until then a failure cannot have delivered the message. It keeps and writes nothing, so credentials and the
    /// message never leave it.
    /// </summary>
    private sealed class DataCommandObserver : IProtocolLogger
    {
        private static ReadOnlySpan<byte> CrLfData => "\r\nDATA"u8;

        private static ReadOnlySpan<byte> CrLfBdat => "\r\nBDAT"u8;

        public bool Sent { get; private set; }

        public IAuthenticationSecretDetector? AuthenticationSecretDetector { get; set; }

        public void LogConnect(Uri uri)
        {
        }

        public void LogClient(byte[] buffer, int offset, int count)
        {
            if (Sent || count < 4)
            {
                return;
            }

            var span = buffer.AsSpan(offset, count);
            Sent = StartsWith(span, "DATA"u8) || StartsWith(span, "BDAT"u8) || span.IndexOf(CrLfData) >= 0 || span.IndexOf(CrLfBdat) >= 0;
        }

        public void LogServer(byte[] buffer, int offset, int count)
        {
        }

        public void Dispose()
        {
        }

        private static bool StartsWith(ReadOnlySpan<byte> span, ReadOnlySpan<byte> prefix)
            => span.Length >= prefix.Length && System.Text.Ascii.EqualsIgnoreCase(span[..prefix.Length], prefix);
    }

    /// <summary>What certificate validation saw, so a failed handshake can say why.</summary>
    private sealed class TlsObservation
    {
        public SslPolicyErrors? Errors { get; set; }

        public X509ChainStatusFlags ChainStatus { get; set; }

        public EmailFailureKind Classify()
        {
            if (Errors is null)
            {
                return EmailFailureKind.TlsHandshake;
            }

            if (Errors.Value.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
            {
                return EmailFailureKind.CertificateNameMismatch;
            }

            if (ChainStatus.HasFlag(X509ChainStatusFlags.NotTimeValid))
            {
                return EmailFailureKind.CertificateExpired;
            }

            if ((ChainStatus & (X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain)) != 0)
            {
                return EmailFailureKind.CertificateUntrusted;
            }

            return Errors.Value == SslPolicyErrors.None ? EmailFailureKind.TlsHandshake : EmailFailureKind.CertificateInvalid;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "SMTP attempt {Attempt} for reference {Reference} failed at {Stage}: {FailureKind}, reply {SmtpReplyCode}, will retry {Retryable}, correlation {CorrelationId}")]
    private static partial void LogAttempt(ILogger logger, string reference, int attempt, Stage stage, EmailFailureKind failureKind, int? smtpReplyCode, bool retryable,
                                           string correlationId);
}
