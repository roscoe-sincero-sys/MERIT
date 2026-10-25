using Merit.Domain.Population;
using Merit.Domain.Results;

namespace Merit.Domain.Thresholds;

public enum ThresholdType
{
    Reportable,
    Case,
    Action,
}

/// <summary>One effective-dated level from config.threshold, already filtered to the collection date.</summary>
public sealed record ThresholdRule(
    Guid ThresholdId,
    string Population,
    int? AgeMinMonths,
    int? AgeMaxMonths,
    string? Speciation,
    string? SampleType,
    ThresholdType Type,
    string? ActionCode,
    string? ActionName,
    string? QueueCode,
    int? DueIntervalDays,
    decimal LowerValue,
    decimal? UpperValue,
    string Comparator,
    string? Authority);

/// <summary>The facts about one result that thresholds are compared against, in canonical units.</summary>
public sealed record ResultForEvaluation(
    ResultOperator Operator,
    decimal Value,
    string? EffectiveSampleType,
    string? Speciation,
    PopulationKind Population,
    int? AgeMonths);

public enum Crossing
{
    Below,
    AtOrAbove,

    /// <summary>A "&lt;" result whose detection limit is above the level: it cannot be judged against it.</summary>
    Indeterminate,
}

public sealed record ThresholdOutcome(ThresholdRule Rule, Crossing Crossing);

/// <summary>
/// Compares a result with the thresholds in force on its collection date. A "&lt;" result is below a level only when
/// its limit is at or below the level; when the limit is above it (a "&lt;5" against 3.5) the result is indeterminate.
/// A "&gt;" result is at or above a level when its limit is. Tiered action rows carry an exclusive upper bound so
/// each tier matches its own band.
/// </summary>
public static class ThresholdEvaluator
{
    public static IReadOnlyList<ThresholdOutcome> Evaluate(ResultForEvaluation result, IEnumerable<ThresholdRule> rules)
        => rules.Where(r => Applies(r, result)).Select(r => new ThresholdOutcome(r, Compare(result, r))).ToList();

    public static bool Applies(ThresholdRule rule, ResultForEvaluation result)
    {
        if (rule.Population != "all" && rule.Population != result.Population.ToCode())
        {
            return false;
        }

        if (rule.AgeMinMonths is not null && (result.AgeMonths is null || result.AgeMonths < rule.AgeMinMonths))
        {
            return false;
        }

        if (rule.AgeMaxMonths is not null && (result.AgeMonths is null || result.AgeMonths >= rule.AgeMaxMonths))
        {
            return false;
        }

        if (rule.Speciation is not null && !string.Equals(rule.Speciation, result.Speciation ?? "total", StringComparison.Ordinal))
        {
            return false;
        }

        return rule.SampleType is null || string.Equals(rule.SampleType, result.EffectiveSampleType, StringComparison.Ordinal);
    }

    public static Crossing Compare(ResultForEvaluation result, ThresholdRule rule)
    {
        bool strict = rule.Comparator == ">";
        bool atOrAboveLower = strict ? result.Value > rule.LowerValue : result.Value >= rule.LowerValue;
        bool belowUpper = rule.UpperValue is null || result.Value < rule.UpperValue;

        switch (result.Operator)
        {
            case ResultOperator.LessThan:
                // The true value is below the limit. At or below the level: below. Above the level: unknown.
                return result.Value <= rule.LowerValue ? Crossing.Below : Crossing.Indeterminate;
            case ResultOperator.GreaterThan:
                // The true value is above the limit, so it is at or above the level when the limit is.
                return result.Value >= rule.LowerValue && rule.UpperValue is null ? Crossing.AtOrAbove
                     : result.Value >= rule.LowerValue && belowUpper ? Crossing.AtOrAbove
                     : Crossing.Below;
            default:
                return atOrAboveLower && belowUpper ? Crossing.AtOrAbove : Crossing.Below;
        }
    }

    /// <summary>Lowest case level for any population: makes a no-DOB review task urgent, and held rows urgent.</summary>
    public static decimal? LowestCaseLevel(IEnumerable<ThresholdRule> rules)
    {
        var levels = rules.Where(r => r.Type == ThresholdType.Case).Select(r => r.LowerValue).ToList();
        return levels.Count == 0 ? null : levels.Min();
    }

    /// <summary>Lowest action level for any population, used to sort urgent held records when no DOB is usable.</summary>
    public static decimal? LowestActionLevel(IEnumerable<ThresholdRule> rules)
    {
        var levels = rules.Where(r => r.Type == ThresholdType.Action).Select(r => r.LowerValue).ToList();
        return levels.Count == 0 ? null : levels.Min();
    }
}
