using System.Globalization;
using System.Text.Json;
using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Security;
using Merit.Domain.Matching;
using Merit.Domain.Text;

namespace Merit.Application.Services;

public sealed record MatchCandidateView(
    CandidateComparison Comparison,
    CandidatePersonRow Person,
    IReadOnlyList<CandidatePlaceRow> Places,
    IReadOnlyList<CandidateResultRow> Results);

public sealed record MatchReviewDetail(MatchReviewRow Row, IReadOnlyDictionary<string, string?> Fields, IncomingIdentity Incoming,
                                       IReadOnlyList<MatchCandidateView> Candidates);

public sealed record MatchDecisionOutcome(bool Released, int RemainingCandidates, string Message);

/// <summary>
/// Person matching review: the held rows whose identity needs a person, each with up to five ranked candidates compared
/// field by field. The reviewer decides Same person, Different person (remembered), New person, or Not sure (to a
/// supervisor); the decision is stored beside the reported values and releasing rows load with it.
/// </summary>
public sealed class MatchReviewService(IClock clock, IReviewData review, IPersonData persons, IIntakeData intake, AuditService audit,
                                       ICurrentSession session)
{
    public const int MaxCandidates = 5;

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "MM/dd/yyyy", "M/d/yyyy", "yyyyMMdd", "M/d/yy"];

    public async Task<IReadOnlyList<MatchReviewRow>> QueueAsync(bool escalatedOnly, int pageNumber, int pageSize, CancellationToken ct = default)
    {
        session.Require(Permissions.ResultReleaseHold);
        return await review.ListMatchReviewRowsAsync(escalatedOnly, null, pageNumber, pageSize, ct).ConfigureAwait(false);
    }

    public async Task<MatchReviewDetail> GetAsync(Guid stagingRowId, CancellationToken ct = default)
    {
        session.Require(Permissions.ResultReleaseHold);
        var row = (await review.ListMatchReviewRowsAsync(false, stagingRowId, 1, 1, ct).ConfigureAwait(false)).FirstOrDefault()
                  ?? throw new NotFoundException("The held row was not found, is no longer held, or does not need a match review.");
        var fields = Fields(row);
        var incoming = Incoming(fields);
        var candidates = await CandidatesAsync(incoming, ct).ConfigureAwait(false);
        await audit.ViewedAsync("stagingRow", stagingRowId, ct).ConfigureAwait(false);
        return new MatchReviewDetail(row, fields, incoming, candidates);
    }

    public async Task<MatchDecisionOutcome> DecideAsync(Guid stagingRowId, string decision, Guid? personId, string? note,
                                                        CancellationToken ct = default)
    {
        session.Require(Permissions.ResultReleaseHold);
        var v = new Validator();
        v.When(decision is not ("same" or "different" or "newPerson" or "notSure"), "Decision",
            "Choose Same person, Different person, New person or Not sure.");
        v.When(decision is "same" or "different" && personId is null, "PersonId", "Choose the candidate the decision is about.");
        v.MaxLength("Note", note, 500, "Note");
        v.ThrowIfInvalid();

        var detail = await GetAsync(stagingRowId, ct).ConfigureAwait(false);
        var incoming = detail.Incoming;
        string key = MatchComparer.IncomingKey(incoming.LastName, incoming.FirstName, incoming.Dob);

        if (decision == "same" && detail.Candidates.All(c => c.Person.PersonId != personId))
        {
            throw new ValidationException("PersonId", "That person is not one of this record's candidates.");
        }

        await review.RecordMatchDecisionAsync(stagingRowId, decision, personId, decision == "different" ? key : null, note, ct)
            .ConfigureAwait(false);
        await audit.ChangedAsync("stagingRow", stagingRowId, ct).ConfigureAwait(false);

        switch (decision)
        {
            case "same":
                await intake.ReleaseRowsAsync([stagingRowId], ct).ConfigureAwait(false);
                return new MatchDecisionOutcome(true, 0, "Linked to the chosen person; the row loads in the background.");
            case "newPerson":
                await intake.ReleaseRowsAsync([stagingRowId], ct).ConfigureAwait(false);
                return new MatchDecisionOutcome(true, 0, "The row loads in the background as a new person.");
            case "notSure":
                return new MatchDecisionOutcome(false, detail.Candidates.Count, "Sent to a supervisor in the match review queue.");
            default:
                var remaining = await CandidatesAsync(incoming, ct).ConfigureAwait(false);
                return new MatchDecisionOutcome(false, remaining.Count,
                    remaining.Count == 0
                        ? "Remembered. No candidates remain; review the incoming record before creating a new person or sending it to a supervisor."
                        : "Remembered: this person will not be suggested for this record again.");
        }
    }

    private async Task<IReadOnlyList<MatchCandidateView>> CandidatesAsync(IncomingIdentity incoming, CancellationToken ct)
    {
        string? lastKey = MatchKeys.NameKey(incoming.LastName);
        if (lastKey is null)
        {
            return [];
        }

        var found = (await persons.FindCandidatesAsync(lastKey, MatchKeys.NameKey(incoming.FirstName), incoming.Dob, 25,
                MatchComparer.IncomingKey(incoming.LastName, incoming.FirstName, incoming.Dob), ct).ConfigureAwait(false))
            .Select(c => c.PersonId).Distinct().ToList();
        if (found.Count == 0)
        {
            return [];
        }

        var details = await review.GetCandidateDetailsAsync(found, ct).ConfigureAwait(false);
        var identities = details.Persons.Select(p => new CandidateIdentity(p.PersonId, p.LastName, p.FirstName, p.MiddleName, p.Dob,
            p.Sex, p.IsPlaceholderName, Aliases(p.Aliases),
            details.Places.Where(pl => pl.PersonId == p.PersonId && pl.Role == "residence")
                .Select(pl => new MatchAddress(pl.AddressLine1, pl.Unit, pl.City, pl.State, pl.Zip5,
                    pl.IsPrimary && (pl.EndDate is null || pl.EndDate >= clock.Today)))
                .ToList())).ToList();

        return MatchComparer.Rank(incoming, identities, MaxCandidates)
            .Select(c => new MatchCandidateView(c, details.Persons.First(p => p.PersonId == c.PersonId),
                details.Places.Where(p => p.PersonId == c.PersonId).ToList(),
                details.Results.Where(r => r.PersonId == c.PersonId).ToList()))
            .ToList();
    }

    private static IReadOnlyDictionary<string, string?> Fields(MatchReviewRow row)
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, string?>>(row.ParsedJson) ?? [];
        if (!string.IsNullOrWhiteSpace(row.CorrectedJson))
        {
            foreach (var (k, value) in JsonSerializer.Deserialize<Dictionary<string, string?>>(row.CorrectedJson) ?? [])
            {
                if (k is not ("matchPersonId" or "matchNewPerson"))
                {
                    fields[k] = value;
                }
            }
        }

        return fields;
    }

    private static IncomingIdentity Incoming(IReadOnlyDictionary<string, string?> f)
    {
        string? Get(string key) => f.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        DateOnly? dob = Get("dob") is string d && DateOnly.TryParseExact(d, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None,
            out var parsed) ? parsed : null;
        var address = Get("addressLine1") is null
            ? null
            : new MatchAddress(Get("addressLine1"), Get("addressUnit"), Get("city"), Get("state"), Get("zip5"), true);
        return new IncomingIdentity(Get("lastName"), Get("firstName"), Get("middleName"), dob, Get("sex"), address);
    }

    private static IReadOnlyList<(string LastName, string? FirstName)> Aliases(string? aliases)
        => string.IsNullOrWhiteSpace(aliases)
            ? []
            : aliases.Split("; ", StringSplitOptions.RemoveEmptyEntries)
                .Select(a => a.Split(", ", 2))
                .Select(p => (p[0], p.Length > 1 && p[1].Length > 0 ? p[1] : (string?)null))
                .ToList();
}

/// <summary>Clusters flagged by detection, their review, and the case map. Nothing is merged automatically.</summary>
public sealed class ClusterService(IReviewData review, AuditService audit, ICurrentSession session)
{
    private static readonly string[] Statuses = ["open", "underReview", "closed"];

    public async Task<IReadOnlyList<ClusterListRow>> ListAsync(string? status, int pageNumber, int pageSize, CancellationToken ct = default)
    {
        session.Require(Permissions.CaseView);
        return await review.ListClustersAsync(string.IsNullOrEmpty(status) ? null : status, pageNumber, pageSize, ct).ConfigureAwait(false);
    }

    public async Task<ClusterDetail> GetAsync(Guid clusterId, CancellationToken ct = default)
    {
        session.Require(Permissions.CaseView);
        var detail = await review.GetClusterAsync(clusterId, ct).ConfigureAwait(false) ?? throw new NotFoundException();
        await audit.ViewedAsync("cluster", clusterId, ct).ConfigureAwait(false);
        return detail;
    }

    public async Task UpdateAsync(Guid clusterId, string status, string? notes, byte[] rowVersion, CancellationToken ct = default)
    {
        session.Require(Permissions.CaseEdit);
        var v = new Validator();
        v.When(!Statuses.Contains(status), "Status", "Choose open, under review or closed.");
        v.MaxLength("Notes", notes, 2000, "Notes");
        v.When(rowVersion.Length != 8, "RowVersion", "Reload the cluster and try again.");
        v.ThrowIfInvalid();
        await review.UpdateClusterAsync(clusterId, status, string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(), rowVersion, ct)
            .ConfigureAwait(false);
        await audit.ChangedAsync("cluster", clusterId, ct).ConfigureAwait(false);
    }

    public async Task SetMemberAsync(Guid clusterId, string caseNumber, bool isMember, CancellationToken ct = default)
    {
        session.Require(Permissions.CaseEdit);
        new Validator().Required("CaseNumber", caseNumber, "Case number").MaxLength("CaseNumber", caseNumber, 20, "Case number").ThrowIfInvalid();
        await review.SetCaseClusterMemberAsync(clusterId, caseNumber.Trim().ToUpperInvariant(), isMember, ct).ConfigureAwait(false);
        await audit.ChangedAsync("cluster", clusterId, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CaseMapRow>> MapAsync(Guid? analyteId, DateOnly? openedFrom, DateOnly? openedTo, bool openOnly,
                                                          CancellationToken ct = default)
    {
        session.Require(Permissions.CaseView);
        var rows = await review.GetCaseMapAsync(analyteId, openedFrom, openedTo, openOnly, ct).ConfigureAwait(false);
        await audit.ListedAsync("caseMap", rows.Count, ct).ConfigureAwait(false);
        return rows;
    }
}

public sealed record ReportColumn(string Key, string Label, string Kind);

/// <summary>How the report screen charts a report: x axis, optional series grouping, and the measures a user can pick.</summary>
public sealed record ReportChart(string Kind, string X, string? Group, IReadOnlyList<string> Measures);

public sealed record ReportDefinition(string Code, string Title, string Description, string Procedure, string Permission,
                                      bool UsesAnalyte, IReadOnlyList<ReportColumn> Columns, ReportChart Chart);

public sealed record ReportResult(ReportDefinition Definition, DateOnly FromDate, DateOnly ToDate, Guid? AnalyteId,
                                  IReadOnlyList<IReadOnlyList<object?>> Rows);

/// <summary>
/// The standard report library. Each report is one stored procedure that aggregates inside the caller's tenant scope;
/// the definitions here name the columns and the chart. Exports need the export permission and are audited.
/// </summary>
public sealed class ReportService(IClock clock, IReviewData review, AuditService audit, ICurrentSession session)
{
    public static IReadOnlyList<ReportDefinition> Library { get; } =
    [
        new("testingCounts", "Testing counts and rates",
            "Valid final results by collection month and population, people tested, and results at or above the case level in force.",
            "dbo.uspReportTestingCounts", Permissions.ResultView, true,
            [new("periodMonth", "Month", "month"), new("population", "Population", "text"), new("testCount", "Tests", "number"),
             new("personCount", "People tested", "number"), new("elevatedCount", "At or above case level", "number")],
            new("line", "periodMonth", "population", ["testCount", "personCount", "elevatedCount"])),
        new("elevatedCases", "Elevated counts by population and analyte",
            "Cases opened in the period by analyte, population and classification, and how many are still open.",
            "dbo.uspReportElevatedCases", Permissions.CaseView, true,
            [new("analyteCode", "Analyte", "text"), new("population", "Population", "text"), new("classification", "Classification", "text"),
             new("caseCount", "Cases", "number"), new("openCount", "Still open", "number")],
            new("bar", "population", "classification", ["caseCount", "openCount"])),
        new("caseTimeliness", "Case timeliness",
            "Median days from collection to receipt, receipt to case opening, and opening to first follow-up (a contact "
            + "attempt, letter, education, referral or medical management), by month opened.",
            "dbo.uspReportCaseTimeliness", Permissions.CaseView, true,
            [new("periodMonth", "Month opened", "month"), new("caseCount", "Cases", "number"),
             new("medianDaysToReceipt", "Median days collection to receipt", "number"),
             new("medianDaysToOpen", "Median days receipt to case", "number"),
             new("medianDaysToFollowUp", "Median days case to first follow-up", "number"),
             new("followedUpWithin7", "Followed up within 7 days", "number"),
             new("notFollowedUpYet", "Not followed up yet", "number")],
            new("line", "periodMonth", null, ["medianDaysToReceipt", "medianDaysToOpen", "medianDaysToFollowUp", "caseCount"])),
        new("investigationOutcomes", "Investigation outcomes",
            "Investigations requested in the period by type and status, regulatory actions, and average days to complete.",
            "dbo.uspReportInvestigationOutcomes", Permissions.CaseView, false,
            [new("investigationType", "Type", "text"), new("investigationStatus", "Status", "text"),
             new("investigationCount", "Investigations", "number"), new("actionCount", "With regulatory action", "number"),
             new("averageDaysToComplete", "Average days to complete", "number")],
            new("bar", "investigationType", "investigationStatus", ["investigationCount", "actionCount"])),
        new("reporterTimeliness", "Reporter timeliness and completeness",
            "By performing laboratory: results received, median days to receipt, share within five days, and missing key fields.",
            "dbo.uspReportReporterTimeliness", Permissions.ResultView, true,
            [new("labName", "Laboratory", "text"), new("resultCount", "Results", "number"),
             new("medianDaysToReceipt", "Median days to receipt", "number"), new("pctWithin5Days", "% within 5 days", "percent"),
             new("pctMissingSampleType", "% missing sample type", "percent"), new("pctMissingMethod", "% missing method", "percent"),
             new("pctMissingProvider", "% missing ordering provider", "percent"), new("pctMissingAddress", "% missing address", "percent"),
             new("heldOrRejectedRows", "Held or rejected rows", "number")],
            new("bar", "labName", null, ["pctWithin5Days", "pctMissingSampleType", "pctMissingMethod", "pctMissingProvider",
                "pctMissingAddress", "resultCount"])),
    ];

    public IReadOnlyList<ReportDefinition> Available()
        => Library.Where(r => session.Permissions.Contains(r.Permission)).ToList();

    public async Task<ReportResult> RunAsync(string code, DateOnly? fromDate, DateOnly? toDate, Guid? analyteId, CancellationToken ct = default)
    {
        var definition = Library.FirstOrDefault(r => r.Code == code) ?? throw new NotFoundException("There is no report with that name.");
        session.Require(definition.Permission);
        var to = toDate ?? clock.Today;
        var from = fromDate ?? to.AddMonths(-12).AddDays(1);
        new Validator().When(from > to, "FromDate", "The start date must be on or before the end date.")
            .When(to.DayNumber - from.DayNumber > 3660, "FromDate", "Choose a period of ten years or less.").ThrowIfInvalid();

        var data = await review.RunReportAsync(definition.Procedure, from, to, definition.UsesAnalyte ? analyteId : null, ct).ConfigureAwait(false);
        var index = definition.Columns.Select(c => data.Columns.ToList().IndexOf(c.Key)).ToList();
        var rows = data.Rows.Select(r => (IReadOnlyList<object?>)index.Select(i => i < 0 ? null : r[i]).ToList()).ToList();
        await audit.ListedAsync("report:" + code, rows.Count, ct).ConfigureAwait(false);
        return new ReportResult(definition, from, to, definition.UsesAnalyte ? analyteId : null, rows);
    }

    /// <summary>Runs a report for export; needs the export permission and records an export audit event.</summary>
    public async Task<ReportResult> ExportAsync(string code, DateOnly? fromDate, DateOnly? toDate, Guid? analyteId, CancellationToken ct = default)
    {
        session.Require(Permissions.DataExport);
        var result = await RunAsync(code, fromDate, toDate, analyteId, ct).ConfigureAwait(false);
        await audit.ExportedAsync("report:" + code, null, result.Rows.Count, ct).ConfigureAwait(false);
        return result;
    }
}
