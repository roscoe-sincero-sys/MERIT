using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Reference;
using Merit.Application.Security;
using Merit.Application.Services;
using Merit.Web.Infrastructure;
using Merit.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Merit.Web.Controllers;

/// <summary>Case pages (htmx): forms and activity logs.</summary>
[Route("cases")]
public sealed class CasesController(IClock clock, CaseService cases, PersonService people, IReferenceDataProvider reference, ICurrentSession session)
    : Controller
{
    [HttpGet("")]
    [Authorize(Policy = Permissions.CaseView)]
    public async Task<IActionResult> Index([FromQuery] CaseFilterModel filter, CancellationToken ct = default)
    {
        // Issue #50: filters, a text search and sorting, all in the query string.
        var rows = await cases.ListAsync(filter.ToQuery(), ct);
        var refData = await reference.GetAsync(ct);
        var analytes = refData.ActiveAnalytes.Select(a => new SelectOption(a.AnalyteId.ToString(), a.DisplayName, a.AnalyteId == filter.AnalyteId)).ToList();
        var owners = (await cases.OwnersAsync(ct)).Select(o => new SelectOption(o.TenantId.ToString(), o.TenantName, o.TenantId == filter.Owner)).ToList();
        var users = (await cases.AssignableUsersAsync(ct))
            .Select(u => new SelectOption(u.AppUserId.ToString(), u.DisplayName, u.AppUserId.ToString() == filter.AssignedFilter)).ToList();
        return this.PageOrPartial("Index", "_CaseList", new CaseListModel(rows, filter, rows.FirstOrDefault()?.TotalCount ?? 0, analytes, owners, users));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Permissions.CaseView)]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
        => this.PageOrPartial("Details", "_CaseBody", await PageAsync(id, null, ct));

    [HttpPost("{id:guid}/update")]
    [Authorize(Policy = Permissions.CaseEdit)]
    public async Task<IActionResult> Update(Guid id, Guid? assignedToUserId, string exposureCategory, string caseStatus, string rowVersion,
                                            CancellationToken ct)
    {
        try
        {
            await cases.UpdateAsync(id, assignedToUserId, exposureCategory, caseStatus, Options.FromHex(rowVersion) ?? [], ct);
        }
        catch (ValidationException ex)
        {
            return this.Invalid("_CaseBody", (await PageAsync(id, null, ct)) with { Draft = Draft("update") }, ex);
        }
        Response.Announce("Case updated.");
        return PartialView("_CaseBody", await PageAsync(id, "Case updated.", ct));
    }

    [HttpPost("{id:guid}/override")]
    [Authorize(Policy = Permissions.CaseEdit)]
    public async Task<IActionResult> Override(Guid id, string classification, string population, string? reason, string rowVersion,
                                              CancellationToken ct)
    {
        try
        {
            await cases.OverrideAsync(id, classification, population, reason, Options.FromHex(rowVersion) ?? [], ct);
        }
        catch (ValidationException ex)
        {
            var model = (await PageAsync(id, null, ct)) with { Draft = Draft("override") };
            return this.Invalid("_CaseBody", model, ex);
        }

        Response.Announce("Classification overridden; the computed value is kept beside it.");
        return PartialView("_CaseBody", await PageAsync(id, null, ct));
    }

    [HttpPost("{id:guid}/activities")]
    [Authorize(Policy = Permissions.CaseEdit)]
    public async Task<IActionResult> AddActivity(Guid id, string activityType, DateOnly? activityDate, Guid? contactPersonId, string? outcome,
                                                 string? note, CancellationToken ct)
    {
        string? message;
        try
        {
            message = await cases.LogActivityAsync(new ActivityInput(id, activityType,
                activityDate ?? clock.Today, contactPersonId, outcome, note), ct);
        }
        catch (ValidationException ex)
        {
            return this.Invalid("_CaseBody", (await PageAsync(id, null, ct)) with { Draft = Draft("activity") }, ex);
        }

        Response.Announce("Activity logged. " + message);
        return PartialView("_CaseBody", await PageAsync(id, message, ct));
    }

    [HttpPost("{id:guid}/close")]
    [Authorize(Policy = Permissions.CaseEdit)]
    public async Task<IActionResult> Close(Guid id, string? closureReason, DateOnly? closedDate, string? note, string rowVersion,
                                           CancellationToken ct)
    {
        try
        {
            await cases.CloseAsync(id, closureReason, closedDate, note, Options.FromHex(rowVersion) ?? [], ct);
        }
        catch (ValidationException ex)
        {
            return this.Invalid("_CaseBody", (await PageAsync(id, null, ct)) with { Draft = Draft("close") }, ex);
        }

        Response.Announce("Case closed.");
        return PartialView("_CaseBody", await PageAsync(id, "Case closed.", ct));
    }

    /// <summary>The investigation request form: the person's places and jobs as targets, and any open investigation of each.</summary>
    [HttpGet("{id:guid}/investigations/new")]
    [Authorize(Policy = Permissions.InvestigationEdit)]
    public async Task<IActionResult> NewInvestigation(Guid id, CancellationToken ct)
    {
        var detail = await cases.GetAsync(id, ct);
        return PartialView("_InvestigationRequest", await RequestFormAsync(detail, new InvestigationRequestModel(), ct));
    }

    [HttpPost("{id:guid}/investigations")]
    [Authorize(Policy = Permissions.InvestigationEdit)]
    public async Task<IActionResult> RequestInvestigation(Guid id, InvestigationRequestModel form, CancellationToken ct)
    {
        var detail = await cases.GetAsync(id, ct);
        if (string.IsNullOrEmpty(form.Target))
        {
            ModelState.AddModelError(nameof(form.Target), "Choose what to investigate.");
            return this.Invalid("_InvestigationRequest", await RequestFormAsync(detail, form, ct));
        }

        // target is "loc:<locationId>" or "job:<employmentId>:<organizationId>:<worksiteLocationId>"
        var parts = form.Target.Split(':');
        string type = parts[0] == "job" ? "workplace" : form.InvestigationType;
        Guid? locationId = parts[0] == "loc" ? Guid.Parse(parts[1]) : null;
        Guid? employmentId = parts[0] == "job" ? Guid.Parse(parts[1]) : null;
        Guid? orgId = parts[0] == "job" ? Guid.Parse(parts[2]) : null;
        Guid? worksite = parts[0] == "job" && parts.Length > 3 && Guid.TryParse(parts[3], out var w) ? w : null;

        if (!form.Checked && form.JoinInvestigationId is null)
        {
            // Before creating one, offer any open investigation of the same target, so siblings share one investigation.
            var existing = await cases.FindOpenInvestigationsAsync(type, locationId, orgId, worksite, ct);
            if (existing.Count > 0)
            {
                form.Existing = existing;
                form.Checked = true;
                return PartialView("_InvestigationRequest", await RequestFormAsync(detail, form, ct));
            }
        }

        var invId = await cases.RequestInvestigationAsync(new InvestigationRequest(id, type, locationId, orgId, worksite, employmentId,
            form.JoinInvestigationId), ct);
        Response.Headers["HX-Redirect"] = Url.Action("Details", "Investigations", new { id = invId });
        return Ok();
    }

    private async Task<InvestigationRequestModel> RequestFormAsync(CaseDetail detail, InvestigationRequestModel form, CancellationToken ct)
    {
        var places = await people.GetPageAsync(detail.Case.PersonId, ct);
        var targets = places.Places.Where(p => p.EndDate is null || p.EndDate >= clock.Today.AddDays(-365))
            .Select(p => new SelectOption($"loc:{p.LocationId}", $"{Options.Humanize(p.Role)}: {p.AddressLabel}", form.Target == $"loc:{p.LocationId}"))
            .ToList();
        targets.AddRange(places.Employment.Select(e => new SelectOption($"job:{e.EmploymentId}:{e.OrganizationId}:{e.WorksiteLocationId}",
            $"Workplace: {e.OrganizationName}{(e.WorksiteAddress is null ? string.Empty : " - " + e.WorksiteAddress)}",
            form.Target?.StartsWith($"job:{e.EmploymentId}", StringComparison.Ordinal) == true)));
        form.ExposureCaseId = detail.Case.ExposureCaseId;
        form.CaseNumber = detail.Case.CaseNumber;
        form.Targets = targets;
        return form;
    }

    private FormDraft Draft(string form) => new(form, Request.Form.ToDictionary(p => p.Key, p => p.Value.ToString()));

    private async Task<CasePageModel> PageAsync(Guid id, string? message, CancellationToken ct)
    {
        var detail = await cases.GetAsync(id, ct);
        var refData = await reference.GetAsync(ct);
        var users = new[] { new SelectOption(string.Empty, "- Unassigned -", detail.Case.AssignedToUserId is null) }
            .Concat((await cases.AssignableUsersAsync(ct)).Select(u =>
                new SelectOption(u.AppUserId.ToString(), u.DisplayName, u.AppUserId == detail.Case.AssignedToUserId)))
            .ToList();
        return new CasePageModel(detail, users, refData.Choices("closureReason", null, includeBlank: true),
            refData.Choices("activityType", "contactAttempt"), refData.Choices("activityOutcome", null, includeBlank: true),
            session.Has(Permissions.CaseEdit) && detail.Case.CaseStatus != "closed", session.Has(Permissions.InvestigationEdit), message);
    }
}

/// <summary>Investigation pages (htmx): target, status, dates, findings, regulatory action, environmental samples.</summary>
[Route("investigations")]
public sealed class InvestigationsController(InvestigationService investigations, CaseService cases, IReferenceDataProvider reference,
                                             ICurrentSession session) : Controller
{
    [HttpGet("")]
    [Authorize(Policy = Permissions.CaseView)]
    public async Task<IActionResult> Index(bool openOnly = true, int page = 1, CancellationToken ct = default)
        => this.PageOrPartial("Index", "_InvestigationList",
            new InvestigationListModel(await investigations.ListAsync(openOnly, false, Math.Max(page, 1), ct), openOnly, Math.Max(page, 1)));

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Permissions.CaseView)]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
        => this.PageOrPartial("Details", "_InvestigationBody", await PageAsync(id, ct));

    [HttpPost("{id:guid}")]
    [Authorize(Policy = Permissions.InvestigationEdit)]
    public async Task<IActionResult> Save(Guid id, string investigationStatus, Guid? assignedToUserId, DateOnly? scheduledDate,
                                          DateOnly? completedDate, string? findings, string? regulatoryAction, DateOnly? referralDate,
                                          string? referralOutcome, string rowVersion, CancellationToken ct)
    {
        try
        {
            await investigations.SaveAsync(new InvestigationUpdate(id, investigationStatus, assignedToUserId, scheduledDate, completedDate,
                findings, regulatoryAction, referralDate, referralOutcome, Options.FromHex(rowVersion) ?? []), ct);
        }
        catch (ValidationException ex)
        {
            foreach (var (k, v) in ex.Errors)
            {
                ModelState.AddModelError(k, string.Join(" ", v));
            }

            return this.Invalid("_InvestigationBody", (await PageAsync(id, ct)) with { Draft = Draft("save") });
        }

        Response.Announce("Investigation saved.");
        return PartialView("_InvestigationBody", await PageAsync(id, ct));
    }

    [HttpPost("{id:guid}/samples")]
    [Authorize(Policy = Permissions.InvestigationEdit)]
    public async Task<IActionResult> AddSample(Guid id, string sampleType, string? roomOrComponent, string? surface, decimal? sampleValue,
                                               string? unit, DateOnly? sampleDate, string? notes, CancellationToken ct)
    {
        if (sampleValue is null || sampleDate is null || string.IsNullOrWhiteSpace(unit))
        {
            ModelState.AddModelError("sampleValue", "Enter the value, unit and sample date.");
            return this.Invalid("_InvestigationBody", (await PageAsync(id, ct)) with { Draft = Draft("sample") });
        }

        bool? exceeds;
        try
        {
            exceeds = await investigations.AddSampleAsync(new EnvironmentalSampleSave(Guid.Empty, id, sampleType,
                roomOrComponent, surface, sampleValue.Value, unit, sampleDate.Value, notes, false), ct);
        }
        catch (ValidationException ex)
        {
            return this.Invalid("_InvestigationBody", (await PageAsync(id, ct)) with { Draft = Draft("sample") }, ex);
        }
        Response.Announce(exceeds switch
        {
            true => "Sample saved: exceeds the standard.",
            false => "Sample saved: below the standard.",
            _ => "Sample saved: no standard applies to this sample type and unit.",
        });
        return PartialView("_InvestigationBody", await PageAsync(id, ct));
    }

    private FormDraft Draft(string form) => new(form, Request.Form.ToDictionary(p => p.Key, p => p.Value.ToString()));

    private async Task<InvestigationPageModel> PageAsync(Guid id, CancellationToken ct)
    {
        var detail = await investigations.GetAsync(id, ct);
        var refData = await reference.GetAsync(ct);
        var users = new[] { new SelectOption(string.Empty, "- Unassigned -", detail.Investigation.AssignedToUserId is null) }
            .Concat((await cases.AssignableUsersAsync(ct)).Select(u =>
                new SelectOption(u.AppUserId.ToString(), u.DisplayName, u.AppUserId == detail.Investigation.AssignedToUserId)))
            .ToList();
        return new InvestigationPageModel(detail, users, Options.Fixed(detail.Investigation.InvestigationStatus, Options.InvestigationStatuses),
            refData.Choices("regulatoryAction", detail.Investigation.RegulatoryAction, includeBlank: true),
            refData.Choices("environmentalSampleType", "dustWipe"), session.Has(Permissions.InvestigationEdit));
    }
}
