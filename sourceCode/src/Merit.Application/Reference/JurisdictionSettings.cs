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

    public int AdultAgeCutoffYears => Int("adultAgeCutoffYears") ?? 16;

    public int PregnancyMaxWeeks => Int("pregnancyMaxWeeks") ?? 42;

    /// <summary>"review" (default): a new lab address creates an address review task; "move": it becomes the primary.</summary>
    public bool NewLabAddressMovesPrimary => string.Equals(Text("primaryResidencePolicy"), "move", StringComparison.Ordinal);

    public int? InvestigationLookbackDays => Int("investigationLookbackDays");

    public decimal? SiteInvestigationMinHours => Decimal("siteInvestigationMinHours");

    public int? ContactAttemptsBeforeLetter => Int("contactAttemptsBeforeLetter");

    public int? ClusterWindowDays => Int("clusterWindowDays");

    public int? ClusterMinCount => Int("clusterMinCount");

    public int PasswordMinLength => PasswordPolicy.EffectiveMinimum(Int("passwordMinLength") ?? PasswordPolicy.MinimumLength);

    /// <summary>Whether the quarterly CDC CBLS extract may be produced (default true).</summary>
    public bool CblsEnabled => Bool("cblsEnabled") ?? true;

    /// <summary>Whether the annual NIOSH ABLES extract may be produced (default true).</summary>
    public bool AblesEnabled => Bool("ablesEnabled") ?? true;

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
    Task<JurisdictionSettings> GetAsOfAsync(DateOnly date, CancellationToken ct = default) => GetAsync(ct);

    void Invalidate();
}

public sealed class SettingsProvider(IConfigData data, IMemoryCache cache, ICurrentSession session, IClock clock) : ISettingsProvider
{
    public Task<JurisdictionSettings> GetAsync(CancellationToken ct = default) => GetAsOfAsync(clock.Today,ct);
    public async Task<JurisdictionSettings> GetAsOfAsync(DateOnly date, CancellationToken ct = default)
    {
        string key = $"merit:settings:{session.RootTenantId}";
        if (!cache.TryGetValue(key, out IReadOnlyList<SettingRow>? rows) || rows is null)
        {
            rows = await data.GetSettingsAsync(null, true, ct).ConfigureAwait(false);
            cache.Set(key, rows, TimeSpan.FromMinutes(2));
        }
        var values = rows.Where(r => r.EffectiveFrom<=date && (r.EffectiveTo is null || r.EffectiveTo>=date)).GroupBy(r => r.SettingKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.EffectiveFrom).First().SettingValue, StringComparer.Ordinal);
        var settings = new JurisdictionSettings(values);
        return settings;
    }

    public void Invalidate() => cache.Remove($"merit:settings:{session.RootTenantId}");
}
