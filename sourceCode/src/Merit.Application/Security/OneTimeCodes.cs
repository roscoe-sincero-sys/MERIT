using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Merit.Application.Security;

/// <summary>Turns a one-time code into the keyed verifier the database stores in place of the code.</summary>
public interface IOneTimeCodeHasher
{
    /// <summary>HMAC-SHA256 over the user, the purpose and the code: 32 bytes.</summary>
    byte[] Hash(Guid appUserId, string purpose, string code);
}

/// <summary>
/// HMAC-SHA256 under a key that only the web application holds (EmailCodes:VerifierKey). The database never sees the
/// key, so a copy of the database cannot be used to try all million six-digit codes offline. The user and purpose are
/// inside the MAC, so a verifier for one account or purpose never matches another.
/// </summary>
public sealed class HmacOneTimeCodeHasher : IOneTimeCodeHasher
{
    public const int MinimumKeyBytes = 32;

    private readonly byte[] key;

    public HmacOneTimeCodeHasher(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length < MinimumKeyBytes)
        {
            throw new ArgumentException($"The one-time code key must be at least {MinimumKeyBytes} bytes.", nameof(key));
        }

        this.key = (byte[])key.Clone();
    }

    public byte[] Hash(Guid appUserId, string purpose, string code)
        => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(string.Join('|', appUserId.ToString("N", CultureInfo.InvariantCulture), purpose, code)));
}

/// <summary>Six-digit codes from the operating system's cryptographic random number generator.</summary>
public static class OneTimeCodes
{
    public const int Digits = 6;

    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    /// <summary>The code as typed, without spaces or hyphens; null unless exactly six ASCII digits remain.</summary>
    public static string? Normalize(string? typed)
    {
        if (typed is null)
        {
            return null;
        }

        var digits = new StringBuilder(Digits);
        foreach (char ch in typed)
        {
            if (ch is ' ' or '-' or '\t')
            {
                continue;
            }

            if (ch is < '0' or > '9' || digits.Length == Digits)
            {
                return null;
            }

            digits.Append(ch);
        }

        return digits.Length == Digits ? digits.ToString() : null;
    }

    /// <summary>A short reference a person can read to support staff: "EM-" and eight characters without look-alikes.</summary>
    public static string NewSupportReference()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> chars = stackalloc char[8];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return "EM-" + new string(chars);
    }

    /// <summary>"j***@example.gov": enough for the person to recognise the address, not enough to harvest it.</summary>
    public static string? MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        string trimmed = email.Trim();
        int at = trimmed.LastIndexOf('@');
        if (at <= 0 || at == trimmed.Length - 1)
        {
            return "***";
        }

        return string.Concat(trimmed.AsSpan(0, 1), "***", trimmed.AsSpan(at));
    }
}
