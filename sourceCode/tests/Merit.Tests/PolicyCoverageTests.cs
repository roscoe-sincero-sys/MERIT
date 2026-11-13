using Merit.Application.Data;
using Merit.Application.Services;
using Merit.Domain.Classification;
using Merit.Domain.Population;
using Merit.Domain.Results;
using Merit.Domain.Thresholds;

namespace Merit.Tests;

/// <summary>Issues #292 and #293: populations with notification rules need case levels; historical notices include the owner.</summary>
public sealed class PolicyCoverageTests
{
    private static NotificationRuleRow Rule(string key, bool active = true)
    {
        var d = MarylandPolicyDrafts.Get(key);
        return new NotificationRuleRow
        {
            RuleName = d["ruleName"], RulePurpose = d["rulePurpose"], EligibilityMode = d["eligibilityMode"], Population = d["population"],
            AnalyteCode = "PB", Specimen = "wholeBlood", RecipientKind = d["recipientKind"], IsActive = active,
            EffectiveFrom = DateOnly.Parse(d["effectiveFrom"], System.Globalization.CultureInfo.InvariantCulture),
            EffectiveTo = d.TryGetValue("effectiveTo", out var to) ? DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture) : null,
        };
    }

    private static ThresholdRow Level(string key, bool active = true, string type = "case")
    {
        var d = MarylandPolicyDrafts.GetThreshold(key)!;
        return new ThresholdRow
        {
            AnalyteCode = "PB", Specimen = "wholeBlood", Population = d.Population, AgeMaxMonths = d.AgeMaxMonths, ThresholdType = type,
            LowerValue = d.LowerValue, EffectiveFrom = d.EffectiveFrom, EffectiveTo = d.EffectiveTo, IsActive = active,
        };
    }

    [Fact]
    public void EveryShippedNoticeDraftIsCoveredByTheShippedReferenceLevelDrafts()
    {
        var rules = MarylandPolicyDrafts.Names.Keys.Select(k => Rule(k)).ToList();
        var levels = MarylandPolicyDrafts.ThresholdNames.Keys.Select(k => Level(k)).ToList();
        Assert.Empty(PolicyCoverage.Gaps(rules, levels));
    }

    [Fact]
    public void PregnancyRulesWithOnlyChildLevelsAreReportedAndChildRulesAreNot()
    {
        var rules = MarylandPolicyDrafts.Names.Keys.Select(k => Rule(k)).ToList();
        var gaps = PolicyCoverage.Gaps(rules, [Level("mdRefChild2020"), Level("mdRefChild2024")]);
        Assert.Equal(4, gaps.Count);
        Assert.All(gaps, g => Assert.Contains("pregnant population", g, StringComparison.Ordinal));
        Assert.DoesNotContain(gaps, g => g.Contains("Lab:", StringComparison.Ordinal)); // laboratory reporting rules open no case
    }

    [Fact]
    public void InactiveLevelsOtherTypesOtherSpecimensAndNonOverlappingPeriodsDoNotCover()
    {
        var pregnant = Rule("mdPregnantAdult"); // from 2024-01-01
        Assert.Single(PolicyCoverage.Gaps([pregnant], [Level("mdRefPregnant2024", active: false)]));
        Assert.Single(PolicyCoverage.Gaps([pregnant], [Level("mdRefPregnant2024", type: "action")]));
        Assert.Single(PolicyCoverage.Gaps([pregnant], [Level("mdRefPregnant2020")])); // ended 2023-12-31
        Assert.Single(PolicyCoverage.Gaps([pregnant], [new ThresholdRow { AnalyteCode = "PB", Specimen = "urine", Population = "pregnant", ThresholdType = "case", EffectiveFrom = new DateOnly(2024, 1, 1), IsActive = true }]));
        Assert.Empty(PolicyCoverage.Gaps([pregnant], [new ThresholdRow { AnalyteCode = "PB", Specimen = "wholeBlood", Population = "all", ThresholdType = "case", EffectiveFrom = new DateOnly(2020, 1, 1), IsActive = true }]));
        Assert.Empty(PolicyCoverage.Gaps([Rule("mdPregnantAdult", active: false)], []));
        Assert.Single(PolicyCoverage.Gaps([Rule("mdChild")], []));
    }

    [Theory]
    [InlineData("mdChild2022", "mdOwnerChild2022", "5", "2022-07-01", "2022-10-27")]
    [InlineData("mdChild2023", "mdOwnerChild2023", "3.5", "2022-10-28", "2023-12-31")]
    public void HistoricalPeriodsHaveACaregiverAndAResidenceOwnerDraftWithTheSameThresholdAndDates(string caregiverKey, string ownerKey, string minimum, string from, string to)
    {
        var caregiver = MarylandPolicyDrafts.Get(caregiverKey); var owner = MarylandPolicyDrafts.Get(ownerKey);
        Assert.Equal("caregiver", caregiver["recipientKind"]); Assert.Equal("mdEbl", caregiver["eligibilityMode"]);
        Assert.Equal("owner", owner["recipientKind"]); Assert.Equal("mdOwner", owner["eligibilityMode"]);
        foreach (var d in new[] { caregiver, owner })
        {
            Assert.Equal(minimum, d["minimumValue"]); Assert.Equal(from, d["effectiveFrom"]); Assert.Equal(to, d["effectiveTo"]);
            Assert.Equal("71", d["maximumAgeMonths"]); Assert.Equal("10", d["dueInDays"]); Assert.Equal("business", d["dayBasis"]);
        }
    }

    [Fact]
    public void CurrentDraftsAreUnchangedAndNoDraftStartsBeforeJuly2022()
    {
        Assert.Equal(13, MarylandPolicyDrafts.Names.Count);
        foreach (var key in new[] { "mdChild", "mdOwnerChild", "mdAffectedChild", "mdPregnantAdult", "mdPregnantMinor", "mdOwnerPregnant", "mdAffectedPregnant", "mdLabElevated" })
        {
            var d = MarylandPolicyDrafts.Get(key);
            Assert.Equal("2024-01-01", d["effectiveFrom"]); Assert.Equal("3.5", d["minimumValue"]); Assert.False(d.ContainsKey("effectiveTo"));
        }
        Assert.All(MarylandPolicyDrafts.Names.Keys, k => Assert.True(DateOnly.Parse(MarylandPolicyDrafts.Get(k)["effectiveFrom"], System.Globalization.CultureInfo.InvariantCulture) >= MarylandPolicyDrafts.EarliestNoticeDraft));
        Assert.Null(MarylandPolicyDrafts.GetThreshold("mdChild"));
        Assert.Equal(new ThresholdDraft("pregnant", null, 3.5m, new DateOnly(2024, 1, 1), null, MarylandPolicyDrafts.ThresholdAuthority), MarylandPolicyDrafts.GetThreshold("mdRefPregnant2024"));
        Assert.Equal(new ThresholdDraft("child", 71, 5m, new DateOnly(2020, 7, 1), new DateOnly(2023, 12, 31), MarylandPolicyDrafts.ThresholdAuthority), MarylandPolicyDrafts.GetThreshold("mdRefChild2020"));
    }

    private static ThresholdRule Case(string population, decimal lower, int? ageMax = null)
        => new(Guid.NewGuid(), population, null, ageMax, null, null, ThresholdType.Case, null, null, null, null, lower, null, ">=", null);

    private static ClassificationDecision Classify(PopulationKind population, int ageMonths, decimal value, IReadOnlyList<ThresholdRule> rules,
                                                   ResultOperator op = ResultOperator.Equal, OpenCaseInfo? open = null)
        => CaseClassifier.Classify(new ClassificationInput("PB", "wholeBlood", population, ageMonths,
            new ClassifiableResult(Guid.NewGuid(), new DateOnly(2026, 6, 1), "venous", "icpMs", null, op, value, true), [], rules, open,
            ClassificationSettings.Default, "ug/dL"));

    [Fact]
    public void AnElevatedResultInAPopulationWithNoCaseLevelGetsOneReviewTaskAndNoCase()
    {
        ThresholdRule[] childOnly = [Case("child", 3.5m, 71)];
        foreach (var (population, age) in new[] { (PopulationKind.Pregnant, 335), (PopulationKind.Child, 85), (PopulationKind.Adult, 360) })
        {
            var d = Classify(population, age, 5m, childOnly);
            Assert.Equal(CaseAction.None, d.Action);
            var task = Assert.Single(d.Tasks);
            Assert.Equal("populationReview", task.QueueCode);
            Assert.Equal("Elevated result with no case level for this population", task.Title);
            Assert.Contains("5 ug/dL", task.Detail, StringComparison.Ordinal);
            Assert.False(task.IsUrgent);
        }
    }

    [Fact]
    public void NoReviewTaskBelowEveryCaseLevelForCensoredResultsOrWhenThePopulationHasItsOwnLevel()
    {
        ThresholdRule[] childOnly = [Case("child", 3.5m, 71)];
        Assert.Empty(Classify(PopulationKind.Pregnant, 335, 3.4m, childOnly).Tasks);
        Assert.Empty(Classify(PopulationKind.Pregnant, 335, 5m, childOnly, ResultOperator.LessThan).Tasks);
        Assert.Empty(Classify(PopulationKind.Adult, 360, 5m, []).Tasks); // nothing configured at all
        // The population has a level and the value is below it: an ordinary "below the case level", no review.
        Assert.Empty(Classify(PopulationKind.Adult, 360, 4m, [Case("child", 3.5m, 71), Case("adult", 5m)]).Tasks);
        // With its own level the pregnant result opens a case, without the review task.
        var opened = Classify(PopulationKind.Pregnant, 335, 5m, [Case("child", 3.5m, 71), Case("pregnant", 3.5m)]);
        Assert.Equal(CaseAction.OpenCase, opened.Action);
        Assert.DoesNotContain(opened.Tasks, t => t.QueueCode == "populationReview");
    }
}
