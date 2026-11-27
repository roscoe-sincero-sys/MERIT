using Merit.Application.Security;
using Merit.Web.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Merit.Tests;

/// <summary>
/// Regression tests for keyed HMAC User-Agent family hashing (#350).
/// </summary>
public sealed class UserAgentHasherTests
{
    private sealed class StubHostEnvironment(string envName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = envName;
        public string ApplicationName { get; set; } = "Merit.Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static string NewBase64Key(byte seed)
    {
        byte[] bytes = Enumerable.Range(0, 32).Select(i => (byte)(seed + i)).ToArray();
        return Convert.ToBase64String(bytes);
    }

    private static UserAgentHasher CreateHasher(string? key, string envName = "Production")
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [UserAgentHasher.KeySetting] = key,
            })
            .Build();
        return new UserAgentHasher(config, NullLogger<UserAgentHasher>.Instance, new StubHostEnvironment(envName));
    }

    [Fact]
    public void BrowserVersionUpdates_ProduceIdenticalHash_SoRule11DoesNotFalsePositive()
    {
        var hasher = CreateHasher(NewBase64Key(1));

        string chrome131 = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
        string chrome132 = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.6834.83 Safari/537.36";
        Assert.Equal(hasher.Hash(chrome131), hasher.Hash(chrome132));

        string ff133 = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:133.0) Gecko/20100101 Firefox/133.0";
        string ff134 = "Mozilla/5.0 (Macintosh; Intel Mac OS X 14.7; rv:134.0) Gecko/20100101 Firefox/134.0";
        Assert.Equal(hasher.Hash(ff133), hasher.Hash(ff134));
    }

    [Fact]
    public void RealClientOrPlatformChanges_ProduceDistinctHashes()
    {
        var hasher = CreateHasher(NewBase64Key(1));

        string chromeWin = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
        string firefoxWin = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:133.0) Gecko/20100101 Firefox/133.0";
        string edgeWin = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0";
        string safariIos = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Mobile/15E148 Safari/604.1";
        string curl = "curl/8.7.1";

        var hashes = new[] { chromeWin, firefoxWin, edgeWin, safariIos, curl }.Select(hasher.Hash).ToList();
        Assert.All(hashes, h => Assert.Equal(16, h?.Length));
        Assert.Equal(hashes.Count, hashes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void DifferentHmacKeys_ProduceDifferentHashes_AndMissingKeyFailsOutsideDevelopment()
    {
        var h1 = CreateHasher(NewBase64Key(1));
        var h2 = CreateHasher(NewBase64Key(99));
        string ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        Assert.NotEqual(h1.Hash(ua), h2.Hash(ua));
        Assert.Throws<InvalidOperationException>(() => CreateHasher(null, "Production"));
        Assert.NotNull(CreateHasher(null, "Development").Hash(ua));
    }
}
