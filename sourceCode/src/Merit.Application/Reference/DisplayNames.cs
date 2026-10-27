namespace Merit.Application.Reference;

/// <summary>
/// The words a person reads for a stored code, in one place for the server-rendered pages and the Angular client
/// (issues #37 and #38): the administrator lookups' display names ("icpMs" is "ICP-MS"), the system codes that have no
/// lookup (hold reasons, classifications), and the labels of the fields a laboratory file carries. A code with no name is
/// split from camel case as a last resort.
/// </summary>
public sealed class DisplayNames
{
    /// <summary>System codes: fixed in the program, not administrator lookups.</summary>
    private static readonly Dictionary<string, string> SystemCodes = new(StringComparer.Ordinal)
    {
        // Why a row is held at intake.
        ["unknownSender"] = "Unknown sender", ["unknownTestCode"] = "Unknown test code", ["unknownUnit"] = "Unknown unit",
        ["inactiveAnalyte"] = "Inactive analyte", ["missingResultValue"] = "Missing result value",
        ["missingDob"] = "Missing date of birth", ["unusableAddress"] = "Unusable address",
        ["possibleDuplicate"] = "Possible duplicate", ["uncertainMatch"] = "Uncertain person match",
        ["outOfJurisdiction"] = "Out of jurisdiction", ["impossibleDates"] = "Impossible dates",
        // Cases, results and specimens.
        ["notACase"] = "Not a case", ["childResidential"] = "Child residential", ["adultOccupational"] = "Adult occupational",
        ["pregnancy"] = "Pregnancy", ["wholeBlood"] = "Whole blood", ["notLookedUp"] = "Not looked up",
    };

    /// <summary>The fields of a laboratory file, in the order a person checks them.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> StagingFields =
    [
        ("lastName", "Last name"), ("firstName", "First name"), ("middleName", "Middle name"), ("dob", "Date of birth"),
        ("sex", "Sex"), ("addressLine1", "Street"), ("addressUnit", "Unit"), ("city", "City"), ("state", "State"),
        ("zip5", "ZIP"), ("guardianName", "Guardian"), ("phone", "Phone"), ("loinc", "Test code (LOINC)"), ("value", "Value"),
        ("unit", "Unit of the value"), ("sampleType", "Sample type"), ("testMethod", "Test method"),
        ("collectedDate", "Collected"), ("analyzedDate", "Analyzed"), ("reportedDate", "Reported"),
        ("accessionNumber", "Accession number"), ("resultStatus", "Result status"),
        ("orderingProviderNpi", "Ordering provider NPI"), ("orderingFacility", "Ordering facility"),
        ("labCliaNumber", "Laboratory CLIA number"), ("speciation", "Speciation"),
    ];

    private static readonly Dictionary<string, string> FieldLabels = StagingFields.ToDictionary(f => f.Key, f => f.Label, StringComparer.Ordinal);

    public DisplayNames(ReferenceData reference)
    {
        var codes = new Dictionary<string, string>(SystemCodes, StringComparer.Ordinal);

        // A lookup code means the same thing in every list it is in, except for wording ("Other", "Other race"); a code
        // whose lists disagree is left to the camel-case split, which gives the plain form.
        foreach (var g in reference.Set.Lookups.GroupBy(l => l.Code, StringComparer.Ordinal))
        {
            var names = g.Select(l => l.DisplayName).Distinct(StringComparer.Ordinal).ToList();
            if (names.Count == 1)
            {
                codes.TryAdd(g.Key, names[0]);
            }
        }

        foreach (var a in reference.Set.Analytes)
        {
            codes.TryAdd(a.Code, a.DisplayName);
        }

        Codes = codes;
    }

    /// <summary>Code to name, for the client.</summary>
    public IReadOnlyDictionary<string, string> Codes { get; }

    /// <summary>Field key to label, for the client.</summary>
    public static IReadOnlyDictionary<string, string> Fields => FieldLabels;

    public string Of(string? code) => string.IsNullOrEmpty(code) ? string.Empty : Codes.TryGetValue(code, out var name) ? name : Split(code);

    /// <summary>A file field's label, or its key split from camel case.</summary>
    public static string Field(string key) => FieldLabels.TryGetValue(key, out var label) ? label : Split(key);

    /// <summary>The order of a file's fields for display: known fields first, in <see cref="StagingFields"/> order.</summary>
    public static int FieldOrder(string key)
    {
        for (int i = 0; i < StagingFields.Count; i++)
        {
            if (StagingFields[i].Key == key)
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    /// <summary>"icpMs" becomes "Icp ms": only for codes that have no name.</summary>
    public static string Split(string code)
    {
        var sb = new System.Text.StringBuilder(code.Length + 4);
        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i];
            sb.Append(i == 0 ? char.ToUpperInvariant(c) : char.IsUpper(c) ? $" {char.ToLowerInvariant(c)}" : c.ToString());
        }

        return sb.ToString();
    }
}
