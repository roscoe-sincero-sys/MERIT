using System.Text.Json;
using Merit.Application.Intake;

namespace Merit.Tests;

public sealed class Hl7ReaderTests
{
    private static string Segment(string name, params (int Field, string Value)[] values)
    {
        var fields = new string[values.Max(v => v.Field) + 1];
        fields[0] = name;
        foreach (var (field, value) in values) fields[field] = value;
        return string.Join('|', fields);
    }

    private static string Message(string version = "2.5.1", string value = "8.4", string type = "NM", string status = "F") => string.Join('\r',
        $"MSH|^~\\&|DEMO|21D0712345|MERIT|MD|20261022120000||ORU^R01^ORU_R01|MSG-1|P|{version}",
        Segment("PID", (1, "1"), (3, "PAT-1^^^LAB^MR~MED-2^^^STATE^MA"), (5, "Example^Ada^Marie^^^^L"), (7, "20200102"), (8, "F"),
            (11, "10 Test Street^2B^Towson^MD^21204^USA^H^^24005")),
        Segment("OBR", (1, "1"), (3, "ACC-1^LAB"), (7, "20261020100000-0400"), (15, "BLDV"), (16, "1234567893^Clinician^Test"), (22, "20261022120000")),
        Segment("OBX", (1, "1"), (2, type), (3, "77307-7^Lead^LN"), (4, "sub-1"), (5, value), (6, "ug/dL^Micrograms per deciliter^UCUM"), (11, status), (14, "20261020100000"), (17, "ICPMS"))) + "\r";

    [Theory]
    [InlineData("2.3.1")]
    [InlineData("2.5.1")]
    public void MapsDemographicsAndClinicalFieldsWithProvenance(string version)
    {
        var row = Assert.Single(Hl7Reader.Read(Message(version), "21D0712345"));
        Assert.Equal("Example", row["lastName"]);
        Assert.Equal("Ada", row["firstName"]);
        Assert.Equal("2020-01-02", row["dob"]);
        Assert.Equal("female", row["sex"]);
        Assert.Equal("005", row["countyFips"]);
        Assert.Equal("77307-7", row["loinc"]);
        Assert.Equal("8.4", row["value"]);
        Assert.Equal("ug/dL", row["unit"]);
        Assert.Equal("venous", row["sampleType"]);
        Assert.Equal("2026-10-20", row["collectedDate"]);
        Assert.Equal("2026-10-22", row["reportedDate"]);
        Assert.Equal("ACC-1", row["accessionNumber"]);
        Assert.Equal("1234567893", row["orderingProviderNpi"]);
        Assert.Equal("21D0712345", row["labCliaNumber"]);
        Assert.Equal("icpMs", row["testMethod"]);
        Assert.Equal("sub-1", row["hl7ObservationSubId"]);
        Assert.Equal(version, row["hl7Version"]);
        var identifiers = JsonSerializer.Deserialize<Hl7PatientIdentifier[]>(row["hl7PatientIdentifiersJson"]!)!;
        Assert.Equal(2, identifiers.Length);
        Assert.Equal(new Hl7PatientIdentifier("mrn", "PAT-1", "LAB"), identifiers[0]);
        Assert.Equal("medicaid", identifiers[1].Type);
    }

    [Fact]
    public void HandlesAlternateSeparatorsRepetitionsAndEscapesBeforeDecoding()
    {
        var message = Message(value: "3.1~4.2").Replace("Example", "O\\S\\Name\\E\\").Replace("10 Test Street", "10 A\\T\\B Street");
        var rows = Hl7Reader.Read(message);
        Assert.Equal(2, rows.Count);
        Assert.Equal("O^Name\\", rows[0]["lastName"]);
        Assert.Equal("10 A&B Street", rows[0]["addressLine1"]);
        Assert.Equal("3.1", rows[0]["value"]);
        Assert.Equal("4.2", rows[1]["value"]);
        Assert.Equal("2", rows[1]["hl7ValueRepetition"]);
        var alternate = Message().Replace('|', '*').Replace('^', '%').Replace('~', '$').Replace('\\', '!').Replace('&', '@');
        Assert.Equal("Example", Assert.Single(Hl7Reader.Read(alternate))["lastName"]);
        Assert.Equal("Ada", Assert.Single(Hl7Reader.Read(alternate))["firstName"]);
    }

    [Fact]
    public void PreservesEveryObservationAndPatientAcrossOrdersAndMessages()
    {
        var more = Segment("OBX", (1, "2"), (2, "NM"), (3, "10368-9^Lead^LN"), (5, "12"), (6, "ug/dL"), (11, "P"));
        var nextOrder = Segment("OBR", (1, "2"), (3, "ACC-2"), (7, "20261021"));
        var content = Message() + more + '\r' + nextOrder + '\r' + more + '\r' + Message("2.3.1").Replace("Example", "Other");
        var rows = Hl7Reader.Read(content);
        Assert.Equal(4, rows.Count);
        Assert.Equal("ACC-2", rows[2]["accessionNumber"]);
        Assert.Equal("preliminary", rows[1]["resultStatus"]);
        Assert.Equal("Other", rows[3]["lastName"]);
        Assert.Equal("2", rows[3]["hl7MessageNumber"]);
    }

    [Fact]
    public void ReadsTrailingSpecimenAndPrefersHomeAddressLegalNameAndAlternateLoinc()
    {
        var message = Message().Replace("Example^Ada^Marie^^^^L", "Alias^Other^^^^^A~Example^Ada^Marie^^^^L")
            .Replace("10 Test Street^2B^Towson^MD^21204^USA^H^^24005", "Work^9^Other^MD^21201^USA^B^^24510~10 Test Street^2B^Towson^MD^21204^USA^H^^24005")
            .Replace("77307-7^Lead^LN", "LOCAL^Lead^99LAB^77307-7^Lead^LN")
            + Segment("SPM", (1, "1"), (2, "SPEC-1^LAB"), (4, "BLDC^Capillary blood^HL70487"), (17, "20261019")) + '\r';
        var row = Assert.Single(Hl7Reader.Read(message));
        Assert.Equal("Example", row["lastName"]);
        Assert.Equal("Towson", row["city"]);
        Assert.Equal("77307-7", row["loinc"]);
        Assert.Equal("capillary", row["sampleType"]);
        Assert.Equal("2026-10-19", row["collectedDate"]);
        Assert.Contains("Alias", row["hl7PatientNames"]);
    }

    [Theory]
    [InlineData("<^2.5", "<2.5")]
    [InlineData("=^2.5", "2.5")]
    public void ReadsScalarStructuredNumericValues(string input, string expected)
        => Assert.Equal(expected, Assert.Single(Hl7Reader.Read(Message(value: input, type: "SN")))["value"]);

    [Theory]
    [InlineData("C", "corrected")]
    [InlineData("X", "cancelled")]
    public void MapsCorrectionAndCancellationStatuses(string input, string expected)
        => Assert.Equal(expected, Assert.Single(Hl7Reader.Read(Message(status: input)))["resultStatus"]);

    [Fact]
    public void SupportsBatchAndMllpEnvelopesAndUtf8HexEscapes()
    {
        var message = Message().Replace("Example", "Ex\\XC3A9\\mple");
        var rows = Hl7Reader.Read("FHS|^~\\&|FILE\rBHS|^~\\&|BATCH\r\v" + message + "\u001c\rBTS|1\rFTS|1\r");
        Assert.Equal("Exémple", Assert.Single(rows)["lastName"]);
    }

    [Fact]
    public void FailsClosedWithoutSourceValuesInDiagnostics()
    {
        foreach (var message in new[] { Message("2.4"), Message().Replace("ORU^R01", "ADT^A01"),
            Message(type: "ED"), Message(value: "1^2^:^3", type: "SN"), Message(status: "U"),
            Message().Replace("PID|", "OBX|"), Message().Replace("Example", "SECRET\\Zunsupported\\"),
            Message() + "DSC|CONTINUATION\r", "NOT HL7", Message().Replace("^~\\&", "^^\\&") })
        {
            var error = Assert.Throws<Hl7ParseException>(() => Hl7Reader.Read(message));
            Assert.DoesNotContain("SECRET", error.Message);
            Assert.DoesNotContain("Ada", error.Message);
        }
        Assert.Throws<Hl7ParseException>(() => Hl7Reader.Read(Message(), "21D0900001"));
    }

    [Fact]
    public void MissingClinicalValuesRemainForHeldRecordValidation()
    {
        var row = Assert.Single(Hl7Reader.Read(Message(value: "ND").Replace("20200102", "2020")));
        Assert.Equal("ND", row["value"]);
        Assert.Equal("2020", row["dob"]);
        var local = Assert.Single(Hl7Reader.Read(Message().Replace("77307-7^Lead^LN", "77307-7^Local^99LOCAL")));
        Assert.Equal("99LOCAL:77307-7", local["loinc"]);
    }

    [Theory]
    [InlineData("value")]
    [InlineData("unit")]
    [InlineData("testMethod")]
    [InlineData("accessionNumber")]
    public void OversizedMappedValuesPauseInsteadOfFailingDuringDatabaseWrites(string field)
    {
        var message = field switch
        {
            "value" => Message(value: new string('1', 61)),
            "unit" => Message().Replace("ug/dL", new string('U', 21)),
            "testMethod" => Message().Replace("ICPMS", new string('M', 31)),
            _ => Message().Replace("ACC-1", new string('A', 61)),
        };
        Assert.Contains("Mapped " + field, Assert.Throws<Hl7ParseException>(() => Hl7Reader.Read(message)).Message);
    }
}
