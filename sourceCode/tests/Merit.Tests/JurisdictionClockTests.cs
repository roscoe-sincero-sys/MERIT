using Merit.Application.Security;

namespace Merit.Tests;

/// <summary>Issue #36: "today" and displayed times follow the jurisdiction's time zone, not the UTC clock.</summary>
public sealed class JurisdictionClockTests
{
    // 00:30 UTC on Oct 27 is 8:30 p.m. on Oct 26 in Maryland and 5:30 p.m. in California.
    private static readonly DateTimeOffset Evening = new(2026, 10, 27, 0, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Eastern Standard Time", 26)]
    [InlineData("Pacific Standard Time", 26)]
    [InlineData("West Pacific Standard Time", 27)]   // Guam is already on Oct 27
    [InlineData(null, 26)]                             // no zone set: Eastern
    [InlineData("Not A Zone", 26)]                     // unknown: Eastern
    public void TodayIsTheJurisdictionsDate(string? zone, int day)
        => Assert.Equal(new DateOnly(2026, 10, day), Clock(zone).Today);

    [Fact]
    public void TimesAreShownOnTheJurisdictionsClock()
    {
        var local = Clock("Eastern Standard Time").ToLocal(Evening.UtcDateTime);
        Assert.Equal(new DateTime(2026, 10, 26, 20, 30, 0), local);
    }

    [Fact]
    public void EveryChoiceIsAZoneThisMachineKnowsAndHasAnIanaName()
    {
        foreach (var (id, _) in JurisdictionTimeZones.Choices)
        {
            Assert.True(TimeZoneInfo.TryFindSystemTimeZoneById(id, out _), id);
            Assert.Contains('/', JurisdictionTimeZones.IanaId(id));
        }
    }

    private static JurisdictionClock Clock(string? zone) => new(new Session(zone), new FixedTime(Evening));

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Session(string? zone) : ICurrentSession
    {
        public bool IsAuthenticated => true;

        public Guid? UserId => null;

        public string? UserName => null;

        public string? DisplayName => null;

        public Guid? ScopeKey => null;

        public Guid? RootTenantId => null;

        public Guid? ActingTenantId => null;

        public string? ActingTenantName => null;

        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();

        public string TimeZoneId => zone!;
    }
}
