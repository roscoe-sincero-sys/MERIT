namespace Merit.Domain.Identifiers;

/// <summary>Format checks run before any registry lookup of an NPI or CLIA number.</summary>
public static class ProviderIdentifiers
{
    /// <summary>
    /// An NPI is ten digits whose last digit is a Luhn check digit computed over the prefix 80840 followed by the first
    /// nine digits (CMS NPI check digit algorithm).
    /// </summary>
    public static bool IsValidNpi(string? npi)
    {
        if (npi is null || npi.Length != 10 || !npi.All(char.IsAsciiDigit))
        {
            return false;
        }

        // Luhn over "80840" + first nine digits; the constant 24 accounts for the prefix.
        int sum = 24;
        bool doubleIt = true;
        for (int i = 8; i >= 0; i--)
        {
            int d = npi[i] - '0';
            if (doubleIt)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
            doubleIt = !doubleIt;
        }

        int check = (10 - (sum % 10)) % 10;
        return check == npi[9] - '0';
    }

    /// <summary>A CLIA number is two digits, the letter D, then seven digits.</summary>
    public static bool IsValidClia(string? clia)
        => clia is { Length: 10 }
           && char.IsAsciiDigit(clia[0]) && char.IsAsciiDigit(clia[1])
           && (clia[2] == 'D' || clia[2] == 'd')
           && clia[3..].All(char.IsAsciiDigit);

    /// <summary>
    /// The one form a CLIA number is stored and looked up in: spaces and hyphens removed, upper case, so 21d-0123456 and
    /// "21D 0123456" both match the registry's 21D0123456. Anything else is left for <see cref="IsValidClia"/> to refuse.
    /// </summary>
    public static string? NormalizeClia(string? clia) => WithoutSeparators(clia)?.ToUpperInvariant();

    /// <summary>
    /// The one form an NPI is stored and looked up in: spaces and hyphens removed, so "1234 567 893" and 1234-567-893 match
    /// 1234567893. Other characters are kept, so <see cref="IsValidNpi"/> refuses them rather than a stray letter vanishing.
    /// </summary>
    public static string? NormalizeNpi(string? npi) => WithoutSeparators(npi);

    private static string? WithoutSeparators(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string kept = new(value.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        return kept.Length == 0 ? null : kept;
    }
}
