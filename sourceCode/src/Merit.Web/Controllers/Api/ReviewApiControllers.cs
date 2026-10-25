using Merit.Application.Data;
using Merit.Application.Security;
using Merit.Application.Services;
using Merit.Contracts;
using Merit.Domain.Matching;
using Merit.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Merit.Web.Controllers.Api;

/// <summary>Person matching review for the Angular review screen: the queue, one record with ranked candidates, decisions.</summary>
[ApiController]
[Route("api/v1/match-review")]
public sealed class MatchReviewController(MatchReviewService review) : ControllerBase
{
    private static readonly (string Key, string Label)[] IncomingFields =
    [
        ("lastName", "Last name"), ("firstName", "First name"), ("middleName", "Middle name"), ("dob", "Date of birth"),
        ("sex", "Sex"), ("addressLine1", "Street"), ("addressUnit", "Apartment or unit"), ("city", "City"), ("state", "State"), ("zip5", "ZIP"),
        ("guardianName", "Guardian"), ("collectedDate", "Collected"), ("loinc", "Test code"), ("value", "Value"), ("unit", "Result unit"),
        ("accessionNumber", "Accession"),
    ];

    [HttpGet]
    [Authorize(Policy = Permissions.ResultReleaseHold)]
    public async Task<PagedResult<MatchQueueItemDto>> Queue(bool escalated = false, int page = 1, int pageSize = 100, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 500);
        var rows = await review.QueueAsync(escalated, page, pageSize, ct);
        return new PagedResult<MatchQueueItemDto>(rows.Select(r =>
        {
            var f = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(r.ParsedJson) ?? [];
            string? name = f.GetValueOrDefault("lastName") is string last ? $"{last}, {f.GetValueOrDefault("firstName")}".TrimEnd(',', ' ') : null;
            return new MatchQueueItemDto(r.StagingRowId, r.FileName, r.SenderName, r.RowNumber, name, f.GetValueOrDefault("dob"),
                r.IsUrgent, r.HeldSinceUtc, r.ReasonMessage, r.IsEscalated, r.EscalationNote);
        }).ToList(), rows.FirstOrDefault()?.TotalCount ?? 0, page, pageSize);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Permissions.ResultReleaseHold)]
    public async Task<MatchReviewDto> Get(Guid id, CancellationToken ct)
    {
        var d = await review.GetAsync(id, ct);
        var incoming = IncomingFields
            .Where(f => d.Fields.TryGetValue(f.Key, out var v) && !string.IsNullOrWhiteSpace(v))
            .Select(f => new IncomingFieldDto(f.Key, f.Label, d.Fields[f.Key])).ToList();
        var candidates = d.Candidates.Select((c, i) => new MatchCandidateDto(c.Person.PersonId, i + 1,
            $"{c.Person.FirstName} {c.Person.LastName}".Trim(), c.Comparison.Explanation, c.Comparison.Warnings,
            c.Comparison.Fields.Select(f => new MatchFieldDto(f.Field, f.Label, f.Incoming, f.Candidate, Agreement(f.Agreement), f.Note)).ToList(),
            c.Person.Aliases, c.Person.ResultCount, c.Person.LastCollectedDate, c.Person.OpenCaseNumbers,
            c.Places.Select(p => new CandidatePlaceDto(p.Role, p.IsPrimary, p.StartDate, p.EndDate, PlaceLabel(p))).ToList(),
            c.Results.Select(r => new CandidateResultDto(r.CollectedDate, r.AnalyteCode,
                $"{(r.Operator == "=" ? string.Empty : r.Operator)}{r.ValueCanonical:0.##} {r.UnitCanonical}", r.SampleType, r.LabName)).ToList()))
            .ToList();
        return new MatchReviewDto(d.Row.StagingRowId, d.Row.FileName, d.Row.SenderName, d.Row.RowNumber, d.Row.IsUrgent, d.Row.IsEscalated,
            d.Row.EscalationNote, d.Row.ReasonMessage, incoming, candidates);
    }

    [HttpPost("{id:guid}/decision")]
    [Authorize(Policy = Permissions.ResultReleaseHold)]
    public async Task<MatchDecisionResponse> Decide(Guid id, MatchDecisionRequest request, CancellationToken ct)
    {
        var outcome = await review.DecideAsync(id, request.Decision, request.PersonId, request.Note, ct);
        return new MatchDecisionResponse(outcome.Released, outcome.RemainingCandidates, outcome.Message);
    }

    private static string Agreement(FieldAgreement a) => a switch
    {
        FieldAgreement.Agree => "agree",
        FieldAgreement.Close => "close",
        FieldAgreement.Different => "different",
        _ => "missing",
    };

    private static string PlaceLabel(CandidatePlaceRow p)
        => string.Join(", ", new[] { p.AddressLine1 + (string.IsNullOrEmpty(p.Unit) ? string.Empty : " " + p.Unit), p.City, p.State, p.Zip5 }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>Cluster review and the case map.</summary>
[ApiController]
[Route("api/v1")]
public sealed class ClustersController(ClusterService clusters) : ControllerBase
{
    [HttpGet("clusters")]
    [Authorize(Policy = Permissions.CaseView)]
    public async Task<PagedResult<ClusterListItemDto>> List(string? status, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 500);
        var rows = await clusters.ListAsync(status, page, pageSize, ct);
        return new PagedResult<ClusterListItemDto>(rows.Select(r => new ClusterListItemDto(r.ClusterId, r.ClusterType, r.Title, r.ClusterStatus,
            r.WindowStart, r.WindowEnd, r.MemberCount, r.CreatedDateUtc, r.OwningTenantName)).ToList(), rows.FirstOrDefault()?.TotalCount ?? 0,
            page, pageSize);
    }

    [HttpGet("clusters/{id:guid}")]
    [Authorize(Policy = Permissions.CaseView)]
    public async Task<ClusterDto> Get(Guid id, CancellationToken ct) => ToDto(await clusters.GetAsync(id, ct));

    [HttpPut("clusters/{id:guid}")]
    [Authorize(Policy = Permissions.CaseEdit)]
    public async Task<ClusterDto> Update(Guid id, ClusterUpdateRequest request, CancellationToken ct)
    {
        await clusters.UpdateAsync(id, request.Status, request.Notes, RowVersion(request.RowVersion), ct);
        return ToDto(await clusters.GetAsync(id, ct));
    }

    [HttpPost("clusters/{id:guid}/members")]
    [Authorize(Policy = Permissions.CaseEdit)]
    public async Task<ClusterDto> Member(Guid id, ClusterMemberRequest request, CancellationToken ct)
    {
        await clusters.SetMemberAsync(id, request.CaseNumber, request.IsMember, ct);
        return ToDto(await clusters.GetAsync(id, ct));
    }

    [HttpGet("cases/map")]
    [Authorize(Policy = Permissions.CaseView)]
    public async Task<IReadOnlyList<CaseMapPointDto>> Map(Guid? analyteId, DateOnly? from, DateOnly? to, bool openOnly = true,
                                                          CancellationToken ct = default)
        => (await clusters.MapAsync(analyteId, from, to, openOnly, ct)).Select(r => new CaseMapPointDto(r.ExposureCaseId, r.CaseNumber,
            r.PersonId, $"{r.FirstName} {r.LastName}".Trim(), r.AnalyteCode, r.Population, r.Classification, r.CaseStatus, r.OpenedDate,
            r.HighestValue, r.HighestUnit, r.AddressLine1 is null ? null : $"{r.AddressLine1}, {r.City}", r.CountyFips, r.Latitude, r.Longitude,
            r.GeocodeQuality, r.OpenClusterCount > 0)).ToList();

    private static byte[] RowVersion(string? text)
    {
        try
        {
            return string.IsNullOrEmpty(text) ? [] : Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return [];
        }
    }

    private static ClusterDto ToDto(ClusterDetail d)
        => new(d.Cluster.ClusterId, d.Cluster.ClusterType, d.Cluster.Title, d.Cluster.ClusterStatus, d.Cluster.WindowStart, d.Cluster.WindowEnd,
            d.Cluster.Notes, d.Cluster.OwningTenantName, Convert.ToBase64String(d.Cluster.RowVersion),
            d.Members.Select(m => new ClusterMemberDto(m.ExposureCaseId, m.CaseNumber, m.PersonId, $"{m.FirstName} {m.LastName}".Trim(),
                m.AnalyteCode, m.Population, m.Classification, m.CaseStatus, m.OpenedDate, m.HighestValue, m.HighestUnit, m.JoinedDate,
                m.LeftDate, m.AddressLine1 is null ? null : $"{m.AddressLine1}{(m.Unit is null ? string.Empty : " " + m.Unit)}, {m.City}",
                m.Latitude, m.Longitude)).ToList(),
            d.Tasks.Select(t => new ClusterTaskDto(t.WorkTaskId, t.Title, t.Detail, t.DueDate, t.IsUrgent, t.AssignedToName)).ToList());
}

/// <summary>The standard report library: definitions, results with chart hints, and CSV or Excel exports.</summary>
[ApiController]
[Route("api/v1/reports")]
public sealed class ReportsController(ReportService reports, ICurrentSession session) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = Permissions.SignedIn)]
    public IReadOnlyList<ReportSummaryDto> Library()
        => reports.Available().Select(r => new ReportSummaryDto(r.Code, r.Title, r.Description, r.UsesAnalyte)).ToList();

    [HttpGet("{code}")]
    [Authorize(Policy = Permissions.SignedIn)]
    public async Task<ReportDto> Run(string code, DateOnly? from, DateOnly? to, Guid? analyteId, CancellationToken ct)
    {
        var r = await reports.RunAsync(code, from, to, analyteId, ct);
        var d = r.Definition;
        return new ReportDto(d.Code, d.Title, d.Description, r.FromDate, r.ToDate, r.AnalyteId, d.UsesAnalyte,
            session.Has(Permissions.DataExport),
            d.Columns.Select(c => new ReportColumnDto(c.Key, c.Label, c.Kind)).ToList(),
            new ReportChartDto(d.Chart.Kind, d.Chart.X, d.Chart.Group, d.Chart.Measures), r.Rows);
    }

    [HttpGet("{code}/export")]
    [Authorize(Policy = Permissions.DataExport)]
    public async Task<IActionResult> Export(string code, DateOnly? from, DateOnly? to, Guid? analyteId, string format = "csv",
                                            CancellationToken ct = default)
    {
        var r = await reports.ExportAsync(code, from, to, analyteId, ct);
        var headers = r.Definition.Columns.Select(c => c.Label).ToList();
        string name = $"{r.Definition.Code}_{r.FromDate:yyyyMMdd}_{r.ToDate:yyyyMMdd}";
        return string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase)
            ? File(TabularExport.Xlsx(r.Definition.Title, headers, r.Rows), TabularExport.XlsxType, name + ".xlsx")
            : File(TabularExport.Csv(headers, r.Rows), TabularExport.CsvType, name + ".csv");
    }
}
