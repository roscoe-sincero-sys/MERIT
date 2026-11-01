using System.IO.Compression;
using System.Text;
using Merit.Domain.Matching;
using Merit.Web.Infrastructure;

namespace Merit.Tests;

/// <summary>The field-by-field comparison behind the person matching review screen.</summary>
public sealed class MatchComparerTests
{
    private static readonly MatchAddress Home = new("1214 W Lombard Street", "2F", "Baltimore", "MD", "21223", true);

    private static CandidateIdentity Candidate(string last, string? first, string dob, string sex = "female", MatchAddress? address = null,
                                               bool placeholder = false, params (string, string?)[] aliases)
        => new(Guid.NewGuid(), last, first, null, DateOnly.Parse(dob), sex, placeholder, aliases, address is null ? [] : [address]);

    private static IncomingIdentity Incoming(string? last, string? first, string? dob, string? sex = "female", MatchAddress? address = null)
        => new(last, first, null, dob is null ? null : DateOnly.Parse(dob), sex, address);

    private static FieldAgreement Field(CandidateComparison c, string field) => c.Fields.Single(f => f.Field == field).Agreement;

    [Fact]
    public void NicknameIsClose()
    {
        var c = MatchComparer.Compare(Incoming("Carter", "Jim", "1980-08-19", "male"), Candidate("Carter", "James", "1980-08-19", "male"));
        Assert.Equal(FieldAgreement.Close, Field(c, "firstName"));
        Assert.Contains("nickname", c.Explanation, StringComparison.Ordinal);
        Assert.StartsWith("Same last name", c.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void SwappedDayAndMonthIsClose()
    {
        var c = MatchComparer.Compare(Incoming("Rivera", "Luis", "1992-08-07", "male"), Candidate("Rivera", "Luis", "1992-07-08", "male"));
        Assert.Equal(FieldAgreement.Close, Field(c, "dob"));
        Assert.Contains("day and month swapped", c.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void OneDigitTypoInDobIsCloseButOtherwiseDifferent()
    {
        Assert.Equal(FieldAgreement.Close, Field(MatchComparer.Compare(Incoming("A", "B", "1990-01-15"), Candidate("A", "B", "1990-01-16")), "dob"));
        Assert.Equal(FieldAgreement.Different, Field(MatchComparer.Compare(Incoming("A", "B", "1990-01-15"), Candidate("A", "B", "1985-06-30")), "dob"));
    }

    [Fact]
    public void SameDobAndAddressWithDifferentFirstNameWarnsOfTwins()
    {
        var c = MatchComparer.Compare(Incoming("Rivera", "Mya", "2023-05-14", address: Home), Candidate("Rivera", "Mia", "2023-05-14", address: Home));
        Assert.Equal(FieldAgreement.Different, Field(c, "firstName"));
        Assert.Equal(FieldAgreement.Agree, Field(c, "address"));
        Assert.Contains(c.Warnings, w => w.Contains("twin", StringComparison.Ordinal));
    }

    [Fact]
    public void PlaceholderNamesAreFlaggedAndNotCompared()
    {
        var c = MatchComparer.Compare(Incoming("Baker", "Baby Boy", "2026-09-30", "male"), Candidate("Baker", "Noah", "2026-09-30", "male"));
        Assert.Equal(FieldAgreement.Missing, Field(c, "firstName"));
        Assert.Contains(c.Warnings, w => w.Contains("Placeholder", StringComparison.Ordinal));
    }

    [Fact]
    public void HyphenatedLastNameAndAliasesAreClose()
    {
        Assert.Equal(FieldAgreement.Close,
            Field(MatchComparer.Compare(Incoming("Rivera-Lopez", "Ana", "1995-11-30"), Candidate("Rivera", "Ana", "1995-11-30")), "lastName"));
        Assert.Equal(FieldAgreement.Close,
            Field(MatchComparer.Compare(Incoming("Smith", "Ana", "1995-11-30"), Candidate("Rivera", "Ana", "1995-11-30", aliases: ("Smith", "Ana"))), "lastName"));
    }

    [Fact]
    public void AddressAbbreviationsAgreeAndAnotherUnitIsClose()
    {
        var abbreviated = new MatchAddress("1214 W. Lombard St", "Apt 2F", "BALTIMORE", "md", "21223", true);
        Assert.Equal(FieldAgreement.Agree, Field(MatchComparer.Compare(Incoming("A", "B", "2000-01-01", address: abbreviated),
            Candidate("A", "B", "2000-01-01", address: Home)), "address"));
        var otherUnit = Home with { Unit = "3R" };
        Assert.Equal(FieldAgreement.Close, Field(MatchComparer.Compare(Incoming("A", "B", "2000-01-01", address: otherUnit),
            Candidate("A", "B", "2000-01-01", address: Home)), "address"));
    }

    [Fact]
    public void RankPutsTheBestCandidateFirstAndCapsTheList()
    {
        var incoming = Incoming("Rivera", "Ana", "1995-11-30", address: Home);
        var candidates = Enumerable.Range(0, 8).Select(i => Candidate("Rivera", "Zed" + i, "1960-01-01"))
            .Append(Candidate("Rivera", "Ana", "1995-11-30", address: Home)).ToList();
        var ranked = MatchComparer.Rank(incoming, candidates, 5);
        Assert.Equal(5, ranked.Count);
        Assert.Equal(candidates[^1].PersonId, ranked[0].PersonId);
    }

    [Fact]
    public void IncomingKeyIsStableAcrossSpellingNoise()
        => Assert.Equal(MatchComparer.IncomingKey("O'Brien", "Mary-Kate", new DateOnly(2001, 2, 3)),
            MatchComparer.IncomingKey("obrien", "MARY KATE", new DateOnly(2001, 2, 3)));
}

public sealed class TabularExportTests
{
    [Fact]
    public void InstantsAreExplicitUtcInBothExportFormatsAndDatesStayDates()
    {
        IReadOnlyList<IReadOnlyList<object?>> rows = [[new DateTime(2026, 7, 1, 12, 30, 0),
            new DateTimeOffset(2026, 7, 1, 8, 30, 0, TimeSpan.FromHours(-4)), new DateOnly(2026, 7, 1)]];
        var csv = Encoding.UTF8.GetString(TabularExport.Csv(["SQL instant", "Offset instant", "Date"], rows));
        Assert.Equal(2, csv.Split("2026-07-01 12:30:00 UTC").Length - 1);
        Assert.Contains("\"2026-07-01\"", csv);
        using var zip = new ZipArchive(new MemoryStream(TabularExport.Xlsx("Times", ["SQL instant", "Offset instant", "Date"], rows)));
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        Assert.Equal(2, reader.ReadToEnd().Split("2026-07-01 12:30:00 UTC").Length - 1);
    }

    [Fact]
    public void CsvQuotesValuesAndDefusesFormulas()
    {
        string csv = Encoding.UTF8.GetString(TabularExport.Csv(["Name", "Value"], [["=HYPERLINK(\"x\")", -5m], ["Lab \"A\"", null]]));
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"-5\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"Lab \"\"A\"\"\",", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void XlsxIsAWorkbookWithOneSheet()
    {
        byte[] bytes = TabularExport.Xlsx("Testing counts", ["Month", "Tests"], [["2026-07", 3], ["@cmd", 1.5m]]);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.NotNull(zip.GetEntry("xl/workbook.xml"));
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        string sheet = reader.ReadToEnd();
        Assert.Contains("<v>3</v>", sheet, StringComparison.Ordinal);
        Assert.Contains("&apos;@cmd", sheet, StringComparison.Ordinal);
    }
}
