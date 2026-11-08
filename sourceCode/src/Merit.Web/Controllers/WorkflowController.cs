using System.Text.Json;
using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Reference;
using Merit.Application.Security;
using Merit.Application.Services;
using Merit.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Merit.Web.Controllers;

[Authorize(Policy = Permissions.SignedIn)]
public sealed class WorkflowController(WorkflowService workflows, PersonService people, IReferenceDataProvider reference, IClock clock) : Controller
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    [HttpGet("config/workflows")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Configuration(Guid? id, string? template, CancellationToken ct, bool copy = false)
    {
        var rows = await workflows.DefinitionsAsync(ct);
        var row = rows.FirstOrDefault(d => d.WorkflowDefinitionId == id);
        var draft = row is null ? new WorkflowDefinitionSave { WorkflowDefinitionId = Guid.NewGuid(), EffectiveFrom = clock.Today } : JsonSerializer.Deserialize<WorkflowDefinitionSave>(JsonSerializer.Serialize(row, JsonOptions), JsonOptions)!;
        if (copy && row is not null) { draft.WorkflowDefinitionId = Guid.NewGuid(); draft.Version++; draft.VersionStamp = null; }
        if (template == "outreach" && row is null)
        {
            draft.Name = "Lost-to-follow-up outreach review"; draft.TriggerEvent = "caseOpened"; draft.CompletionKind = "internal";
            draft.MinimumContactAttempts = 3; draft.BlocksClosure = true; draft.ClosureReason = "lostToFollowUp"; draft.AllowWaiver = true; draft.RequiresEligibilityReview = true;
            draft.Description = "Draft example only. Verify the program-approved policy and whether the patient moved and cannot be located before applying it. Record each unsuccessful contact with date, recipient and outcome. No jurisdiction rule is activated by this draft.";
        }
        return await ConfigPage(draft, rows, [], null, null, null, null, ct);
    }
    private async Task<IActionResult> ConfigPage(WorkflowDefinitionSave draft, IReadOnlyList<WorkflowDefinitionRow>? rows, IReadOnlyList<WorkflowPreviewRow> previews,
        Guid? sampleA, Guid? sampleB, Guid? caseA, Guid? caseB, CancellationToken ct)
    {
        var r = await reference.GetAsync(ct);
        return View("Configuration", new WorkflowConfigurationModel(draft, rows ?? await workflows.DefinitionsAsync(ct), previews,
            r.Set.AnalyteSpecimens.Select(s => new SelectOption(s.AnalyteSpecimenId.ToString(), $"{r.Analyte(s.AnalyteId)?.DisplayName} / {s.Specimen} / {s.CanonicalUnit}")).ToArray(), sampleA, sampleB, caseA, caseB));
    }
    [HttpPost("config/workflows/draft")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Save(WorkflowDefinitionSave draft, string? evidenceFields, string? holidayDates, CancellationToken ct)
    {
        try
        {
            if (ModelState.IsValid)
            {
                var fields = new List<EvidenceField>();
                foreach (var line in (evidenceFields ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var f = line.Split('|', StringSplitOptions.TrimEntries);
                    if (f.Length != 4 || !bool.TryParse(f[3], out bool required)) throw new ValidationException("evidenceFields", "Use key | label | text/date/number/yesNo | true/false, one field per line.");
                    fields.Add(new(f[0], f[1], f[2], required));
                }
                draft.EvidenceFieldsJson = JsonSerializer.Serialize(fields, JsonOptions);
                draft.HolidayDatesJson = JsonSerializer.Serialize((holidayDates ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), JsonOptions);
                if (string.IsNullOrWhiteSpace(draft.AssignmentRoleCode)) draft.AssignmentRoleCode = null;
                await workflows.SaveAsync(draft, ct);
            }
        }
        catch (MeritException ex) when (FormError(ex)) { return await ConfigPage(draft, null, [], null, null, null, null, ct); }
        if (!ModelState.IsValid) { Response.StatusCode=422; return await ConfigPage(draft, null, [], null, null, null, null, ct); }
        TempData["WorkMessage"] = "Draft saved. Preview sample patients before publishing.";
        return RedirectToAction(nameof(Configuration), new { id = draft.WorkflowDefinitionId });
    }
    [HttpPost("config/workflows/{id:guid}/preview")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Preview(Guid id, Guid samplePersonId, Guid secondSamplePersonId, Guid? sampleCaseId, Guid? secondSampleCaseId, CancellationToken ct)
    {
        var rows = await workflows.DefinitionsAsync(ct); var draft = rows.FirstOrDefault(d => d.WorkflowDefinitionId == id) ?? throw new NotFoundException();
        var previews = new List<WorkflowPreviewRow>();
        try { if (ModelState.IsValid) { previews.AddRange(await workflows.PreviewAsync(id, samplePersonId, sampleCaseId, ct)); previews.AddRange(await workflows.PreviewAsync(id, secondSamplePersonId, secondSampleCaseId, ct)); } }
        catch (MeritException ex) when (FormError(ex)) { }
        return await ConfigPage(JsonSerializer.Deserialize<WorkflowDefinitionSave>(JsonSerializer.Serialize(draft, JsonOptions), JsonOptions)!, rows, previews, samplePersonId, secondSamplePersonId, sampleCaseId, secondSampleCaseId, ct);
    }
    [HttpPost("config/workflows/{id:guid}/publish")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Publish(Guid id, string versionStamp, string approvalReference, bool approved, Guid samplePersonId, Guid secondSamplePersonId, Guid? sampleCaseId, Guid? secondSampleCaseId, CancellationToken ct)
    {
        try { if (ModelState.IsValid) await workflows.PublishAsync(id, versionStamp, approvalReference, approved, samplePersonId, secondSamplePersonId, sampleCaseId, secondSampleCaseId, ct); }
        catch (MeritException ex) when (FormError(ex)) { return await Configuration(id, null, ct); }
        if (!ModelState.IsValid) { Response.StatusCode=422; return await Configuration(id, null, ct); }
        TempData["WorkMessage"] = "Approved version published. Evaluate existing patients explicitly; new received results evaluate automatically.";
        return RedirectToAction(nameof(Configuration), new { id });
    }
    [HttpPost("config/workflows/{id:guid}/retire")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Retire(Guid id, CancellationToken ct)
    { await workflows.RetireAsync(id, ct); TempData["WorkMessage"] = "Version retired. Existing task snapshots and evidence remain intact."; return RedirectToAction(nameof(Configuration)); }
    [HttpPost("config/workflows/{id:guid}/evaluate")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Backfill(Guid id, CancellationToken ct)
    { await workflows.QueueEvaluationAsync(id, ct); TempData["WorkMessage"] = "Existing-patient evaluation queued under the current profile and agency scope."; return RedirectToAction(nameof(Configuration)); }

    [HttpGet("people/{personId:guid}/work")]
    [Authorize(Policy = Permissions.PersonView)]
    [Authorize(Policy = Permissions.TaskView)]
    public async Task<IActionResult> PatientWork(Guid personId, CancellationToken ct)
        => View("PatientWork", new PatientWorkModel(await people.GetPageAsync(personId, ct), await workflows.PatientWorkAsync(personId, ct)));
    [HttpPost("people/{personId:guid}/work/evaluate")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public async Task<IActionResult> Evaluate(Guid personId, CancellationToken ct)
    { TempData["WorkMessage"] = $"Created {await workflows.EvaluatePatientAsync(personId, null, ct)} missing program tasks. Existing evidence was retained."; return RedirectToAction(nameof(PatientWork), new { personId }); }
    [HttpPost("people/{personId:guid}/work")]
    [Authorize(Policy = Permissions.PersonEdit)]
    [Authorize(Policy = Permissions.TaskEdit)]
    public async Task<IActionResult> Create(Guid personId, Guid workTaskId, string title, string description, string completionKind, Guid? exposureCaseId, DateOnly? dueDate, CancellationToken ct)
    {
        try { if (ModelState.IsValid) await workflows.CreateAsync(workTaskId, personId, title, description, completionKind, exposureCaseId, dueDate, ct); }
        catch (MeritException ex) when (FormError(ex)) { return await PatientWork(personId, ct); }
        if (!ModelState.IsValid) { Response.StatusCode=422; return await PatientWork(personId, ct); }
        TempData["WorkMessage"] = "Ad-hoc patient/case task created."; return RedirectToAction(nameof(PatientWork), new { personId });
    }
    [HttpGet("work/tasks/{id:guid}")]
    [Authorize(Policy = Permissions.TaskView)]
    public async Task<IActionResult> TaskEvidence(Guid id, CancellationToken ct)
    {
        var task = await workflows.TaskAsync(id, ct);
        if (task.QueueCode == "notificationDue") return Redirect($"/people/{task.PatientId}/care?caseId={task.ExposureCaseId}#notifications");
        if (task.QueueCode is "receiptFinding" or "missingInformation" or "labReportingReview") return Redirect($"/receipt-quality?taskId={id}&personId={task.PatientId}");
        if (task.QueueCode is "testingFollowUp" or "testingDischarge") return Redirect($"/people/{task.PatientId}/care/testing");
        if (task.QueueCode == "assessmentOwnerClosure") return Redirect("/app/queues?queue=assessmentOwnerClosure");
        return View("TaskEvidence", new TaskEvidenceModel(task, await people.GetPageAsync(task.PatientId, ct)));
    }
    [HttpPost("work/tasks/{id:guid}")]
    [Authorize(Policy = Permissions.TaskEdit)]
    public async Task<IActionResult> Resolve(Guid id, Guid personActivityId, string action, DateOnly activityDate, string note, string? outcome, string? audience, string? channel, Guid? contactPersonId, string? recipientLabel, Guid? transferCaseId, bool confirmed, CancellationToken ct)
    {
        try
        {
            if (!confirmed) throw new ValidationException("confirmed", "Confirm that this records what actually happened.");
            var task = await workflows.TaskAsync(id, ct);
            var fields = task.EvidenceFields.ToDictionary(f => f.Key, f => Request.Form["evidence_" + f.Key].ToString());
            foreach(var field in fields) { ModelState.SetModelValue("evidence_"+field.Key,field.Value,field.Value); ModelState.MarkFieldValid("evidence_"+field.Key); }
            if (ModelState.IsValid) await workflows.ResolveAsync(new(id, personActivityId, action, activityDate, note, outcome ?? "completed", audience ?? "internal", channel ?? "internal", contactPersonId, recipientLabel, JsonSerializer.Serialize(fields, JsonOptions), transferCaseId), ct);
        }
        catch (MeritException ex) when (FormError(ex)) { return await TaskEvidence(id, ct); }
        if (!ModelState.IsValid) { Response.StatusCode=422; return await TaskEvidence(id, ct); }
        TempData["WorkMessage"] = action == "attempt" ? "Contact attempt recorded; the task remains open." : "Task outcome and its patient/case activity recorded together.";
        return RedirectToAction(nameof(TaskEvidence), new { id });
    }
    [HttpPost("people/{personId:guid}/work/correct")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public async Task<IActionResult> Correct(Guid personId, Guid activityId, string sourceKind, string reason, CancellationToken ct)
    {
        try { if (ModelState.IsValid) await workflows.CorrectAsync(personId, activityId, sourceKind, reason, ct); }
        catch (MeritException ex) when (FormError(ex)) { return View("Correction", new ActivityCorrectionModel(personId, activityId, sourceKind, reason)); }
        if (!ModelState.IsValid) return View("Correction", new ActivityCorrectionModel(personId, activityId, sourceKind, reason));
        return Redirect($"/people/{personId}/care#activity");
    }
    private bool FormError(MeritException ex)
    {
        if (ex is ValidationException v) foreach (var e in v.Errors) foreach (var message in e.Value) ModelState.AddModelError(e.Key, message);
        else if (ex is BusinessRuleException or ConcurrencyException) ModelState.AddModelError("", ex.Message); else return false;
        Response.StatusCode = 422; return true;
    }
}
