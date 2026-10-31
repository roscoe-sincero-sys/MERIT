using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Services;

namespace Merit.Tests;

public sealed class RegistryWorkflowTests
{
    [Theory]
    [InlineData("2023-05-14 Mia Rivera")]
    [InlineData("5/14/2023 Rivera, Mia")]
    [InlineData("Rivera Mia 20230514")]
    public void DateFirstNamesAreNotAddresses(string text)
    {
        var query = SearchService.Parse(text);
        Assert.Equal(new DateOnly(2023, 5, 14), query.Dob);
        Assert.Null(query.AddressKey);
        Assert.NotNull(query.NameKey1);
        Assert.NotNull(query.NameKey2);
    }

    [Theory]
    [InlineData("Juan De La Cruz", "JUAN", "DELACRUZ")]
    [InlineData("De La Cruz, Juan", "DELACRUZ", "JUAN")]
    [InlineData("Mary Ann Smith", "MARYANN", "SMITH")]
    public void SearchRetainsMultiPartNames(string text, string first, string second)
        => Assert.Contains((first, second), SearchService.Parse(text).NamePairs);

    [Fact]
    public void StreetSearchStillUsesTheAddress()
    {
        var query = SearchService.Parse("1214 West Lombard Street");
        Assert.NotNull(query.AddressKey);
        Assert.Null(query.NameKey1);
    }

    [Fact]
    public void SampleCsvSupportsQuotedComponentsAndStableRetryIds()
    {
        var id = Guid.NewGuid(); var import = Guid.NewGuid();
        const string csv = "sampleType,sampleValue,unit,sampleDate,roomOrComponent,notes\r\nxrfPaint,1.2,mg/cm2,2026-01-01,\"Kitchen, door\",\"second \"\"reading\"\"\"\r\n";
        var samples = EnvironmentalSamples.ParseCsv(id, import, csv, new DateOnly(2026, 2, 1), EnvironmentalSamples.DefaultUnits);
        var row = Assert.Single(samples);
        Assert.Equal("Kitchen, door", row.RoomOrComponent);
        Assert.Equal("second \"reading\"", row.Notes);
        Assert.Equal(row.EnvironmentalSampleId, Assert.Single(EnvironmentalSamples.ParseCsv(id, import, csv, new DateOnly(2026, 2, 1), EnvironmentalSamples.DefaultUnits)).EnvironmentalSampleId);
        Assert.NotEqual(row.EnvironmentalSampleId, Assert.Single(EnvironmentalSamples.ParseCsv(id, Guid.NewGuid(), csv, new DateOnly(2026, 2, 1), EnvironmentalSamples.DefaultUnits)).EnvironmentalSampleId);
    }

    [Theory]
    [InlineData("xrfPaint,1,ppm,2026-01-01")]
    [InlineData("water,-1,ug/L,2026-01-01")]
    [InlineData("water,1,ug/L,2030-01-01")]
    [InlineData("water,abc,ug/L,2026-01-01")]
    [InlineData("water,1,ug/L,01/01/2026")]
    public void AFileWithAnyInvalidReadingIsRejected(string invalid)
    {
        string csv = "sampleType,sampleValue,unit,sampleDate\nwater,1,ug/L,2026-01-01\n" + invalid;
        var error = Assert.Throws<ValidationException>(() => EnvironmentalSamples.ParseCsv(Guid.NewGuid(), Guid.NewGuid(), csv,
            new DateOnly(2026, 2, 1), EnvironmentalSamples.DefaultUnits));
        Assert.Contains("Row 3", error.Message + string.Join(" ", error.Errors.Values.SelectMany(v => v)));
    }

    [Fact]
    public void SurfaceIsOnlyApplicableToWipes()
        => Assert.Throws<ValidationException>(() => EnvironmentalSamples.Validate(new EnvironmentalSampleSave(Guid.Empty, Guid.NewGuid(),
            "xrfPaint", "Window", "floor", 1, "mg/cm2", new DateOnly(2026, 1, 1), null, false),
            new DateOnly(2026, 2, 1), EnvironmentalSamples.DefaultUnits));
}
