using Merit.Domain.Classification;
using Merit.Domain.Population;
using Merit.Domain.Results;
using Merit.Domain.Thresholds;

namespace Merit.Tests;

/// <summary>Case classification for blood lead: the child and adult rules, preliminary results and detection limits.</summary>
public sealed class ClassificationTests
{
    private static readonly DateOnly Collected = new(2026, 6, 1);

    private static readonly ThresholdRule ChildCase = Rule("child", ThresholdType.Case, 3.5m);
    private static readonly ThresholdRule AdultCase = Rule("adult", ThresholdType.Case, 5m);
    private static readonly ThresholdRule AdultRemoval = Rule("adult", ThresholdType.Action, 50m) with
    {
        ActionCode = "medicalRemoval", ActionName = "Medical removal", QueueCode = "occupationalFollowUp", DueIntervalDays = 2,
    };

    private static ThresholdRule Rule(string population, ThresholdType type, decimal lower)
        => new(Guid.NewGuid(), population, null, null, null, null, type, null, null, null, null, lower, null, ">=", null);

    private static ClassifiableResult Result(decimal? value, string? sample = "venous", string? method = "icpMs",
                                             bool final = true, ResultOperator op = ResultOperator.Equal, DateOnly? on = null)
        => new(Guid.NewGuid(), on ?? Collected, sample, method, null, op, value, final);

    private static ClassificationDecision Classify(PopulationKind population, ClassifiableResult r,
                                                   IReadOnlyList<ClassifiableResult>? prior = null, OpenCaseInfo? open = null)
        => CaseClassifier.Classify(new ClassificationInput("PB", "wholeBlood", population,
            population == PopulationKind.Child ? 36 : 420, r, prior ?? [], [ChildCase, AdultCase, AdultRemoval], open,
            ClassificationSettings.Default));

    [Fact]
    public void ChildVenousAtTheLevelByAConfirmingMethodIsConfirmed()
    {
        var d = Classify(PopulationKind.Child, Result(3.5m));
        Assert.Equal(CaseAction.OpenCase, d.Action);
        Assert.Equal(CaseClassification.Confirmed, d.Classification);
    }

    [Fact]
    public void ChildVenousByANonConfirmingMethodCountsAsCapillary()
    {
        var d = Classify(PopulationKind.Child, Result(6m, method: "leadCare"));
        Assert.Equal("capillary", d.EffectiveSampleType);
        Assert.Equal(CaseClassification.Suspect, d.Classification);
        Assert.Contains(d.Tasks, t => t.QueueCode == "confirmatoryTest");
    }

    [Fact]
    public void TwoChildCapillaryResultsWithinTwelveWeeksConfirm()
    {
        var earlier = Result(4m, sample: "capillary", on: Collected.AddDays(-60));
        var d = Classify(PopulationKind.Child, Result(5m, sample: "capillary"), [earlier]);
        Assert.Equal(CaseClassification.Confirmed, d.Classification);
    }

    [Fact]
    public void TwoChildCapillaryResultsMoreThanTwelveWeeksApartStaySuspect()
    {
        var earlier = Result(4m, sample: "capillary", on: Collected.AddDays(-120));
        Assert.Equal(CaseClassification.Suspect, Classify(PopulationKind.Child, Result(5m, sample: "capillary"), [earlier]).Classification);
    }

    [Fact]
    public void AdultUnknownSampleTypeCountsAsVenous()
    {
        var d = Classify(PopulationKind.Adult, Result(12m, sample: null));
        Assert.Equal("venous", d.EffectiveSampleType);
        Assert.Equal(CaseClassification.Confirmed, d.Classification);
    }

    [Fact]
    public void BelowTheLevelOpensNothing()
    {
        var d = Classify(PopulationKind.Adult, Result(4.9m));
        Assert.Equal(CaseAction.None, d.Action);
        Assert.Equal(CaseClassification.NotACase, d.Classification);
    }

    [Fact]
    public void ActionLevelCreatesAnUrgentTask()
    {
        var d = Classify(PopulationKind.Adult, Result(55m));
        var task = Assert.Single(d.Tasks, t => t.QueueCode == "occupationalFollowUp");
        Assert.True(task.IsUrgent);
    }

    [Fact]
    public void PreliminaryResultsTriggerActionsButNeverClassify()
    {
        var d = Classify(PopulationKind.Adult, Result(55m, final: false));
        Assert.Equal(CaseAction.None, d.Action);
        Assert.Contains(d.Tasks, t => t.QueueCode == "occupationalFollowUp");
    }

    [Fact]
    public void DetectionLimitAboveTheLevelIsIndeterminate()
    {
        var d = Classify(PopulationKind.Child, Result(5m, op: ResultOperator.LessThan));
        Assert.True(d.IsIndeterminate);
        Assert.Equal(CaseAction.None, d.Action);
        Assert.Contains(d.Tasks, t => t.QueueCode == "detectionLimit");
    }

    [Fact]
    public void UnknownPopulationGoesToReviewNotACase()
    {
        var d = Classify(PopulationKind.Unknown, Result(10m));
        Assert.Equal(CaseAction.None, d.Action);
        var task = Assert.Single(d.Tasks);
        Assert.Equal("populationReview", task.QueueCode);
        Assert.True(task.IsUrgent);
    }

    [Fact]
    public void AnOpenCaseIsUpgradedNeverDowngraded()
    {
        var open = new OpenCaseInfo(Guid.NewGuid(), CaseClassification.Suspect, 4m);
        var d = Classify(PopulationKind.Adult, Result(8m), open: open);
        Assert.Equal(CaseAction.AttachToCase, d.Action);
        Assert.Equal(CaseClassification.Confirmed, d.Classification);
        Assert.True(d.IsHighest);

        var lower = Classify(PopulationKind.Adult, Result(3m), open: open with { ComputedClassification = CaseClassification.Confirmed });
        Assert.Equal(CaseClassification.Confirmed, lower.Classification);
        Assert.False(lower.IsHighest);
    }

    [Fact]
    public void ThresholdComparatorAndUpperBoundAreHonored()
    {
        var tier = Rule("adult", ThresholdType.Action, 10m) with { UpperValue = 20m, Comparator = ">" };
        Crossing At(decimal v) => ThresholdEvaluator.Compare(
            new ResultForEvaluation(ResultOperator.Equal, v, "venous", null, PopulationKind.Adult, 420), tier);
        Assert.Equal(Crossing.Below, At(10m));
        Assert.Equal(Crossing.AtOrAbove, At(10.1m));
        Assert.Equal(Crossing.Below, At(20m));
    }
}
