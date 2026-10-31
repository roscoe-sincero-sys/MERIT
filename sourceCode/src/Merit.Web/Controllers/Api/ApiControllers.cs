using System.Text.Json;
using Merit.Application.Data;
using Merit.Web.Infrastructure;
using Merit.Application.Intake;
using Merit.Application.Reference;
using Merit.Application.Security;
using Merit.Application.Services;
using Merit.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Merit.Web.Controllers.Api;

// JSON API under /api/v1 for the Angular client and outside integrations. Controllers marked [ApiController], not
// minimal APIs (architecture rule 5); errors are RFC 9457 problem details (MeritExceptionFilter); every action carries a
// permission policy. Responses are contract types from Merit.Contracts, never application or domain types.

[ApiController]
[Route("api/v1/me")]
public sealed class MeController(ICurrentSession session) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = Permissions.SignedIn)]
    public MeDto Get() => new(session.UserName ?? string.Empty, session.DisplayName ?? string.Empty, session.ActingTenantName,
        session.Permissions.Order(StringComparer.Ordinal).ToList(), JurisdictionTimeZones.IanaId(session.TimeZoneId));
}

[ApiController]
[Route("api/v1")]
public sealed class QueuesController(QueueService queues, CaseService cases, ICurrentSession session) : ControllerBase
{
    [HttpGet("queues")]
    [Authorize(Policy = Permissions.TaskView)]
    public async Task<IReadOnlyList<QueueCountDto>> Counts(CancellationToken ct)
        => (await queues.CountsAsync(ct)).Select(q => new QueueCountDto(q.QueueCode, q.QueueName, q.OpenCount, q.UrgentCount,
            q.OverdueCount, q.MineCount)).ToList();

    /// <summary>One page of tasks: server-side paging, sorting and filtering for the shared data grid.</summary>
    [HttpGet("tasks")]
    [Authorize(Policy = Permissions.TaskView)]
    public async Task<PagedResult<WorkTaskDto>> Tasks(string? queue, string? status = "open", bool mine = false, bool urgent = false,
                                                      string sort = "dueDate", bool desc = false, int page = 1, int pageSize = 50,
                                                      CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 500);
        var result = await Paging.ReadAsync(page, pageSize, p => queues.ListAsync(new WorkTaskQuery(string.IsNullOrEmpty(queue) ? null : queue,
            string.IsNullOrEmpty(status) ? null : status, mine, urgent, sort, desc, p, pageSize), ct), r => r.TotalCount);
        var rows = result.Rows;
        return new PagedResult<WorkTaskDto>(rows.Select(r => new WorkTaskDto(r.WorkTaskId, r.QueueCode, r.Title, r.Detail, r.IsUrgent,
                r.DueDate, r.TaskStatus, r.CreatedDateUtc, r.PersonId,
                r.LastName is null ? null : $"{r.LastName}, {r.FirstName}", r.ExposureCaseId, r.CaseNumber, r.AssignedToUserId,
                r.AssignedToName, r.CanEdit && session.Has(Permissions.TaskEdit))).ToList(),
            result.Total, result.Page, pageSize);
    }

    [HttpPost("tasks/bulk")]
    [Authorize(Policy = Permissions.TaskEdit)]
    public async Task<BulkActionResponse> Bulk(BulkTaskActionRequest request, CancellationToken ct)
        => new(await queues.BulkAsync(request.TaskIds, request.Action, request.AssignToUserId, request.Note, ct));

    [HttpGet("users/assignable")]
    [Authorize(Policy = Permissions.TaskView)]
    public async Task<IReadOnlyList<AssignableUserDto>> AssignableUsers(CancellationToken ct)
        => (await cases.AssignableUsersAsync(ct)).Select(u => new AssignableUserDto(u.AppUserId, u.DisplayName)).ToList();
}

/// <summary>The words for stored codes and file fields (issues #37 and #38), so the client shows the same names as the pages.</summary>
[ApiController]
[Route("api/v1/reference")]
public sealed class ReferenceLabelsController(IReferenceDataProvider reference, ConfigService config) : ControllerBase
{
    [HttpGet("labels")]
    [Authorize(Policy = Permissions.SignedIn)]
    public async Task<LabelsDto> Labels(CancellationToken ct)
        => new((await reference.GetAsync(ct)).Names.Codes, DisplayNames.Fields, DisplayNames.StagingFields.Select(f => f.Key).ToList());

    /// <summary>The jurisdiction's county outlines, the maps' context layer (issue #48). Served from MERIT; no tile service.</summary>
    [HttpGet("boundaries")]
    [Authorize(Policy = Permissions.SignedIn)]
    public async Task<IReadOnlyList<BoundaryDto>> Boundaries(CancellationToken ct)
        => (await config.BoundariesAsync(ct)).Select(b =>
        {
            using var geometry = JsonDocument.Parse(b.Boundary);
            return new BoundaryDto(b.CountyFips, b.SubJurisdictionName, geometry.RootElement.Clone(), b.LabelLatitude, b.LabelLongitude);
        }).ToList();
}

[ApiController]
[Route("api/v1/held")]
public sealed class HeldRecordsController(IntakeService intake, IReferenceDataProvider reference) : ControllerBase
{
    [HttpGet("groups")]
    [Authorize(Policy = Permissions.ResultReleaseHold)]
    public async Task<IReadOnlyList<HeldGroupDto>> Groups(Guid? batch = null, CancellationToken ct = default)
        => (await intake.HeldGroupsAsync(batch, ct)).Select(g => new HeldGroupDto(g.ReasonCode, g.ReportedValue, g.SenderOrganizationId, g.SenderName, g.HeldRowCount,
            g.UrgentCount, g.OldestHeldUtc, g.BatchCount, g.SampleMessage)).ToList();

    [HttpGet("overview")]
    [Authorize(Policy = Permissions.ResultReleaseHold)]
    public async Task<HeldOverviewDto> Overview(Guid? batch = null, CancellationToken ct = default)
    {
        var overview = await intake.HeldOverviewAsync(batch, ct);
        return new HeldOverviewDto(overview.Groups.Select(g => new HeldGroupDto(g.ReasonCode, g.ReportedValue,
            g.SenderOrganizationId, g.SenderName, g.HeldRowCount, g.UrgentCount, g.OldestHeldUtc, g.BatchCount, g.SampleMessage)).ToList(),
            overview.Summary.TotalCount, overview.Summary.BatchFileName);
    }

    [HttpGet("summary")]
    [Authorize(Policy = Permissions.ResultReleaseHold)]
    public async Task<HeldSummaryDto> Summary(Guid? batch = null, CancellationToken ct = default)
    {
        var summary = await intake.HeldSummaryAsync(batch, ct);
        return new HeldSummaryDto(summary.TotalCount, summary.BatchFileName);
    }

    /// <summary>
    /// Held rows. <c>exact=true</c> with <c>reason</c>, <c>value</c> and <c>sender</c> lists one group from <c>groups</c>,
    /// where a missing value or sender means the group has none.
    /// </summary>
    [HttpGet("rows")]
    [Authorize(Policy = Permissions.ResultReleaseHold)]
    public async Task<PagedResult<HeldRowDto>> Rows(string? reason, string? value, Guid? sender, bool exact = false, Guid? batch = null,
                                                    int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 500);
        var result = await Paging.ReadAsync(page, pageSize, p => intake.HeldRowsAsync(reason, value, sender, exact, batch, p, pageSize, ct), r => r.TotalCount);
        var rows = result.Rows;
        var names = (await reference.GetAsync(ct)).Names;
        return new PagedResult<HeldRowDto>(rows.Select(r => new HeldRowDto(r.StagingRowId, r.IntakeBatchId, r.FileName, r.SenderName,
                r.RowNumber, Parse(r.ParsedJson), Parse(r.CorrectedJson), r.IsUrgent, r.HeldSinceUtc, r.AssignedToName, ReasonText(r.Reasons, names), ReasonCodes(r.Reasons))).ToList(),
            result.Total, result.Page, pageSize);
    }

    [HttpPut("rows/{id:guid}/correction")]
    [Authorize(Policy = Permissions.ResultReleaseHold)]
    public async Task<IActionResult> Correct(Guid id, HeldCorrectionRequest request, CancellationToken ct)
    {
        await intake.CorrectAsync(id, request.Corrections, ct);
        return NoContent();
    }

    [HttpPost("release")]
    [Authorize(Policy = Permissions.ResultReleaseHold)]
    public async Task<BulkActionResponse> Release(HeldActionRequest request, CancellationToken ct)
        => new(await intake.ReleaseAsync(request.StagingRowIds, ct));

    [HttpPost("reject")]
    [Authorize(Policy = Permissions.ResultReleaseHold)]
    public async Task<BulkActionResponse> Reject(HeldActionRequest request, CancellationToken ct)
        => new(await intake.RejectAsync(request.StagingRowIds, request.Reason, ct));

    private static IReadOnlyDictionary<string, string?> Parse(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? new Dictionary<string, string?>()
            : JsonSerializer.Deserialize<Dictionary<string, string?>>(json) ?? [];

    /// <summary>The codes alone ("uncertainMatch"), so the client can offer the step that clears each one (issue #42).</summary>
    private static IReadOnlyList<string> ReasonCodes(string? reasons)
        => reasons is null ? [] : reasons.Split(" | ").Select(part =>
        {
            int colon = part.IndexOf(": ", StringComparison.Ordinal);
            return colon > 0 ? part[..colon] : part;
        }).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>"uncertainMatch: …" as "Uncertain person match: …" (issue #37); the procedure joins a row's reasons with " | ".</summary>
    private static string? ReasonText(string? reasons, DisplayNames names)
        => reasons is null ? null : string.Join(" | ", reasons.Split(" | ").Select(part =>
        {
            int colon = part.IndexOf(": ", StringComparison.Ordinal);
            return colon > 0 ? $"{names.Of(part[..colon])}: {part[(colon + 2)..]}" : part;
        }));
}

[ApiController]
[Route("api/v1")]
public sealed class ResultsApiController(ResultService results, PersonService people, IReferenceDataProvider reference, AuditService audit)
    : ControllerBase
{
    [HttpGet("analytes")]
    [Authorize(Policy = Permissions.ResultView)]
    public async Task<IReadOnlyList<AnalyteOptionDto>> Analytes(CancellationToken ct)
        => (await reference.GetAsync(ct)).ActiveAnalytes.Select(a => new AnalyteOptionDto(a.AnalyteId, a.Code, a.DisplayName)).ToList();

    /// <summary>The results line list, server-side paged, sorted and filtered.</summary>
    [HttpGet("results")]
    [Authorize(Policy = Permissions.ResultView)]
    public async Task<PagedResult<ResultListItemDto>> List(Guid? analyteId, DateOnly? from, DateOnly? to, string? population,
                                                           decimal? minValue, bool unmatched = false, string sort = "collectedDate",
                                                           bool desc = true, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 500);
        var rows = await results.ListAsync(new ResultListQuery(analyteId, from, to, string.IsNullOrEmpty(population) ? null : population,
            minValue, null, unmatched, sort, desc, page, pageSize), ct);
        return new PagedResult<ResultListItemDto>(rows.Select(r => new ResultListItemDto(r.LabResultId, r.PersonId,
                r.LastName is null ? null : $"{r.LastName}, {r.FirstName}", r.Dob, r.AnalyteCode, r.Specimen, r.CollectedDate, r.ReceivedUtc,
                r.Operator, r.ValueCanonical, r.UnitCanonical, r.SampleType, r.TestMethod, r.Population, r.ResultStatus, r.ResultRowStatus,
                r.IsIndeterminate, r.LabName)).ToList(),
            rows.FirstOrDefault()?.TotalCount ?? 0, page, pageSize);
    }

    /// <summary>Results, address changes, job changes and case events on one time axis, for the person timeline.</summary>
    [HttpGet("people/{id:guid}/timeline")]
    [Authorize(Policy = Permissions.PersonView)]
    public async Task<PersonTimelineDto> Timeline(Guid id, CancellationToken ct)
    {
        var page = await people.GetPageAsync(id, ct);
        var events = new List<TimelineEventDto>();
        events.AddRange(page.Results.Where(r => r.ResultRowStatus == "valid").Select(r => new TimelineEventDto(r.CollectedDate, null, "result",
            $"{r.AnalyteCode} {(r.Operator == "=" ? string.Empty : r.Operator)}{r.ValueCanonical:0.##} {r.UnitCanonical}",
            r.LabName, r.ValueCanonical, r.UnitCanonical, r.SampleType, r.AnalyteCode)));
        events.AddRange(page.Places.Select(p => new TimelineEventDto(p.StartDate, p.EndDate, "place",
            $"{(p.IsPrimary ? "Primary residence" : p.Role)}: {p.AddressLabel}", p.CountyName, null, null, null, null)));
        events.AddRange(page.Employment.Where(e => e.StartDate is not null).Select(e => new TimelineEventDto(e.StartDate!.Value, e.EndDate, "job",
            $"{e.JobTitle ?? "Job"} at {e.OrganizationName}", e.MetalTasks, null, null, null, null)));
        events.AddRange(page.Cases.Select(c => new TimelineEventDto(c.OpenedDate, c.ClosedDate, "case",
            $"Case {c.CaseNumber} ({c.Classification})", c.CaseStatus, null, null, null, c.AnalyteCode)));
        events.AddRange(page.Pregnancies.Select(p => new TimelineEventDto(p.StartDate, p.ActualEndDate ?? p.ExpectedEndDate, "pregnancy",
            "Pregnancy", p.Outcome, null, null, null, null)));
        await audit.ViewedAsync("person", id, ct);
        var name = $"{page.Header.Person.FirstName} {page.Header.Person.LastName}".Trim();
        return new PersonTimelineDto(id, name, events.OrderBy(e => e.Date).ToList(), []);
    }
}
