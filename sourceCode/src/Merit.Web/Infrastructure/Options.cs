using System.Globalization;
using Merit.Application.Reference;
using Merit.Web.Models;

namespace Merit.Web.Infrastructure;

/// <summary>Drop-down options built from reference data, for view models. Display only; no rules here.</summary>
public static class Options
{
    public static string Language(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "Not recorded";
        try { return CultureInfo.GetCultureInfo(code).EnglishName; }
        catch (CultureNotFoundException) { return Humanize(code); }
    }
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

    /// <summary>A stored UTC instant as the jurisdiction's date and time (see <see cref="DisplayTime"/>).</summary>
    public static string Date(DateTime? d) => d is null ? string.Empty
        : DisplayTime.ToLocal(d.Value).ToString("MMM d, yyyy h:mm tt", CultureInfo.InvariantCulture);

    /// <summary>
    /// An intake profile as a choice: its format first, so a narrow list still shows it, then the sender, then whatever
    /// the profile's name adds beyond the sender and the format (issue #148): "HL7 message from Chesapeake Reference
    /// Laboratory", not "Chesapeake Reference Laboratory — Chesapeake Reference Laboratory HL7 (v1, hl7)".
    /// </summary>
    public static string IntakeProfile(string sender, string profileName, string fileType)
    {
        string format = fileType switch
        {
            "hl7" => "HL7 message",
            "tsv" => "Tab-separated file",
            "pipe" => "Pipe-separated file",
            _ => "CSV file",
        };
        string rest = profileName.StartsWith(sender, StringComparison.OrdinalIgnoreCase) ? profileName[sender.Length..] : profileName;
        rest = string.Join(' ', rest.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !w.Equals(fileType, StringComparison.OrdinalIgnoreCase) && !w.Equals("csv", StringComparison.OrdinalIgnoreCase)
                        && !w.Equals("hl7", StringComparison.OrdinalIgnoreCase))).Trim(' ', '-', '—', ',');
        return $"{format} from {sender}" + (rest.Length == 0 ? string.Empty : $" ({rest})");
    }

    /// <summary>
    /// One choice per unit (issue #165): spellings of the same unit ("ug/dL", "mcg/dL", "µg/dL") with the same multiplier
    /// are one choice, shown as the canonical spelling when it is one of them, else the first. A chosen synonym selects its
    /// unit. Files may still use any accepted spelling.
    /// </summary>
    public static IReadOnlyList<SelectOption> UnitChoices(IEnumerable<(string Text, decimal Multiplier)> units, string canonical, string? selected)
    {
        static string Key(string unit) => unit.Trim().Replace("µ", "u", StringComparison.Ordinal).Replace("μ", "u", StringComparison.Ordinal)
            .Replace("mcg", "ug", StringComparison.OrdinalIgnoreCase).Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        string want = selected ?? canonical;
        return units.GroupBy(u => (Key(u.Text), u.Multiplier))
            .Select(g =>
            {
                string text = g.Any(u => u.Text == canonical) ? canonical : g.First().Text;
                return new SelectOption(text, text, g.Any(u => u.Text == want) || Key(want) == g.Key.Item1);
            })
            .ToList();
    }

    /// <summary>A count with its noun, singular or plural: "1 task", "3 tasks" (issue #166).</summary>
    public static string Count(int? n, string singular, string? plural = null)
        => $"{n ?? 0} {(n == 1 ? singular : plural ?? singular + "s")}";

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

    /// <summary>
    /// The name of a stored code: its lookup's display name ("icpMs" is "ICP-MS"), a system code's name, or the code split
    /// from camel case (issue #37). The names come from <see cref="DisplayTime"/>'s per-request reference data.
    /// </summary>
    public static string Humanize(string? code)
        => string.IsNullOrEmpty(code) ? string.Empty : DisplayTime.Names?.Of(code) ?? Merit.Application.Reference.DisplayNames.Split(code);

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
