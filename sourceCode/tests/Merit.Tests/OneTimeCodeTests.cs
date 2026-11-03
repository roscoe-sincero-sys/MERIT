using Merit.Application.Security;

namespace Merit.Tests;

/// <summary>Email one-time codes: six CSPRNG digits, a keyed verifier bound to the account and purpose, and masked addresses.</summary>
public sealed class OneTimeCodeTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)(i * 7)).ToArray();

    [Fact]
    public void CodesAreSixDigitsAndSpreadAcrossTheRange()
    {
        var codes = Enumerable.Range(0, 2000).Select(_ => OneTimeCodes.NewCode()).ToList();
        Assert.All(codes, c => Assert.Matches("^[0-9]{6}$", c));
        // Leading zeros are kept, and 2000 draws from a million values barely repeat.
        Assert.Contains(codes, c => c[0] == '0');
        Assert.True(codes.Distinct().Count() > 1990);
    }

    [Theory]
    [InlineData("123456", "123456")]
    [InlineData(" 123 456 ", "123456")]
    [InlineData("123-456", "123456")]
    [InlineData("12345", null)]
    [InlineData("1234567", null)]
    [InlineData("12345a", null)]
    [InlineData("１２３４５６", null)] // full-width digits are not ASCII digits
    [InlineData(null, null)]
    public void TypedCodesAreNormalizedStrictly(string? typed, string? expected) => Assert.Equal(expected, OneTimeCodes.Normalize(typed));

    [Fact]
    public void TheVerifierIsKeyedAndBoundToTheAccountAndPurpose()
    {
        var hasher = new HmacOneTimeCodeHasher(Key);
        var user = Guid.NewGuid();
        byte[] v = hasher.Hash(user, "signIn", "123456");
        Assert.Equal(32, v.Length);
        Assert.Equal(v, hasher.Hash(user, "signIn", "123456"));
        Assert.NotEqual(v, hasher.Hash(user, "enroll", "123456"));
        Assert.NotEqual(v, hasher.Hash(Guid.NewGuid(), "signIn", "123456"));
        Assert.NotEqual(v, hasher.Hash(user, "signIn", "123457"));
        Assert.NotEqual(v, new HmacOneTimeCodeHasher(Enumerable.Repeat((byte)1, 32).ToArray()).Hash(user, "signIn", "123456"));
    }

    [Fact]
    public void AShortKeyIsRefused() => Assert.Throws<ArgumentException>(() => new HmacOneTimeCodeHasher(new byte[16]));

    [Theory]
    [InlineData("jordan.lee@health.example.gov", "j***@health.example.gov")]
    [InlineData("a@example.test", "a***@example.test")]
    [InlineData("not-an-address", "***")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void AddressesAreMasked(string? email, string? masked) => Assert.Equal(masked, OneTimeCodes.MaskEmail(email));

    [Fact]
    public void SupportReferencesAreShortAndReadable()
    {
        Assert.All(Enumerable.Range(0, 200).Select(_ => OneTimeCodes.NewSupportReference()), r => Assert.Matches("^EM-[A-HJ-NP-Z2-9]{8}$", r));
    }
}
