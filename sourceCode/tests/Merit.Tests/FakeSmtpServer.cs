using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Merit.Tests;

/// <summary>How the fake server behaves on one connection. Null replies mean "250 OK".</summary>
public sealed record SmtpScript
{
    /// <summary>The greeting; "421 ..." makes the service unavailable.</summary>
    public string Greeting { get; init; } = "220 fake.test ESMTP ready";

    /// <summary>Accept the connection and never say anything.</summary>
    public bool Silent { get; init; }

    public bool OfferStartTls { get; init; }

    /// <summary>The certificate for STARTTLS or implicit TLS.</summary>
    public X509Certificate2? Certificate { get; init; }

    /// <summary>TLS from the first byte (implicit TLS).</summary>
    public bool ImplicitTls { get; init; }

    /// <summary>Advertise AUTH PLAIN and accept only these credentials.</summary>
    public (string User, string Password)? Credentials { get; init; }

    public string? MailReply { get; init; }

    public string? RecipientReply { get; init; }

    /// <summary>Reply after the message body; "250 ..." with <see cref="Discard"/> is "accepted but not delivered".</summary>
    public string? DataReply { get; init; }

    /// <summary>Close the connection after the message body without replying: the client cannot know if it was accepted.</summary>
    public bool DropAfterData { get; init; }

    /// <summary>Close the connection when the client starts a message (MAIL FROM), before anything was handed over.</summary>
    public bool DropBeforeMessage { get; init; }

    /// <summary>Accept the message and throw it away.</summary>
    public bool Discard { get; init; }
}

/// <summary>
/// A scripted SMTP server on 127.0.0.1 for fault injection: each connection takes the next script (the last repeats),
/// so "fail, then succeed" is two scripts. Records what clients did. Never relays anything anywhere.
/// </summary>
public sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly SmtpScript[] scripts;
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private int connections;

    public FakeSmtpServer(params SmtpScript[] scripts)
    {
        this.scripts = scripts.Length == 0 ? [new SmtpScript()] : scripts;
        listener.Start();
        loop = AcceptAsync();
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public int Connections => Volatile.Read(ref connections);

    /// <summary>Messages the server accepted and kept, as raw text.</summary>
    public ConcurrentQueue<string> Delivered { get; } = new();

    /// <summary>Messages that reached DATA, delivered or not.</summary>
    public ConcurrentQueue<string> Received { get; } = new();

    public ConcurrentQueue<string> AuthenticatedUsers { get; } = new();

    public ConcurrentQueue<string> Commands { get; } = new();

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        listener.Stop();
        try
        {
            await loop;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }

        stop.Dispose();
    }

    /// <summary>A port with nothing listening on it: bound, then released.</summary>
    public static int UnusedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task AcceptAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(stop.Token);
            int n = Interlocked.Increment(ref connections);
            var script = scripts[Math.Min(n - 1, scripts.Length - 1)];
            _ = Task.Run(() => ServeAsync(client, script));
        }
    }

    private async Task ServeAsync(TcpClient client, SmtpScript script)
    {
        using (client)
        {
            Stream stream = client.GetStream();
            try
            {
                if (script.Silent)
                {
                    await Task.Delay(Timeout.Infinite, stop.Token);
                    return;
                }

                if (script.ImplicitTls)
                {
                    stream = await TlsAsync(stream, script.Certificate!);
                }

                await WriteAsync(stream, script.Greeting);
                if (!script.Greeting.StartsWith("220", StringComparison.Ordinal))
                {
                    return;
                }

                bool secure = script.ImplicitTls;
                while (await ReadLineAsync(stream) is { } line)
                {
                    Commands.Enqueue(line.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase) ? "AUTH" : line);
                    string verb = line.Split(' ', 2)[0].ToUpperInvariant();
                    switch (verb)
                    {
                        case "EHLO":
                        case "HELO":
                            var caps = new List<string> { "fake.test", "8BITMIME", "SIZE 10485760" };
                            if (script.OfferStartTls && !secure)
                            {
                                caps.Add("STARTTLS");
                            }

                            if (script.Credentials is not null)
                            {
                                caps.Add("AUTH PLAIN");
                            }

                            await WriteAsync(stream, string.Join("\r\n", caps.Select((c, i) => (i == caps.Count - 1 ? "250 " : "250-") + c)));
                            break;
                        case "STARTTLS":
                            await WriteAsync(stream, "220 Go ahead");
                            stream = await TlsAsync(stream, script.Certificate!);
                            secure = true;
                            break;
                        case "AUTH":
                            await AuthenticateAsync(stream, line, script);
                            break;
                        case "MAIL":
                            if (script.DropBeforeMessage)
                            {
                                return;
                            }

                            await WriteAsync(stream, script.MailReply ?? "250 2.1.0 OK");
                            break;
                        case "RCPT":
                            await WriteAsync(stream, script.RecipientReply ?? "250 2.1.5 OK");
                            break;
                        case "DATA":
                            await WriteAsync(stream, "354 End data with <CR><LF>.<CR><LF>");
                            var body = new StringBuilder();
                            while (await ReadLineAsync(stream) is { } dataLine && dataLine != ".")
                            {
                                body.Append(dataLine).Append("\r\n");
                            }

                            Received.Enqueue(body.ToString());
                            if (script.DropAfterData)
                            {
                                return;
                            }

                            string reply = script.DataReply ?? "250 2.0.0 Queued";
                            if (reply.StartsWith('2') && !script.Discard)
                            {
                                Delivered.Enqueue(body.ToString());
                            }

                            await WriteAsync(stream, reply);
                            break;
                        case "RSET":
                        case "NOOP":
                            await WriteAsync(stream, "250 OK");
                            break;
                        case "QUIT":
                            await WriteAsync(stream, "221 Bye");
                            return;
                        default:
                            await WriteAsync(stream, "502 5.5.2 Command not recognized");
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException
                                           or System.Security.Authentication.AuthenticationException)
            {
                // The client gave up (a rejected certificate, a timeout): that is what the test is about.
            }
            finally
            {
                if (stream is SslStream ssl)
                {
                    await ssl.DisposeAsync();
                }
            }
        }
    }

    private async Task AuthenticateAsync(Stream stream, string line, SmtpScript script)
    {
        string[] parts = line.Split(' ');
        string? initial = parts.Length > 2 ? parts[2] : null;
        if (initial is null)
        {
            await WriteAsync(stream, "334 ");
            initial = await ReadLineAsync(stream);
        }

        string[] fields = Encoding.UTF8.GetString(Convert.FromBase64String(initial ?? string.Empty)).Split('\0');
        if (script.Credentials is { } expected && fields.Length == 3 && fields[1] == expected.User && fields[2] == expected.Password)
        {
            AuthenticatedUsers.Enqueue(fields[1]);
            await WriteAsync(stream, "235 2.7.0 Authentication successful");
        }
        else
        {
            await WriteAsync(stream, "535 5.7.8 Authentication credentials invalid");
        }
    }

    private async Task<Stream> TlsAsync(Stream inner, X509Certificate2 certificate)
    {
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, stop.Token);
        return ssl;
    }

    private async Task WriteAsync(Stream stream, string reply)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(reply + "\r\n"), stop.Token);
        await stream.FlushAsync(stop.Token);
    }

    /// <summary>One CRLF-terminated line, read a byte at a time so nothing is buffered past a STARTTLS.</summary>
    private async Task<string?> ReadLineAsync(Stream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            int n = await stream.ReadAsync(one, stop.Token);
            if (n == 0)
            {
                return bytes.Count == 0 ? null : Encoding.UTF8.GetString([.. bytes]);
            }

            if (one[0] == '\n')
            {
                if (bytes.Count > 0 && bytes[^1] == '\r')
                {
                    bytes.RemoveAt(bytes.Count - 1);
                }

                return Encoding.UTF8.GetString([.. bytes]);
            }

            bytes.Add(one[0]);
        }
    }
}

/// <summary>Throwaway certificate authorities and server certificates for the TLS scenarios. Nothing is installed in a store.</summary>
public sealed class TestCertificates : IDisposable
{
    public TestCertificates()
    {
        TrustedRoot = NewRoot("CN=MERIT test root (trusted)");
        OtherRoot = NewRoot("CN=MERIT test root (not trusted)");
        Valid = Issue(TrustedRoot, "localhost", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        Expired = Issue(TrustedRoot, "localhost", DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-1));
        WrongHost = Issue(TrustedRoot, "mail.other.example", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        Untrusted = Issue(OtherRoot, "localhost", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        TrustedRootPath = Path.Combine(Path.GetTempPath(), $"merit-test-root-{Guid.NewGuid():N}.pem");
        File.WriteAllText(TrustedRootPath, TrustedRoot.ExportCertificatePem());
    }

    public X509Certificate2 TrustedRoot { get; }

    public X509Certificate2 OtherRoot { get; }

    public X509Certificate2 Valid { get; }

    public X509Certificate2 Expired { get; }

    public X509Certificate2 WrongHost { get; }

    public X509Certificate2 Untrusted { get; }

    public string TrustedRootPath { get; }

    public void Dispose()
    {
        foreach (var c in new[] { TrustedRoot, OtherRoot, Valid, Expired, WrongHost, Untrusted })
        {
            c.Dispose();
        }

        File.Delete(TrustedRootPath);
    }

    private static X509Certificate2 NewRoot(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-60), DateTimeOffset.UtcNow.AddDays(60));
        return X509CertificateLoader.LoadPkcs12(root.Export(X509ContentType.Pfx), null);
    }

    private static X509Certificate2 Issue(X509Certificate2 issuer, string host, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        if (host == "localhost")
        {
            san.AddIpAddress(IPAddress.Loopback);
        }

        request.CertificateExtensions.Add(san.Build());
        byte[] serial = RandomNumberGenerator.GetBytes(12);
        using var leaf = request.Create(issuer, notBefore, notAfter, serial);
        using var withKey = leaf.CopyWithPrivateKey(key);
        // SslStream on Windows needs a key it can use from a persisted PFX, not an ephemeral one.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
    }
}
