namespace Merit.Application.Data;

public sealed class PatientActivityRow
{
    public Guid CaseActivityId { get; init; }
    public Guid PersonId { get; init; }
    public Guid? ExposureCaseId { get; init; }
    public string? CaseNumber { get; init; }
    public string SourceKind { get; init; } = "";
    public string ActivityType { get; init; } = "";
    public DateOnly ActivityDate { get; init; }
    public string Audience { get; init; } = "";
    public string? Channel { get; init; }
    public string? ContactName { get; init; }
    public string? RecipientLabel { get; init; }
    public string? Outcome { get; init; }
    public string? Note { get; init; }
    public string? WorkerName { get; init; }
    public bool IsVoided { get; init; }
    public string? VoidReason { get; init; }
    public string? NoticeFormReference { get; init; }
    public bool OwnerScopeConfirmed { get; init; }
    public DateTime CreatedDateUtc { get; init; }
    public int TotalCount { get; init; }
}

public sealed record PatientActivitySave(Guid PersonActivityId, Guid PersonId, string ActivityType, DateOnly ActivityDate,
    string Audience, string Channel, string Outcome, Guid? ContactPersonId, string? RecipientLabel, string? Note,
    Guid? ExposureCaseId, Guid? LabResultId, Guid? NotificationObligationId, bool CompleteMatching = false,
    string? NoticeFormReference = null, bool OwnerScopeConfirmed = false);

public sealed class PatientDocumentRow
{
    public Guid PersonDocumentId { get; init; }
    public Guid PersonId { get; init; }
    public Guid? ExposureCaseId { get; init; }
    public Guid? SupersedesDocumentId { get; init; }
    public string? CaseNumber { get; init; }
    public string Title { get; init; } = "";
    public string FileName { get; init; } = "";
    public string ContentType { get; init; } = "";
    public string Category { get; init; } = "";
    public DateOnly DocumentDate { get; init; }
    public string Source { get; init; } = "";
    public string? Description { get; init; }
    public string? UploadedBy { get; init; }
    public DateTime CreatedDateUtc { get; init; }
    public long SizeBytes { get; init; }
    public int TotalCount { get; init; }
    public byte[] Content { get; init; } = [];
}

public sealed record PatientDocumentSave(Guid PersonDocumentId, Guid PersonId, string Title, string FileName, string ContentType,
    string Category, DateOnly DocumentDate, string Source, byte[] ContentHash, byte[] Content, string? Description,
    Guid? ExposureCaseId, Guid? SupersedesDocumentId);

public sealed class NotificationRow
{
    public Guid NotificationObligationId { get; init; }
    public Guid PersonId { get; init; }
    public Guid? ExposureCaseId { get; init; }
    public Guid LabResultId { get; init; }
    public string? CaseNumber { get; init; }
    public string LastName { get; init; } = "";
    public string FirstName { get; init; } = "";
    public string RuleName { get; init; } = "";
    public string RecipientKind { get; init; } = "";
    public DateOnly AnchorDate { get; init; }
    public DateOnly DueDate { get; init; }
    public DateOnly PolicyToday { get; init; }
    public DateOnly CollectedDate { get; init; }
    public DateTime ReceivedUtc { get; init; }
    public string TimeZoneId { get; init; } = "";
    public string EvaluationState { get; init; } = "";
    public string? ReviewReason { get; init; }
    public DateOnly? CompletedDate { get; init; }
    public string? RecipientName { get; init; }
    public string? EvidenceChannel { get; init; }
    public string PolicySnapshotJson { get; init; } = "";
    public string EligibilityMode
    {
        get
        {
            if (string.IsNullOrWhiteSpace(PolicySnapshotJson)) return "threshold";
            using var policy = System.Text.Json.JsonDocument.Parse(PolicySnapshotJson);
            return policy.RootElement.TryGetProperty("eligibilityMode", out var mode) ? mode.GetString() ?? "threshold" : "threshold";
        }
    }
    public string ResultRowStatus { get; init; } = "";
    public string AnalyteName { get; init; } = "";
    public string Specimen { get; init; } = "";
    public string Operator { get; init; } = "";
    public decimal? ValueCanonical { get; init; }
    public string CanonicalUnit { get; init; } = "";
    public int TotalCount { get; init; }
    public string Status => EvaluationState == "notApplicable" ? "Not applicable" : EvaluationState == "needsReview" ? "Needs eligibility review"
        : CompletedDate is DateOnly done ? done <= DueDate ? "Completed on time" : "Completed late"
        : DueDate < PolicyToday ? "Overdue" : DueDate == PolicyToday ? "Due today" : DueDate <= PolicyToday.AddDays(2) ? "Due within 2 days" : "Pending";
}

public sealed class NotificationRuleRow
{
    public string RulePurpose { get; init; } = "notification";
    public string EligibilityMode { get; init; } = "threshold";
    public string? RequiredFieldsCsv { get; init; }
    public Guid NotificationRuleId { get; init; }
    public string RuleName { get; init; } = "";
    public string ScopeName { get; init; } = "";
    public string AnalyteCode { get; init; } = "";
    public string Specimen { get; init; } = "";
    public string CanonicalUnit { get; init; } = "";
    public decimal MinimumValue { get; init; }
    public decimal? MaximumValue { get; init; }
    public string Population { get; init; } = "";
    public string RecipientKind { get; init; } = "";
    public int DueInDays { get; init; }
    public string DayBasis { get; init; } = "";
    public string CalendarName { get; init; } = "";
    public string TimeZoneId { get; init; } = "";
    public string HolidayDatesJson { get; init; } = "[]";
    public string ApprovalReference { get; init; } = "";
    public DateOnly EffectiveFrom { get; init; }
    public DateOnly? EffectiveTo { get; init; }
    public bool IsActive { get; init; }
}

public sealed record NotificationRuleSave(Guid NotificationRuleId, string RuleName, Guid AnalyteSpecimenId, string Population,
    decimal MinimumValue, string RecipientKind, int DueInDays, string DayBasis, string TimeZoneId, string CalendarName,
    string HolidayDatesJson, DateOnly EffectiveFrom, string ApprovalReference, int? MinimumAgeMonths, int? MaximumAgeMonths,
    decimal? MaximumValue, DateOnly? EffectiveTo, string RulePurpose = "notification", string EligibilityMode = "threshold", string? RequiredFieldsCsv = null);

public sealed record NotificationSummaryRow(string Status, int ObligationCount);

public interface ICareData
{
    Task ReconcileReceiptAsync(Guid? labResultId, Guid? stagingRowId, CancellationToken ct);
    Task<IReadOnlyList<ReceiptWorkRow>> ReceiptWorkAsync(Guid? personId, int pageNumber, CancellationToken ct, Guid? workTaskId = null);
    Task ReceiveInformationAsync(Guid workTaskId, DateOnly receivedDate, string evidence, bool confirmed, CancellationToken ct);
    Task VerifyTimelineAsync(Guid personId, Guid labResultId, DateOnly? finalResultDate, string evidence, bool eligibilityConfirmed, CancellationToken ct, bool applies = true, bool sameDayChecked = false, bool lateConfirmed = false);
    Task<IReadOnlyList<ReceiptResultRow>> ReceiptResultsAsync(Guid? notificationRuleId, Guid? personId, int pageNumber, CancellationToken ct);
    Task<IReadOnlyList<PatientActivityRow>> ActivitiesAsync(Guid personId, Guid? exposureCaseId, int pageNumber, CancellationToken ct);
    Task<int> RecordAsync(PatientActivitySave row, CancellationToken ct);
    Task LinkAsync(Guid personId, Guid exposureCaseId, Guid recordId, CancellationToken ct);
    Task<IReadOnlyList<PatientDocumentRow>> DocumentsAsync(Guid personId, Guid? exposureCaseId, int pageNumber, CancellationToken ct);
    Task<PatientDocumentRow?> DocumentAsync(Guid personId, Guid personDocumentId, CancellationToken ct);
    Task SaveDocumentAsync(PatientDocumentSave row, CancellationToken ct);
    Task<IReadOnlyList<NotificationSummaryRow>> SummaryAsync(CancellationToken ct);
    Task<IReadOnlyList<NotificationRow>> NotificationsAsync(Guid? personId, Guid? exposureCaseId, int pageNumber, CancellationToken ct);
    Task ReviewAsync(Guid personId, Guid notificationObligationId, bool applies, string reason, CancellationToken ct, bool criteriaConfirmed = false);
    Task WithdrawAsync(Guid personId, Guid notificationObligationId, string reason, CancellationToken ct);
    Task<int> ReconcileAsync(Guid? labResultId, Guid? notificationRuleId, Guid? personId, CancellationToken ct);
    Task<IReadOnlyList<NotificationRuleRow>> RulesAsync(CancellationToken ct);
    Task SaveRuleAsync(NotificationRuleSave row, CancellationToken ct);
    Task RetireRuleAsync(Guid notificationRuleId, CancellationToken ct);
}

public sealed record ReceiptResultRow(Guid LabResultId);
public sealed class ReceiptWorkRow
{
    public Guid WorkTaskId { get; init; }
    public Guid? PersonId { get; init; }
    public Guid? LabResultId { get; init; }
    public Guid? StagingRowId { get; init; }
    public Guid? IntakeBatchId { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string TaskStatus { get; init; } = "";
    public string QueueCode { get; init; } = "";
    public DateTime CreatedDateUtc { get; init; }
    public DateTime? CompletedUtc { get; init; }
    public DateOnly? DueDate { get; init; }
    public string? ResolutionNote { get; init; }
    public int TotalCount { get; init; }
}
