using System.Diagnostics;
using Merit.Application.Email;
using Merit.Web.Infrastructure.Email;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Merit.Tests;

/// <summary>
/// SMTP fault injection against the real MailKit sender: every failure is classified, retried only when it cannot have
/// delivered the message, and bounded in time; certificates are always checked; STARTTLS cannot be stripped. The
/// server is <see cref="FakeSmtpServer"/> on 127.0.0.1 and the recipient is at example.test.
/// </summary>
public sealed class SmtpFaultInjectionTests : IClassFixture<TestCertificateFixture>
{
    private static readonly OutgoingEmail Message = new("synthetic.user@example.test", "Your MERIT verification code",
        "Your MERIT verification code is 123456", "EM-TEST2345", "test-correlation");

    private readonly TestCertificates certs;

    public SmtpFaultInjectionTests(TestCertificateFixture fixture) => certs = fixture.Certificates;

    [Fact]
    public async Task StartTlsWithAuthenticationAgainstATrustedPrivateAuthoritySubmits()
    {
        await using var server = new FakeSmtpServer(new SmtpScript { OfferStartTls = true, Certificate = certs.Valid, Credentials = ("merit", "s3cret-pass") });
        var result = await Sender(server.Port, SmtpSecurity.StartTls, user: "merit", password: "s3cret-pass").SendAsync(Message);

        Assert.True(result.Submitted, result.Failure.ToString());
        Assert.Equal(1, result.Attempts);
        var raw = Assert.Single(server.Delivered);
        Assert.Contains("Auto-Submitted: auto-generated", raw, StringComparison.Ordinal);
        Assert.Contains("X-Merit-Reference: EM-TEST2345", raw, StringComparison.Ordinal);
        Assert.Equal("merit", Assert.Single(server.AuthenticatedUsers));
        // The credentials went only over TLS: AUTH comes after STARTTLS.
        var commands = server.Commands.ToList();
        Assert.True(commands.IndexOf("STARTTLS") < commands.IndexOf("AUTH"));
    }

    [Fact]
    public async Task ImplicitTlsSubmits()
    {
        await using var server = new FakeSmtpServer(new SmtpScript { ImplicitTls = true, Certificate = certs.Valid });
        var result = await Sender(server.Port, SmtpSecurity.SslOnConnect).SendAsync(Message);
        Assert.True(result.Submitted, result.Failure.ToString());
        Assert.Single(server.Delivered);
    }

    [Fact] // 1. SMTP server unavailable (421 greeting): temporary, retried, then reported.
    public async Task AnUnavailableServerIsRetriedThenReported()
    {
        await using var server = new FakeSmtpServer(new SmtpScript { Greeting = "421 4.3.2 Service not available" });
        var result = await Sender(server.Port).SendAsync(Message);
        AssertFailed(result, EmailFailureKind.ServiceUnavailable, retryable: true, attempts: 3);
        Assert.Equal(421, result.SmtpReplyCode);
        Assert.Equal(3, server.Connections);
    }

    [Fact] // 2. Connection refused.
    public async Task ARefusedConnectionIsRetriedThenReported()
    {
        var result = await Sender(FakeSmtpServer.UnusedPort()).SendAsync(Message);
        AssertFailed(result, EmailFailureKind.ConnectionRefused, retryable: true, attempts: 3);
    }

    [Fact] // 3. DNS resolution failure: a name that cannot resolve (RFC 6761 .invalid) is not retried.
    public async Task AnUnresolvableHostIsNotRetried()
    {
        var result = await Sender(25, host: "merit-smtp.invalid").SendAsync(Message);
        AssertFailed(result, EmailFailureKind.Dns, retryable: false, attempts: 1);
    }

    [Fact] // 4. Connection timeout: a server that never answers cannot hold the page past the overall budget.
    public async Task ASilentServerTimesOutWithinTheBudget()
    {
        await using var server = new FakeSmtpServer(new SmtpScript { Silent = true });
        var clock = Stopwatch.StartNew();
        var result = await Sender(server.Port, operationTimeout: 1, totalTimeout: 4, attempts: 5).SendAsync(Message);
        Assert.False(result.Submitted);
        Assert.Equal(EmailFailureKind.Timeout, result.Failure);
        Assert.InRange(clock.Elapsed.TotalSeconds, 0.5, 6.5);
        Assert.Empty(server.Received);
    }

    [Fact] // 5. Authentication rejected (535): permanent, one attempt, nothing sent.
    public async Task RejectedCredentialsAreNotRetried()
    {
        await using var server = new FakeSmtpServer(new SmtpScript { OfferStartTls = true, Certificate = certs.Valid, Credentials = ("merit", "right") });
        var result = await Sender(server.Port, SmtpSecurity.StartTls, user: "merit", password: "wrong").SendAsync(Message);
        AssertFailed(result, EmailFailureKind.AuthenticationFailed, retryable: false, attempts: 1);
        Assert.Empty(server.Received);
    }

    [Fact] // 6. Expired certificate.
    public async Task AnExpiredCertificateIsRefused()
    {
        await using var server = new FakeSmtpServer(new SmtpScript { OfferStartTls = true, Certificate = certs.Expired });
        var result = await Sender(server.Port, SmtpSecurity.StartTls).SendAsync(Message);
        AssertFailed(result, EmailFailureKind.CertificateExpired, retryable: false, attempts: 1);
        Assert.Empty(server.Received);
    }

    [Fact] // 7. Untrusted certificate authority.
    public async Task ACertificateFromAnUntrustedAuthorityIsRefused()
    {
        await using var server = new FakeSmtpServer(new SmtpScript { ImplicitTls = true, Certificate = certs.Untrusted });
        var result = await Sender(server.Port, SmtpSecurity.SslOnConnect).SendAsync(Message);
        AssertFailed(result, EmailFailureKind.CertificateUntrusted, retryable: false, attempts: 1);
    }

    [Fact] // 7b. Without the private root configured, even a well-formed private certificate is refused: no "accept any".
    public async Task APrivateAuthorityIsTrustedOnlyWhenConfigured()
    {
        await using var server = new FakeSmtpServer(new SmtpScript { OfferStartTls = true, Certificate = certs.Valid });
        var result = await Sender(server.Port, SmtpSecurity.StartTls, trustRoot: false).SendAsync(Message);
        AssertFailed(result, EmailFailureKind.CertificateUntrusted, retryable: false, attempts: 1);
    }

    [Fact] // 8. Hostname mismatch.
    public async Task ACertificateForAnotherHostIsRefused()
    {
        await using var server = new FakeSmtpServer(new SmtpScript { OfferStartTls = true, Certificate = certs.WrongHost });
        var result = await Sender(server.Port, SmtpSecurity.StartTls).SendAsync(Message);
        AssertFailed(result, EmailFailureKind.CertificateNameMismatch, retryable: false, attempts: 1);
    }

    [Fact] // 9. STARTTLS unavailable (or stripped): nothing but EHLO is said in plain text.
    public async Task AServerWithoutStartTlsGetsNeitherCredentialsNorTheMessage()
    {
        await using var server = new FakeSmtpServer(new SmtpScript { OfferStartTls = false, Credentials = ("merit", "s3cret-pass") });
        var result = await Sender(server.Port, SmtpSecurity.StartTls, user: "merit", password: "s3cret-pass").SendAsync(Message);
        AssertFailed(result, EmailFailureKind.StartTlsUnavailable, retryable: false, attempts: 1);
        Assert.DoesNotContain("AUTH", server.Commands);
        Assert.DoesNotContain(server.Commands, c => c.StartsWith("MAIL", StringComparison.Ordinal));
    }

    [Fact] // 10. Temporary rejection (451): retried, then reported.
    public async Task ATemporaryRejectionIsRetried()
    {
        await using var server = new FakeSmtpServer(new SmtpScript { MailReply = "451 4.3.0 Try again later" });
        var result = await Sender(server.Port).SendAsync(Message);
        AssertFailed(result, EmailFailureKind.TemporaryRejection, retryable: true, attempts: 3);
        Assert.Equal(451, result.SmtpReplyCode);
    }

    [Fact] // 11. Permanent rejection (550 after the message): not retried.
    public async Task APermanentRejectionIsNotRetried()
    {
        await using var server = new FakeSmtpServer(new SmtpScript { DataReply = "550 5.7.1 Message refused" });
        var result = await Sender(server.Port).SendAsync(Message);
        AssertFailed(result, EmailFailureKind.MessageRejected, retryable: false, attempts: 1);
        Assert.Equal(550, result.SmtpReplyCode);
        Assert.Equal(1, server.Connections);
    }

    [Fact] // 12. Recipient rejection.
    public async Task ARejectedRecipientIsNotRetried()
    {
        await using var server = new FakeSmtpServer(new SmtpScript { RecipientReply = "550 5.1.1 No such user" });
        var result = await Sender(server.Port).SendAsync(Message);
        AssertFailed(result, EmailFailureKind.RecipientRejected, retryable: false, attempts: 1);
        Assert.Empty(server.Received);
    }

    [Fact] // 12b. A sender the relay refuses.
    public async Task ARejectedSenderIsNotRetried()
    {
        await using var server = new FakeSmtpServer(new SmtpScript { MailReply = "553 5.7.1 Sender not allowed" });
        var result = await Sender(server.Port).SendAsync(Message);
        AssertFailed(result, EmailFailureKind.SenderRejected, retryable: false, attempts: 1);
    }

    [Fact] // 13. Intermittent: the first connection fails temporarily, the second delivers.
    public async Task AnIntermittentFailureSucceedsOnRetry()
    {
        await using var server = new FakeSmtpServer(new SmtpScript { MailReply = "451 4.4.1 Busy" }, new SmtpScript());
        var result = await Sender(server.Port).SendAsync(Message);
        Assert.True(result.Submitted);
        Assert.Equal(2, result.Attempts);
        Assert.Single(server.Delivered);
    }

    [Fact] // 13b. A connection dropped before the message is retried.
    public async Task ADroppedConnectionBeforeTheMessageIsRetried()
    {
        await using var server = new FakeSmtpServer(new SmtpScript { DropBeforeMessage = true }, new SmtpScript());
        var result = await Sender(server.Port).SendAsync(Message);
        Assert.True(result.Submitted);
        Assert.Equal(2, result.Attempts);
        Assert.Single(server.Delivered);
    }

    [Fact] // 14. Accepted but not delivered: SMTP can say "accepted", never "in the mailbox". MERIT reports submitted.
    public async Task AcceptedButUndeliveredIsReportedAsSubmittedOnly()
    {
        await using var server = new FakeSmtpServer(new SmtpScript { Discard = true });
        var result = await Sender(server.Port).SendAsync(Message);
        Assert.True(result.Submitted);
        Assert.Single(server.Received);
        Assert.Empty(server.Delivered);
    }

    [Fact] // 14b. The connection drops after the message: the server may have it, so it is never sent twice.
    public async Task ADropAfterTheMessageIsNotRetried()
    {
        await using var server = new FakeSmtpServer(new SmtpScript { DropAfterData = true }, new SmtpScript());
        var result = await Sender(server.Port).SendAsync(Message);
        Assert.False(result.Submitted);
        Assert.False(result.Retryable);
        Assert.Equal(1, result.Attempts);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task AnInvalidRecipientAddressNeverConnects()
    {
        await using var server = new FakeSmtpServer();
        var result = await Sender(server.Port).SendAsync(Message with { To = "not an address" });
        Assert.Equal(EmailFailureKind.RecipientRejected, result.Failure);
        Assert.Equal(0, server.Connections);
    }

    [Fact]
    public async Task CancellationByTheCallerStopsTheSend()
    {
        await using var server = new FakeSmtpServer(new SmtpScript { Silent = true });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var result = await Sender(server.Port, operationTimeout: 10, totalTimeout: 20).SendAsync(Message, cts.Token);
        Assert.Equal(EmailFailureKind.Canceled, result.Failure);
        Assert.Equal(1, result.Attempts);
    }

    [Theory]
    [InlineData("smtp.example.gov", SmtpSecurity.None, false)]
    [InlineData("localhost", SmtpSecurity.None, true)]
    [InlineData("127.0.0.1", SmtpSecurity.None, true)]
    [InlineData("::1", SmtpSecurity.None, true)]
    [InlineData("smtp.example.gov", SmtpSecurity.StartTls, true)]
    public void PlainTextIsAllowedOnlyForAMailServerOnThisMachine(string host, SmtpSecurity security, bool valid)
    {
        var options = new EmailOptions { Provider = EmailProvider.Smtp, Smtp = new SmtpOptions { Host = host, Security = security, FromAddress = "merit@example.test" } };
        Assert.Equal(valid, new EmailOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void IncompleteOrUnsafeConfigurationIsRefusedAtStartup()
    {
        var validator = new EmailOptionsValidator();
        Assert.True(validator.Validate(null, new EmailOptions()).Succeeded); // Disabled needs nothing.
        Assert.False(validator.Validate(null, new EmailOptions { Provider = EmailProvider.Smtp }).Succeeded);
        Assert.False(validator.Validate(null, new EmailOptions
        {
            Provider = EmailProvider.Smtp,
            Smtp = new SmtpOptions { Host = "smtp.example.gov", FromAddress = "merit@example.test", UserName = "merit" },
        }).Succeeded);
        Assert.False(validator.Validate(null, new EmailOptions
        {
            Provider = EmailProvider.Smtp,
            Smtp = new SmtpOptions { Host = "smtp.example.gov", FromAddress = "merit@example.test", MaxAttempts = 50 },
        }).Succeeded);
    }

    private SmtpEmailSender Sender(int port, SmtpSecurity security = SmtpSecurity.None, string host = "127.0.0.1", string? user = null, string? password = null,
                                   int operationTimeout = 5, int totalTimeout = 20, int attempts = 3, bool trustRoot = true)
    {
        var options = new EmailOptions
        {
            Provider = EmailProvider.Smtp,
            Smtp = new SmtpOptions
            {
                Host = security == SmtpSecurity.None ? host : (host == "127.0.0.1" ? "localhost" : host),
                Port = port,
                Security = security,
                UserName = user,
                Password = password,
                FromAddress = "merit@example.test",
                ConnectTimeoutSeconds = 3,
                OperationTimeoutSeconds = operationTimeout,
                TotalTimeoutSeconds = totalTimeout,
                MaxAttempts = attempts,
                RetryDelayMilliseconds = 20,
                TrustedRootCertificatePath = trustRoot ? certs.TrustedRootPath : null,
                CheckCertificateRevocation = false,
            },
        };
        return new SmtpEmailSender(Options.Create(options), NullLogger<SmtpEmailSender>.Instance);
    }

    private static void AssertFailed(EmailSendResult result, EmailFailureKind kind, bool retryable, int attempts)
    {
        Assert.False(result.Submitted);
        Assert.Equal(kind, result.Failure);
        Assert.Equal(retryable, result.Retryable);
        Assert.Equal(attempts, result.Attempts);
    }
}

public sealed class TestCertificateFixture : IDisposable
{
    public TestCertificates Certificates { get; } = new();

    public void Dispose() => Certificates.Dispose();
}
