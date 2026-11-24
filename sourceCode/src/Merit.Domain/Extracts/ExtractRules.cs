using System.Globalization;
using System.Text;

namespace Merit.Domain.Extracts;

public sealed record LayoutField(int FieldOrder, string FieldName, int FieldWidth, string SourceKey, char PadWith,
                                 bool AlignRight, bool IsRequired);

public sealed record ExtractLayout(string ExtractType, string LayoutVersion, string FileFormat, string? Delimiter,
                                   IReadOnlyList<LayoutField> Fields);

public sealed record ExtractIssue(string RecordId, string FieldName, string Message);

public sealed record ExtractFile(string Content, int RecordCount, IReadOnlyList<ExtractIssue> Issues);

/// <summary>
/// Writes extract records to a federal layout. The layout -- field order, widths, padding, required fields -- is
/// configuration (config.extractLayout), so a federal layout change is a data update, not a release. Names, street
/// addresses, phone numbers, SSNs and record numbers never reach this code: the source rows do not carry them.
/// </summary>
public static class ExtractWriter
{
    public static ExtractFile Write(ExtractLayout layout, IEnumerable<(string RecordId, IReadOnlyDictionary<string, string?> Values)> records,
                                    IEnumerable<ExtractIssue>? crossFieldIssues = null)
    {
        var sb = new StringBuilder();
        var issues = new List<ExtractIssue>(crossFieldIssues ?? []);
        int count = 0;
        bool delimited = layout.FileFormat == "delimited";
        string delimiter = layout.Delimiter ?? ",";
        var fields = layout.Fields.OrderBy(f => f.FieldOrder).ToList();

        if (delimited)
        {
            sb.AppendJoin(delimiter, fields.Select(f => f.FieldName)).Append("\r\n");
        }

        foreach (var (recordId, values) in records)
        {
            count++;
            var parts = new List<string>();
            foreach (var f in fields)
            {
                values.TryGetValue(f.SourceKey, out var raw);
                string value = raw ?? string.Empty;
                if (f.IsRequired && value.Length == 0)
                {
                    issues.Add(new ExtractIssue(recordId, f.FieldName, "Required by the layout but blank."));
                }

                if (value.Length > f.FieldWidth)
                {
                    issues.Add(new ExtractIssue(recordId, f.FieldName, $"Longer than {f.FieldWidth} characters; truncated."));
                    value = value[..f.FieldWidth];
                }

                if (delimited)
                {
                    parts.Add(value.Contains(delimiter, StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal)
                        ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
                        : value);
                }
                else
                {
                    char pad = value.Length == 0 ? ' ' : f.PadWith;
                    parts.Add(f.AlignRight ? value.PadLeft(f.FieldWidth, pad) : value.PadRight(f.FieldWidth, pad));
                }
            }

            sb.Append(delimited ? string.Join(delimiter, parts) : string.Concat(parts)).Append("\r\n");
        }

        return new ExtractFile(sb.ToString(), count, issues);
    }
}

/// <summary>Federal code mappings shared by both extracts.</summary>
public static class ExtractCodes
{
    public static string SexCode(string? sex) => sex switch
    {
        "male" => "1",
        "female" => "2",
        "other" => "3",
        _ => "9",
    };

    public static string EthnicityCode(string? ethnicity) => ethnicity switch
    {
        "hispanic" => "1",
        "notHispanic" => "2",
        _ => "9",
    };

    public static string Date(DateOnly? d) => d?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>ABLES WorkRel from the case exposure category.</summary>
    public static string WorkRel(string? exposureCategory) => exposureCategory switch
    {
        "occupational" => "1",
        "residential" or "otherNonOccupational" => "2",
        "both" => "3",
        _ => "9",
    };

    public static bool IsWorkRelated(string workRel) => workRel is "1" or "3";

    /// <summary>
    /// ABLES Status follows NIOSH's own rule, not the case level: the person's highest result in the calendar year is
    /// compared with 10 ug/dL. At or above: a new case (1) or an existing case (2) depending on whether the person was
    /// elevated the year before. Below: a new adult (3) or one already in the registry (4). 9 when unknown.
    /// </summary>
    public static string AblesStatus(decimal? highestValue, bool priorYearElevated, bool inRegistryBefore)
    {
        if (highestValue is null)
        {
            return "9";
        }

        if (highestValue >= 10m)
        {
            return priorYearElevated ? "2" : "1";
        }

        return inRegistryBefore ? "4" : "3";
    }

    /// <summary>Whole number, rounded half away from zero, the layout's rounding rule.</summary>
    public static string WholeNumber(decimal? value)
        => value is null ? string.Empty : decimal.Round(value.Value, 0, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);

    public static string OneDecimal(decimal? value)
        => value is null ? string.Empty : decimal.Round(value.Value, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>The eight ABLES race flags: 1 when the person has a race mapped to that flag, else 0.</summary>
    public static IReadOnlyDictionary<string, string> RaceFlags(IEnumerable<string> ablesRaceCodes)
    {
        var set = ablesRaceCodes.ToHashSet(StringComparer.Ordinal);
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i <= 8; i++)
        {
            flags["race" + i.ToString(CultureInfo.InvariantCulture)] = set.Contains(i.ToString(CultureInfo.InvariantCulture)) ? "1" : "0";
        }

        return flags;
    }

    /// <summary>NIOSH cross-field rules: NAICS and COC are required when work-related and blank when not.</summary>
    public static IEnumerable<ExtractIssue> AblesCrossFieldIssues(string recordId, string workRel, string? naics, string? coc)
    {
        if (IsWorkRelated(workRel))
        {
            if (string.IsNullOrEmpty(naics))
            {
                yield return new ExtractIssue(recordId, "NAICS", "Work-related exposure needs an industry code (999 when unknown).");
            }

            if (string.IsNullOrEmpty(coc))
            {
                yield return new ExtractIssue(recordId, "COC", "Work-related exposure needs an occupation code (990 when unknown).");
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(naics))
            {
                yield return new ExtractIssue(recordId, "NAICS", "Must be blank when the exposure is not work-related.");
            }

            if (!string.IsNullOrEmpty(coc))
            {
                yield return new ExtractIssue(recordId, "COC", "Must be blank when the exposure is not work-related.");
            }
        }
    }

    /// <summary>
    /// A stable surrogate id of at most 15 characters, never a name or SSN: the person id's first 15 hex digits, which
    /// are random (the time-ordered bytes are at the end of the key).
    /// </summary>
    public static string SurrogateId(Guid personId) => personId.ToString("N", CultureInfo.InvariantCulture)[..15].ToUpperInvariant();
}
