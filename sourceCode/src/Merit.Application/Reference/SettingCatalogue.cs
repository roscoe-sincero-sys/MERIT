using System.Globalization;

namespace Merit.Application.Reference;

/// <summary>How a setting's value is checked.</summary>
public enum SettingKind
{
    Integer,
    Decimal,
    Boolean,
    Code,
    CodeList,
    PostalCode,
    Fips,
    TenantCode,
    Text,
}

/// <summary>
/// One jurisdiction setting MERIT knows (issue #289). <see cref="InUse"/> false marks a setting stored for a feature
/// that is not built: it is shown read-only and cannot be changed, so the page never offers a control with no effect.
/// </summary>
public sealed record SettingDefinition(string Key, SettingKind Kind, decimal? Min = null, decimal? Max = null,
                                       IReadOnlyList<string>? Codes = null, bool AllowNotSet = false, bool InUse = true)
{
    /// <summary>The kind as config.tvfSettingCatalogue names it.</summary>
    public string KindCode => char.ToLowerInvariant(Kind.ToString()[0]) + Kind.ToString()[1..];

    /// <summary>What the Settings page says an acceptable value is.</summary>
    public string Hint => Kind switch
    {
        SettingKind.Integer => $"Whole number, {Number(Min)} to {Number(Max)}" + NotSetHint,
        SettingKind.Decimal => $"Number, {Number(Min)} to {Number(Max)}" + NotSetHint,
        SettingKind.Boolean => "true or false",
        SettingKind.Code => "One of: " + string.Join(", ", Codes!),
        SettingKind.CodeList => "One or more of: " + string.Join(", ", Codes!) + " (comma-separated)",
        SettingKind.PostalCode => "Two capital letters",
        SettingKind.Fips => "Two digits",
        SettingKind.TenantCode => "Code of an active jurisdiction or department in this data root",
        _ => string.Empty,
    };

    private string NotSetHint => AllowNotSet ? ", or NOTSET" : string.Empty;

    private static string Number(decimal? value) => (value ?? 0).ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>
/// The settings catalogue: the same rows as config.tvfSettingCatalogue, which config.uspSaveSetting enforces again.
/// An integration test compares the two, so a setting cannot be added to one side only.
/// </summary>
public static class SettingCatalogue
{
    public const string NotSet = "NOTSET";

    public const string NotInUseMessage = "This setting is not in use in this version of MERIT and cannot be changed.";

    public const int SessionTimeoutMinimum = 5;

    public const int SessionTimeoutMaximum = 60;

    private static readonly string[] Methods = ["icpMs", "graphiteFurnaceAa", "anodicStrippingVoltammetry", "pointOfCare", "other", "unknown"];

    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        new("statePostalCode", SettingKind.PostalCode),
        new("stateFips", SettingKind.Fips),
        new("agencyName", SettingKind.Text, InUse: false),
        new("adultAgeCutoffYears", SettingKind.Integer, 12, 21),
        new("pregnancyMaxWeeks", SettingKind.Integer, 20, 52),
        new("primaryResidencePolicy", SettingKind.Code, Codes: ["review", "move"]),
        new("investigationLookbackDays", SettingKind.Integer, 1, 3650, AllowNotSet: true),
        new("siteInvestigationMinHours", SettingKind.Decimal, 0.5m, 168, AllowNotSet: true),
        new("contactAttemptsBeforeLetter", SettingKind.Integer, 1, 20, AllowNotSet: true),
        new("localNotification", SettingKind.Boolean, InUse: false),
        new("arsenicHandling", SettingKind.Code, Codes: ["task", "case"]),
        new("occupationalReferralAgency", SettingKind.Text, InUse: false),
        new("confirmingMethods", SettingKind.CodeList, Codes: Methods),
        new("unreportedMethodConfirms", SettingKind.Boolean),
        new("matchingPreset", SettingKind.Code, Codes: ["strict", "balanced", "lenient"], InUse: false),
        new("cblsEnabled", SettingKind.Boolean),
        new("ablesEnabled", SettingKind.Boolean),
        new("providerLookups", SettingKind.Code, Codes: ["live", "file"], InUse: false),
        new("emailCodesEnabled", SettingKind.Boolean),
        new("smsCodesEnabled", SettingKind.Boolean, InUse: false),
        new("adultCaseOwnerTenantCode", SettingKind.TenantCode),
        new("clusterWindowDays", SettingKind.Integer, 1, 3650, AllowNotSet: true),
        new("clusterMinCount", SettingKind.Integer, 2, 1000, AllowNotSet: true),
        new("passwordMinLength", SettingKind.Integer, 15, 128),
        new("sessionTimeoutMinutes", SettingKind.Integer, SessionTimeoutMinimum, SessionTimeoutMaximum),
        new("testingPolicyVersion", SettingKind.Code, Codes: ["none", "MDH-2025-03"]),
    ];

    private static readonly Dictionary<string, SettingDefinition> ByKey = All.ToDictionary(d => d.Key, StringComparer.Ordinal);

    public static SettingDefinition? Find(string? key) => key is not null && ByKey.TryGetValue(key, out var d) ? d : null;

    /// <summary>Why the key and (already trimmed) value cannot be saved, or null. A tenant code is checked by the database.</summary>
    public static string? Problem(string? key, string? value)
    {
        var d = Find(key);
        if (d is null)
        {
            return "This is not a MERIT setting.";
        }

        if (!d.InUse)
        {
            return NotInUseMessage;
        }

        if (string.IsNullOrEmpty(value))
        {
            return "Enter a value.";
        }

        if (d.AllowNotSet && value == NotSet)
        {
            return null;
        }

        switch (d.Kind)
        {
            case SettingKind.Integer:
                bool whole = value.Length <= 6 && value.All(char.IsAsciiDigit)
                             && int.Parse(value, CultureInfo.InvariantCulture) >= d.Min && int.Parse(value, CultureInfo.InvariantCulture) <= d.Max;
                return whole ? null : $"Enter a whole number from {d.Min:0} to {d.Max:0}.";
            case SettingKind.Decimal:
                int point = value.IndexOf('.', StringComparison.Ordinal);
                bool number = value.Length <= 8 && value.All(c => char.IsAsciiDigit(c) || c == '.') && value.Any(char.IsAsciiDigit)
                              && value.Count(c => c == '.') <= 1 && (point < 0 || value.Length - point - 1 <= 2)
                              && decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal parsed)
                              && parsed >= d.Min && parsed <= d.Max;
                return number ? null : string.Create(CultureInfo.InvariantCulture, $"Enter a number from {d.Min:0.##} to {d.Max:0.##}, with at most two decimal places.");
            case SettingKind.Boolean:
                return value is "true" or "false" ? null : "Enter true or false.";
            case SettingKind.Code:
                return d.Codes!.Contains(value, StringComparer.Ordinal) ? null : $"Choose one of: {string.Join(", ", d.Codes!)}.";
            case SettingKind.CodeList:
                string[] parts = value.Split(',');
                bool listed = parts.All(p => d.Codes!.Contains(p, StringComparer.Ordinal)) && parts.Distinct(StringComparer.Ordinal).Count() == parts.Length;
                return listed ? null : $"Enter one or more of these, separated by commas and without repeats: {string.Join(", ", d.Codes!)}.";
            case SettingKind.PostalCode:
                return value.Length == 2 && value.All(char.IsAsciiLetterUpper) ? null : "Enter the two-letter postal code in capitals.";
            case SettingKind.Fips:
                return value.Length == 2 && value.All(char.IsAsciiDigit) ? null : "Enter the two-digit state FIPS code.";
            default:
                return null;
        }
    }
}
