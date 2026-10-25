using System.Security.Cryptography;
using System.Text;

namespace Merit.Application.Security;

/// <summary>
/// Time-based one-time passwords (RFC 6238, HMAC-SHA1, 30-second steps, 6 digits) -- the format every authenticator
/// app understands. Secrets are 160 random bits, shown to the user once as Base32 and stored only after the data
/// protection layer has encrypted them.
/// </summary>
public static class Totp
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static string ToBase32(byte[] data)
    {
        var sb = new StringBuilder((data.Length + 4) / 5 * 8);
        int buffer = 0, bits = 0;
        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            sb.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return sb.ToString();
    }

    public static byte[] FromBase32(string text)
    {
        string s = text.Trim().TrimEnd('=').Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        var output = new List<byte>(s.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (char c in s)
        {
            int v = Base32Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (v < 0)
            {
                throw new FormatException("Not a Base32 string.");
            }

            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. output];
    }

    public static string Code(byte[] secret, long timeStep)
    {
        Span<byte> counter = stackalloc byte[8];
        for (int i = 7; i >= 0; i--)
        {
            counter[i] = (byte)(timeStep & 0xFF);
            timeStep >>= 8;
        }

        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);
        int offset = hash[19] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Accepts the current code and one step either side, for clock drift.</summary>
    public static bool Verify(byte[] secret, string? code, DateTimeOffset now)
    {
        if (code is null)
        {
            return false;
        }

        string digits = new(code.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length != 6)
        {
            return false;
        }

        long step = now.ToUnixTimeSeconds() / 30;
        for (long s = step - 1; s <= step + 1; s++)
        {
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, s)), Encoding.ASCII.GetBytes(digits)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The otpauth:// URI authenticator apps scan from a QR code.</summary>
    public static string ProvisioningUri(string issuer, string account, byte[] secret)
        => $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}"
           + $"?secret={ToBase32(secret)}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period=30";
}

/// <summary>Password hashing, implemented in Merit.Web with ASP.NET Core Identity's PasswordHasher.</summary>
public interface IPasswordHasher
{
    string Hash(string password);

    PasswordCheck Verify(string hash, string password);
}

public enum PasswordCheck
{
    Failed,
    Success,
    SuccessRehashNeeded,
}

/// <summary>Encrypts secrets at rest (TOTP secrets) with ASP.NET Core data protection.</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    string Unprotect(string protectedText);
}

/// <summary>Known-breached password check (NIST SP 800-63B-4). The shipped implementation checks a local list.</summary>
public interface IBreachedPasswordList
{
    bool IsBreached(string password);
}
