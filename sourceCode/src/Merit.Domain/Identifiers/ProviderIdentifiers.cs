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

    public static string? NormalizeClia(string? clia)
        => string.IsNullOrWhiteSpace(clia) ? null : clia.Trim().ToUpperInvariant();
}
