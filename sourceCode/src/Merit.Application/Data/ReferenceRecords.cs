namespace Merit.Application.Data;

// Rows returned by the config.* stored procedures.

public sealed class AnalyteRow
{
    public Guid AnalyteId { get; init; }

    public string Code { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public bool IsActive { get; init; }

    public int SortOrder { get; init; }

    public string ExtractTargets { get; init; } = string.Empty;
}

public sealed class AnalyteSpecimenRow
{
    public Guid AnalyteSpecimenId { get; init; }

    public Guid AnalyteId { get; init; }

    public string Specimen { get; init; } = string.Empty;

    public string CanonicalUnit { get; init; } = string.Empty;

    public bool UsesSampleType { get; init; }

    public bool RequiresCreatinine { get; init; }

    public string? SpeciationOptions { get; init; }

    public bool IsActive { get; init; }

    public IReadOnlyList<string> SpeciationList
        => string.IsNullOrWhiteSpace(SpeciationOptions)
            ? []
            : SpeciationOptions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public sealed class AcceptedUnitRow
{
    public Guid AcceptedUnitId { get; init; }

    public Guid AnalyteSpecimenId { get; init; }

    public string UnitText { get; init; } = string.Empty;

    public decimal MultiplierToCanonical { get; init; }

    public int SortOrder { get; init; }
}

public sealed class LoincRow
{
    public Guid LoincCodeId { get; init; }

    public Guid AnalyteSpecimenId { get; init; }

    public string LoincCode { get; init; } = string.Empty;

    public string? ImpliedSampleType { get; init; }

    public string? ImpliedSpeciation { get; init; }

    public bool IsDiscouraged { get; init; }

    public string? LoincDescription { get; init; }
}

public sealed class ActionCodeRow
{
    public Guid ActionCodeId { get; init; }

    public string Code { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string QueueCode { get; init; } = string.Empty;
}

public sealed class LookupRow
{
    public Guid LookupValueId { get; init; }

    public string LookupType { get; init; } = string.Empty;

    public string Code { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string? InverseLabel { get; init; }

    public int SortOrder { get; init; }

    public bool IsActive { get; init; }

    public string? CblsCode { get; init; }

    public string? AblesCode { get; init; }
}

public sealed record ReferenceDataSet(
    IReadOnlyList<AnalyteRow> Analytes,
    IReadOnlyList<AnalyteSpecimenRow> AnalyteSpecimens,
    IReadOnlyList<AcceptedUnitRow> AcceptedUnits,
    IReadOnlyList<LoincRow> LoincCodes,
    IReadOnlyList<ActionCodeRow> ActionCodes,
    IReadOnlyList<LookupRow> Lookups);

public sealed class ThresholdRow
{
    public Guid ThresholdId { get; init; }

    public Guid AnalyteSpecimenId { get; init; }

    public string AnalyteCode { get; init; } = string.Empty;

    public string AnalyteName { get; init; } = string.Empty;

    public string Specimen { get; init; } = string.Empty;

    public string Population { get; init; } = string.Empty;

    public int? AgeMinMonths { get; init; }

    public int? AgeMaxMonths { get; init; }

    public string? Speciation { get; init; }

    public string? SampleType { get; init; }

    public string ThresholdType { get; init; } = string.Empty;

    public Guid? ActionCodeId { get; init; }

    public string? ActionCode { get; init; }

    public string? ActionName { get; init; }

    public string? QueueCode { get; init; }

    public int? DueIntervalDays { get; init; }

    public decimal LowerValue { get; init; }

    public decimal? UpperValue { get; init; }

    public string Unit { get; init; } = string.Empty;

    public string Comparator { get; init; } = ">=";

    public DateOnly EffectiveFrom { get; init; }

    public DateOnly? EffectiveTo { get; init; }

    public string? Authority { get; init; }

    public bool IsActive { get; init; }

    public byte[] RowVersion { get; init; } = [];
}

public sealed class SettingRow
{
    public Guid JurisdictionSettingId { get; init; }

    public string SettingKey { get; init; } = string.Empty;

    public string SettingValue { get; init; } = string.Empty;

    public DateOnly EffectiveFrom { get; init; }

    public DateOnly? EffectiveTo { get; init; }

    public string? SettingDescription { get; init; }

    public string ModifiedBy { get; init; } = string.Empty;

    public DateTime ModifiedDateUtc { get; init; }
}

public sealed class SubJurisdictionRow
{
    public Guid SubJurisdictionId { get; init; }

    public string CountyFips { get; init; } = string.Empty;

    public string SubJurisdictionName { get; init; } = string.Empty;

    public string JurisdictionType { get; init; } = string.Empty;

    public string? ServedBy { get; init; }

    /// <summary>Ids of the tenants that serve this county, comma-separated.</summary>
    public string? ServedByTenantIds { get; init; }

    public bool IsServedBy(Guid tenantId)
        => (ServedByTenantIds ?? string.Empty).Split(',').Any(id => Guid.TryParse(id, out var g) && g == tenantId);
}

public sealed class EnvironmentalStandardRow
{
    public Guid EnvironmentalStandardId { get; init; }

    public string SampleType { get; init; } = string.Empty;

    public string? Surface { get; init; }

    public decimal StandardValue { get; init; }

    public string Unit { get; init; } = string.Empty;

    public string? Authority { get; init; }

    public DateOnly EffectiveFrom { get; init; }

    public DateOnly? EffectiveTo { get; init; }
}

public sealed class ExtractLayoutRow
{
    public Guid ExtractLayoutId { get; init; }

    public string ExtractType { get; init; } = string.Empty;

    public string LayoutVersion { get; init; } = string.Empty;

    public string FileFormat { get; init; } = string.Empty;

    public string? Delimiter { get; init; }

    public DateOnly EffectiveFrom { get; init; }
}

public sealed class ExtractLayoutFieldRow
{
    public int FieldOrder { get; init; }

    public string FieldName { get; init; } = string.Empty;

    public int FieldWidth { get; init; }

    public string SourceKey { get; init; } = string.Empty;

    public string PadWith { get; init; } = " ";

    public bool AlignRight { get; init; }

    public bool IsRequired { get; init; }
}

public sealed record ExtractLayoutData(ExtractLayoutRow Layout, IReadOnlyList<ExtractLayoutFieldRow> Fields);

/// <summary>Input for config.uspSaveThreshold.</summary>
public sealed record ThresholdSave(
    Guid ThresholdId,
    Guid AnalyteSpecimenId,
    string Population,
    int? AgeMinMonths,
    int? AgeMaxMonths,
    string? Speciation,
    string? SampleType,
    string ThresholdType,
    Guid? ActionCodeId,
    int? DueIntervalDays,
    decimal LowerValue,
    decimal? UpperValue,
    string Unit,
    string Comparator,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    string? Authority,
    bool IsActive,
    byte[]? RowVersion);
