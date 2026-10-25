using System.Globalization;
using Merit.Application.Data;
using Merit.Application.Security;
using Merit.Domain.Classification;
using Microsoft.Extensions.Caching.Memory;

namespace Merit.Application.Reference;

/// <summary>
/// The jurisdiction settings in force today, typed. Anything that differs between jurisdictions is a setting rather
/// than code; settings the specification says "must be set at install" stay null until an administrator sets them,
/// and the features that depend on them say so instead of guessing.
/// </summary>
public sealed class JurisdictionSettings
{
    public JurisdictionSettings(IReadOnlyDictionary<string, string> values)
    {
        Values = values;
    }

    public IReadOnlyDictionary<string, string> Values { get; }

    public string StatePostalCode => Text("statePostalCode") ?? string.Empty;

    public string StateFips => Text("stateFips") ?? string.Empty;

    public string AgencyName => Text("agencyName") ?? "MERIT";

    public int AdultAgeCutoffYears => Int("adultAgeCutoffYears") ?? 16;

    public int PregnancyMaxWeeks => Int("pregnancyMaxWeeks") ?? 42;

    /// <summary>"review" (default): a new lab address creates an address review task; "move": it becomes the primary.</summary>
    public bool NewLabAddressMovesPrimary => string.Equals(Text("primaryResidencePolicy"), "move", StringComparison.Ordinal);

    public int? InvestigationLookbackDays => Int("investigationLookbackDays");

    public decimal? SiteInvestigationMinHours => Decimal("siteInvestigationMinHours");

    public int? ContactAttemptsBeforeLetter => Int("contactAttemptsBeforeLetter");

    public int? ClusterWindowDays => Int("clusterWindowDays");

    public int? ClusterMinCount => Int("clusterMinCount");

    public int PasswordMinLength => Int("passwordMinLength") ?? 8;

    public int SessionTimeoutMinutes => Int("sessionTimeoutMinutes") ?? 20;

    public ClassificationSettings Classification => new(
        (Text("confirmingMethods") ?? "graphiteFurnaceAa,icpMs").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal),
        Bool("unreportedMethodConfirms") ?? true,
        string.Equals(Text("arsenicHandling"), "case", StringComparison.Ordinal));

    public string? Text(string key)
        => Values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) && v != "NOTSET" ? v : null;

    private int? Int(string key) => int.TryParse(Text(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    private decimal? Decimal(string key) => decimal.TryParse(Text(key), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;

    private bool? Bool(string key) => bool.TryParse(Text(key), out var b) ? b : null;
}

public interface ISettingsProvider
{
    Task<JurisdictionSettings> GetAsync(CancellationToken ct = default);

    void Invalidate();
}

public sealed class SettingsProvider(IConfigData data, IMemoryCache cache, ICurrentSession session) : ISettingsProvider
{
    public async Task<JurisdictionSettings> GetAsync(CancellationToken ct = default)
    {
        string key = $"merit:settings:{session.RootTenantId}";
        if (cache.TryGetValue(key, out JurisdictionSettings? cached) && cached is not null)
        {
            return cached;
        }

        var rows = await data.GetSettingsAsync(null, false, ct).ConfigureAwait(false);
        var values = rows.GroupBy(r => r.SettingKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.EffectiveFrom).First().SettingValue, StringComparer.Ordinal);
        var settings = new JurisdictionSettings(values);
        cache.Set(key, settings, TimeSpan.FromMinutes(2));
        return settings;
    }

    public void Invalidate() => cache.Remove($"merit:settings:{session.RootTenantId}");
}
