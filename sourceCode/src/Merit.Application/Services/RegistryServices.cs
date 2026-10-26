using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Intake;
using Merit.Application.Security;
using Merit.Domain.Identifiers;
using Merit.Domain.Text;

namespace Merit.Application.Services;

/// <summary>
/// The reporter registry: laboratories (CLIA), facilities that order tests (NPI or CLIA) and individual providers (NPI).
/// Intake links each result to its laboratory by CLIA number and to its ordering provider by NPI; a result whose laboratory
/// is not registered is held as "unknown sender". Entries are added one at a time or from a CSV file, which is always
/// checked first and imported only when the user confirms.
/// </summary>
public sealed class RegistryService(IPlaceData places, ICurrentSession session)
{
    public const int MaxFileBytes = 2 * 1024 * 1024;
    public const int MaxRows = 5000;

    public static readonly IReadOnlyList<string> Kinds = ["laboratory", "facility", "provider"];

    /// <summary>The CSV columns, in the template's order. Only "type" is always required.</summary>
    public static readonly IReadOnlyList<string> Columns = ["type", "name", "lastName", "firstName", "credential", "npi", "clia", "phone"];

    public async Task<IReadOnlyList<ReporterRow>> ListAsync(string? text, string? kind, CancellationToken ct = default)
    {
        session.Require(Permissions.PersonView);
        return await places.ListReportersAsync(string.IsNullOrWhiteSpace(text) ? null : text.Trim(),
            kind is not null && Kinds.Contains(kind) ? kind : null, 200, ct).ConfigureAwait(false);
    }

    /// <summary>Adds or updates one entry, matched on its CLIA number or NPI.</summary>
    public async Task<RegistryImportCounts> SaveAsync(ReporterInput input, CancellationToken ct = default)
    {
        session.Require(Permissions.RegistryEdit);
        var (row, problems) = Check(1, input);
        if (row is null)
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["Reporter"] = problems.ToArray() });
        }

        return await places.ImportReportersAsync([row], ct).ConfigureAwait(false);
    }

    /// <summary>Checks a CSV file without saving anything: which rows are ready and what is wrong with the rest.</summary>
    public RegistryCheck CheckFile(string content)
    {
        session.Require(Permissions.RegistryEdit);
        var lines = CsvReader.Read(content.TrimStart('﻿'), ',');
        if (lines.Count == 0)
        {
            throw new ValidationException("File", "The file is empty.");
        }

        var header = lines[0].Select(h => h.Trim()).ToList();
        int Col(string name) => header.FindIndex(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
        if (Col("type") < 0)
        {
            throw new ValidationException("File", $"The first line must name the columns: {string.Join(",", Columns)}. Download the template to start.");
        }

        if (lines.Count - 1 > MaxRows)
        {
            throw new ValidationException("File", $"Load at most {MaxRows} rows at a time; split the file.");
        }

        var ready = new List<ReporterSave>();
        var rejected = new List<RegistryProblem>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 1; i < lines.Count; i++)
        {
            var cells = lines[i];
            if (cells.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            string? Get(string name)
            {
                int c = Col(name);
                return c >= 0 && c < cells.Count && !string.IsNullOrWhiteSpace(cells[c]) ? cells[c].Trim() : null;
            }

            int line = i + 1;
            var input = new ReporterInput(Get("type")?.ToLowerInvariant(), Get("name"), Get("lastName"), Get("firstName"), Get("credential"),
                Get("npi"), Get("clia"), Get("phone"));
            var (row, problems) = Check(line, input);
            if (row is not null)
            {
                // Laboratories and facilities are both organizations, so a CLIA number or NPI names one organization
                // whatever the row's type (issue #11); providers are a separate registry, keyed by NPI.
                string scope = row.Kind == "provider" ? "provider" : "organization";
                var keys = new[] { ("CLIA number", row.Clia), ("NPI", row.Npi) }
                    .Where(k => k.Item2 is not null).Select(k => (Label: k.Item1, Key: $"{scope}|{k.Item1}|{k.Item2}")).ToList();
                var repeat = keys.FirstOrDefault(k => seen.ContainsKey(k.Key));
                if (repeat.Key is not null)
                {
                    problems.Add($"Same {repeat.Label} as line {seen[repeat.Key]}; only the first is loaded.");
                    row = null;
                }
                else
                {
                    keys.ForEach(k => seen[k.Key] = line);
                }
            }

            if (row is null)
            {
                rejected.Add(new RegistryProblem(line, input.Name ?? string.Join(", ", new[] { input.LastName, input.FirstName }.Where(x => x is not null)),
                    string.Join(" ", problems)));
            }
            else
            {
                ready.Add(row);
            }
        }

        return new RegistryCheck(ready, rejected);
    }

    /// <summary>Saves the rows a check found ready. The file is checked again, so a changed file cannot slip through.</summary>
    public async Task<(RegistryCheck Check, RegistryImportCounts Counts)> ImportFileAsync(string content, CancellationToken ct = default)
    {
        var check = CheckFile(content);
        var counts = check.Ready.Count == 0
            ? new RegistryImportCounts(0, 0)
            : await places.ImportReportersAsync(check.Ready, ct).ConfigureAwait(false);
        return (check, counts);
    }

    /// <summary>The format rules for one entry. Returns the row to save, or null and the reasons it cannot be saved.</summary>
    public static (ReporterSave? Row, List<string> Problems) Check(int rowNumber, ReporterInput input)
    {
        var problems = new List<string>();
        string? kind = input.Kind?.Trim().ToLowerInvariant();
        string? npi = ProviderIdentifiers.NormalizeNpi(input.Npi);
        string? clia = ProviderIdentifiers.NormalizeClia(input.Clia);

        if (kind is null || !Kinds.Contains(kind))
        {
            problems.Add("Type must be laboratory, facility or provider.");
            return (null, problems);
        }

        if (npi is not null && !ProviderIdentifiers.IsValidNpi(npi))
        {
            problems.Add($"NPI {input.Npi} is not valid: an NPI is 10 digits and its last digit is a check digit. Copy it again from NPPES.");
        }

        if (clia is not null && !ProviderIdentifiers.IsValidClia(clia))
        {
            problems.Add($"CLIA number {input.Clia} is not valid: it is two digits, the letter D, then seven digits (for example 21D0123456).");
        }

        switch (kind)
        {
            case "laboratory":
                if (clia is null) { problems.Add("A laboratory needs its CLIA number."); }
                if (string.IsNullOrWhiteSpace(input.Name)) { problems.Add("A laboratory needs a name."); }
                break;
            case "facility":
                if (npi is null && clia is null) { problems.Add("A facility needs an NPI or a CLIA number."); }
                if (string.IsNullOrWhiteSpace(input.Name)) { problems.Add("A facility needs a name."); }
                break;
            default:
                if (npi is null) { problems.Add("A provider needs an NPI."); }
                if (clia is not null) { problems.Add("Providers have no CLIA number; leave clia blank."); }
                if (string.IsNullOrWhiteSpace(input.LastName)) { problems.Add("A provider needs a last name."); }
                break;
        }

        void Max(string? value, int max, string label)
        {
            if (value is not null && value.Trim().Length > max) { problems.Add($"{label} is longer than {max} characters."); }
        }

        Max(input.Name, 120, "Name");
        Max(input.LastName, 60, "Last name");
        Max(input.FirstName, 60, "First name");
        Max(input.Credential, 20, "Credential");
        Max(input.Phone, 30, "Phone");
        if (problems.Count > 0)
        {
            return (null, problems);
        }

        string? name = kind == "provider" ? null : input.Name!.Trim();
        return (new ReporterSave(rowNumber, kind, name, name is null ? null : MatchKeys.NormalizeWords(name), kind == "provider" ? input.LastName!.Trim() : null,
            kind == "provider" ? Trim(input.FirstName) : null, kind == "provider" ? Trim(input.Credential) : null, npi, clia, Trim(input.Phone)), problems);
    }

    /// <summary>The template users download: the header and one example of each kind (the identifiers pass the checks).</summary>
    public static string Template()
        => string.Join(",", Columns) + "\r\n"
           + "laboratory,Chesapeake Reference Laboratory,,,,,21D0123456,410-555-0100\r\n"
           + "facility,Harbor Pediatrics,,,,1234567893,,410-555-0142\r\n"
           + "provider,,Rivera,Ana,MD,1245319599,,\r\n";

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

public sealed record ReporterInput(string? Kind, string? Name, string? LastName, string? FirstName, string? Credential, string? Npi, string? Clia,
                                   string? Phone);

public sealed record RegistryProblem(int Line, string Name, string Problem);

public sealed record RegistryCheck(IReadOnlyList<ReporterSave> Ready, IReadOnlyList<RegistryProblem> Rejected);
