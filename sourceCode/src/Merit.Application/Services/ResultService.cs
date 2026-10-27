using System.Text.Json;
using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Reference;
using Merit.Application.Security;
using Merit.Domain.Classification;
using Merit.Domain.Ids;
using Merit.Domain.Results;
using Merit.Domain.Thresholds;

namespace Merit.Application.Services;

/// <summary>What the result entry form posts.</summary>
public sealed record ResultEntryInput(
    Guid PersonId,
    Guid? AnalyteId,
    Guid? AnalyteSpecimenId,
    string? ValueText,
    string? Unit,
    string? SampleType,
    string? Speciation,
    decimal? Creatinine,
    DateOnly? CollectedDate,
    DateOnly? AnalyzedDate,
    DateOnly? ReportedDate,
    string? TestMethod,
    Guid? LabOrganizationId,
    Guid? OrderingProviderId,
    string? AccessionNumber,
    string ResultStatus,
    string? ReasonForTest,
    string? PayerType);

public sealed record ThresholdLine(string Type, string Label, decimal Level, string Unit, string Outcome);

/// <summary>"On save the form shows the canonical value and whether any threshold was crossed."</summary>
public sealed record ResultSaveSummary(
    Guid LabResultId,
    string AnalyteName,
    string ValueDisplay,
    string CanonicalDisplay,
    string Population,
    IReadOnlyList<ThresholdLine> Thresholds,
    string CaseOutcome,
    Guid? ExposureCaseId,
    IReadOnlyList<string> Explanation,
    IReadOnlyList<string> TasksCreated,
    bool IsDuplicate);

/// <summary>Manual result entry (fax and paper) and result reads. Intake files take the same path in the worker.</summary>
public sealed class ResultService(
    IClock clock,
    IResultData results,
    ClassificationService classification,
    IReferenceDataProvider reference,
    IThresholdProvider thresholds,
    AuditService audit,
    ICurrentSession session)
{
    public async Task<ResultSaveSummary> SaveManualAsync(ResultEntryInput input, CancellationToken ct = default)
    {
        session.Require(Permissions.ResultEdit);
        var refData = await reference.GetAsync(ct).ConfigureAwait(false);
        var v = new Validator();
        v.Required(nameof(input.AnalyteId), input.AnalyteId, "Analyte");
        v.Required(nameof(input.AnalyteSpecimenId), input.AnalyteSpecimenId, "Specimen");
        v.Required(nameof(input.CollectedDate), input.CollectedDate, "Collection date");
        v.Required(nameof(input.LabOrganizationId), input.LabOrganizationId, "Laboratory");
        v.Required(nameof(input.ValueText), input.ValueText, "Result value");
        v.Required(nameof(input.Unit), input.Unit, "Unit");
        v.ThrowIfInvalid();

        var specimen = refData.Specimen(input.AnalyteSpecimenId!.Value);
        v.When(specimen is null || specimen.AnalyteId != input.AnalyteId, nameof(input.AnalyteSpecimenId), "Choose a specimen for this analyte.");
        v.ThrowIfInvalid();

        var parsed = ResultNormalizer.Parse(input.ValueText);
        v.When(parsed.Value is null, nameof(input.ValueText), "Enter a number, optionally with < or >, such as 4.2 or <1.0.");
        v.When(parsed.Value < 0, nameof(input.ValueText), "The value cannot be negative.");
        var unit = ResultNormalizer.FindUnit(input.Unit, refData.DomainUnitsFor(specimen!.AnalyteSpecimenId));
        v.When(unit is null, nameof(input.Unit), "That unit is not accepted for this analyte and specimen.");
        v.When(specimen.UsesSampleType && string.IsNullOrEmpty(input.SampleType), nameof(input.SampleType), "Choose the sample type (unknown is allowed).");
        v.When(specimen.SpeciationList.Count > 0 && input.Speciation is not null && !specimen.SpeciationList.Contains(input.Speciation),
            nameof(input.Speciation), "Choose a speciation from the list.");
        v.When(specimen.RequiresCreatinine && input.Creatinine is null, nameof(input.Creatinine), "Creatinine is required for this specimen.");
        var today = clock.Today;
        v.When(input.CollectedDate > today, nameof(input.CollectedDate), "The collection date cannot be in the future.");
        v.When(input.ReportedDate is not null && input.ReportedDate < input.CollectedDate, nameof(input.ReportedDate),
            "The reported date must be on or after the collection date.");
        v.When(input.AnalyzedDate is not null && (input.AnalyzedDate < input.CollectedDate || input.AnalyzedDate > (input.ReportedDate ?? today)),
            nameof(input.AnalyzedDate), "The analyzed date must fall between the collected and reported dates.");
        v.When(input.ResultStatus is not ("final" or "preliminary" or "corrected"), nameof(input.ResultStatus), "Choose a result status.");
        v.ThrowIfInvalid();

        var normalized = ResultNormalizer.Normalize(parsed.Operator, parsed.Value!.Value, unit!, specimen.CanonicalUnit,
            input.Creatinine, specimen.RequiresCreatinine);
        string? sampleType = specimen.UsesSampleType ? input.SampleType : null;

        var duplicate = await results.FindDuplicateAsync(input.PersonId, specimen.AnalyteSpecimenId, input.CollectedDate!.Value,
            input.LabOrganizationId!.Value, Clean(input.AccessionNumber), normalized.ValueReported, ct).ConfigureAwait(false);
        bool isCorrection = input.ResultStatus == "corrected";
        bool isDuplicate = duplicate is not null && !isCorrection && duplicate.ResultRowStatus == "valid";

        var snapshot = JsonSerializer.Serialize(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["source"] = "manualEntry",
            ["value"] = input.ValueText,
            ["unit"] = input.Unit,
            ["sampleType"] = input.SampleType,
            ["collectedDate"] = input.CollectedDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            ["accessionNumber"] = input.AccessionNumber,
            ["enteredBy"] = session.UserName,
        });

        Guid id = TimeOrderedId.New();
        await results.SaveLabResultAsync(new LabResultSave
        {
            LabResultId = id,
            PersonId = input.PersonId,
            AnalyteSpecimenId = specimen.AnalyteSpecimenId,
            CollectedDate = input.CollectedDate!.Value,
            AnalyzedDate = input.AnalyzedDate,
            ReportedDate = input.ReportedDate,
            ReceivedUtc = DateTime.UtcNow,
            ResultStatus = input.ResultStatus,
            SupersedesLabResultId = isCorrection ? duplicate?.LabResultId : null,
            ValueReported = normalized.ValueReported,
            ValueText = parsed.RawText,
            Operator = normalized.Operator.ToCode(),
            UnitReported = input.Unit!.Trim(),
            ValueCanonical = normalized.ValueCanonical,
            UnitCanonical = normalized.UnitCanonical,
            SampleType = sampleType,
            TestMethod = Clean(input.TestMethod),
            Speciation = Clean(input.Speciation),
            CreatinineValue = input.Creatinine,
            ValueCreatinineAdj = normalized.ValueCreatinineAdjusted,
            ReasonForTest = Clean(input.ReasonForTest),
            PayerType = Clean(input.PayerType),
            LabOrganizationId = input.LabOrganizationId.Value,
            OrderingProviderId = input.OrderingProviderId,
            AccessionNumber = Clean(input.AccessionNumber),
            ReportedSnapshotJson = snapshot,
            ResultRowStatus = isDuplicate ? "duplicate" : "valid",
        }, ct).ConfigureAwait(false);
        await audit.CreatedAsync("labResult", id, ct).ConfigureAwait(false);

        var analyte = refData.Analyte(specimen.AnalyteId)!;
        string valueDisplay = $"{normalized.Operator.ToCode().Replace("=", string.Empty, StringComparison.Ordinal)}{normalized.ValueReported} {input.Unit}";
        string canonicalDisplay = $"{normalized.Operator.ToCode().Replace("=", string.Empty, StringComparison.Ordinal)}{normalized.ValueCanonical} {normalized.UnitCanonical}";

        if (isDuplicate)
        {
            return new ResultSaveSummary(id, analyte.DisplayName, valueDisplay, canonicalDisplay, "n/a", [],
                "Duplicate of a result already on file (same person, test, date, lab and accession). It was kept and marked duplicate; nothing was reclassified.",
                null, [], [], true);
        }

        var outcome = await classification.ClassifyAsync(id, input.PersonId, specimen.AnalyteSpecimenId,
            new ClassifiableResult(id, input.CollectedDate.Value, sampleType, Clean(input.TestMethod), Clean(input.Speciation),
                normalized.Operator, normalized.ValueCanonical, input.ResultStatus != "preliminary"), ct).ConfigureAwait(false);

        var rules = await thresholds.GetRulesAsync(specimen.AnalyteSpecimenId, input.CollectedDate.Value, ct).ConfigureAwait(false);
        var eval = new ResultForEvaluation(normalized.Operator, normalized.ValueCanonical, outcome.Decision.EffectiveSampleType,
            Clean(input.Speciation), outcome.Population, outcome.AgeMonths);
        var lines = ThresholdEvaluator.Evaluate(eval, rules)
            .Select(o => new ThresholdLine(o.Rule.Type.ToString(), o.Rule.ActionName ?? o.Rule.Type.ToString(), o.Rule.LowerValue,
                specimen.CanonicalUnit, o.Crossing switch
                {
                    Crossing.AtOrAbove => "At or above",
                    Crossing.Indeterminate => "Indeterminate (detection limit above the level)",
                    _ => "Below",
                }))
            .ToList();

        return new ResultSaveSummary(id, analyte.DisplayName, valueDisplay, canonicalDisplay, outcome.Population.ToString(), lines,
            Describe(outcome), outcome.ExposureCaseId, outcome.Decision.Explanation, outcome.TasksCreated, false);
    }

    public async Task<LabResultDetailRow> GetAsync(Guid labResultId, CancellationToken ct = default)
    {
        session.Require(Permissions.ResultView);
        var row = await results.GetLabResultAsync(labResultId, ct).ConfigureAwait(false) ?? throw new NotFoundException();
        await audit.ViewedAsync("labResult", labResultId, ct).ConfigureAwait(false);
        return row;
    }

    public async Task<IReadOnlyList<ResultListRow>> ListAsync(ResultListQuery query, CancellationToken ct = default)
    {
        session.Require(Permissions.ResultView);
        var rows = await results.ListResultsAsync(query, ct).ConfigureAwait(false);
        await audit.ListedAsync("labResult", rows.Count, ct).ConfigureAwait(false);
        return rows;
    }

    private static string Describe(ClassificationOutcome o)
    {
        var d = o.Decision;
        return d.Action switch
        {
            CaseAction.OpenCase => $"Opened {d.Classification.ToCode()} case {o.CaseNumber}" + (o.OwningTenantName is null ? "." : $", owned by {o.OwningTenantName}."),
            CaseAction.AttachToCase when o.ClassificationBefore is not null && o.ClassificationBefore != d.Classification.ToCode()
                => $"Attached to open case {o.CaseNumber}; classification changed from {o.ClassificationBefore} to {d.Classification.ToCode()}.",
            CaseAction.AttachToCase => $"Attached to open case {o.CaseNumber} ({d.Classification.ToCode()}).",
            _ when d.IsIndeterminate => "No case: the detection limit is above the case level, so the result is indeterminate.",
            _ when o.Population == Domain.Population.PopulationKind.Unknown => "No case: the date of birth is missing, so the result went to review.",
            _ => "No case: below the case level.",
        };
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
