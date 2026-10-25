using System.Text;

namespace Merit.Application.Intake;

/// <summary>A small RFC 4180 reader for CSV, TSV and pipe-delimited intake files: quoted fields, doubled quotes, CRLF.</summary>
public static class CsvReader
{
    public static IReadOnlyList<IReadOnlyList<string>> Read(string content, char delimiter)
    {
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;

        for (int i = 0; i < content.Length; i++)
        {
            char c = content[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            if (c == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < content.Length && content[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(field.ToString());
                field.Clear();
                if (row.Count > 1 || row[0].Length > 0)
                {
                    rows.Add(row);
                }

                row = [];
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            if (row.Count > 1 || row[0].Length > 0)
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    public static char DelimiterFor(string fileType) => fileType switch
    {
        "tsv" => '\t',
        "pipe" => '|',
        _ => ',',
    };
}
