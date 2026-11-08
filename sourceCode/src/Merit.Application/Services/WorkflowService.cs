using System.Text.Json;
using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Jobs;
using Merit.Application.Security;
using Merit.Domain.Ids;

namespace Merit.Application.Services;

public sealed class WorkflowService(IWorkflowData data, ICurrentSession session, AuditService audit, IWorkData work, CaseService cases)
{
    public Task<IReadOnlyList<WorkflowDefinitionRow>> DefinitionsAsync(CancellationToken ct)
    { session.Require(Permissions.ConfigEdit); return data.DefinitionsAsync(ct); }
    public async Task SaveAsync(WorkflowDefinitionSave draft, CancellationToken ct)
    {
        session.Require(Permissions.ConfigEdit);
        new Validator().Required("Name", draft.Name, "Task name").MaxLength("Name", draft.Name, 120, "Task name")
            .Required("Description", draft.Description, "Instructions").MaxLength("Description", draft.Description, 1000, "Instructions").ThrowIfInvalid();
        await data.SaveAsync(draft, ct).ConfigureAwait(false); await audit.ChangedAsync("workflowDefinition", draft.WorkflowDefinitionId, ct).ConfigureAwait(false);
    }
    public Task<IReadOnlyList<WorkflowPreviewRow>> PreviewAsync(Guid id, Guid patient, Guid? caseId, CancellationToken ct)
    { session.Require(Permissions.ConfigEdit); return data.PreviewAsync(id, patient, caseId, ct); }
    public async Task PublishAsync(Guid id, string stamp, string reference, bool approved, Guid patientA, Guid patientB, Guid? caseA, Guid? caseB, CancellationToken ct)
    {
        session.Require(Permissions.ConfigEdit);
        new Validator().Required("approvalReference", reference, "Approval reference").MaxLength("approvalReference", reference, 300, "Approval reference").ThrowIfInvalid();
        await data.PublishAsync(id, stamp, reference, approved, patientA, patientB, caseA, caseB, ct).ConfigureAwait(false);
        await audit.ChangedAsync("workflowDefinition", id, ct).ConfigureAwait(false);
    }
    public async Task RetireAsync(Guid id, CancellationToken ct)
    { session.Require(Permissions.ConfigEdit); await data.RetireAsync(id, ct).ConfigureAwait(false); await audit.ChangedAsync("workflowDefinition", id, ct).ConfigureAwait(false); }
    public async Task QueueEvaluationAsync(Guid id, CancellationToken ct)
    {
        session.Require(Permissions.ConfigEdit);
        if (!(await data.DefinitionsAsync(ct).ConfigureAwait(false)).Any(d => d.WorkflowDefinitionId == id && d.DefinitionStatus == "published")) throw new NotFoundException();
        await work.EnqueueJobAsync(TimeOrderedId.New(), JobTypes.WorkflowEvaluation, JsonSerializer.Serialize(new { workflowDefinitionId = id }), ct).ConfigureAwait(false);
    }
    public async Task<int> EvaluateAllAsync(Guid id, CancellationToken ct)
    {
        session.Require(Permissions.ConfigEdit); int total = 0;
        for (int p = 1; ; p++)
        {
            var rows = await data.PatientsAsync(id, p, ct).ConfigureAwait(false);
            foreach (var row in rows) total += await EvaluatePatientAsync(row.PersonId, id, ct).ConfigureAwait(false);
            if (rows.Count < 200) break;
        }
        return total;
    }
    public async Task<int> EvaluatePatientAsync(Guid person, Guid? definition, CancellationToken ct)
    {
        if (!session.Has(Permissions.PersonEdit) && !session.Has(Permissions.ConfigEdit)) throw new PermissionDeniedException(Permissions.PersonEdit);
        int total = await data.ReconcileAsync(person, null, null, definition, ct).ConfigureAwait(false);
        foreach (var source in await data.SourcesAsync(person, ct).ConfigureAwait(false))
            total += await data.ReconcileAsync(person, source.LabResultId, source.ExposureCaseId, definition, ct).ConfigureAwait(false);
        return total;
    }
    public async Task<IReadOnlyList<PatientWorkRow>> PatientWorkAsync(Guid person, CancellationToken ct)
    {
        session.Require(Permissions.PersonView); session.Require(Permissions.TaskView);
        var rows = await data.PatientWorkAsync(person, ct).ConfigureAwait(false); await audit.ListedAsync("patientWork", rows.Count, ct).ConfigureAwait(false); return rows;
    }
    public async Task<PatientWorkRow> TaskAsync(Guid id, CancellationToken ct)
    {
        session.Require(Permissions.TaskView); session.Require(Permissions.PersonView);
        var row = await data.TaskAsync(id, ct).ConfigureAwait(false) ?? throw new NotFoundException(); await audit.ViewedAsync("workTask", id, ct).ConfigureAwait(false); return row;
    }
    public async Task CreateAsync(Guid id, Guid person, string title, string description, string kind, Guid? caseId, DateOnly? due, CancellationToken ct)
    {
        session.Require(Permissions.PersonEdit); session.Require(Permissions.TaskEdit);
        new Validator().Required("title", title, "Task").MaxLength("title", title, 200, "Task").Required("description", description, "Instructions").MaxLength("description", description, 1000, "Instructions").ThrowIfInvalid();
        await data.CreateAsync(id, person, title, description, kind, caseId, due, ct).ConfigureAwait(false); await audit.CreatedAsync("workTask", id, ct).ConfigureAwait(false);
    }
    public async Task ResolveAsync(WorkEvidenceSave evidence, CancellationToken ct)
    {
        session.Require(Permissions.TaskEdit); session.Require(Permissions.PersonEdit);
        new Validator().Required("note", evidence.Note, "Source evidence").MaxLength("note", evidence.Note, 1000, "Source evidence").ThrowIfInvalid();
        var task = await TaskAsync(evidence.WorkTaskId, ct).ConfigureAwait(false);
        await data.ResolveAsync(evidence, ct).ConfigureAwait(false); await audit.ChangedAsync("workTask", evidence.WorkTaskId, ct).ConfigureAwait(false);
        if (evidence.Action == "attempt" && task.ExposureCaseId is Guid caseId)
        {
            int failed = (await cases.GetAsync(caseId, ct).ConfigureAwait(false)).Activities.Count(a => !a.IsVoided && a.ActivityType == "contactAttempt" && a.Outcome is "noAnswer" or "leftMessage" or "wrongNumber");
            await cases.ProposeLetterAsync(caseId, failed, "contactAttempt", evidence.Outcome, ct).ConfigureAwait(false);
        }
    }
    public async Task CorrectAsync(Guid person, Guid id, string source, string reason, CancellationToken ct)
    {
        session.Require(Permissions.PersonEdit);
        new Validator().Required("reason", reason, "Correction reason").MaxLength("reason", reason, 500, "Correction reason").ThrowIfInvalid();
        await data.CorrectAsync(person, id, source, reason, ct).ConfigureAwait(false); await audit.ChangedAsync("patientActivity", id, ct).ConfigureAwait(false);
    }
}
