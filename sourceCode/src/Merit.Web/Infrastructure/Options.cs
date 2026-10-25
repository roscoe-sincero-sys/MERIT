using System.Globalization;
using Merit.Application.Reference;
using Merit.Web.Models;

namespace Merit.Web.Infrastructure;

/// <summary>Drop-down options built from reference data, for view models. Display only; no rules here.</summary>
public static class Options
{
    public static IReadOnlyList<SelectOption> Choices(this ReferenceData data, string lookupType, string? selected = null,
                                                     bool includeBlank = false)
    {
        var list = new List<SelectOption>();
        if (includeBlank)
        {
            list.Add(new SelectOption(string.Empty, "- Select -", selected is null));
        }

        list.AddRange(data.Lookups(lookupType).Select(l => new SelectOption(l.Code, l.DisplayName, l.Code == selected)));
        return list;
    }

    public static IReadOnlyList<SelectOption> Fixed(string? selected, params (string Value, string Label)[] items)
        => items.Select(i => new SelectOption(i.Value, i.Label, i.Value == selected)).ToList();

    public static string Date(DateOnly? d) => d?.ToString("MMM d, yyyy", CultureInfo.InvariantCulture) ?? string.Empty;

    public static string Date(DateTime? d) => d?.ToLocalTime().ToString("MMM d, yyyy h:mm tt", CultureInfo.InvariantCulture) ?? string.Empty;

    public static string Iso(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    public static string Hex(byte[]? rowVersion) => rowVersion is null || rowVersion.Length == 0 ? string.Empty : Convert.ToHexString(rowVersion);

    public static byte[]? FromHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }

        string h = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        return Convert.FromHexString(h);
    }

    public static string Value(string op, decimal? value, string unit)
        => value is null ? string.Empty
            : $"{(op == "=" ? string.Empty : op)}{value.Value.ToString("0.##", CultureInfo.InvariantCulture)} {unit}";

    public static string Population(string? code) => code switch
    {
        "child" => "Child",
        "adult" => "Adult",
        "pregnant" => "Pregnant",
        _ => "Population unknown",
    };

    public static string Humanize(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i];
            if (i == 0)
            {
                sb.Append(char.ToUpperInvariant(c));
            }
            else if (char.IsUpper(c))
            {
                sb.Append(' ').Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    public static readonly (string, string)[] Sexes = [("female", "Female"), ("male", "Male"), ("other", "Other"), ("unknown", "Unknown")];

    public static readonly (string, string)[] Ethnicities = [("hispanic", "Hispanic or Latino"), ("notHispanic", "Not Hispanic or Latino"), ("unknown", "Unknown")];

    public static readonly (string, string)[] ResultStatuses = [("final", "Final"), ("preliminary", "Preliminary"), ("corrected", "Corrected")];

    public static readonly (string, string)[] Outcomes = [("liveBirth", "Live birth"), ("loss", "Loss"), ("unknown", "Unknown")];

    public static readonly (string, string)[] ExposureCategories =
        [("unknown", "Unknown"), ("residential", "Residential"), ("occupational", "Occupational"), ("both", "Both"), ("otherNonOccupational", "Other non-occupational")];

    public static readonly (string, string)[] InvestigationStatuses =
        [("requested", "Requested"), ("scheduled", "Scheduled"), ("inProgress", "In progress"), ("findingsIssued", "Findings issued"),
         ("remediation", "Remediation"), ("cleared", "Cleared"), ("closed", "Closed")];

    public static readonly (string, string)[] Populations = [("child", "Child"), ("adult", "Adult"), ("pregnant", "Pregnant"), ("all", "All")];

    public static readonly (string, string)[] ThresholdTypes = [("reportable", "Reportable"), ("case", "Case"), ("action", "Action")];
}
