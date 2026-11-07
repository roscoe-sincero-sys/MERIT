using System.Text;

namespace Merit.Application.Security;

/// <summary>MERIT's stronger-than-MFA-baseline policy, shared by every password creation/change path.</summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 15;
    public const int MaximumLength = 128;

    public static int EffectiveMinimum(int configured) => Math.Clamp(configured, MinimumLength, MaximumLength);

    /// <summary>NFC preserves spaces and case. Invalid UTF-16 is refused rather than silently replaced.</summary>
    public static string Normalize(string password) => password.Normalize(NormalizationForm.FormC);

    public static int Length(string normalized)
    {
        int count = 0;
        foreach (var _ in normalized.EnumerateRunes()) count++;
        return count;
    }

    /// <summary>Compare the whole value with public account/service names and trivial numeric/symbol suffixes.</summary>
    public static bool IsExpected(string normalized, string? userName)
    {
        string candidate = normalized.Trim();
        return DerivativeOf(candidate, "MERIT")
               || (!string.IsNullOrWhiteSpace(userName) && DerivativeOf(candidate, Normalize(userName.Trim())));
    }

    private static bool DerivativeOf(string candidate, string context)
        => candidate.StartsWith(context, StringComparison.OrdinalIgnoreCase)
           && candidate.AsSpan(context.Length).IndexOfAnyExcept("0123456789!@#$%^&*()_+-=[]{};:',.<>/?\\|`~\"".AsSpan()) < 0;
}
