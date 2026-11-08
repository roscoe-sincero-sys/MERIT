using System.IO.Compression;
using System.Text;
using Merit.Application.Errors;
using Merit.Application.Services;

namespace Merit.Tests;

public sealed class PatientDocumentTests
{
    [Theory]
    [InlineData("report.pdf", "%PDF-1.7\noriginal", "application/pdf")]
    [InlineData("REPORT.PDF", "%PDF-1.4\noriginal", "application/pdf")]
    public void IdentifiesPdfFromOriginalBytes(string name, string content, string expected)
        => Assert.Equal(expected, PatientDocumentValidator.Validate(name, Encoding.UTF8.GetBytes(content)));

    [Theory]
    [InlineData("photo.jpg", "%PDF-1.7")]
    [InlineData("script.svg", "<svg/>")]
    [InlineData("report.xlsx", "not a zip")]
    [InlineData("report.pdf", "<html>pretend PDF</html>")]
    public void RejectsMismatchedOrUnsupportedFiles(string name, string content)
        => Assert.Throws<ValidationException>(() => PatientDocumentValidator.Validate(name, Encoding.UTF8.GetBytes(content)));

    [Fact]
    public void BoundsUploadsBeforeParsing()
    {
        Assert.Throws<ValidationException>(() => PatientDocumentValidator.Validate("report.pdf", []));
        Assert.Throws<ValidationException>(() => PatientDocumentValidator.Validate("report.pdf", new byte[PatientDocumentValidator.MaximumBytes + 1]));
    }

    [Theory]
    [InlineData("xl/vbaProject.bin", "macro")]
    [InlineData("xl/embeddings/object.xml", "embedded")]
    [InlineData("xl/_rels/workbook.xml.rels", "<Relationships><Relationship TargetMode=\"External\" Target=\"https://example.test\"/></Relationships>")]
    [InlineData("xl/worksheets/sheet1.xml", "<!DOCTYPE sheet [<!ENTITY x SYSTEM 'file:///secret'>]><sheet>&x;</sheet>")]
    public void RejectsActiveWorkbookContents(string name, string content)
        => Assert.Throws<ValidationException>(() => PatientDocumentValidator.Validate("book.xlsx", Workbook(name, content)));

    [Fact]
    public void AcceptsPlainWorkbook() => Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        PatientDocumentValidator.Validate("book.xlsx", Workbook("xl/worksheets/sheet1.xml", "<worksheet/>")));

    private static byte[] Workbook(string name, string content)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            foreach (var (path, text) in new[] { ("[Content_Types].xml", "<Types/>"), ("xl/workbook.xml", "<workbook/>"), (name, content) })
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open()); writer.Write(text);
            }
        }
        return bytes.ToArray();
    }
}
