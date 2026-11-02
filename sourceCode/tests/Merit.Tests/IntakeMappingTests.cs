using Merit.Application.Intake;
using Merit.Web.Infrastructure;

namespace Merit.Tests;

/// <summary>Issue #157: a column map that sends two columns to one field put the result unit into addresses.</summary>
public sealed class IntakeMappingTests
{
    private static readonly string[] Header =
        ["PatientLast", "PatientFirst", "Street", "Apt", "City", "State", "Zip", "LOINC", "Result", "Units"];

    private static readonly string[] Row =
        ["Rivera", "Mia", "1214 W Lombard St", "2F", "Baltimore", "MD", "21223", "77307-7", "5.2", "ug/dL"];

    private static Dictionary<string, string> Map(string aptField) => new(StringComparer.Ordinal)
    {
        ["PatientLast"] = "lastName", ["PatientFirst"] = "firstName", ["Street"] = "addressLine1", ["Apt"] = aptField,
        ["City"] = "city", ["State"] = "state", ["Zip"] = "zip5", ["LOINC"] = "loinc", ["Result"] = "value", ["Units"] = "unit",
    };

    private static Dictionary<string, string?> Stage(Dictionary<string, string> map)
        => IntakeProcessor.StageFields(Header, Row, map, new Dictionary<string, Dictionary<string, string>>(), new Dictionary<string, string>());

    [Fact]
    public void AMapThatSendsTwoColumnsToOneFieldIsCaught()
    {
        // The demo profile's first version: Apt and Units both to the result unit.
        var shared = Assert.Single(IntakeProcessor.SharedTargets(Header, Map("unit")));
        Assert.Equal("unit", shared.Field);
        Assert.Equal(["Apt", "Units"], shared.Columns);

        // Staged regardless, the apartment is lost under the result unit: the defect the check prevents.
        var fields = Stage(Map("unit"));
        Assert.Equal("ug/dL", fields["unit"]);
        Assert.False(fields.ContainsKey("addressUnit"));
    }

    [Fact]
    public void TheCorrectedMapKeepsTheApartmentAndTheResultUnitApart()
    {
        Assert.Empty(IntakeProcessor.SharedTargets(Header, Map("addressUnit")));

        var fields = Stage(Map("addressUnit"));
        Assert.Equal("2F", fields["addressUnit"]);
        Assert.Equal("ug/dL", fields["unit"]);
        Assert.Equal("1214 W Lombard St", fields["addressLine1"]);
    }

    [Fact]
    public void BlankValuesAreNullTranslationsApplyAndConstantsFillOnlyWhatIsMissing()
    {
        string[] header = ["Sex", "Apt", "Lab"];
        string[] row = ["F", "  ", "21D0000000"];
        var map = new Dictionary<string, string> { ["Sex"] = "sex", ["Apt"] = "addressUnit", ["Lab"] = "labCliaNumber" };
        var translations = new Dictionary<string, Dictionary<string, string>> { ["sex"] = new() { ["F"] = "female" } };
        var constants = new Dictionary<string, string> { ["labCliaNumber"] = "21D0712345", ["state"] = "MD" };

        var fields = IntakeProcessor.StageFields(header, row, map, translations, constants);

        Assert.Equal("female", fields["sex"]);
        Assert.Null(fields["addressUnit"]);
        Assert.Equal("21D0000000", fields["labCliaNumber"]);
        Assert.Equal("MD", fields["state"]);
    }

    // Issue #148: the format comes first and the sender's name is not repeated.
    [Theory]
    [InlineData("Chesapeake Reference Laboratory", "Chesapeake Reference Laboratory CSV", "csv", "CSV file from Chesapeake Reference Laboratory")]
    [InlineData("Chesapeake Reference Laboratory", "Chesapeake Reference Laboratory HL7", "hl7", "HL7 message from Chesapeake Reference Laboratory")]
    [InlineData("County Lab", "Weekly export", "tsv", "Tab-separated file from County Lab (Weekly export)")]
    [InlineData("County Lab", "County Lab pipe", "pipe", "Pipe-separated file from County Lab")]
    public void IntakeProfilesReadAsFormatThenSender(string sender, string profile, string fileType, string label)
        => Assert.Equal(label, Options.IntakeProfile(sender, profile, fileType));

    // Issue #165: one choice per unit, however many spellings files use.
    [Fact]
    public void UnitSpellingsAreOneChoice()
    {
        (string, decimal)[] units = [("ug/dL", 1m), ("mcg/dL", 1m), ("µg/dL", 1m), ("umol/L", 20.72m)];
        var choices = Options.UnitChoices(units, "ug/dL", null);
        Assert.Equal(["ug/dL", "umol/L"], choices.Select(c => c.Value));
        Assert.True(choices[0].Selected);
        Assert.True(Options.UnitChoices(units, "ug/dL", "mcg/dL")[0].Selected);
        Assert.True(Options.UnitChoices(units, "ug/dL", "umol/L")[1].Selected);
    }
}
