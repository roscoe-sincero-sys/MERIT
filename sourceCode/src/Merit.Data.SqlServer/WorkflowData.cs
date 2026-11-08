using Merit.Application.Data;

namespace Merit.Data.SqlServer;

public sealed class WorkflowData(Db db) : IWorkflowData
{
    public Task<IReadOnlyList<WorkflowDefinitionRow>> DefinitionsAsync(CancellationToken ct) => db.QueryAsync<WorkflowDefinitionRow>("config.uspListWorkflowDefinitions", null, ct);
    public Task SaveAsync(WorkflowDefinitionSave draft, CancellationToken ct) => db.ExecuteAsync("config.uspSaveWorkflowDefinition", draft, ct);
    public Task<IReadOnlyList<WorkflowPreviewRow>> PreviewAsync(Guid workflowDefinitionId, Guid personId, Guid? exposureCaseId, CancellationToken ct)
        => db.QueryAsync<WorkflowPreviewRow>("config.uspPreviewWorkflowDefinition", new { workflowDefinitionId, personId, exposureCaseId }, ct);
    public Task PublishAsync(Guid workflowDefinitionId, string versionStamp, string approvalReference, bool approved, Guid samplePersonId, Guid secondSamplePersonId, Guid? sampleCaseId, Guid? secondSampleCaseId, CancellationToken ct)
        => db.ExecuteAsync("config.uspPublishWorkflowDefinition", new { workflowDefinitionId, versionStamp, approvalReference, approved, samplePersonId, secondSamplePersonId, sampleCaseId, secondSampleCaseId }, ct);
    public Task RetireAsync(Guid workflowDefinitionId, CancellationToken ct) => db.ExecuteAsync("config.uspRetireWorkflowDefinition", new { workflowDefinitionId }, ct);
    public async Task<int> ReconcileAsync(Guid personId, Guid? labResultId, Guid? exposureCaseId, Guid? workflowDefinitionId, CancellationToken ct)
        => await db.QuerySingleAsync<int>("dbo.uspReconcileWorkflowTasks", new { personId, labResultId, exposureCaseId, workflowDefinitionId }, ct).ConfigureAwait(false);
    public Task<IReadOnlyList<WorkflowPatientRow>> PatientsAsync(Guid workflowDefinitionId, int pageNumber, CancellationToken ct)
        => db.QueryAsync<WorkflowPatientRow>("dbo.uspListWorkflowPatients", new { workflowDefinitionId, pageNumber }, ct);
    public Task<IReadOnlyList<PatientWorkRow>> PatientWorkAsync(Guid personId, CancellationToken ct) => db.QueryAsync<PatientWorkRow>("dbo.uspListPatientWork", new { personId }, ct);
    public Task<IReadOnlyList<WorkflowSourceRow>> SourcesAsync(Guid personId, CancellationToken ct) => db.QueryAsync<WorkflowSourceRow>("dbo.uspListWorkflowSources", new { personId }, ct);
    public Task<PatientWorkRow?> TaskAsync(Guid workTaskId, CancellationToken ct) => db.QuerySingleAsync<PatientWorkRow>("dbo.uspGetWorkTask", new { workTaskId }, ct);
    public Task CreateAsync(Guid workTaskId, Guid personId, string title, string description, string completionKind, Guid? exposureCaseId, DateOnly? dueDate, CancellationToken ct)
        => db.ExecuteAsync("dbo.uspCreateAdHocWork", new { workTaskId, personId, title, description, completionKind, exposureCaseId, dueDate }, ct);
    public Task ResolveAsync(WorkEvidenceSave evidence, CancellationToken ct) => db.ExecuteAsync("dbo.uspResolveWorkWithEvidence", evidence, ct);
    public Task CorrectAsync(Guid personId, Guid activityId, string sourceKind, string reason, CancellationToken ct)
        => db.ExecuteAsync("dbo.uspCorrectPatientActivity", new { personId, activityId, sourceKind, reason }, ct);
}
