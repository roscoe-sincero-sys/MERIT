using System.Net.Http.Json;
using System.Text.Json;
using Merit.Application.Email;
using Merit.Web.Infrastructure.Email;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Merit.Tests;

/// <summary>Runs only when the variable names a running local mail tool (start one with tools/mail/Start-MailTool.ps1).</summary>
public sealed class MailToolFactAttribute : FactAttribute
{
    public MailToolFactAttribute(string variable)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)))
        {
            Skip = $"Set {variable} (for example http://127.0.0.1:8025) with the tool running to run this test.";
        }
    }
}

/// <summary>
/// The real SMTP sender against the two local capture tools, using only their documented APIs: Mailpit
/// (/api/v1/messages, /api/v1/message/{ID}/headers, DELETE /api/v1/messages, PUT /api/v1/chaos) and smtp4dev
/// (/api/messages, /api/messages/{id}). Both run bound to 127.0.0.1 with relaying off, and the recipient is at
/// example.test, so nothing can reach a real mailbox. Opt-in: MERIT_MAILPIT_URL (+ MERIT_MAILPIT_SMTP_PORT, default
/// 1025) and MERIT_SMTP4DEV_URL (+ MERIT_SMTP4DEV_SMTP_PORT, default 2525).
/// </summary>
public sealed class LocalMailToolTests
{
    private const string MailpitVariable = "MERIT_MAILPIT_URL";
    private const string Smtp4devVariable = "MERIT_SMTP4DEV_URL";

    [MailToolFact(MailpitVariable)]
    public async Task MailpitCapturesTheMessageWithItsHeaders()
    {
        using var api = Api(MailpitVariable);
        var message = Message();
        var result = await Sender(Port("MERIT_MAILPIT_SMTP_PORT", 1025)).SendAsync(message);
        Assert.True(result.Submitted, result.Failure.ToString());

        string id = await MailpitIdAsync(api, message);
        using var headers = JsonDocument.Parse(await api.GetStringAsync($"/api/v1/message/{id}/headers"));
        Assert.Equal("auto-generated", headers.RootElement.GetProperty("Auto-Submitted")[0].GetString());
        Assert.Equal(message.Reference, headers.RootElement.GetProperty("X-Merit-Reference")[0].GetString());
        using var deleted = await api.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/v1/messages") { Content = JsonContent.Create(new { IDs = new[] { id } }) });
        deleted.EnsureSuccessStatusCode();
    }

    [MailToolFact(MailpitVariable)]
    public async Task MailpitChaosTemporaryRecipientErrorsAreRetriedThenReported()
    {
        using var api = Api(MailpitVariable);
        await using var chaos = await Chaos.SetAsync(api, recipient: (451, 100));
        var result = await Sender(Port("MERIT_MAILPIT_SMTP_PORT", 1025)).SendAsync(Message());
        Assert.False(result.Submitted);
        Assert.Equal(EmailFailureKind.TemporaryRejection, result.Failure);
        Assert.Equal(451, result.SmtpReplyCode);
        Assert.Equal(3, result.Attempts);
    }

    [MailToolFact(MailpitVariable)]
    public async Task MailpitChaosPermanentSenderAndRecipientErrorsAreNotRetried()
    {
        using var api = Api(MailpitVariable);
        await using (await Chaos.SetAsync(api, sender: (550, 100)))
        {
            var result = await Sender(Port("MERIT_MAILPIT_SMTP_PORT", 1025)).SendAsync(Message());
            Assert.Equal(EmailFailureKind.SenderRejected, result.Failure);
            Assert.Equal(550, result.SmtpReplyCode);
            Assert.Equal(1, result.Attempts);
        }

        await using (await Chaos.SetAsync(api, recipient: (550, 100)))
        {
            var result = await Sender(Port("MERIT_MAILPIT_SMTP_PORT", 1025)).SendAsync(Message());
            Assert.Equal(EmailFailureKind.RecipientRejected, result.Failure);
            Assert.Equal(1, result.Attempts);
        }
    }

    [MailToolFact(MailpitVariable)]
    public async Task MailpitRecoversWhenChaosIsTurnedOff()
    {
        using var api = Api(MailpitVariable);
        await using (await Chaos.SetAsync(api, recipient: (451, 100)))
        {
            Assert.False((await Sender(Port("MERIT_MAILPIT_SMTP_PORT", 1025), attempts: 1).SendAsync(Message())).Submitted);
        }

        var message = Message();
        Assert.True((await Sender(Port("MERIT_MAILPIT_SMTP_PORT", 1025)).SendAsync(message)).Submitted);
        string id = await MailpitIdAsync(api, message);
        using var deleted = await api.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/v1/messages") { Content = JsonContent.Create(new { IDs = new[] { id } }) });
        deleted.EnsureSuccessStatusCode();
    }

    [MailToolFact(Smtp4devVariable)]
    public async Task Smtp4devCapturesTheMessage()
    {
        using var api = Api(Smtp4devVariable);
        var message = Message();
        var result = await Sender(Port("MERIT_SMTP4DEV_SMTP_PORT", 2525)).SendAsync(message);
        Assert.True(result.Submitted, result.Failure.ToString());

        string? id = null;
        for (int i = 0; i < 20 && id is null; i++)
        {
            using var list = JsonDocument.Parse(await api.GetStringAsync("/api/messages?pageSize=50"));
            id = list.RootElement.GetProperty("results").EnumerateArray()
                .Where(m => m.GetProperty("subject").GetString() == message.Subject
                            && m.GetProperty("to").EnumerateArray().Any(t => t.GetString() == message.To))
                .Select(m => m.GetProperty("id").GetString()).FirstOrDefault();
            if (id is null)
            {
                await Task.Delay(250);
            }
        }

        Assert.NotNull(id);
        using var detail = JsonDocument.Parse(await api.GetStringAsync($"/api/messages/{id}"));
        Assert.Equal(message.Subject, detail.RootElement.GetProperty("subject").GetString());
        Assert.False(detail.RootElement.GetProperty("secureConnection").GetBoolean()); // loopback, plain text by design
    }

    private static async Task<string> MailpitIdAsync(HttpClient api, OutgoingEmail message)
    {
        for (int i = 0; i < 20; i++)
        {
            using var list = JsonDocument.Parse(await api.GetStringAsync("/api/v1/messages?limit=50"));
            var match = list.RootElement.GetProperty("messages").EnumerateArray()
                .FirstOrDefault(m => m.GetProperty("Subject").GetString() == message.Subject
                                     && m.GetProperty("To").EnumerateArray().Any(t => t.GetProperty("Address").GetString() == message.To));
            if (match.ValueKind == JsonValueKind.Object)
            {
                return match.GetProperty("ID").GetString()!;
            }

            await Task.Delay(250);
        }

        throw new Xunit.Sdk.XunitException("Mailpit did not capture the message.");
    }

    /// <summary>A unique synthetic message: the subject carries a fresh reference so concurrent runs do not collide.</summary>
    private static OutgoingEmail Message()
    {
        string reference = "EM-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        return new OutgoingEmail($"synthetic.{reference.ToLowerInvariant()}@example.test", $"MERIT test message {reference}",
            "Your MERIT verification code is 000000 (synthetic test message).", reference, "local-tool-test");
    }

    private static SmtpEmailSender Sender(int port, int attempts = 3)
        => new(Options.Create(new EmailOptions
        {
            Provider = EmailProvider.Smtp,
            Smtp = new SmtpOptions
            {
                Host = "127.0.0.1", Port = port, Security = SmtpSecurity.None, FromAddress = "merit-test@localhost.test",
                MaxAttempts = attempts, RetryDelayMilliseconds = 50, ConnectTimeoutSeconds = 5, OperationTimeoutSeconds = 5, TotalTimeoutSeconds = 20,
            },
        }), NullLogger<SmtpEmailSender>.Instance);

    private static HttpClient Api(string variable) => new() { BaseAddress = new Uri(Environment.GetEnvironmentVariable(variable)!) };

    private static int Port(string variable, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(variable), out int p) ? p : fallback;

    /// <summary>Mailpit's chaos triggers, set for one test and always turned off again.</summary>
    private sealed class Chaos(HttpClient api) : IAsyncDisposable
    {
        public static async Task<Chaos> SetAsync(HttpClient api, (int Code, int Probability)? sender = null, (int Code, int Probability)? recipient = null)
        {
            await PutAsync(api, sender ?? (451, 0), recipient ?? (451, 0));
            return new Chaos(api);
        }

        public async ValueTask DisposeAsync() => await PutAsync(api, (451, 0), (451, 0));

        private static async Task PutAsync(HttpClient api, (int Code, int Probability) sender, (int Code, int Probability) recipient)
        {
            using var response = await api.PutAsJsonAsync("/api/v1/chaos", new
            {
                Sender = new { ErrorCode = sender.Code, Probability = sender.Probability },
                Recipient = new { ErrorCode = recipient.Code, Probability = recipient.Probability },
                Authentication = new { ErrorCode = 535, Probability = 0 },
            });
            response.EnsureSuccessStatusCode();
        }
    }
}
