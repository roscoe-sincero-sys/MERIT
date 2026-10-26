using Merit.Domain.Identifiers;
using Merit.Domain.Ids;
using Merit.Domain.Population;
using Merit.Domain.Results;
using Merit.Domain.Tenants;
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

    // Issue #12: the registry and intake share one normalization, so a formatted identifier in a lab file still matches.
    [Theory]
    [InlineData("21D0456789", "21D0456789")]
    [InlineData("21D-0456789", "21D0456789")]
    [InlineData(" 21d 0456789 ", "21D0456789")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void CliaNumbersAreNormalizedOneWay(string? reported, string? stored) => Assert.Equal(stored, ProviderIdentifiers.NormalizeClia(reported));

    [Theory]
    [InlineData("1234567893", "1234567893")]
    [InlineData("1234 567 893", "1234567893")]
    [InlineData("123-456-7893", "1234567893")]
    [InlineData("12345678AB", "12345678AB")]
    [InlineData(" - ", null)]
    public void NpisAreNormalizedOneWay(string? reported, string? stored) => Assert.Equal(stored, ProviderIdentifiers.NormalizeNpi(reported));

    [Fact]
    public void TheRegistryAndIntakeStoreTheSameForm()
    {
        var (row, problems) = Merit.Application.Services.RegistryService.Check(1,
            new("laboratory", "Chesapeake Reference Laboratory", null, null, null, "1234 567 893", "21d-0456789", null));
        Assert.Empty(problems);
        Assert.Equal(ProviderIdentifiers.NormalizeClia("21D 0456789"), row!.Clia);
        Assert.Equal(ProviderIdentifiers.NormalizeNpi("1234-567-893"), row.Npi);
    }
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

/// <summary>Where each kind of tenant may be added, and the form's code and county formats.</summary>
public sealed class TenantRulesTests
{
    [Theory]
    [InlineData("platform", "region", true)]
    [InlineData("platform", "program", false)]
    [InlineData("region", "state", true)]
    [InlineData("region", "jurisdiction", false)]
    [InlineData("state", "jurisdiction", true)]
    [InlineData("state", "state", false)]
    [InlineData("jurisdiction", "program", true)]
    public void ChildTypesFollowTheTree(string parent, string child, bool allowed)
        => Assert.Equal(allowed, TenantRules.ChildTypes(parent).Contains(child));

    [Fact]
    public void OnlyThePlatformAndRegionsHoldDataRoots()
    {
        Assert.True(TenantRules.CreatesDataRoot("platform"));
        Assert.True(TenantRules.CreatesDataRoot("region"));
        Assert.False(TenantRules.CreatesDataRoot("state"));
    }

    [Theory]
    [InlineData(true, "state", true)]          // under the data root: any of its counties, and new ones
    [InlineData(false, "jurisdiction", false)] // under a county: only the county's own
    [InlineData(false, "program", false)]
    [InlineData(false, "region", true)]        // a new data root under a region chooses its own counties
    public void OnlyTheDataRootChoosesAnyCounty(bool parentIsDataRoot, string parentType, bool expected)
        => Assert.Equal(expected, TenantRules.MayChooseAnyCounty(parentIsDataRoot, parentType));

    [Theory]
    [InlineData("WV-JEFFERSON", true)]
    [InlineData("R3", true)]
    [InlineData("wv-jefferson", false)]
    [InlineData("-WV", false)]
    [InlineData("WV JEFF", false)]
    [InlineData("", false)]
    public void TenantCodes(string code, bool valid) => Assert.Equal(valid, TenantRules.IsValidCode(code));

    [Fact]
    public void FipsListsAreSplitAndDeduplicated()
        => Assert.Equal(["005", "510", "033"], TenantRules.ParseFipsList("005, 510 033;005"));

    [Fact]
    public void NewCountyLinesNeedACodeAndAName()
    {
        var counties = Merit.Application.Services.AdminService.ParseNewCounties("037 Jefferson County\n003, Berkeley County\n\n12 Bad\n065", out var bad);
        Assert.Equal(["037", "003"], counties.Select(c => c.Fips));
        Assert.Equal("Berkeley County", counties[1].Name);
        Assert.Equal(["12 Bad", "065"], bad);
    }
}

/// <summary>The reporter registry's format rules: what a laboratory, facility and provider each need.</summary>
public sealed class RegistryRulesTests
{
    private static (Merit.Application.Data.ReporterSave? Row, List<string> Problems) Check(string? kind, string? name = null, string? last = null,
                                                                                          string? npi = null, string? clia = null)
        => Merit.Application.Services.RegistryService.Check(1, new Merit.Application.Services.ReporterInput(kind, name, last, null, null, npi, clia, null));

    [Fact]
    public void ALaboratoryNeedsAValidClia()
    {
        Assert.NotNull(Check("laboratory", "Tidewater Clinical Laboratory", clia: "21d-0456789").Row);
        Assert.Equal("21D0456789", Check("laboratory", "Tidewater", clia: "21D0456789").Row!.Clia);
        Assert.Contains(Check("laboratory", "Allegany", clia: "21D04567").Problems, p => p.Contains("CLIA number 21D04567 is not valid"));
        Assert.Contains(Check("laboratory", "No CLIA").Problems, p => p.Contains("needs its CLIA number"));
    }

    [Fact]
    public void AFacilityNeedsAnNpiOrClia()
    {
        Assert.NotNull(Check("facility", "Patapsco Family Health Center", npi: "1987654328").Row);
        Assert.Contains(Check("facility", "Severn", npi: "1765432100").Problems, p => p.Contains("check digit"));
        Assert.Contains(Check("facility", "Nothing").Problems, p => p.Contains("needs an NPI or a CLIA number"));
    }

    [Fact]
    public void AProviderNeedsAnNpiAndALastName()
    {
        var ok = Check("provider", last: "Okafor", npi: "1543 210 987").Row;
        Assert.Equal("1543210987", ok!.Npi);
        Assert.Null(ok.Name);
        Assert.Contains(Check("provider", last: "Patel").Problems, p => p.Contains("needs an NPI"));
        Assert.Contains(Check("provider", npi: "1543210987").Problems, p => p.Contains("needs a last name"));
        Assert.Contains(Check("clinic", "x").Problems, p => p.Contains("laboratory, facility or provider"));
    }

    [Fact]
    public void TheTemplateExamplesPassTheirOwnChecks()
    {
        var lines = Merit.Application.Services.RegistryService.Template().Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1);
        foreach (var line in lines)
        {
            var c = line.Split(',');
            Assert.NotNull(Merit.Application.Services.RegistryService.Check(1,
                new Merit.Application.Services.ReporterInput(c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7])).Row);
        }
    }
}
