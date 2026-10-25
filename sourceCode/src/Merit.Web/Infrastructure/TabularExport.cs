using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace Merit.Web.Infrastructure;

/// <summary>
/// Writes a list or report as CSV or as an Excel workbook (.xlsx, one sheet, inline strings), with no third-party
/// package. Text that a spreadsheet would run as a formula (starting with =, +, - or @) is prefixed with an apostrophe,
/// so an exported value can never execute in the reader's spreadsheet.
/// </summary>
public static class TabularExport
{
    public const string CsvType = "text/csv";
    public const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static byte[] Csv(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', headers.Select(h => Quote(Safe(h)))));
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(',', row.Select(v => v is null ? string.Empty : Quote(Text(v)))));
        }

        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    public static byte[] Xlsx(string sheetName, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>""");
            Add(zip, "_rels/.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Add(zip, "xl/workbook.xml",
                $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="{Xml(SheetName(sheetName))}" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Add(zip, "xl/_rels/workbook.xml.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
            Add(zip, "xl/styles.xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="1"><fill><patternFill patternType="none"/></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="2"><xf fontId="0"/><xf fontId="1" applyFont="1"/></cellXfs></styleSheet>""");

            var sheet = new StringBuilder();
            sheet.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews><sheetData>""");
            sheet.Append("<row r=\"1\">");
            foreach (var h in headers)
            {
                sheet.Append(CultureInfo.InvariantCulture, $"<c t=\"inlineStr\" s=\"1\"><is><t>{Xml(h)}</t></is></c>");
            }

            sheet.Append("</row>");
            int r = 2;
            foreach (var row in rows)
            {
                sheet.Append(CultureInfo.InvariantCulture, $"<row r=\"{r++}\">");
                foreach (var v in row)
                {
                    sheet.Append(v switch
                    {
                        null => "<c/>",
                        int or long or decimal or double or float or short =>
                            $"<c><v>{Convert.ToString(v, CultureInfo.InvariantCulture)}</v></c>",
                        _ => $"<c t=\"inlineStr\"><is><t xml:space=\"preserve\">{Xml(Text(v))}</t></is></c>",
                    });
                }

                sheet.Append("</row>");
            }

            sheet.Append("</sheetData></worksheet>");
            Add(zip, "xl/worksheets/sheet1.xml", sheet.ToString());
        }

        return stream.ToArray();
    }

    private static string Text(object v) => Safe(v switch
    {
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime d => d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        bool b => b ? "yes" : "no",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? string.Empty,
    });

    private static string Safe(string s) => s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r' && !IsNumber(s) ? "'" + s : s;

    private static bool IsNumber(string s) => decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private static string Quote(string s) => "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string Xml(string s) => SecurityElement.Escape(s) ?? string.Empty;

    private static string SheetName(string s)
    {
        var clean = new string(s.Where(ch => ch is not ('\\' or '/' or '?' or '*' or '[' or ']' or ':')).ToArray());
        return clean.Length > 31 ? clean[..31] : clean.Length == 0 ? "Sheet1" : clean;
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
