namespace Merit.Application.Data;

// Rows returned by the work queue, job, intake and extract procedures.

public sealed class QueueCountRow
{
    public string QueueCode { get; init; } = string.Empty;

    public string QueueName { get; init; } = string.Empty;

    public int OpenCount { get; init; }

    public int UrgentCount { get; init; }

    public int OverdueCount { get; init; }

    public int MineCount { get; init; }
}

public sealed class DashboardCountsRow
{
    public int MyOpenCases { get; init; }

    public int MyOpenTasks { get; init; }

    public int OverdueTasks { get; init; }

    public int UrgentTasks { get; init; }

    public int UrgentHeld { get; init; }

    public int OpenCasesInScope { get; init; }
}

public sealed class DashboardItemRow
{
    public string ItemType { get; init; } = string.Empty;

    public Guid ItemId { get; init; }

    public string Title { get; init; } = string.Empty;

    public string? Detail { get; init; }

    public DateOnly? DueDate { get; init; }

    public DateTime? WaitingSinceUtc { get; init; }

    public Guid? PersonId { get; init; }

    public Guid? ExposureCaseId { get; init; }
}

public sealed class DashboardCaseRow
{
    public Guid ExposureCaseId { get; init; }

    public string CaseNumber { get; init; } = string.Empty;

    public Guid PersonId { get; init; }

    public string LastName { get; init; } = string.Empty;

    public string? FirstName { get; init; }

    public string AnalyteCode { get; init; } = string.Empty;

    public string Classification { get; init; } = string.Empty;

    public DateOnly OpenedDate { get; init; }
}

public sealed record DashboardData(DashboardCountsRow Counts, IReadOnlyList<DashboardItemRow> UrgentItems,
                                   IReadOnlyList<DashboardCaseRow> MyCases);

public sealed class WorkTaskRow
{
    public Guid WorkTaskId { get; init; }

    public string QueueCode { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string? Detail { get; init; }

    public bool IsUrgent { get; init; }

    public DateOnly? DueDate { get; init; }

    public string TaskStatus { get; init; } = string.Empty;

    public DateTime CreatedDateUtc { get; init; }

    public Guid? PersonId { get; init; }

    public string? LastName { get; init; }

    public string? FirstName { get; init; }

    public Guid? ExposureCaseId { get; init; }

    public string? CaseNumber { get; init; }

    public Guid? LabResultId { get; init; }

    public Guid? InvestigationId { get; init; }

    public Guid? StagingRowId { get; init; }

    public Guid? AssignedToUserId { get; init; }

    public string? AssignedToName { get; init; }

    public byte[] RowVersion { get; init; } = [];

    public int TotalCount { get; init; }
}

public sealed record WorkTaskQuery(string? QueueCode, string? TaskStatus, bool AssignedToMe, bool UrgentOnly, string SortBy,
                                   bool SortDescending, int PageNumber, int PageSize);

public sealed record WorkTaskCreate(
    Guid WorkTaskId,
    string QueueCode,
    string Title,
    string? Detail,
    Guid? PersonId,
    Guid? ExposureCaseId,
    Guid? LabResultId,
    Guid? InvestigationId,
    Guid? StagingRowId,
    bool IsUrgent,
    DateOnly? DueDate,
    Guid? OwningTenantId = null);

public sealed class ClaimedJobRow
{
    public Guid JobId { get; init; }

    public Guid RootTenantId { get; init; }

    public Guid OwningTenantId { get; init; }

    public string JobType { get; init; } = string.Empty;

    public string PayloadJson { get; init; } = "{}";

    public int PayloadVersion { get; init; }

    public int Attempts { get; init; }

    public int MaxAttempts { get; init; }

    public Guid RequestedByUserId { get; init; }

    /// <summary>The queuing session's profile; null for the master profile.</summary>
    public Guid? RequestedProfileId { get; init; }

    /// <summary>The user the administrator was viewing MERIT as when the job was queued.</summary>
    public Guid? RequestedViewAsUserId { get; init; }

    /// <summary>The tenant the user was working in when the job was queued.</summary>
    public Guid? RequestedActingTenantId { get; init; }
}

public sealed class JobRow
{
    public Guid JobId { get; init; }

    public string JobType { get; init; } = string.Empty;

    public string JobStatus { get; init; } = string.Empty;

    public int Attempts { get; init; }

    public int MaxAttempts { get; init; }

    public DateTime AvailableUtc { get; init; }

    public DateTime? ClaimedUtc { get; init; }

    public DateTime? CompletedUtc { get; init; }

    public string? LastError { get; init; }

    public string? ResultJson { get; init; }

    public DateTime CreatedDateUtc { get; init; }
}

public sealed class QueueDepthRow
{
    public Guid RootTenantId { get; init; }

    public string TenantName { get; init; } = string.Empty;

    public string JobType { get; init; } = string.Empty;

    public int Queued { get; init; }

    public int Running { get; init; }

    public int Failed { get; init; }

    public DateTime? OldestWaitingUtc { get; init; }
}

// ------------------------------------------------------------------------------------------------ intake

public sealed class IntakeProfileListRow
{
    public Guid IntakeProfileId { get; init; }

    public string ProfileName { get; init; } = string.Empty;

    public int ProfileVersion { get; init; }

    public string FileType { get; init; } = string.Empty;

    public Guid SenderOrganizationId { get; init; }

    public string SenderName { get; init; } = string.Empty;

    public string? HeaderSignature { get; init; }
}

public sealed class IntakeProfileRow
{
    public Guid IntakeProfileId { get; init; }

    public Guid SenderOrganizationId { get; init; }

    public string SenderName { get; init; } = string.Empty;

    public string? SenderClia { get; init; }

    public string ProfileName { get; init; } = string.Empty;

    public int ProfileVersion { get; init; }

    public string FileType { get; init; } = string.Empty;

    public int HeaderRow { get; init; }

    public string? DateFormats { get; init; }

    public string ColumnMapJson { get; init; } = "{}";

    public string? ValueTranslationsJson { get; init; }

    public string? ConstantsJson { get; init; }

    public string? HeaderSignature { get; init; }

    public bool IsActive { get; init; }
}

public sealed class IntakeBatchRow
{
    public Guid IntakeBatchId { get; init; }

    public Guid? IntakeProfileId { get; init; }

    public Guid? SenderOrganizationId { get; init; }

    public string? SenderName { get; init; }

    public string? FileName { get; init; }

    public string SourceChannel { get; init; } = string.Empty;

    public DateTime ReceivedUtc { get; init; }

    public string BatchStatus { get; init; } = string.Empty;

    public string? PauseReason { get; init; }

    public int ReceivedCount { get; init; }

    public int LoadedCount { get; init; }

    public int HeldCount { get; init; }

    public int RejectedCount { get; init; }

    public string? RawContent { get; init; }

    public string? SubmittedBy { get; init; }
}

public sealed class StagingRowForProcessing
{
    public Guid StagingRowId { get; init; }

    public int RowNumber { get; init; }

    public string ParsedJson { get; init; } = "{}";

    public string? CorrectedJson { get; init; }

    public string RowStatus { get; init; } = string.Empty;
}

public sealed record StagingRowNew(Guid StagingRowId, int RowNumber, string ParsedJson);

public sealed record HoldReason(string ReasonCode, string? FieldName, string ReasonMessage, string? ReportedValue);

public sealed class HeldGroupRow
{
    public string ReasonCode { get; init; } = string.Empty;

    public string? ReportedValue { get; init; }

    public Guid? SenderOrganizationId { get; init; }

    public string? SenderName { get; init; }

    public int HeldRowCount { get; init; }

    public int UrgentCount { get; init; }

    public DateTime? OldestHeldUtc { get; init; }

    public int BatchCount { get; init; }

    public string? SampleMessage { get; init; }
}

public sealed class HeldRowRow
{
    public Guid StagingRowId { get; init; }

    public Guid IntakeBatchId { get; init; }

    public string? FileName { get; init; }

    public string? SenderName { get; init; }

    public int RowNumber { get; init; }

    public string ParsedJson { get; init; } = "{}";

    public string? CorrectedJson { get; init; }

    public bool IsUrgent { get; init; }

    public DateTime? HeldSinceUtc { get; init; }

    public Guid? AssignedToUserId { get; init; }

    public string? AssignedToName { get; init; }

    public string? Reasons { get; init; }

    public int TotalCount { get; init; }
}

public sealed class PersonCandidateRow
{
    public Guid PersonId { get; init; }

    public string LastName { get; init; } = string.Empty;

    public string? FirstName { get; init; }

    public string LastNameKey { get; init; } = string.Empty;

    public string? FirstNameKey { get; init; }

    public DateOnly? Dob { get; init; }

    public string Sex { get; init; } = "unknown";

    public bool IsPlaceholderName { get; init; }

    public string Via { get; init; } = string.Empty;
}

// ------------------------------------------------------------------------------------------------ extracts

public sealed class ExtractRunRow
{
    public Guid ExtractRunId { get; init; }

    public string ExtractType { get; init; } = string.Empty;

    public DateOnly PeriodStart { get; init; }

    public DateOnly PeriodEnd { get; init; }

    public Guid ExtractLayoutId { get; init; }

    public string RunStatus { get; init; } = string.Empty;

    public int? RecordCount { get; init; }

    public int? ErrorCount { get; init; }

    public string? ValidationReportJson { get; init; }

    public string? FileName { get; init; }

    public string? FileContent { get; init; }

    public Guid RequestedByUserId { get; init; }

    public Guid? JobId { get; init; }

    public DateTime CreatedDateUtc { get; init; }

    public string? LayoutVersion { get; init; }

    public string? RequestedBy { get; init; }

    public string? JobStatus { get; init; }

    public string? LastError { get; init; }
}

public sealed class CblsSourceRow
{
    public Guid LabResultId { get; init; }

    public Guid PersonId { get; init; }

    public DateOnly? Dob { get; init; }

    public string Sex { get; init; } = "unknown";

    public string Ethnicity { get; init; } = "unknown";

    public string? RaceCodes { get; init; }

    public DateOnly CollectedDate { get; init; }

    public string? SampleType { get; init; }

    public string? TestMethod { get; init; }

    public string Operator { get; init; } = "=";

    public decimal? ValueCanonical { get; init; }

    public string? ReasonForTest { get; init; }

    public string? PayerType { get; init; }

    public int? AgeAtCollectionMonths { get; init; }

    public string? CountyFips { get; init; }

    public string? Zip5 { get; init; }

    public string? CensusTract { get; init; }

    public int? YearBuilt { get; init; }

    public string? LabClia { get; init; }

    public string? Specimen { get; init; }
}

public sealed class AblesSourceRow
{
    public Guid LabResultId { get; init; }

    public Guid PersonId { get; init; }

    public DateOnly? Dob { get; init; }

    public string Sex { get; init; } = "unknown";

    public string Ethnicity { get; init; } = "unknown";

    public string? RaceCodes { get; init; }

    public DateOnly CollectedDate { get; init; }

    public decimal? ValueCanonical { get; init; }

    public int? AgeAtCollectionMonths { get; init; }

    public string? StateRes { get; init; }

    public string? CountyRes { get; init; }

    public string? CountryRes { get; init; }

    public string? ExposureCategory { get; init; }

    public string? Naics { get; init; }

    public string? OccupationCode { get; init; }

    public string? StateExp { get; init; }

    public string? CountyExp { get; init; }

    public string? ProcessText { get; init; }

    public string? ProcessCode { get; init; }

    public bool PriorYearElevated { get; init; }

    public bool InRegistryBefore { get; init; }
}
