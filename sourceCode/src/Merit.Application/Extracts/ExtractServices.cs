using System.Globalization;
using System.Text.Json;
using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Reference;
using Merit.Application.Security;
using Merit.Application.Services;
using Merit.Domain.Extracts;
using Merit.Domain.Ids;
using Merit.Domain.Population;

namespace Merit.Application.Extracts;

/// <summary>Federal extracts from the web side: request a run (a background job), show status, download the file.</summary>
public sealed class ExtractService(IExtractData extracts, AuditService audit, ICurrentSession session)
{
    public async Task<Guid> RequestAsync(string extractType, int year, int? quarter, CancellationToken ct = default)
    {
        session.Require(Permissions.ExtractRun);
        var v = new Validator();
        v.When(extractType is not ("CBLS" or "ABLES"), "ExtractType", "Choose CBLS or ABLES.");
        v.When(year < 2000 || year > DateTime.UtcNow.Year, "Year", "Choose a year up to the current one.");
        v.When(extractType == "CBLS" && quarter is not (>= 1 and <= 4), "Quarter", "CBLS files are quarterly: choose a quarter.");
        v.ThrowIfInvalid();

        DateOnly start, end;
        if (extractType == "CBLS")
        {
            start = new DateOnly(year, ((quarter!.Value - 1) * 3) + 1, 1);
            end = start.AddMonths(3).AddDays(-1);
        }
        else
        {
            start = new DateOnly(year, 1, 1);
            end = new DateOnly(year, 12, 31);
        }

        Guid id = TimeOrderedId.New();
        await extracts.CreateRunAsync(id, extractType, start, end, TimeOrderedId.New(), ct).ConfigureAwait(false);
        return id;
    }

    public async Task<IReadOnlyList<ExtractRunRow>> RunsAsync(CancellationToken ct = default)
    {
        session.Require(Permissions.ExtractRun);
        return await extracts.ListRunsAsync(20, ct).ConfigureAwait(false);
    }

    public async Task<ExtractRunRow> DownloadAsync(Guid runId, CancellationToken ct = default)
    {
        session.Require(Permissions.ExtractRun);
        var run = await extracts.GetRunAsync(runId, true, ct).ConfigureAwait(false) ?? throw new NotFoundException();
        if (run.RunStatus is not ("ready" or "submitted") || run.FileContent is null)
        {
            throw new BusinessRuleException("The file is not ready yet.");
        }

        await audit.ExportedAsync("extractRun", runId, run.RecordCount ?? 0, ct).ConfigureAwait(false);
        return run;
    }

    /// <summary>The validation findings of one run: record, field and message. No person data is stored in them.</summary>
    public async Task<(ExtractRunRow Run, IReadOnlyList<ExtractFinding> Findings)> FindingsAsync(Guid runId, CancellationToken ct = default)
    {
        session.Require(Permissions.ExtractRun);
        var run = await extracts.GetRunAsync(runId, false, ct).ConfigureAwait(false) ?? throw new NotFoundException();
        var findings = string.IsNullOrWhiteSpace(run.ValidationReportJson)
            ? []
            : JsonSerializer.Deserialize<List<ExtractFinding>>(run.ValidationReportJson) ?? [];
        var links = session.Has(Permissions.PersonView)
            ? await extracts.ResolveFindingPersonsAsync(runId, findings.Where(f => f.RecordId is not null).Select(f => f.RecordId!).Distinct().ToList(), ct).ConfigureAwait(false)
            : [];
        var byId = links.ToDictionary(p => p.RecordId, p => p.PersonId, StringComparer.OrdinalIgnoreCase);
        return (run, findings.Select(f => f with { PersonId = f.RecordId is not null && byId.TryGetValue(f.RecordId, out var id) ? id : null }).ToList());
    }

    /// <summary>Records that the file was sent; the run stays downloadable.</summary>
    public async Task MarkSubmittedAsync(Guid runId, CancellationToken ct = default)
    {
        session.Require(Permissions.ExtractSubmit);
        await extracts.MarkSubmittedAsync(runId, ct).ConfigureAwait(false);
    }
}

/// <summary>One validation finding as the builder stores it.</summary>
public sealed record ExtractFinding(string? RecordId, string? FieldName, string? Message, Guid? PersonId = null);

/// <summary>
/// Builds a federal extract in the worker from the configured layout. Names, street addresses, phone numbers, SSNs and
/// medical record numbers never leave: the source procedures do not return them, and the person is identified by a
/// stable surrogate id. The validator applies NIOSH's cross-field rules before submission.
/// </summary>
public sealed class ExtractBuilder(IExtractData extracts, IConfigData config, IReferenceDataProvider reference,
                                   ISettingsProvider settings)
{
    public async Task<(int Records, int Errors)> BuildAsync(Guid extractRunId, CancellationToken ct = default)
    {
        var run = await extracts.GetRunAsync(extractRunId, false, ct).ConfigureAwait(false)
                  ?? throw new InvalidOperationException($"Extract run {extractRunId} not found.");
        await extracts.UpdateRunAsync(extractRunId, "running", null, null, null, null, null, ct).ConfigureAwait(false);

        var layoutData = await config.GetExtractLayoutAsync(run.ExtractType, ct).ConfigureAwait(false)
                         ?? throw new InvalidOperationException("No active layout.");
        var layout = new ExtractLayout(layoutData.Layout.ExtractType, layoutData.Layout.LayoutVersion, layoutData.Layout.FileFormat,
            layoutData.Layout.Delimiter, layoutData.Fields.Select(x => new LayoutField(x.FieldOrder, x.FieldName, x.FieldWidth,
                x.SourceKey, string.IsNullOrEmpty(x.PadWith) ? ' ' : x.PadWith[0], x.AlignRight, x.IsRequired)).ToList());
        var refData = await reference.GetAsync(ct).ConfigureAwait(false);
        var jurisdiction = await settings.GetAsync(ct).ConfigureAwait(false);

        var records = new List<(string, IReadOnlyDictionary<string, string?>)>();
        var crossField = new List<ExtractIssue>();

        if (run.ExtractType == "CBLS")
        {
            foreach (var r in await extracts.GetCblsRowsAsync(run.PeriodStart, run.PeriodEnd, ct).ConfigureAwait(false))
            {
                string id = ExtractCodes.SurrogateId(r.PersonId);
                string firstRace = r.RaceCodes?.Split(',', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "9";
                records.Add((id, new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["stateFips"] = jurisdiction.StateFips,
                    ["surrogateId"] = id,
                    ["dob"] = ExtractCodes.Date(r.Dob),
                    ["sexCode"] = ExtractCodes.SexCode(r.Sex),
                    ["raceCode"] = firstRace,
                    ["ethnicityCode"] = ExtractCodes.EthnicityCode(r.Ethnicity),
                    ["zip5"] = r.Zip5,
                    ["countyFips"] = r.CountyFips ?? "999",
                    ["censusTract"] = r.CensusTract,
                    ["collectedDate"] = ExtractCodes.Date(r.CollectedDate),
                    ["sampleTypeCode"] = refData.Lookup("sampleType", r.SampleType ?? "unknown")?.CblsCode ?? "9",
                    ["operator"] = r.Operator == "=" ? string.Empty : r.Operator,
                    ["bllOneDecimal"] = ExtractCodes.OneDecimal(r.ValueCanonical),
                    ["testMethodCode"] = refData.Lookup("testMethod", r.TestMethod ?? "unknown")?.CblsCode ?? "9",
                    ["labClia"] = r.LabClia,
                    ["payerCode"] = refData.Lookup("payerType", r.PayerType ?? "unknown")?.CblsCode ?? "9",
                    ["yearBuilt"] = r.YearBuilt?.ToString(CultureInfo.InvariantCulture),
                }));
            }
        }
        else
        {
            foreach (var r in await extracts.GetAblesRowsAsync(run.PeriodStart.Year, ct).ConfigureAwait(false))
            {
                string id = ExtractCodes.SurrogateId(r.PersonId);
                string workRel = ExtractCodes.WorkRel(r.ExposureCategory);
                bool work = ExtractCodes.IsWorkRelated(workRel);
                string? naics = work ? (r.Naics ?? "999") : null;
                string? coc = work ? (r.OccupationCode ?? "990") : null;
                crossField.AddRange(ExtractCodes.AblesCrossFieldIssues(id, workRel, naics, coc));
                var values = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["statePostalCode"] = jurisdiction.StatePostalCode,
                    ["stateRes"] = r.CountryRes is "CA" ? "CN" : r.CountryRes is "MX" ? "MX" : r.StateRes ?? "99",
                    ["countyRes"] = r.CountryRes is "CA" or "MX" ? "999" : r.CountyRes ?? "999",
                    ["stateExp"] = r.StateExp,
                    ["countyExp"] = r.CountyExp,
                    ["surrogateId"] = id,
                    ["ablesStatus"] = ExtractCodes.AblesStatus(r.ValueCanonical, r.PriorYearElevated, r.InRegistryBefore),
                    ["collectedDate"] = ExtractCodes.Date(r.CollectedDate),
                    ["dateType"] = "1",
                    ["bllWhole"] = ExtractCodes.WholeNumber(r.ValueCanonical),
                    ["dob"] = ExtractCodes.Date(r.Dob),
                    ["ageYears"] = r.Dob is DateOnly dob ? PopulationRules.AgeInYears(dob, r.CollectedDate).ToString(CultureInfo.InvariantCulture) : null,
                    ["sexCode"] = ExtractCodes.SexCode(r.Sex),
                    ["ethnicityCode"] = ExtractCodes.EthnicityCode(r.Ethnicity),
                    ["workRel"] = workRel,
                    ["naics"] = naics,
                    ["coc"] = coc,
                    ["process"] = work || workRel == "9" ? "NA" : (r.ProcessText ?? r.ProcessCode ?? "NA"),
                };
                foreach (var (k, v) in ExtractCodes.RaceFlags((r.RaceCodes ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries)))
                {
                    values[k] = v;
                }

                records.Add((id, values));
            }
        }

        var file = ExtractWriter.Write(layout, records, crossField);
        string report = JsonSerializer.Serialize(file.Issues.Select(i => new { i.RecordId, i.FieldName, i.Message }));
        string fileName = run.ExtractType == "CBLS"
            ? $"CBLS_{jurisdiction.StatePostalCode}_{run.PeriodStart:yyyy}Q{((run.PeriodStart.Month - 1) / 3) + 1}.txt"
            : $"ABLES_{jurisdiction.StatePostalCode}_{run.PeriodStart:yyyy}.csv";
        await extracts.UpdateRunAsync(extractRunId, "ready", file.RecordCount, file.Issues.Count, report, fileName, file.Content, ct)
            .ConfigureAwait(false);
        return (file.RecordCount, file.Issues.Count);
    }
}
