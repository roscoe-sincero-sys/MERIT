using Merit.Application.Data;
using Merit.Application.Reference;

namespace Merit.Tests;

/// <summary>Issues #37 and #38: stored codes and laboratory file fields are shown as words.</summary>
public sealed class DisplayNamesTests
{
    private static readonly DisplayNames Names = new ReferenceData(new ReferenceDataSet(
        [new AnalyteRow { AnalyteId = Guid.NewGuid(), Code = "PB", DisplayName = "Lead" }], [], [], [], [],
        [
            new LookupRow { LookupType = "testMethod", Code = "icpMs", DisplayName = "ICP-MS" },
            new LookupRow { LookupType = "testMethod", Code = "other", DisplayName = "Other" },
            new LookupRow { LookupType = "race", Code = "other", DisplayName = "Other race" },
        ])).Names;

    [Theory]
    [InlineData("icpMs", "ICP-MS")]                       // a lookup's display name, not "Icp ms"
    [InlineData("PB", "Lead")]                            // an analyte
    [InlineData("uncertainMatch", "Uncertain person match")]
    [InlineData("notACase", "Not a case")]
    [InlineData("wholeBlood", "Whole blood")]
    [InlineData("other", "Other")]                        // lists disagree ("Other race"): the plain split
    [InlineData("someNewCode", "Some new code")]          // no name: split from camel case
    public void CodesReadAsWords(string code, string words) => Assert.Equal(words, Names.Of(code));

    [Fact]
    public void FileFieldsHaveLabelsInReadingOrder()
    {
        Assert.Equal("Laboratory CLIA number", DisplayNames.Field("labCliaNumber"));
        Assert.Equal("ZIP", DisplayNames.Field("zip5"));
        Assert.Equal("Some extra field", DisplayNames.Field("someExtraField"));
        var keys = new[] { "labCliaNumber", "zip5", "dob", "lastName", "someExtraField" }.OrderBy(DisplayNames.FieldOrder).ToList();
        Assert.Equal(["lastName", "dob", "zip5", "labCliaNumber", "someExtraField"], keys);
    }
}
