using System.Globalization;
using System.Text;

namespace Merit.Domain.Text;

/// <summary>
/// Normalized keys used for matching people, organizations and locations. The database stores these keys beside the
/// values as reported; the reported values themselves are never changed. The demo data generator
/// (database/tools/generateDemo.py) implements the same rules.
/// </summary>
public static class MatchKeys
{
    private static readonly Dictionary<string, string> Abbreviations = new(StringComparer.Ordinal)
    {
        ["STREET"] = "ST", ["AVENUE"] = "AVE", ["ROAD"] = "RD", ["DRIVE"] = "DR", ["BOULEVARD"] = "BLVD",
        ["LANE"] = "LN", ["COURT"] = "CT", ["PLACE"] = "PL", ["TERRACE"] = "TER", ["CIRCLE"] = "CIR",
        ["PARKWAY"] = "PKWY", ["HIGHWAY"] = "HWY", ["NORTH"] = "N", ["SOUTH"] = "S", ["EAST"] = "E", ["WEST"] = "W",
        ["APARTMENT"] = "APT", ["SUITE"] = "STE",
    };

    private static readonly HashSet<string> UnitPrefixes = new(StringComparer.Ordinal) { "APT", "UNIT", "STE", "NO", "FL", "RM" };

    private static readonly HashSet<string> PlaceholderFirstNames = new(StringComparer.Ordinal)
    {
        "BABYBOY", "BABYGIRL", "BABY", "INFANT", "NEWBORN", "BOY", "GIRL", "UNKNOWN", "UNK", "NONE", "TWINA", "TWINB",
        "BABYA", "BABYB", "BBOY", "BGIRL",
    };

    /// <summary>Upper case, accents removed, letters only. NULL for an empty name.</summary>
    public static string? NameKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var sb = new StringBuilder(name.Length);
        foreach (char ch in StripAccents(name).ToUpperInvariant())
        {
            if (ch is >= 'A' and <= 'Z')
            {
                sb.Append(ch);
            }
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>True for placeholder first names labs send for newborns ("BABY BOY"); these never link automatically.</summary>
    public static bool IsPlaceholderName(string? firstName)
    {
        string? key = NameKey(firstName);
        return key is null || PlaceholderFirstNames.Contains(key);
    }

    /// <summary>Words upper-cased, punctuation removed, common street words abbreviated.</summary>
    public static string NormalizeWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        foreach (char ch in StripAccents(text).ToUpperInvariant())
        {
            sb.Append(ch is (>= 'A' and <= 'Z') or (>= '0' and <= '9') ? ch : ' ');
        }

        var words = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => Abbreviations.TryGetValue(w, out var abbr) ? abbr : w);
        return string.Join(' ', words);
    }

    public static string NormalizeUnit(string? unit)
    {
        var words = NormalizeWords(unit).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 0 && UnitPrefixes.Contains(words[0]))
        {
            words.RemoveAt(0);
        }

        return string.Join(' ', words);
    }

    /// <summary>Key that identifies one address and unit; two apartments are two locations.</summary>
    public static string AddressKey(string? line1, string? unit, string? city, string? state, string? zip5)
    {
        string zip = (zip5 ?? string.Empty).Trim();
        if (zip.Length > 5)
        {
            zip = zip[..5];
        }

        return string.Join('|', NormalizeWords(line1), NormalizeUnit(unit), NormalizeWords(city), NormalizeWords(state), zip);
    }

    /// <summary>The building record's key: the same address with no unit.</summary>
    public static string BuildingKey(string? line1, string? city, string? state, string? zip5)
        => AddressKey(line1, null, city, state, zip5);

    /// <summary>Prefix of an address key for type-ahead: normalized street words only.</summary>
    public static string AddressSearchPrefix(string? text) => NormalizeWords(text);

    private static string StripAccents(string s)
    {
        string formD = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (char ch in formD)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
