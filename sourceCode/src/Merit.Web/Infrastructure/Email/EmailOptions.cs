using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace Merit.Web.Infrastructure.Email;

public enum EmailProvider
{
    /// <summary>No mail server: nothing is sent and email codes are not offered. The default everywhere.</summary>
    Disabled,

    Smtp,
}

public enum SmtpSecurity
{
    /// <summary>Connect in plain text, then require STARTTLS; a server that does not offer it is refused. Port 587.</summary>
    StartTls,

    /// <summary>TLS from the first byte (implicit TLS, "SMTPS"). Port 465.</summary>
    SslOnConnect,

    /// <summary>No encryption. Accepted only for a mail server on this machine (Mailpit, smtp4dev), never a remote one.</summary>
    None,
}

/// <summary>
/// Outgoing mail (configuration section Email). Credentials come from user secrets or environment variables
/// (Email__Smtp__Password), never from appsettings files. Certificate validation cannot be turned off; a private
/// certificate authority can be trusted by naming its root certificate.
/// </summary>
public sealed class EmailOptions
{
    public const string Section = "Email";

    public EmailProvider Provider { get; set; } = EmailProvider.Disabled;

    public SmtpOptions Smtp { get; set; } = new();
}

public sealed class SmtpOptions
{
    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;

    /// <summary>Leave empty for a relay that accepts mail without signing in (anonymous).</summary>
    public string? UserName { get; set; }

    public string? Password { get; set; }

    public string? FromAddress { get; set; }

    public string FromName { get; set; } = "MERIT";

    /// <summary>The name given in EHLO; the machine name when empty.</summary>
    public string? ClientName { get; set; }

    public int ConnectTimeoutSeconds { get; set; } = 10;

    /// <summary>Each SMTP command after connecting (EHLO, STARTTLS, AUTH, MAIL, RCPT, DATA).</summary>
    public int OperationTimeoutSeconds { get; set; } = 15;

    /// <summary>The whole send, retries and waits included. Keeps the person's page from waiting longer than this.</summary>
    public int TotalTimeoutSeconds { get; set; } = 25;

    /// <summary>Connections per message. Only failures that cannot have delivered the message are retried.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>First wait between attempts; it doubles each time, with jitter.</summary>
    public int RetryDelayMilliseconds { get; set; } = 750;

    /// <summary>PEM or DER root certificate of a private certificate authority the server's certificate chains to.</summary>
    public string? TrustedRootCertificatePath { get; set; }

    public bool CheckCertificateRevocation { get; set; } = true;
}

/// <summary>Refuses an unsafe or incomplete mail configuration at startup rather than at the first sign-in.</summary>
public sealed class EmailOptionsValidator : IValidateOptions<EmailOptions>
{
    public ValidateOptionsResult Validate(string? name, EmailOptions options)
    {
        if (options.Provider != EmailProvider.Smtp)
        {
            return ValidateOptionsResult.Success;
        }

        var smtp = options.Smtp;
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(smtp.Host))
        {
            problems.Add("Email:Smtp:Host is required when Email:Provider is Smtp.");
        }

        if (smtp.Port is < 1 or > 65535)
        {
            problems.Add("Email:Smtp:Port must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(smtp.FromAddress) || !MailAddress.TryCreate(smtp.FromAddress, out _))
        {
            problems.Add("Email:Smtp:FromAddress must be an email address.");
        }

        if (smtp.Security == SmtpSecurity.None && !string.IsNullOrWhiteSpace(smtp.Host) && !IsLoopback(smtp.Host))
        {
            problems.Add("Email:Smtp:Security None is allowed only for a mail server on this machine (localhost). Use StartTls or SslOnConnect.");
        }

        if (string.IsNullOrEmpty(smtp.UserName) != string.IsNullOrEmpty(smtp.Password))
        {
            problems.Add("Set both Email:Smtp:UserName and Email:Smtp:Password, or neither for an anonymous relay.");
        }

        if (smtp.ConnectTimeoutSeconds is < 1 or > 120 || smtp.OperationTimeoutSeconds is < 1 or > 120 || smtp.TotalTimeoutSeconds is < 1 or > 300)
        {
            problems.Add("Email:Smtp timeouts must be between 1 and 120 seconds (TotalTimeoutSeconds up to 300).");
        }

        if (smtp.MaxAttempts is < 1 or > 5 || smtp.RetryDelayMilliseconds is < 0 or > 10_000)
        {
            problems.Add("Email:Smtp:MaxAttempts must be 1 to 5 and RetryDelayMilliseconds 0 to 10000.");
        }

        if (!string.IsNullOrWhiteSpace(smtp.TrustedRootCertificatePath) && !File.Exists(smtp.TrustedRootCertificatePath))
        {
            problems.Add("Email:Smtp:TrustedRootCertificatePath names a file that does not exist.");
        }

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }

    public static bool IsLoopback(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
           || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
}
