using Merit.Domain.Population;
using Merit.Domain.Results;
using Merit.Domain.Thresholds;

namespace Merit.Domain.Classification;

public enum CaseClassification
{
    NotACase,
    Suspect,
    Confirmed,
}

/// <summary>A result as classification sees it. Values are canonical.</summary>
public sealed record ClassifiableResult(
    Guid LabResultId,
    DateOnly CollectedDate,
    string? SampleType,
    string? TestMethod,
    string? Speciation,
    ResultOperator Operator,
    decimal? ValueCanonical,
    bool IsFinal);

public sealed record ClassificationSettings(
    IReadOnlySet<string> ConfirmingMethods,
    bool UnreportedMethodConfirms,
    bool ArsenicTotalOpensCase)
{
    public static ClassificationSettings Default { get; } =
        new(new HashSet<string>(StringComparer.Ordinal) { "graphiteFurnaceAa", "icpMs" }, true, false);
}

public sealed record OpenCaseInfo(Guid ExposureCaseId, CaseClassification ComputedClassification, decimal? HighestValue);

public sealed record ClassificationInput(
    string AnalyteCode,
    string Specimen,
    PopulationKind Population,
    int? AgeMonths,
    ClassifiableResult Result,
    IReadOnlyList<ClassifiableResult> PriorResults,
    IReadOnlyList<ThresholdRule> Thresholds,
    OpenCaseInfo? OpenCase,
    ClassificationSettings Settings);

public enum CaseAction
{
    None,
    OpenCase,
    AttachToCase,
}

/// <summary>A task the result calls for. Automation proposes and staff decide, so consequences are tasks.</summary>
public sealed record ProposedTask(string QueueCode, string Title, string Detail, int? DueInDays, bool IsUrgent);

public sealed record ClassificationDecision(
    CaseAction Action,
    CaseClassification Classification,
    bool CrossedCaseLevel,
    bool IsIndeterminate,
    bool CrossedReportableLevel,
    string EffectiveSampleType,
    IReadOnlyList<ProposedTask> Tasks,
    IReadOnlyList<string> Explanation,
    bool IsHighest);

/// <summary>
/// Case classification, run per result for one analyte with the population derived for that result and the thresholds
/// in force on its collection date. Child lead follows CDC's Lead in Blood 2023 case definition (CSTE 22-EH-01) with the
/// level taken from the threshold table; adult and pregnancy lead and the other metals follow the specification's rules.
/// </summary>
public static class CaseClassifier
{
    private const int ConfirmWindowDays = 12 * 7;

    public static ClassificationDecision Classify(ClassificationInput input)
    {
        var explain = new List<string>();
        var tasks = new List<ProposedTask>();
        var r = input.Result;
        string effectiveSampleType = EffectiveSampleType(input.AnalyteCode, input.Specimen, input.Population, r, input.Settings);

        if (r.ValueCanonical is null)
        {
            explain.Add("The result has no numeric value, so it cannot be compared with any level.");
            return new ClassificationDecision(CaseAction.None, CaseClassification.NotACase, false, false, false,
                effectiveSampleType, tasks, explain, false);
        }

        var evaluation = new ResultForEvaluation(r.Operator, r.ValueCanonical.Value, effectiveSampleType, r.Speciation,
            input.Population, input.AgeMonths);
        var outcomes = ThresholdEvaluator.Evaluate(evaluation, input.Thresholds);
        var caseOutcomes = outcomes.Where(o => o.Rule.Type == ThresholdType.Case).ToList();
        bool crossedCase = caseOutcomes.Any(o => o.Crossing == Crossing.AtOrAbove);
        bool indeterminate = !crossedCase && caseOutcomes.Any(o => o.Crossing == Crossing.Indeterminate);
        bool reportable = outcomes.Any(o => o.Rule.Type == ThresholdType.Reportable && o.Crossing == Crossing.AtOrAbove);
        bool isHighest = input.OpenCase?.HighestValue is not decimal h || r.ValueCanonical > h;

        // Population unknown: no case opens automatically; a review task, urgent at or above the lowest case level.
        if (input.Population == PopulationKind.Unknown)
        {
            decimal? lowest = ThresholdEvaluator.LowestCaseLevel(input.Thresholds);
            bool urgent = lowest is not null && r.ValueCanonical >= lowest;
            tasks.Add(new ProposedTask("populationReview", "Date of birth missing: review result",
                $"No date of birth, so the population is unknown and no case opens automatically. Value {r.ValueCanonical} "
                + (urgent ? "is at or above the lowest case level for any population." : "is below every case level."),
                urgent ? 2 : 30, urgent));
            explain.Add("Population unknown (no date of birth): sent to review.");
            return new ClassificationDecision(CaseAction.None, CaseClassification.NotACase, false, false, reportable,
                effectiveSampleType, tasks, explain, isHighest);
        }

        // Action tiers create tasks, preliminary results included; they never count toward classification.
        foreach (var o in outcomes.Where(o => o.Rule.Type == ThresholdType.Action && o.Crossing == Crossing.AtOrAbove))
        {
            tasks.Add(new ProposedTask(o.Rule.QueueCode ?? "actionLevel", o.Rule.ActionName ?? "Action level reached",
                $"{r.ValueCanonical} is at or above {o.Rule.LowerValue}"
                + (o.Rule.UpperValue is null ? string.Empty : $" (tier below {o.Rule.UpperValue})")
                + (o.Rule.Authority is null ? "." : $": {o.Rule.Authority}"),
                o.Rule.DueIntervalDays, o.Rule.DueIntervalDays is <= 7));
            explain.Add($"Action level {o.Rule.LowerValue} reached: {o.Rule.ActionName}.");
        }

        if (indeterminate)
        {
            tasks.Add(new ProposedTask("detectionLimit", "Detection limit above the case level",
                $"The lab reported <{r.ValueCanonical}, a limit above the case level, so the result can neither open nor rule "
                + "out a case. Follow up with the reporter.", 30, false));
            explain.Add("Detection limit above the case level: indeterminate, no case.");
        }

        if (!r.IsFinal)
        {
            explain.Add("Preliminary result: may trigger action tasks but does not count toward classification.");
            return new ClassificationDecision(CaseAction.None, CaseClassification.NotACase, crossedCase, indeterminate,
                reportable, effectiveSampleType, tasks, explain, false);
        }

        // Arsenic: total urine arsenic above the action level is often harmless seafood arsenic.
        bool arsenicTotalOnly = input.AnalyteCode == "AS" && (r.Speciation ?? "total") == "total";
        if (arsenicTotalOnly && !crossedCase && input.Settings.ArsenicTotalOpensCase
            && outcomes.Any(o => o.Rule.ActionCode == "speciationRecommended" && o.Crossing == Crossing.AtOrAbove))
        {
            crossedCase = true;
            tasks.RemoveAll(t => t.QueueCode == "speciationRecommended");
            explain.Add("Jurisdiction setting: a high total urine arsenic opens a case.");
        }

        if (!crossedCase)
        {
            if (!indeterminate)
            {
                explain.Add(caseOutcomes.Count == 0
                    ? "No case level is configured for this analyte, specimen and population."
                    : $"Below the case level ({string.Join(", ", caseOutcomes.Select(o => o.Rule.LowerValue))}).");
            }

            return new ClassificationDecision(input.OpenCase is null ? CaseAction.None : CaseAction.AttachToCase,
                input.OpenCase?.ComputedClassification ?? CaseClassification.NotACase, false, indeterminate, reportable,
                effectiveSampleType, tasks, explain, isHighest);
        }

        CaseClassification computed = input.AnalyteCode == "PB" && input.Specimen == "wholeBlood"
            ? ClassifyLead(input, effectiveSampleType, explain, tasks)
            : Confirmed(explain, "At or above the case level for this analyte, specimen and population.");

        if (input.OpenCase is not null)
        {
            var merged = computed > input.OpenCase.ComputedClassification ? computed : input.OpenCase.ComputedClassification;
            if (merged != input.OpenCase.ComputedClassification)
            {
                explain.Add($"The open case is upgraded from {input.OpenCase.ComputedClassification} to {merged}.");
            }

            return new ClassificationDecision(CaseAction.AttachToCase, merged, true, false, reportable, effectiveSampleType,
                tasks, explain, isHighest);
        }

        return new ClassificationDecision(CaseAction.OpenCase, computed, true, false, reportable, effectiveSampleType,
            tasks, explain, true);
    }

    /// <summary>
    /// Sample type as the case definition counts it. For children an unknown sample type counts as capillary, and so does
    /// a venous result from a method that does not confirm. For adults and pregnant people unknown counts as venous.
    /// </summary>
    public static string EffectiveSampleType(string analyteCode, string specimen, PopulationKind population,
                                             ClassifiableResult r, ClassificationSettings settings)
    {
        if (specimen != "wholeBlood")
        {
            return "notApplicable";
        }

        string reported = r.SampleType ?? "unknown";
        if (population == PopulationKind.Child && analyteCode == "PB")
        {
            if (reported == "unknown")
            {
                return "capillary";
            }

            if (reported == "venous" && !MethodConfirms(r.TestMethod, settings))
            {
                return "capillary";
            }

            return reported;
        }

        return reported == "unknown" ? "venous" : reported;
    }

    public static bool MethodConfirms(string? method, ClassificationSettings settings)
        => string.IsNullOrEmpty(method) || method == "unknown"
            ? settings.UnreportedMethodConfirms
            : settings.ConfirmingMethods.Contains(method);

    private static CaseClassification ClassifyLead(ClassificationInput input, string sampleType, List<string> explain,
                                                   List<ProposedTask> tasks)
    {
        if (input.Population == PopulationKind.Child)
        {
            if (sampleType == "venous")
            {
                return Confirmed(explain, "Child: one venous result at or above the level, by a confirming method.");
            }

            // Two capillary results at or above the level within 12 weeks of each other confirm.
            var caseLevel = input.Thresholds.Where(t => t.Type == ThresholdType.Case).ToList();
            var qualifyingCapillary = input.PriorResults
                .Where(p => p.IsFinal && p.LabResultId != input.Result.LabResultId && p.ValueCanonical is not null)
                .Where(p => EffectiveSampleType("PB", "wholeBlood", PopulationKind.Child, p, input.Settings) == "capillary")
                .Where(p => ThresholdEvaluator.Evaluate(
                        new ResultForEvaluation(p.Operator, p.ValueCanonical!.Value, "capillary", null, PopulationKind.Child,
                            input.AgeMonths), caseLevel)
                    .Any(o => o.Crossing == Crossing.AtOrAbove))
                .ToList();

            if (qualifyingCapillary.Any(p => Math.Abs(p.CollectedDate.DayNumber - input.Result.CollectedDate.DayNumber)
                                             <= ConfirmWindowDays))
            {
                return Confirmed(explain, "Child: two capillary results at or above the level within 12 weeks.");
            }

            explain.Add(qualifyingCapillary.Count > 0
                ? "Child: two capillary results at or above the level more than 12 weeks apart: suspect."
                : "Child: one capillary result at or above the level: suspect.");
            if (!tasks.Any(t => t.QueueCode == "confirmatoryTest"))
            {
                tasks.Add(new ProposedTask("confirmatoryTest", "Confirmatory venous test due",
                    "A suspect case opens with a confirmatory venous test task.", 90, false));
            }

            return CaseClassification.Suspect;
        }

        // Adults and pregnant people: unknown sample type already counts as venous.
        if (sampleType == "capillary")
        {
            explain.Add("Capillary result at or above the level: suspect, with a confirmatory venous test task.");
            if (!tasks.Any(t => t.QueueCode == "confirmatoryTest"))
            {
                tasks.Add(new ProposedTask("confirmatoryTest", "Confirmatory venous test due",
                    "A capillary result opens a suspect case with a confirmatory venous test task.", 30, false));
            }

            return CaseClassification.Suspect;
        }

        return Confirmed(explain, "Venous result at or above the case level.");
    }

    private static CaseClassification Confirmed(List<string> explain, string why)
    {
        explain.Add(why);
        return CaseClassification.Confirmed;
    }

    public static string ToCode(this CaseClassification c) => c switch
    {
        CaseClassification.Confirmed => "confirmed",
        CaseClassification.Suspect => "suspect",
        _ => "notACase",
    };

    public static CaseClassification ParseClassification(string? code) => code switch
    {
        "confirmed" => CaseClassification.Confirmed,
        "suspect" => CaseClassification.Suspect,
        _ => CaseClassification.NotACase,
    };

    /// <summary>The workflow template the case's population picks.</summary>
    public static string WorkflowTemplate(PopulationKind population) => population switch
    {
        PopulationKind.Child => "childResidential",
        PopulationKind.Pregnant => "pregnancy",
        _ => "adultOccupational",
    };
}
