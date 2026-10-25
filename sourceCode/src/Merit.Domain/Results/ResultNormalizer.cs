using System.Globalization;

namespace Merit.Domain.Results;

public enum ResultOperator
{
    Equal,
    LessThan,
    GreaterThan,
}

public sealed record AcceptedUnit(string UnitText, decimal MultiplierToCanonical);

public sealed record ParsedValue(ResultOperator Operator, decimal? Value, string RawText);

public sealed record NormalizedValue(ResultOperator Operator, decimal ValueReported, string UnitReported,
                                     decimal ValueCanonical, string UnitCanonical, decimal? ValueCreatinineAdjusted);

/// <summary>
/// Intake normalization steps 2, 3 and 5: parse the operator and number (keeping the raw text), convert to the
/// canonical unit with the accepted-unit multiplier, and compute the creatinine-adjusted value when required.
/// </summary>
public static class ResultNormalizer
{
    public static string ToCode(this ResultOperator op) => op switch
    {
        ResultOperator.LessThan => "<",
        ResultOperator.GreaterThan => ">",
        _ => "=",
    };

    public static ResultOperator ParseOperator(string? code) => code switch
    {
        "<" => ResultOperator.LessThan,
        ">" => ResultOperator.GreaterThan,
        _ => ResultOperator.Equal,
    };

    /// <summary>Parses text such as "4.2", "&lt;1.0", "&gt; 65", "&lt;=2" or "ND". Non-numeric text keeps a NULL value.</summary>
    public static ParsedValue Parse(string? text)
    {
        string raw = (text ?? string.Empty).Trim();
        string s = raw.Replace(" ", string.Empty, StringComparison.Ordinal);
        var op = ResultOperator.Equal;
        if (s.StartsWith('<'))
        {
            op = ResultOperator.LessThan;
            s = s.TrimStart('<', '=');
        }
        else if (s.StartsWith('>'))
        {
            op = ResultOperator.GreaterThan;
            s = s.TrimStart('>', '=');
        }

        return decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? new ParsedValue(op, value, raw)
            : new ParsedValue(op, null, raw);
    }

    /// <summary>
    /// Finds the accepted unit (case-insensitive, ignoring spaces and the micro sign variants) or returns NULL, in which
    /// case the result goes to the error queue.
    /// </summary>
    public static AcceptedUnit? FindUnit(string? unitText, IEnumerable<AcceptedUnit> accepted)
    {
        string key = UnitKey(unitText);
        return key.Length == 0 ? null : accepted.FirstOrDefault(u => UnitKey(u.UnitText) == key);
    }

    public static NormalizedValue Normalize(ResultOperator op, decimal value, AcceptedUnit unit, string canonicalUnit,
                                            decimal? creatinineGramsPerLiter, bool requiresCreatinine)
    {
        decimal canonical = decimal.Round(value * unit.MultiplierToCanonical, 4, MidpointRounding.AwayFromZero);
        decimal? adjusted = null;
        if (requiresCreatinine && creatinineGramsPerLiter is > 0)
        {
            // ug/L divided by g/L of creatinine gives ug/g creatinine.
            adjusted = decimal.Round(canonical / creatinineGramsPerLiter.Value, 4, MidpointRounding.AwayFromZero);
        }

        return new NormalizedValue(op, value, unit.UnitText, canonical, canonicalUnit, adjusted);
    }

    private static string UnitKey(string? unit)
        => (unit ?? string.Empty).Trim().Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace('µ', 'u').Replace('μ', 'u').ToUpperInvariant().Replace("MCG", "UG", StringComparison.Ordinal);
}
