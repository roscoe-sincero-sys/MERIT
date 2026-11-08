using System.IO.Compression;
using System.Xml;
using Merit.Application.Errors;

namespace Merit.Application.Services;

/// <summary>Bounds private supporting files and identifies their format from bytes, never a browser MIME claim.</summary>
public static class PatientDocumentValidator
{
    public const int MaximumBytes = 10 * 1024 * 1024;
    public static string Validate(string fileName, byte[] bytes)
    {
        if (bytes.Length is 0 or > MaximumBytes) throw Invalid("Choose a file from 1 byte to 10 MB.");
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var b = bytes.AsSpan();
        if (extension == ".pdf" && b.StartsWith("%PDF-"u8)) return "application/pdf";
        if (extension == ".png" && b.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (extension is ".jpg" or ".jpeg" && b.StartsWith(new byte[] { 255, 216, 255 })) return "image/jpeg";
        if (extension == ".webp" && b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        if (extension == ".xlsx")
        {
            try
            {
                using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                if (zip.Entries.Count > 2000 || zip.Entries.Sum(e => e.Length) > 50L * 1024 * 1024
                    || zip.GetEntry("[Content_Types].xml") is null || zip.GetEntry("xl/workbook.xml") is null)
                    throw Invalid("Choose a valid Excel workbook without macros (XLSX), up to 50 MB when expanded.");
                long expandedBytes = 0;
                foreach (var entry in zip.Entries)
                {
                    var name = entry.FullName;
                    if (name.Contains("..", StringComparison.Ordinal) || name.StartsWith('/') || name.Contains('\\')
                        || name.Contains("vbaProject", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("embeddings/", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("externalLinks/", StringComparison.OrdinalIgnoreCase)
                        || Path.GetExtension(name).ToLowerInvariant() is ".exe" or ".dll" or ".bin" or ".js" or ".html")
                        throw Invalid("Remove macros, embedded files and external links before saving this workbook as XLSX.");
                    // Enforce actual expanded bytes as well as ZIP directory lengths, including malformed archives.
                    using var expanded = new MemoryStream();
                    using (var stream = entry.Open())
                    {
                        var buffer = new byte[8192];
                        int read;
                        while ((read = stream.Read(buffer)) > 0)
                        {
                            expandedBytes += read;
                            if (expandedBytes > 50L * 1024 * 1024) throw Invalid("This workbook exceeds the 50 MB expanded size limit.");
                            expanded.Write(buffer, 0, read);
                        }
                    }
                    expanded.Position = 0;
                    if (name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                    {
                        using var reader = XmlReader.Create(expanded, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 50 * 1024 * 1024 });
                        while (reader.Read())
                        {
                            if (reader.NodeType == XmlNodeType.Element && reader.GetAttribute("TargetMode") == "External")
                                throw Invalid("Remove external workbook links before uploading.");
                        }
                    }
                }
                return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            }
            catch (InvalidDataException) { throw Invalid("This file is not a readable XLSX workbook."); }
            catch (XmlException) { throw Invalid("This workbook contains invalid or unsupported XML."); }
        }
        throw Invalid("Choose a PDF, XLSX, JPEG, PNG or WebP file with matching file contents and extension.");
    }
    private static ValidationException Invalid(string message) => new("file", message);
}
