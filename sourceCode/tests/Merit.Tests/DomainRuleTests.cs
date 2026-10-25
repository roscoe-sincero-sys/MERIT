using Merit.Domain.Identifiers;
using Merit.Domain.Ids;
using Merit.Domain.Population;
using Merit.Domain.Results;
using Merit.Domain.Text;

namespace Merit.Tests;

/// <summary>Population, normalization, identifiers and keys: the small rules every intake row runs through.</summary>
public sealed class PopulationRulesTests
{
    private static readonly DateOnly Collected = new(2026, 3, 15);

    [Fact]
    public void NoDateOfBirthIsUnknown()
    {
        var r = PopulationRules.Derive(null, Collected, [], 18, 42);
        Assert.Equal(PopulationKind.Unknown, r.Population);
        Assert.Null(r.AgeMonths);
    }

    [Theory]
    [InlineData("2008-03-16", PopulationKind.Child)]   // one day short of 18
    [InlineData("2008-03-15", PopulationKind.Adult)]   // 18 on the collection date
    [InlineData("2024-01-01", PopulationKind.Child)]
    public void AgeCutoffSplitsChildAndAdult(string dob, PopulationKind expected)
        => Assert.Equal(expected, PopulationRules.Derive(DateOnly.Parse(dob), Collected, [], 18, 42).Population);

    [Fact]
    public void OpenPregnancyOnTheCollectionDateWins()
    {
        var episode = new PregnancyEpisode(new DateOnly(2026, 1, 1), new DateOnly(2026, 9, 1), null);
        Assert.Equal(PopulationKind.Pregnant, PopulationRules.Derive(new DateOnly(1995, 5, 5), Collected, [episode], 18, 42).Population);
    }

    [Fact]
    public void EndedPregnancyDoesNotCount()
    {
        var episode = new PregnancyEpisode(new DateOnly(2025, 1, 1), null, new DateOnly(2025, 9, 1));
        Assert.Equal(PopulationKind.Adult, PopulationRules.Derive(new DateOnly(1995, 5, 5), Collected, [episode], 18, 42).Population);
    }

    [Fact]
    public void PregnancyWithNoEndDateClosesAfterTheMaximumWeeks()
    {
        var episode = new PregnancyEpisode(new DateOnly(2025, 1, 1), null, null);
        Assert.False(episode.IsOpenOn(new DateOnly(2025, 1, 1).AddDays(43 * 7), 42));
        Assert.True(episode.IsOpenOn(new DateOnly(2025, 1, 1).AddDays(41 * 7), 42));
    }

    [Theory]
    [InlineData("2020-01-31", "2020-02-29", 0)]
    [InlineData("2020-01-15", "2021-01-15", 12)]
    [InlineData("2020-01-15", "2021-01-14", 11)]
    public void AgeInMonthsCountsWholeMonths(string dob, string asOf, int months)
        => Assert.Equal(months, PopulationRules.AgeInMonths(DateOnly.Parse(dob), DateOnly.Parse(asOf)));
}

public sealed class ResultNormalizerTests
{
    [Theory]
    [InlineData("5.2", ResultOperator.Equal, "5.2")]
    [InlineData("<1", ResultOperator.LessThan, "1")]
    [InlineData("< = 0.5", ResultOperator.LessThan, "0.5")]
    [InlineData(">60", ResultOperator.GreaterThan, "60")]
    public void ParsesOperatorAndValue(string text, ResultOperator op, string value)
    {
        var p = ResultNormalizer.Parse(text);
        Assert.Equal(op, p.Operator);
        Assert.Equal(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), p.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("positive")]
    [InlineData(null)]
    public void NonNumericHasNoValue(string? text) => Assert.Null(ResultNormalizer.Parse(text).Value);

    [Theory]
    [InlineData("µg/dL")]
    [InlineData("μg/dL")]
    [InlineData("mcg/dl")]
    [InlineData(" ug / dL ")]
    public void UnitSpellingsMatch(string unit)
        => Assert.NotNull(ResultNormalizer.FindUnit(unit, [new AcceptedUnit("ug/dL", 1m)]));

    [Fact]
    public void ConvertsToCanonicalUnits()
    {
        var n = ResultNormalizer.Normalize(ResultOperator.Equal, 0.5m, new AcceptedUnit("umol/L", 20.72m), "ug/dL", null, false);
        Assert.Equal(10.36m, n.ValueCanonical);
        Assert.Equal(0.5m, n.ValueReported);
    }

    [Fact]
    public void CreatinineAdjustsWhenRequired()
    {
        var n = ResultNormalizer.Normalize(ResultOperator.Equal, 30m, new AcceptedUnit("ug/L", 1m), "ug/L", 1.5m, true);
        Assert.Equal(20m, n.ValueCreatinineAdjusted);
    }
}

public sealed class MatchKeyTests
{
    [Theory]
    [InlineData("O'Brien-Núñez", "OBRIENNUNEZ")]
    [InlineData("  smith ", "SMITH")]
    [InlineData("123", null)]
    public void NameKeyKeepsLettersOnly(string name, string? key) => Assert.Equal(key, MatchKeys.NameKey(name));

    [Theory]
    [InlineData("Baby Boy")]
    [InlineData("UNKNOWN")]
    [InlineData("")]
    public void PlaceholderNamesAreRecognized(string first) => Assert.True(MatchKeys.IsPlaceholderName(first));

    [Fact]
    public void AddressKeyAbbreviatesAndDropsUnitPrefixes()
    {
        string a = MatchKeys.AddressKey("1214 West Lombard Street", "Apt 2F", "Baltimore", "MD", "21223-1234");
        string b = MatchKeys.AddressKey("1214 W. Lombard St", "2F", "BALTIMORE", "md", "21223");
        Assert.Equal(a, b);
        Assert.Equal("1214 W LOMBARD ST|2F|BALTIMORE|MD|21223", a);
    }
}

public sealed class ProviderIdentifierTests
{
    [Theory]
    [InlineData("1234567893", true)]
    [InlineData("1234567890", false)]
    [InlineData("123456789", false)]
    [InlineData("12345678AB", false)]
    [InlineData(null, false)]
    public void NpiUsesTheLuhnCheckDigit(string? npi, bool valid) => Assert.Equal(valid, ProviderIdentifiers.IsValidNpi(npi));

    [Theory]
    [InlineData("21D0012345", true)]
    [InlineData("21d0012345", true)]
    [InlineData("21X0012345", false)]
    [InlineData("21D001234", false)]
    public void CliaFormat(string clia, bool valid) => Assert.Equal(valid, ProviderIdentifiers.IsValidClia(clia));
}

public sealed class TimeOrderedIdTests
{
    [Fact]
    public void CarriesItsCreationTime()
    {
        var at = new DateTimeOffset(2026, 10, 25, 12, 30, 45, 123, TimeSpan.Zero);
        Assert.Equal(at, TimeOrderedId.TimeOf(TimeOrderedId.New(at)));
    }

    [Fact]
    public void LaterIdsSortAfterEarlierOnesInSqlServerOrder()
    {
        // SQL Server orders uniqueidentifier by bytes 10-15 first, so time in those bytes keeps inserts in order.
        var earlier = new System.Data.SqlTypes.SqlGuid(TimeOrderedId.New(DateTimeOffset.UtcNow.AddSeconds(-1)));
        var later = new System.Data.SqlTypes.SqlGuid(TimeOrderedId.New(DateTimeOffset.UtcNow));
        Assert.True(earlier.CompareTo(later) < 0);
    }
}
