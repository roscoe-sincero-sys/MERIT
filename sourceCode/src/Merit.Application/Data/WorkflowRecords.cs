namespace Merit.Application.Data;

public class WorkflowDefinitionSave
{
    public Guid WorkflowDefinitionId { get; set; }
    public string Name { get; set; } = "";
    public int Version { get; set; } = 1;
    public string Description { get; set; } = "";
    public string TriggerEvent { get; set; } = "patientReview";
    public string Population { get; set; } = "all";
    public string CompletionKind { get; set; } = "internal";
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public string TimeZoneId { get; set; } = "Eastern Standard Time";
    public string EvidenceFieldsJson { get; set; } = "[]";
    public Guid? AnalyteSpecimenId { get; set; }
    public int? MinimumAgeMonths { get; set; }
    public int? MaximumAgeMonths { get; set; }
    public decimal? MinimumValue { get; set; }
    public decimal? MaximumValue { get; set; }
    public bool RequiresEligibilityReview { get; set; }
    public string? AssignmentRoleCode { get; set; }
    public int? DueInDays { get; set; }
    public int? RecurrenceDays { get; set; }
    public string DayBasis { get; set; } = "calendar";
    public string HolidayDatesJson { get; set; } = "[]";
    public bool BlocksClosure { get; set; }
    public string ClosureReason { get; set; } = "all";
    public int MinimumContactAttempts { get; set; }
    public bool AllowWaiver { get; set; }
    public string? VersionStamp { get; set; }
}
public sealed class WorkflowDefinitionRow : WorkflowDefinitionSave
{
    public string DefinitionStatus { get; init; } = "";
    public string ScopeName { get; init; } = "";
    public string? ApprovalReference { get; init; }
}
public sealed record WorkflowPreviewRow(Guid PersonId, Guid? ExposureCaseId, Guid? LabResultId, DateOnly? AnchorDate, string EvaluationState, string Explanation);
public sealed record WorkflowPatientRow(Guid PersonId);
public sealed record WorkflowSourceRow(Guid? LabResultId, Guid? ExposureCaseId);
public sealed record EvidenceField(string Key, string Label, string Type, bool Required);
public sealed class PatientWorkRow
{
    public Guid WorkTaskId { get; init; }
    public Guid PatientId { get; init; }
    public Guid? ExposureCaseId { get; init; }
    public string? CaseNumber { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string QueueCode { get; init; } = "";
    public string TaskStatus { get; init; } = "";
    public DateOnly? DueDate { get; init; }
    public string? ResolutionKind { get; init; }
    public string? ResolutionNote { get; init; }
    public Guid? CompletionActivityId { get; init; }
    public string? WorkflowSnapshotJson { get; init; }
    public string CompletionKind => Snapshot("completionKind") ?? (QueueCode is "letterDue" or "prenatalNotification" ? "delivery" : QueueCode is "confirmatoryTest" or "newbornTesting" or "actionLevel" or "crossLink" or "investigationRequest" or "assessmentContact" ? "contact" : "internal");
    public string EvaluationState => Snapshot("evaluationState") ?? "eligible";
    public string? Snapshot(string property)
    {
        if (string.IsNullOrWhiteSpace(WorkflowSnapshotJson)) return null;
        using var json = System.Text.Json.JsonDocument.Parse(WorkflowSnapshotJson);
        return json.RootElement.TryGetProperty(property, out var value) ? value.ToString() : null;
    }
    public IReadOnlyList<EvidenceField> EvidenceFields => System.Text.Json.JsonSerializer.Deserialize<EvidenceField[]>(Snapshot("evidenceFieldsJson") ?? "[]", new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) ?? [];
}
public sealed record WorkEvidenceSave(Guid WorkTaskId, Guid PersonActivityId, string Action, DateOnly ActivityDate, string Note,
    string Outcome, string Audience, string Channel, Guid? ContactPersonId, string? RecipientLabel, string EvidenceJson, Guid? TransferCaseId);
public interface IWorkflowData
{
    Task<IReadOnlyList<WorkflowDefinitionRow>> DefinitionsAsync(CancellationToken ct);
    Task SaveAsync(WorkflowDefinitionSave draft, CancellationToken ct);
    Task<IReadOnlyList<WorkflowPreviewRow>> PreviewAsync(Guid workflowDefinitionId, Guid personId, Guid? exposureCaseId, CancellationToken ct);
    Task PublishAsync(Guid workflowDefinitionId, string versionStamp, string approvalReference, bool approved, Guid samplePersonId, Guid secondSamplePersonId, Guid? sampleCaseId, Guid? secondSampleCaseId, CancellationToken ct);
    Task RetireAsync(Guid workflowDefinitionId, CancellationToken ct);
    Task<int> ReconcileAsync(Guid personId, Guid? labResultId, Guid? exposureCaseId, Guid? workflowDefinitionId, CancellationToken ct);
    Task<IReadOnlyList<WorkflowPatientRow>> PatientsAsync(Guid workflowDefinitionId, int pageNumber, CancellationToken ct);
    Task<IReadOnlyList<WorkflowSourceRow>> SourcesAsync(Guid personId, CancellationToken ct);
    Task<IReadOnlyList<PatientWorkRow>> PatientWorkAsync(Guid personId, CancellationToken ct);
    Task<PatientWorkRow?> TaskAsync(Guid workTaskId, CancellationToken ct);
    Task CreateAsync(Guid workTaskId, Guid personId, string title, string description, string completionKind, Guid? exposureCaseId, DateOnly? dueDate, CancellationToken ct);
    Task ResolveAsync(WorkEvidenceSave evidence, CancellationToken ct);
    Task CorrectAsync(Guid personId, Guid activityId, string sourceKind, string reason, CancellationToken ct);
}
