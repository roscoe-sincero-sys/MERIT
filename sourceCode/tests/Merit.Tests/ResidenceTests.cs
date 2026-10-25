using Merit.Domain.Residence;

namespace Merit.Tests;

/// <summary>Primary residence planning and investigation scope: the place rules behind the set-primary preview.</summary>
public sealed class ResidenceTests
{
    private static PlaceRow Place(string role, bool primary, string start, string? end = null, decimal? hours = null,
                                  string? county = "24510")
        => new(Guid.NewGuid(), Guid.NewGuid(), null, role, primary, null, null, hours, null, DateOnly.Parse(start),
            end is null ? null : DateOnly.Parse(end), "test", null, null, county, county, role + " " + start);

    [Fact]
    public void SetPrimaryEndsTheOldPrimaryAndKeepsTheAddressAsNonPrimary()
    {
        var oldHome = Place("residence", true, "2020-01-01");
        var newHome = Place("residence", false, "2026-01-01", county: "24005");
        var plan = PrimaryResidencePlanner.PlanSetPrimary([oldHome, newHome], newHome.PersonLocationId, new DateOnly(2026, 1, 1),
            "Lives here most nights", Guid.NewGuid);

        Assert.True(plan.IsValid, string.Join("; ", plan.Errors));
        var ended = Assert.Single(plan.Changes, c => c.Row.PersonLocationId == oldHome.PersonLocationId);
        Assert.Equal(new DateOnly(2025, 12, 31), ended.Row.EndDate);
        Assert.Contains(plan.Changes, c => c.Kind == ChangeKind.Insert && c.Row.LocationId == oldHome.LocationId && !c.Row.IsPrimary);
        Assert.Contains(plan.Changes, c => c.Row.PersonLocationId == newHome.PersonLocationId && c.Row.IsPrimary);
        Assert.True(plan.CountyChanges);
    }

    [Fact]
    public void SetPrimarySplitsATargetThatBeganEarlier()
    {
        var oldHome = Place("residence", true, "2020-01-01") with { PrimaryReason = "Lives here most nights" };
        var second = Place("residence", false, "2024-01-01");
        var plan = PrimaryResidencePlanner.PlanSetPrimary([oldHome, second], second.PersonLocationId, new DateOnly(2026, 1, 1),
            "Moved in full time", Guid.NewGuid);

        Assert.True(plan.IsValid, string.Join("; ", plan.Errors));
        Assert.Contains(plan.Changes, c => c.Row.PersonLocationId == second.PersonLocationId && c.Row.EndDate == new DateOnly(2025, 12, 31));
        Assert.Contains(plan.Changes, c => c.Kind == ChangeKind.Insert && c.Row.LocationId == second.LocationId && c.Row.IsPrimary
                                           && c.Row.StartDate == new DateOnly(2026, 1, 1));
    }

    [Fact]
    public void OverlappingResidencesNeedAReasonForThePrimary()
    {
        var oldHome = Place("residence", true, "2020-01-01");
        var second = Place("residence", false, "2024-01-01");
        var plan = PrimaryResidencePlanner.PlanSetPrimary([oldHome, second], second.PersonLocationId, new DateOnly(2026, 1, 1),
            null, Guid.NewGuid);
        Assert.Contains(plan.Errors, e => e.Contains("needs a reason", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyAResidenceCanBePrimary()
    {
        var job = Place("workplace", false, "2024-01-01");
        Assert.False(PrimaryResidencePlanner.PlanSetPrimary([job], job.PersonLocationId, new DateOnly(2026, 1, 1), null, Guid.NewGuid).IsValid);
    }

    [Fact]
    public void StartBeforeTheResidenceBeganIsAnError()
    {
        var home = Place("residence", false, "2025-06-01");
        Assert.False(PrimaryResidencePlanner.PlanSetPrimary([home], home.PersonLocationId, new DateOnly(2025, 1, 1), null, Guid.NewGuid).IsValid);
    }

    [Fact]
    public void FirstResidenceAddedBecomesPrimary()
    {
        var plan = PrimaryResidencePlanner.PlanAddPlace([], Place("residence", false, "2025-01-01"), null, null, Guid.NewGuid);
        Assert.True(Assert.Single(plan.Changes).Row.IsPrimary);
    }

    [Fact]
    public void EndBeforeStartIsRejected()
        => Assert.False(PrimaryResidencePlanner.PlanAddPlace([], Place("residence", false, "2025-01-01", "2024-01-01"), null, null,
            Guid.NewGuid).IsValid);

    [Fact]
    public void CountyOnReadsThePrimaryInForce()
    {
        var rows = new[] { Place("residence", true, "2020-01-01", "2024-12-31"), Place("residence", true, "2025-01-01", county: "24005") };
        Assert.Equal("24510", PrimaryResidencePlanner.CountyOn(rows, new DateOnly(2023, 1, 1)));
        Assert.Equal("24005", PrimaryResidencePlanner.CountyOn(rows, new DateOnly(2026, 1, 1)));
    }

    [Fact]
    public void InvestigationScopeOrdersPrimaryFirstAndReviewsShortSchedules()
    {
        var places = new[]
        {
            Place("residence", false, "2024-01-01"),
            Place("residence", true, "2020-01-01"),
            Place("childCare", false, "2025-01-01"),
            Place("other", false, "2025-01-01", hours: 2m),
            Place("residence", true, "2010-01-01", "2012-01-01"),
        };
        var scope = InvestigationScope.Propose(places, new DateOnly(2026, 3, 1), 180, 10m);

        Assert.Equal(4, scope.Count);
        Assert.Equal("Primary residence.", scope.OrderBy(s => s.Order).First().Reason);
        Assert.Equal(ScopeOutcome.ReviewTask, Assert.Single(scope, s => s.Place.Role == "other").Outcome);
    }
}
