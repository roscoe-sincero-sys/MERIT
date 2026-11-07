using System.Buffers.Binary;
using Merit.Application.Errors;
using Merit.Application.Reference;
using Merit.Application.Security;
using Merit.Application.Services;
using Merit.Web.Infrastructure;
using Microsoft.AspNetCore.Identity;

namespace Merit.Tests;

public sealed class PasswordPolicyTests
{
    // These tests exercise the pure password validator; persistence/MFA/event dependencies are intentionally unused.
    private static AccountService Accounts() => new(null!, new IdentityPasswordHasher(), null!, new CommonBreachedPasswordList(), null!);

    [Theory]
    [InlineData(null, 15)]
    [InlineData("invalid", 15)]
    [InlineData("-1", 15)]
    [InlineData("0", 15)]
    [InlineData("8", 15)]
    [InlineData("14", 15)]
    [InlineData("15", 15)]
    [InlineData("30", 30)]
    [InlineData("128", 128)]
    [InlineData("129", 128)]
    public void SettingsCannotReduceTheMinimumOrMakeThePolicyImpossible(string? configured, int expected)
    {
        var values = new Dictionary<string, string>();
        if (configured is not null) values["passwordMinLength"] = configured;
        Assert.Equal(expected, new JurisdictionSettings(values).PasswordMinLength);
    }

    [Fact]
    public void FloorAppliesEvenWhenACallerSuppliesZeroAndNoCompositionRulesAreAdded()
    {
        Assert.Throws<ValidationException>(() => Accounts().ValidateNewPassword("abcdefgh", 0));
        Accounts().ValidateNewPassword("oak river cloud", 0); // exactly 15, lowercase and spaces only
        Assert.Throws<ValidationException>(() => Accounts().ValidateNewPassword("oak river cloud", 16));
    }

    [Fact]
    public void CountsCodePointsAfterNfcRatherThanUtf16UnitsOrCombiningMarks()
    {
        string Emoji(int count) => string.Concat(Enumerable.Repeat("😀", count));
        Assert.Throws<ValidationException>(() => Accounts().ValidateNewPassword(Emoji(14), 15));
        Accounts().ValidateNewPassword(Emoji(15), 15);
        Accounts().ValidateNewPassword(Emoji(128), 15);
        Assert.Throws<ValidationException>(() => Accounts().ValidateNewPassword(Emoji(129), 15));
        Assert.Throws<ValidationException>(() => Accounts().ValidateNewPassword(string.Concat(Enumerable.Repeat("e\u0301", 14)), 15));
        Accounts().ValidateNewPassword(string.Concat(Enumerable.Repeat("e\u0301", 15)), 15);
        // Three Hangul Jamo normalize to one code point: a valid 128-character password can exceed 256 raw UTF-16 units.
        string hangul = string.Concat(Enumerable.Repeat("\u1100\u1161\u11a8", 128));
        Accounts().ValidateNewPassword(hangul, 15);
        var hasher = new IdentityPasswordHasher();
        Assert.Equal(PasswordCheck.Success, hasher.Verify(hasher.Hash(hangul), hangul));
        Assert.Throws<ValidationException>(() => Accounts().ValidateNewPassword("oak river cloud\ud800", 15));
    }

    [Fact]
    public void ScreensWholeCommonPasswordsAndExpectedNamesWithoutRejectingWordsInsidePassphrases()
    {
        var list = new CommonBreachedPasswordList();
        Assert.True(list.IsBreached("passwordpassword"));
        Assert.True(list.IsBreached("  PASSWORDPASSWORD  "));
        Assert.Throws<ValidationException>(() => Accounts().ValidateNewPassword("passwordpassword", 15));
        Assert.Throws<ValidationException>(() => Accounts().ValidateNewPassword("long.user.name2026!", 15, userName: "long.user.name"));
        Assert.Throws<ValidationException>(() => Accounts().ValidateNewPassword("MERIT1234567890!", 15));
        Accounts().ValidateNewPassword("long.user.name walks by the river", 15, userName: "long.user.name");
        Accounts().ValidateNewPassword("MERIT password constellation", 15);
        Accounts().ValidateNewPassword("a long password with unrelated words", 15);
    }

    [Fact]
    public void NewHashesUseNfcIndependentSaltsAndAnExplicitSha512WorkFactor()
    {
        var hasher = new IdentityPasswordHasher();
        const string composed = "café lake stone";
        const string decomposed = "cafe\u0301 lake stone";
        string hash = hasher.Hash(decomposed);
        Assert.StartsWith(IdentityPasswordHasher.FormatPrefix, hash, StringComparison.Ordinal);
        byte[] blob = Convert.FromBase64String(hash[IdentityPasswordHasher.FormatPrefix.Length..]);
        Assert.Equal(1, blob[0]);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(1, 4))); // PBKDF2-HMAC-SHA512
        Assert.Equal(220_000u, BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(5, 4)));
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(9, 4)));
        Assert.NotEqual(hash, hasher.Hash(composed));
        Assert.Equal(PasswordCheck.Success, hasher.Verify(hash, composed));
        Assert.Equal(PasswordCheck.Success, hasher.Verify(hash, decomposed));
        Assert.Equal(PasswordCheck.Failed, hasher.Verify(hash, composed.ToUpperInvariant()));
    }

    [Fact]
    public void LegacyHashRetainsItsOriginalBytesUntilExplicitUpgrade()
    {
        var legacy = new PasswordHasher<object>(Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions { IterationCount = 100_000 }));
        const string original = "cafe\u0301 lake stone";
        string hash = legacy.HashPassword(new object(), original);
        var current = new IdentityPasswordHasher();
        Assert.Equal(PasswordCheck.SuccessRehashNeeded, current.Verify(hash, original));
        Assert.Equal(PasswordCheck.Failed, current.Verify(hash, "café lake stone"));
        string upgraded = current.Hash(original);
        Assert.Equal(PasswordCheck.Success, current.Verify(upgraded, original));
        Assert.Equal(PasswordCheck.Success, current.Verify(upgraded, "café lake stone"));
    }

    [Fact]
    public void HashingPreservesAllSpacesAndDoesNotTruncateAndMalformedInputsFailClosed()
    {
        var hasher = new IdentityPasswordHasher();
        string value = " " + new string('x', 126) + " ";
        string hash = hasher.Hash(value);
        Assert.Equal(PasswordCheck.Success, hasher.Verify(hash, value));
        Assert.Equal(PasswordCheck.Failed, hasher.Verify(hash, value.Trim()));
        Assert.Equal(PasswordCheck.Failed, hasher.Verify(hash, value[..^1] + "!"));
        Assert.Equal(PasswordCheck.Failed, hasher.Verify(hash, "invalid\ud800"));
        Assert.Equal(PasswordCheck.Failed, hasher.Verify("not a hash", value));
    }
}
