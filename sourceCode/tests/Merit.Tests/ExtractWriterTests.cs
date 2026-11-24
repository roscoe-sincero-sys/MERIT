using Merit.Application.Services;
using Merit.Domain.Extracts;

namespace Merit.Tests;

/// <summary>
/// Regression tests for issue #312 (CRLF injection / record splitting in federal extracts).
/// </summary>
public sealed class ExtractWriterTests
{
    private static readonly ExtractLayout DelimitedLayout = new(
        "ABLES",
        "2024.1",
        "delimited",
        ",",
        [
            new LayoutField(1, "ID", 15, "id", ' ', false, true),
            new LayoutField(2, "Process", 50, "process", ' ', false, false),
        ]);

    private static readonly ExtractLayout FixedWidthLayout = new(
        "CBLS",
        "2024.1",
        "fixedWidth",
        null,
        [
            new LayoutField(1, "CHILDID", 6, "id", ' ', false, true),
            new LayoutField(2, "NOTE", 12, "note", ' ', false, false),
        ]);

    [Theory]
    [InlineData("Line1\nLine2")]
    [InlineData("Line1\r\nLine2")]
    [InlineData("Line1\rLine2")]
    public void DelimitedFieldReplacesLineBreaksWithSpaceAndReportsIssue(string value)
    {
        var file = ExtractWriter.Write(DelimitedLayout,
        [
            ("REC001", new Dictionary<string, string?> { ["id"] = "REC001", ["process"] = value }),
            ("REC002", new Dictionary<string, string?> { ["id"] = "REC002", ["process"] = "Clean value" }),
        ]);

        Assert.Equal(2, file.RecordCount);
        var lines = file.Content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // Header + 2 data records, with no stray CR or LF inside any line.
        Assert.Equal(3, lines.Length);
        Assert.All(lines, line => Assert.Equal(-1, line.IndexOfAny(['\r', '\n'])));
        Assert.Equal("REC001,Line1 Line2", lines[1]);
        Assert.Equal("REC002,Clean value", lines[2]);

        var issue = Assert.Single(file.Issues);
        Assert.Equal("REC001", issue.RecordId);
        Assert.Equal("Process", issue.FieldName);
        Assert.Contains("Line break", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("A\nB")]
    [InlineData("A\r\nB")]
    [InlineData("A\rB")]
    public void FixedWidthFieldReplacesLineBreaksWithSpaceAndReportsIssue(string value)
    {
        var file = ExtractWriter.Write(FixedWidthLayout,
        [
            ("REC001", new Dictionary<string, string?> { ["id"] = "REC001", ["note"] = value }),
            ("REC002", new Dictionary<string, string?> { ["id"] = "REC002", ["note"] = "CLEAN" }),
        ]);

        Assert.Equal(2, file.RecordCount);
        var lines = file.Content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // Fixed-width has no header row: exactly one line per record.
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line =>
        {
            Assert.Equal(-1, line.IndexOfAny(['\r', '\n']));
            Assert.Equal(18, line.Length);
        });
        Assert.Equal("REC001A B         ", lines[0]);
        Assert.Equal("REC002CLEAN       ", lines[1]);

        var issue = Assert.Single(file.Issues);
        Assert.Equal("REC001", issue.RecordId);
        Assert.Equal("NOTE", issue.FieldName);
        Assert.Contains("Line break", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DelimitedFieldWithLineBreakAndCommaOrQuoteRemainsSingleLineAndQuoted()
    {
        var file = ExtractWriter.Write(DelimitedLayout,
        [
            ("REC001", new Dictionary<string, string?> { ["id"] = "REC001", ["process"] = "Spice, \"brand\"\r\nimported" }),
        ]);

        var lines = file.Content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("REC001,\"Spice, \"\"brand\"\" imported\"", lines[1]);
        Assert.Single(file.Issues);
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+10")]
    [InlineData("-5")]
    [InlineData("@mention")]
    public void FormulaLikeCharactersAreNotPrefixedInRegulatorySubmissions(string value)
    {
        var file = ExtractWriter.Write(DelimitedLayout,
        [
            ("REC001", new Dictionary<string, string?> { ["id"] = "REC001", ["process"] = value }),
        ]);

        var lines = file.Content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal($"REC001,{value}", lines[1]);
        Assert.Empty(file.Issues);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   \r\n  ", null)]
    [InlineData("Ground turmeric", "Ground turmeric")]
    [InlineData("Line 1\nLine 2", "Line 1 Line 2")]
    [InlineData("Line 1\r\nLine 2", "Line 1 Line 2")]
    [InlineData("Line 1\rLine 2", "Line 1 Line 2")]
    [InlineData("  Line 1\r\n\r\nLine 2  ", "Line 1 Line 2")]
    public void CleanSingleLineCollapsesEmbeddedLineBreaks(string? input, string? expected)
        => Assert.Equal(expected, PersonService.CleanSingleLine(input));
}
