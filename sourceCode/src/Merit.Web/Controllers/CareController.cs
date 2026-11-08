using System.Text;
using System.Text.Json;
using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Reference;
using Merit.Application.Security;
using Merit.Application.Services;
using Merit.Domain.Ids;
using Merit.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Merit.Web.Controllers;

[Authorize(Policy = Permissions.PersonView)]
public sealed class CareController(CareService care, PersonService people, IReferenceDataProvider reference) : Controller
{
    [HttpGet("people/{personId:guid}/care")]
    public async Task<IActionResult> Index(Guid personId, Guid? caseId, Guid? completing, int activityPage = 1, int documentPage = 1, int notificationPage = 1, CancellationToken ct = default)
        => await Workspace(personId, caseId, completing, ct, activityPage, documentPage, notificationPage);

    private async Task<IActionResult> Workspace(Guid personId, Guid? caseId, Guid? completing, CancellationToken ct, int ap = 1, int dp = 1, int np = 1)
    {
        var page = await care.PageAsync(personId, caseId, ap, dp, np, ct);
        NotificationRow? obligation = null;
        if (completing is not null)
        {
            // Completion links can point to a later page; never trust hidden result/recipient fields as evidence.
            for (int p = 1; ; p++)
            {
                var rows = p == np ? page.Notifications : (await care.PageAsync(personId, caseId, ap, dp, p, ct)).Notifications;
                obligation = rows.FirstOrDefault(r => r.NotificationObligationId == completing);
                if (obligation is not null || rows.Count < 50) break;
            }
            if (obligation is null) throw new NotFoundException();
        }
        return View("Index", new CareWorkspaceModel(page, await people.GetPageAsync(personId, ct), obligation));
    }

    [HttpPost("people/{personId:guid}/care/activity")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public async Task<IActionResult> Activity(Guid personId, Guid? caseId, Guid personActivityId, string activityType, DateOnly activityDate,
        string audience, string channel, string outcome, Guid? contactPersonId, string? recipientLabel, string? note,
        Guid? labResultId, Guid? notificationObligationId, bool confirmed, bool completeMatching, CancellationToken ct)
    {
        try
        {
            if (!ModelState.IsValid) return await Workspace(personId, caseId, notificationObligationId, ct);
            var followUp = await care.RecordAsync(new(personActivityId, personId, activityType, activityDate, audience, channel, outcome,
                contactPersonId, recipientLabel, note, caseId, labResultId, notificationObligationId, completeMatching), confirmed, ct);
            TempData["CareMessage"] = "Activity recorded. " + followUp;
            return RedirectToAction(nameof(Index), new { personId, caseId });
        }
        catch (MeritException ex) when (FormError(ex)) { return await Workspace(personId, caseId, notificationObligationId, ct); }
    }

    [HttpPost("people/{personId:guid}/care/link")]
    [Authorize(Policy = Permissions.CaseEdit)]
    public async Task<IActionResult> Link(Guid personId, Guid exposureCaseId, Guid activityId, CancellationToken ct)
    {
        try { if (ModelState.IsValid) await care.LinkAsync(personId, exposureCaseId, activityId, ct); }
        catch (MeritException ex) when (FormError(ex)) { return await Workspace(personId, null, null, ct); }
        if (!ModelState.IsValid) return await Workspace(personId, null, null, ct);
        TempData["CareMessage"] = "Activity linked to the case; its original date and author were retained.";
        return RedirectToAction(nameof(Index), new { personId });
    }

    [HttpPost("people/{personId:guid}/care/documents")]
    [Authorize(Policy = Permissions.DocumentEdit)]
    [RequestSizeLimit(11 * 1024 * 1024)]
    public async Task<IActionResult> Upload(Guid personId, Guid? caseId, Guid personDocumentId, Guid? supersedesDocumentId,
        string title, string category, DateOnly documentDate, string source, string? description, IFormFile? file, CancellationToken ct)
    {
        try
        {
            if (file is null || file.Length is 0 or > PatientDocumentValidator.MaximumBytes)
                throw new ValidationException("file", "Choose a supported file up to 10 MB.");
            if (!ModelState.IsValid) throw new ValidationException("file", "Check the document fields and choose the file again.");
            using var bytes = new MemoryStream();
            await file.CopyToAsync(bytes, ct);
            await care.UploadAsync(personDocumentId, personId, caseId, supersedesDocumentId, title, file.FileName,
                category, documentDate, source, description, bytes.ToArray(), ct);
            if (Request.Headers["X-Merit-Upload"] == "1") return Ok(new { message = "Document saved. Refresh the list to see it." });
            TempData["CareMessage"] = "Document saved; earlier revisions retained.";
            return RedirectToAction(nameof(Index), new { personId, caseId });
        }
        catch (MeritException ex) when (FormError(ex))
        {
            if (Request.Headers["X-Merit-Upload"] == "1") return UnprocessableEntity(new { message = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)) });
            return await Workspace(personId, caseId, null, ct);
        }
    }

    [HttpGet("people/{personId:guid}/care/documents/{id:guid}")]
    [Authorize(Policy = Permissions.DocumentView)]
    public async Task<IActionResult> Download(Guid personId, Guid id, CancellationToken ct)
    {
        var row = await care.DocumentAsync(personId, id, ct);
        Response.Headers.CacheControl = "no-store"; Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(row.Content, row.ContentType, row.FileName);
    }

    [HttpGet("people/{personId:guid}/care/documents/{id:guid}/preview")]
    [Authorize(Policy = Permissions.DocumentView)]
    public async Task<IActionResult> Preview(Guid personId, Guid id, CancellationToken ct)
    {
        var row = await care.DocumentAsync(personId, id, ct);
        Response.Headers.CacheControl = "no-store";
        return View("Preview", row);
    }

    // Only raster images are embedded. PDF and XLSX originals download into the user's chosen viewer.
    [HttpGet("people/{personId:guid}/care/documents/{id:guid}/image")]
    [Authorize(Policy = Permissions.DocumentView)]
    public async Task<IActionResult> Image(Guid personId, Guid id, CancellationToken ct)
    {
        var row = await care.DocumentAsync(personId, id, ct);
        if (!row.ContentType.StartsWith("image/", StringComparison.Ordinal)) return NotFound();
        Response.Headers.CacheControl = "no-store"; Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(row.Content, row.ContentType);
    }

    [HttpPost("people/{personId:guid}/care/review")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public async Task<IActionResult> Review(Guid personId, Guid? caseId, Guid notificationObligationId, bool applies, string reason, CancellationToken ct)
    {
        try { if (ModelState.IsValid) await care.ReviewAsync(personId, notificationObligationId, applies, reason, ct); }
        catch (MeritException ex) when (FormError(ex)) { return await Workspace(personId, caseId, null, ct); }
        if (!ModelState.IsValid) return await Workspace(personId, caseId, null, ct);
        TempData["CareMessage"] = "Eligibility decision saved with your reason.";
        return RedirectToAction(nameof(Index), new { personId, caseId });
    }
    [HttpPost("people/{personId:guid}/care/withdraw")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public async Task<IActionResult> Withdraw(Guid personId, Guid? caseId, Guid notificationObligationId, string reason, CancellationToken ct)
    {
        try { if (ModelState.IsValid) await care.WithdrawAsync(personId, notificationObligationId, reason, ct); }
        catch (MeritException ex) when (FormError(ex)) { return await Workspace(personId, caseId, null, ct); }
        if (!ModelState.IsValid) return await Workspace(personId, caseId, null, ct);
        TempData["CareMessage"] = "Evidence withdrawn; all obligations using this evidence reopened. The original activity remains in history.";
        return RedirectToAction(nameof(Index), new { personId, caseId });
    }
    [HttpPost("people/{personId:guid}/care/evaluate")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public async Task<IActionResult> Evaluate(Guid personId, Guid? caseId, CancellationToken ct)
    {
        TempData["CareMessage"] = $"Created {await care.EvaluatePatientAsync(personId, ct)} notification obligations from approved rules.";
        return RedirectToAction(nameof(Index), new { personId, caseId });
    }

    [HttpGet("notifications")]
    public async Task<IActionResult> Notifications(int page = 1, CancellationToken ct = default)
    {
        var result = await care.NotificationsAsync(page, ct);
        return View("Notifications", new NotificationReportModel(result.Rows, result.Page, await care.SummaryAsync(ct)));
    }

    [HttpGet("notifications.csv")]
    [Authorize(Policy = Permissions.DataExport)]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var rows = await care.ExportAsync(ct);
        static string Cell(string? value) => "\"" + ((value ?? "").Length > 0 && "=+-@\t\r\n".Contains(value![0]) ? "'" + value : value ?? "").Replace("\"", "\"\"") + "\"";
        var csv = new StringBuilder("Patient ID,Rule,Recipient,Receipt date,Due date,Time zone,Status,Completed date,Recipient name,Channel,Source result status\r\n");
        foreach (var r in rows) csv.AppendLine(string.Join(',', new[] { r.PersonId.ToString(), r.RuleName, r.RecipientKind, r.AnchorDate.ToString("yyyy-MM-dd"), r.DueDate.ToString("yyyy-MM-dd"), r.TimeZoneId, r.Status, r.CompletedDate?.ToString("yyyy-MM-dd"), r.RecipientName, r.EvidenceChannel, r.ResultRowStatus }.Select(Cell)));
        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", "notification-compliance.csv");
    }

    [HttpGet("config/notification-rules")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public Task<IActionResult> Policies(CancellationToken ct) => PolicyPage(ct);

    private async Task<IActionResult> PolicyPage(CancellationToken ct)
    {
        var r = await reference.GetAsync(ct);
        return View("Policies", new NotificationPolicyModel(await care.RulesAsync(ct), r.Set.AnalyteSpecimens.Where(s => s.IsActive)
            .Select(s => new SelectOption(s.AnalyteSpecimenId.ToString(), $"{r.Analyte(s.AnalyteId)?.DisplayName} / {s.Specimen} / {s.CanonicalUnit}")).ToList()));
    }

    [HttpPost("config/notification-rules")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> SavePolicy(Guid notificationRuleId, string ruleName, Guid analyteSpecimenId, string population,
        decimal minimumValue, decimal? maximumValue, int? minimumAgeMonths, int? maximumAgeMonths, string recipientKind,
        int dueInDays, string dayBasis, string timeZoneId, string calendarName, string? holidays, DateOnly effectiveFrom,
        DateOnly? effectiveTo, string approvalReference, bool approved, CancellationToken ct)
    {
        try
        {
            if (!ModelState.IsValid) return await PolicyPage(ct);
            var dates = (holidays ?? "").Split(['\r', '\n', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (dates.Any(d => !DateOnly.TryParseExact(d, "yyyy-MM-dd", out _))) throw new ValidationException("holidays", "Enter holiday dates as YYYY-MM-DD, one per line.");
            await care.SaveRuleAsync(new(notificationRuleId, ruleName, analyteSpecimenId, population, minimumValue, recipientKind,
                dueInDays, dayBasis, timeZoneId, calendarName, JsonSerializer.Serialize(dates.Distinct()), effectiveFrom,
                approvalReference, minimumAgeMonths, maximumAgeMonths, maximumValue, effectiveTo), approved, ct);
            TempData["CareMessage"] = "Approved rule activated for future results. Use Evaluate existing results to backfill received results in its effective period.";
            return RedirectToAction(nameof(Policies));
        }
        catch (MeritException ex) when (FormError(ex)) { return await PolicyPage(ct); }
    }

    [HttpPost("config/notification-rules/{id:guid}/evaluate")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> EvaluateRule(Guid id, CancellationToken ct)
    {
        await care.QueueEvaluationAsync(id, ct); TempData["CareMessage"] = "Evaluation queued. The worker creates only missing obligations; rerunning is safe.";
        return RedirectToAction(nameof(Policies));
    }
    [HttpPost("config/notification-rules/{id:guid}/retire")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Retire(Guid id, CancellationToken ct)
    {
        await care.RetireAsync(id, ct); TempData["CareMessage"] = "Rule retired. Existing obligations keep their original policy and deadline.";
        return RedirectToAction(nameof(Policies));
    }
    private bool FormError(MeritException ex)
    {
        if (ex is ValidationException v) foreach (var e in v.Errors) foreach (var message in e.Value) ModelState.AddModelError(e.Key, message);
        else if (ex is BusinessRuleException or ConcurrencyException) ModelState.AddModelError("", ex.Message);
        else return false;
        Response.StatusCode = 422;
        return true;
    }
}
