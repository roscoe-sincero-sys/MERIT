using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Security;
using Merit.Application.Services;
using Merit.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Merit.Web.Controllers;

public sealed class ContactCorrectionsModel
{
    public ContactCorrectionInput Request { get; set; } = new();
    public IReadOnlyList<ContactCorrectionRow> Rows { get; set; } = [];
    public bool PatientContext { get; set; }
}

[Authorize(Policy = Permissions.PersonEdit)]
public sealed class ContactCorrectionsController(ContactCorrectionService corrections) : Controller
{
    [HttpGet("contact-corrections")]
    public async Task<IActionResult> Index(Guid? personId, Guid? caregiverId, CancellationToken ct)
        => await Page(new() { Request = new() { PersonId = personId ?? Guid.Empty, CaregiverId = caregiverId ?? Guid.Empty }, PatientContext = personId is not null }, ct);

    [HttpPost("contact-corrections")]
    public async Task<IActionResult> Create(ContactCorrectionsModel model, CancellationToken ct)
    {
        model.PatientContext = true;
        try
        {
            if (ModelState.IsValid)
            {
                await corrections.RequestAsync(model.Request, ct);
                TempData["CorrectionMessage"] = "Correction requested. The caregiver's owning team can review it; current contact records have not been changed.";
                return RedirectToAction(nameof(Index), new { personId = model.Request.PersonId });
            }
        }
        catch (ValidationException ex) { foreach (var error in ex.Errors) foreach (var message in error.Value) ModelState.AddModelError("Request." + error.Key, message); }
        Response.StatusCode = 422;
        return await Page(model, ct);
    }
    [HttpPost("contact-corrections/{id:guid}/review")]
    public async Task<IActionResult> Review(Guid id, string requestStatus, string reviewNote, string rowVersion, Guid? personId, CancellationToken ct)
    {
        try
        {
            if (ModelState.IsValid)
            {
                if (rowVersion.Length != 16 || !rowVersion.All(Uri.IsHexDigit)) throw new ValidationException("rowVersion", "Reload the request before reviewing it.");
                await corrections.ReviewAsync(id, requestStatus, reviewNote, Options.FromHex(rowVersion), ct);
                TempData["CorrectionMessage"] = "Review recorded. The request and its original evidence remain in history.";
                return RedirectToAction(nameof(Index), new { personId });
            }
        }
        catch (MeritException ex) when (ex is ValidationException or BusinessRuleException or ConcurrencyException) { ModelState.AddModelError("", ex.Message); }
        Response.StatusCode = 422;
        return await Page(new() { PatientContext = personId is not null, Request = new() { PersonId = personId ?? Guid.Empty } }, ct);
    }
    private async Task<IActionResult> Page(ContactCorrectionsModel model, CancellationToken ct)
    {
        model.Rows = await corrections.ListAsync(model.PatientContext ? model.Request.PersonId : null, ct);
        return View("Index", model);
    }
}
