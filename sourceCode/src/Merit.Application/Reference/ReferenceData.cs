using Merit.Application.Data;
using Merit.Application.Security;
using Merit.Domain.Results;
using Merit.Domain.Thresholds;
using Microsoft.Extensions.Caching.Memory;

namespace Merit.Application.Reference;

/// <summary>Shipped reference data with the lookups the result form, intake and every drop-down need.</summary>
public sealed class ReferenceData
{
    private readonly Dictionary<string, AnalyteRow> analytesByCode;
    private readonly Dictionary<Guid, AnalyteRow> analytesById;
    private readonly Dictionary<Guid, AnalyteSpecimenRow> specimensById;
    private readonly Dictionary<string, LoincRow> loincByCode;
    private readonly ILookup<string, LookupRow> lookupsByType;

    public ReferenceData(ReferenceDataSet set)
    {
        Set = set;
        analytesByCode = set.Analytes.ToDictionary(a => a.Code, StringComparer.OrdinalIgnoreCase);
        analytesById = set.Analytes.ToDictionary(a => a.AnalyteId);
        specimensById = set.AnalyteSpecimens.ToDictionary(s => s.AnalyteSpecimenId);
        loincByCode = set.LoincCodes.ToDictionary(l => l.LoincCode, StringComparer.OrdinalIgnoreCase);
        lookupsByType = set.Lookups.ToLookup(l => l.LookupType, StringComparer.Ordinal);
    }

    public ReferenceDataSet Set { get; }

    public IEnumerable<AnalyteRow> ActiveAnalytes => Set.Analytes.Where(a => a.IsActive).OrderBy(a => a.SortOrder);

    public AnalyteRow? Analyte(string code) => analytesByCode.GetValueOrDefault(code);

    public AnalyteRow? Analyte(Guid id) => analytesById.GetValueOrDefault(id);

    public AnalyteSpecimenRow? Specimen(Guid analyteSpecimenId) => specimensById.GetValueOrDefault(analyteSpecimenId);

    public IEnumerable<AnalyteSpecimenRow> SpecimensFor(Guid analyteId)
        => Set.AnalyteSpecimens.Where(s => s.AnalyteId == analyteId && s.IsActive).OrderBy(s => s.Specimen == "wholeBlood" ? 0 : 1);

    /// <summary>Accepted units for a pair, canonical unit first.</summary>
    public IEnumerable<AcceptedUnitRow> UnitsFor(Guid analyteSpecimenId)
    {
        var spec = Specimen(analyteSpecimenId);
        return Set.AcceptedUnits.Where(u => u.AnalyteSpecimenId == analyteSpecimenId)
            .OrderBy(u => spec is not null && u.UnitText == spec.CanonicalUnit ? 0 : 1).ThenBy(u => u.SortOrder);
    }

    public IReadOnlyList<AcceptedUnit> DomainUnitsFor(Guid analyteSpecimenId)
        => UnitsFor(analyteSpecimenId).Select(u => new AcceptedUnit(u.UnitText, u.MultiplierToCanonical)).ToList();

    public LoincRow? Loinc(string? code) => code is null ? null : loincByCode.GetValueOrDefault(code.Trim());

    public IEnumerable<LookupRow> Lookups(string lookupType, bool activeOnly = true)
        => lookupsByType[lookupType].Where(l => !activeOnly || l.IsActive).OrderBy(l => l.SortOrder);

    public string Label(string lookupType, string? code)
        => code is null ? string.Empty : lookupsByType[lookupType].FirstOrDefault(l => l.Code == code)?.DisplayName ?? code;

    public LookupRow? Lookup(string lookupType, string? code)
        => code is null ? null : lookupsByType[lookupType].FirstOrDefault(l => l.Code == code);
}

public interface IReferenceDataProvider
{
    Task<ReferenceData> GetAsync(CancellationToken ct = default);

    void Invalidate();
}

/// <summary>Reference data changes only with a deployment or an administrator edit, so it is cached for ten minutes.</summary>
public sealed class ReferenceDataProvider(IConfigData data, IMemoryCache cache) : IReferenceDataProvider
{
    private const string Key = "merit:referenceData";

    public async Task<ReferenceData> GetAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(Key, out ReferenceData? cached) && cached is not null)
        {
            return cached;
        }

        var set = await data.GetReferenceDataAsync(ct).ConfigureAwait(false);
        var value = new ReferenceData(set);
        cache.Set(Key, value, TimeSpan.FromMinutes(10));
        return value;
    }

    public void Invalidate() => cache.Remove(Key);
}

/// <summary>Thresholds in force on a collection date, for the caller's root tenant, cached briefly per root.</summary>
public interface IThresholdProvider
{
    Task<IReadOnlyList<ThresholdRule>> GetRulesAsync(Guid analyteSpecimenId, DateOnly collectedDate, CancellationToken ct = default);

    void Invalidate();
}

public sealed class ThresholdProvider(IConfigData data, IMemoryCache cache, ICurrentSession session) : IThresholdProvider
{
    public async Task<IReadOnlyList<ThresholdRule>> GetRulesAsync(Guid analyteSpecimenId, DateOnly collectedDate, CancellationToken ct = default)
    {
        string key = $"merit:thresholds:{session.RootTenantId}:{analyteSpecimenId}:{collectedDate:yyyyMMdd}";
        if (cache.TryGetValue(key, out IReadOnlyList<ThresholdRule>? cached) && cached is not null)
        {
            return cached;
        }

        var rows = await data.GetThresholdsAsync(analyteSpecimenId, collectedDate, ct).ConfigureAwait(false);
        IReadOnlyList<ThresholdRule> rules = rows.Select(ToRule).ToList();
        cache.Set(key, rules, TimeSpan.FromMinutes(2));
        return rules;
    }

    public void Invalidate()
    {
        if (cache is MemoryCache mc)
        {
            mc.Compact(1.0);
        }
    }

    public static ThresholdRule ToRule(ThresholdRow t) => new(
        t.ThresholdId, t.Population, t.AgeMinMonths, t.AgeMaxMonths, t.Speciation, t.SampleType,
        t.ThresholdType switch { "case" => ThresholdType.Case, "action" => ThresholdType.Action, _ => ThresholdType.Reportable },
        t.ActionCode, t.ActionName, t.QueueCode, t.DueIntervalDays, t.LowerValue, t.UpperValue, t.Comparator, t.Authority);
}
