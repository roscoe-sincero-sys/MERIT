namespace Merit.Application.Data;

// Rows returned by the result, case and investigation procedures.

public sealed class ContextPersonRow
{
    public Guid PersonId { get; init; }

    public DateOnly? Dob { get; init; }

    public string Sex { get; init; } = "unknown";

    public DateOnly? DeceasedDate { get; init; }

    public Guid? MergedIntoPersonId { get; init; }
}

public sealed class ContextEpisodeRow
{
    public Guid PregnancyEpisodeId { get; init; }

    public DateOnly StartDate { get; init; }

    public DateOnly? ExpectedEndDate { get; init; }

    public DateOnly? ActualEndDate { get; init; }
}

public sealed class ContextOpenCaseRow
{
    public Guid ExposureCaseId { get; init; }

    public string CaseNumber { get; init; } = string.Empty;

    public string Classification { get; init; } = string.Empty;

    public string ComputedClassification { get; init; } = string.Empty;

    public string? OverrideReason { get; init; }

    public string Population { get; init; } = string.Empty;

    public string CaseStatus { get; init; } = string.Empty;

    public Guid? HighestLabResultId { get; init; }

    public decimal? HighestValue { get; init; }

    public DateOnly OpenedDate { get; init; }

    public Guid OwningTenantId { get; init; }
}

public sealed class ContextPriorResultRow
{
    public Guid LabResultId { get; init; }

    public Guid AnalyteSpecimenId { get; init; }

    public string Specimen { get; init; } = string.Empty;

    public DateOnly CollectedDate { get; init; }

    public string? SampleType { get; init; }

    public string? TestMethod { get; init; }

    public string? Speciation { get; init; }

    public string Operator { get; init; } = "=";

    public decimal? ValueCanonical { get; init; }

    public string Population { get; init; } = string.Empty;

    public bool IsIndeterminate { get; init; }
}

public sealed record ClassificationContext(
    ContextPersonRow? Person,
    IReadOnlyList<ContextEpisodeRow> Episodes,
    ContextOpenCaseRow? OpenCase,
    IReadOnlyList<ContextPriorResultRow> PriorResults);

public sealed class DuplicateResultRow
{
    public Guid LabResultId { get; init; }

    public string ResultStatus { get; init; } = string.Empty;

    public string ResultRowStatus { get; init; } = string.Empty;

    public decimal? ValueReported { get; init; }

    public string? AccessionNumber { get; init; }
}

/// <summary>Input for dbo.uspSaveLabResult.</summary>
public sealed record LabResultSave
{
    public Guid LabResultId { get; init; }

    public Guid? PersonId { get; init; }

    public Guid AnalyteSpecimenId { get; init; }

    public DateOnly CollectedDate { get; init; }

    public DateOnly? AnalyzedDate { get; init; }

    public DateOnly? ReportedDate { get; init; }

    public DateTime ReceivedUtc { get; init; }

    public string ResultStatus { get; init; } = "final";

    public Guid? SupersedesLabResultId { get; init; }

    public decimal? ValueReported { get; init; }

    public string? ValueText { get; init; }

    public string Operator { get; init; } = "=";

    public string UnitReported { get; init; } = string.Empty;

    public decimal? ValueCanonical { get; init; }

    public string UnitCanonical { get; init; } = string.Empty;

    public string? SampleType { get; init; }

    public string? TestMethod { get; init; }

    public string? DeviceModel { get; init; }

    public string? DeviceSerial { get; init; }

    public string? KitLot { get; init; }

    public string? Speciation { get; init; }

    public decimal? CreatinineValue { get; init; }

    public decimal? ValueCreatinineAdj { get; init; }

    public string? ReasonForTest { get; init; }

    public string? PayerType { get; init; }

    public Guid LabOrganizationId { get; init; }

    public Guid? OrderingProviderId { get; init; }

    public Guid? OrderingFacilityId { get; init; }

    public string? AccessionNumber { get; init; }

    public string? LoincReported { get; init; }

    public string ReportedSnapshotJson { get; init; } = "{}";

    public Guid? ReportedLocationId { get; init; }

    public int? AgeAtCollectionMonths { get; init; }

    public string Population { get; init; } = "unknown";

    public bool IsIndeterminate { get; init; }

    public string ResultRowStatus { get; init; } = "valid";

    public Guid? IntakeBatchId { get; init; }
}

public sealed class SaveLabResultResult
{
    public Guid LabResultId { get; init; }

    public Guid IntakeBatchId { get; init; }
}

public sealed class LabResultDetailRow
{
    public Guid LabResultId { get; init; }

    public Guid? PersonId { get; init; }

    public Guid AnalyteSpecimenId { get; init; }

    public Guid AnalyteId { get; init; }

    public string AnalyteCode { get; init; } = string.Empty;

    public string AnalyteName { get; init; } = string.Empty;

    public string Specimen { get; init; } = string.Empty;

    public DateOnly CollectedDate { get; init; }

    public DateOnly? AnalyzedDate { get; init; }

    public DateOnly? ReportedDate { get; init; }

    public DateTime ReceivedUtc { get; init; }

    public string ResultStatus { get; init; } = string.Empty;

    public Guid? SupersedesLabResultId { get; init; }

    public decimal? ValueReported { get; init; }

    public string? ValueText { get; init; }

    public string Operator { get; init; } = "=";

    public string UnitReported { get; init; } = string.Empty;

    public decimal? ValueCanonical { get; init; }

    public string UnitCanonical { get; init; } = string.Empty;

    public string? SampleType { get; init; }

    public string? TestMethod { get; init; }

    public string? DeviceModel { get; init; }

    public string? DeviceSerial { get; init; }

    public string? KitLot { get; init; }

    public string? Speciation { get; init; }

    public decimal? CreatinineValue { get; init; }

    public decimal? ValueCreatinineAdj { get; init; }

    public string? ReasonForTest { get; init; }

    public string? PayerType { get; init; }

    public string? LabName { get; init; }

    public string? OrderingFacilityName { get; init; }

    public string? OrderingProviderName { get; init; }

    public string? AccessionNumber { get; init; }

    public string? LoincReported { get; init; }

    public string ReportedSnapshotJson { get; init; } = "{}";

    public int? AgeAtCollectionMonths { get; init; }

    public string Population { get; init; } = string.Empty;

    public bool IsIndeterminate { get; init; }

    public string ResultRowStatus { get; init; } = string.Empty;

    public Guid IntakeBatchId { get; init; }

    public string? SourceChannel { get; init; }

    public string? FileName { get; init; }

    public byte[] RowVersion { get; init; } = [];
}

public sealed class ResultListRow
{
    public Guid LabResultId { get; init; }

    public Guid? PersonId { get; init; }

    public string? LastName { get; init; }

    public string? FirstName { get; init; }

    public DateOnly? Dob { get; init; }

    public string AnalyteCode { get; init; } = string.Empty;

    public string Specimen { get; init; } = string.Empty;

    public DateOnly CollectedDate { get; init; }

    public DateTime ReceivedUtc { get; init; }

    public string Operator { get; init; } = "=";

    public decimal? ValueCanonical { get; init; }

    public string UnitCanonical { get; init; } = string.Empty;

    public string? SampleType { get; init; }

    public string? TestMethod { get; init; }

    public string Population { get; init; } = string.Empty;

    public string ResultStatus { get; init; } = string.Empty;

    public string ResultRowStatus { get; init; } = string.Empty;

    public bool IsIndeterminate { get; init; }

    public string? LabName { get; init; }

    public int TotalCount { get; init; }
}

public sealed record ResultListQuery(Guid? AnalyteId, DateOnly? CollectedFrom, DateOnly? CollectedTo, string? Population,
                                     decimal? MinValue, Guid? LabOrganizationId, bool UnmatchedOnly, string SortBy,
                                     bool SortDescending, int PageNumber, int PageSize);

// ------------------------------------------------------------------------------------------------ cases

public sealed class OpenCaseResult
{
    public string CaseNumber { get; init; } = string.Empty;

    public Guid OwningTenantId { get; init; }

    public string? OwningTenantName { get; init; }
}

public sealed class AttachResultOutcome
{
    public string? ClassificationBefore { get; init; }

    public string? ClassificationAfter { get; init; }
}

public sealed class CaseRow
{
    public Guid ExposureCaseId { get; init; }

    public string CaseNumber { get; init; } = string.Empty;

    public Guid PersonId { get; init; }

    public string LastName { get; init; } = string.Empty;

    public string? FirstName { get; init; }

    public DateOnly? Dob { get; init; }

    public string Sex { get; init; } = string.Empty;

    public Guid AnalyteId { get; init; }

    public string AnalyteCode { get; init; } = string.Empty;

    public string AnalyteName { get; init; } = string.Empty;

    public string Population { get; init; } = string.Empty;

    public string Classification { get; init; } = string.Empty;

    public string ComputedClassification { get; init; } = string.Empty;

    public string? OverrideReason { get; init; }

    public string CaseStatus { get; init; } = string.Empty;

    public string ExposureCategory { get; init; } = string.Empty;

    public string WorkflowTemplate { get; init; } = string.Empty;

    public DateOnly OpenedDate { get; init; }

    public DateOnly? ClosedDate { get; init; }

    public string? ClosureReason { get; init; }

    public Guid? AssignedToUserId { get; init; }

    public string? AssignedToName { get; init; }

    public Guid OwningTenantId { get; init; }

    public string? OwningTenantName { get; init; }

    public Guid QualifyingLabResultId { get; init; }

    public Guid? HighestLabResultId { get; init; }

    public decimal? HighestValue { get; init; }

    public string? HighestUnit { get; init; }

    public DateOnly? HighestCollectedDate { get; init; }

    public byte[] RowVersion { get; init; } = [];
}

public sealed class CaseResultRow
{
    public Guid LabResultId { get; init; }

    public DateOnly CollectedDate { get; init; }

    public string Operator { get; init; } = "=";

    public decimal? ValueCanonical { get; init; }

    public string UnitCanonical { get; init; } = string.Empty;

    public string? SampleType { get; init; }

    public string? TestMethod { get; init; }

    public string ResultStatus { get; init; } = string.Empty;

    public string ResultRowStatus { get; init; } = string.Empty;

    public string Specimen { get; init; } = string.Empty;

    public string? LabName { get; init; }
}

public sealed class CaseActivityRow
{
    public Guid CaseActivityId { get; init; }

    public string ActivityType { get; init; } = string.Empty;

    public DateOnly ActivityDate { get; init; }

    public string? Outcome { get; init; }

    public string? Note { get; init; }

    public string? WorkerName { get; init; }

    public string? ContactLastName { get; init; }

    public string? ContactFirstName { get; init; }

    public DateTime CreatedDateUtc { get; init; }
}

public sealed class CaseInvestigationRow
{
    public Guid InvestigationId { get; init; }

    public string InvestigationNumber { get; init; } = string.Empty;

    public string InvestigationType { get; init; } = string.Empty;

    public string InvestigationStatus { get; init; } = string.Empty;

    public DateOnly RequestedDate { get; init; }

    public DateOnly? CompletedDate { get; init; }

    public string? TargetLabel { get; init; }

    public string? TargetCity { get; init; }
}

public sealed class CaseTaskRow
{
    public bool CanEdit { get; init; }

    public Guid WorkTaskId { get; init; }

    public string QueueCode { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string? Detail { get; init; }

    public DateOnly? DueDate { get; init; }

    public bool IsUrgent { get; init; }

    public string TaskStatus { get; init; } = string.Empty;

    public string? AssignedToName { get; init; }
}

public sealed class PrimaryContactRow
{
    public Guid ContactPersonId { get; init; }

    public string LastName { get; init; } = string.Empty;

    public string? FirstName { get; init; }

    public string Role { get; init; } = string.Empty;

    public string? Phone { get; init; }
}

public sealed record CaseDetail(
    CaseRow Case,
    IReadOnlyList<CaseResultRow> Results,
    IReadOnlyList<CaseActivityRow> Activities,
    IReadOnlyList<CaseInvestigationRow> Investigations,
    IReadOnlyList<CaseTaskRow> Tasks,
    PrimaryContactRow? PrimaryContact);

/// <summary>
/// A case list search (issue #50): every filter optional. Sort is opened, case, classification or person; Assigned is
/// "me", "unassigned" or a user id.
/// </summary>
public sealed record CaseListQuery(bool OpenOnly = true, string? Assigned = null, Guid? AnalyteId = null, string? Classification = null,
                                   string? Population = null, Guid? OwningTenantId = null, string? Search = null, string Sort = "opened",
                                   bool Descending = true, int PageNumber = 1, int PageSize = 25);

public sealed class CaseOwnerRow
{
    public Guid TenantId { get; init; }

    public string TenantName { get; init; } = string.Empty;
}

public sealed class CaseListRow
{
    public Guid ExposureCaseId { get; init; }

    public string CaseNumber { get; init; } = string.Empty;

    public Guid PersonId { get; init; }

    public string LastName { get; init; } = string.Empty;

    public string? FirstName { get; init; }

    public DateOnly? Dob { get; init; }

    public string AnalyteCode { get; init; } = string.Empty;

    public string Population { get; init; } = string.Empty;

    public string Classification { get; init; } = string.Empty;

    public string CaseStatus { get; init; } = string.Empty;

    public DateOnly OpenedDate { get; init; }

    public string? AssignedToName { get; init; }

    public string? OwningTenantName { get; init; }

    public int TotalCount { get; init; }
}

// ------------------------------------------------------------------------------------------------ investigations

public sealed class OpenInvestigationRow
{
    public Guid InvestigationId { get; init; }

    public string InvestigationNumber { get; init; } = string.Empty;

    public string InvestigationStatus { get; init; } = string.Empty;

    public DateOnly RequestedDate { get; init; }

    public int LinkedCases { get; init; }
}

public sealed class InvestigationRow
{
    public Guid InvestigationId { get; init; }

    public string InvestigationNumber { get; init; } = string.Empty;

    public string InvestigationType { get; init; } = string.Empty;

    public Guid? LocationId { get; init; }

    public Guid? OrganizationId { get; init; }

    public Guid? WorksiteLocationId { get; init; }

    public Guid? ExposureSourceId { get; init; }

    public string InvestigationStatus { get; init; } = string.Empty;

    public Guid? AssignedToUserId { get; init; }

    public string? AssignedToName { get; init; }

    public DateOnly RequestedDate { get; init; }

    public DateOnly? ScheduledDate { get; init; }

    public DateOnly? CompletedDate { get; init; }

    public string? Findings { get; init; }

    public string? RegulatoryAction { get; init; }

    public DateOnly? ReferralDate { get; init; }

    public string? ReferralOutcome { get; init; }

    public byte[] RowVersion { get; init; } = [];

    public string? OwningTenantName { get; init; }

    public string? AddressLine1 { get; init; }

    public string? Unit { get; init; }

    public string? City { get; init; }

    public string? State { get; init; }

    public string? Zip5 { get; init; }

    public int? YearBuilt { get; init; }

    public string? OrganizationName { get; init; }

    public string? WorksiteAddress { get; init; }

    public string? WorksiteCity { get; init; }

    public string? SourceType { get; init; }

    public string? SourceDescription { get; init; }
}

public sealed class InvestigationCaseRow
{
    public Guid? ExposureCaseId { get; init; }

    public string? CaseNumber { get; init; }

    public Guid? PersonId { get; init; }

    public string? LastName { get; init; }

    public string? FirstName { get; init; }

    public string? AnalyteCode { get; init; }

    public string? Classification { get; init; }

    public string? CaseStatus { get; init; }

    public string? JobTitle { get; init; }
}

public sealed class EnvironmentalSampleRow
{
    public DateTime ModifiedDateUtc { get; init; }
    public Guid EnvironmentalSampleId { get; init; }

    public string SampleType { get; init; } = string.Empty;

    public string? RoomOrComponent { get; init; }

    public string? Surface { get; init; }

    public decimal SampleValue { get; init; }

    public string Unit { get; init; } = string.Empty;

    public DateOnly SampleDate { get; init; }

    public bool? ExceedsStandard { get; init; }

    public decimal? StandardValue { get; init; }

    public string? StandardUnit { get; init; }

    public string? Authority { get; init; }

    public string? Notes { get; init; }
}

public sealed record InvestigationDetail(InvestigationRow Investigation, IReadOnlyList<InvestigationCaseRow> Cases,
                                         IReadOnlyList<EnvironmentalSampleRow> Samples);

public sealed record InvestigationQuery(bool OpenOnly = true, string? Search = null, string? Assigned = null,
    string? Status = null, string? Type = null, string Sort = "requested", bool Descending = true, int PageNumber = 1);

public sealed class InvestigationListRow
{
    public Guid InvestigationId { get; init; }

    public string InvestigationNumber { get; init; } = string.Empty;

    public string InvestigationType { get; init; } = string.Empty;

    public string InvestigationStatus { get; init; } = string.Empty;

    public DateOnly RequestedDate { get; init; }

    public DateOnly? ScheduledDate { get; init; }

    public string? TargetLabel { get; init; }

    public string? AssignedToName { get; init; }

    public int TotalCount { get; init; }
}

public sealed record InvestigationCreate(Guid InvestigationId, string InvestigationType, Guid? LocationId,
                                         Guid? OrganizationId, Guid? WorksiteLocationId, Guid? ExposureSourceId,
                                         Guid? ExposureCaseId, Guid? EmploymentId, DateOnly RequestedDate);

public sealed record InvestigationUpdate(Guid InvestigationId, string InvestigationStatus, Guid? AssignedToUserId,
                                         DateOnly? ScheduledDate, DateOnly? CompletedDate, string? Findings,
                                         string? RegulatoryAction, DateOnly? ReferralDate, string? ReferralOutcome,
                                         byte[] RowVersion);

public sealed record EnvironmentalSampleSave(Guid EnvironmentalSampleId, Guid InvestigationId, string SampleType,
                                             string? RoomOrComponent, string? Surface, decimal SampleValue, string Unit,
                                             DateOnly SampleDate, string? Notes, bool IsDeleted, DateTime? OriginalModifiedUtc = null);
