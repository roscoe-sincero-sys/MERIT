namespace Merit.Application.Data;

// Rows for person matching review, clusters, the case map and the report library. Shapes follow the stored procedure
// result sets column for column.

public sealed class MatchReviewRow
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

    public string? ReasonMessage { get; init; }

    public bool IsEscalated { get; init; }

    public string? EscalationNote { get; init; }

    public int TotalCount { get; init; }
}

public sealed class CandidatePersonRow
{
    public Guid PersonId { get; init; }

    public string LastName { get; init; } = string.Empty;

    public string? FirstName { get; init; }

    public string? MiddleName { get; init; }

    public DateOnly? Dob { get; init; }

    public string Sex { get; init; } = "unknown";

    public bool IsPlaceholderName { get; init; }

    public string? Aliases { get; init; }

    public int ResultCount { get; init; }

    public DateOnly? LastCollectedDate { get; init; }

    public string? OpenCaseNumbers { get; init; }
}

public sealed class CandidatePlaceRow
{
    public Guid PersonId { get; init; }

    public string Role { get; init; } = string.Empty;

    public bool IsPrimary { get; init; }

    public DateOnly StartDate { get; init; }

    public DateOnly? EndDate { get; init; }

    public string? AddressLine1 { get; init; }

    public string? Unit { get; init; }

    public string City { get; init; } = string.Empty;

    public string? State { get; init; }

    public string? Zip5 { get; init; }
}

public sealed class CandidateResultRow
{
    public Guid PersonId { get; init; }

    public DateOnly CollectedDate { get; init; }

    public string AnalyteCode { get; init; } = string.Empty;

    public string Operator { get; init; } = "=";

    public decimal? ValueCanonical { get; init; }

    public string UnitCanonical { get; init; } = string.Empty;

    public string? SampleType { get; init; }

    public string? LabName { get; init; }
}

public sealed record CandidateDetails(IReadOnlyList<CandidatePersonRow> Persons, IReadOnlyList<CandidatePlaceRow> Places,
                                      IReadOnlyList<CandidateResultRow> Results);

public sealed class ClusterListRow
{
    public Guid ClusterId { get; init; }

    public string ClusterType { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string ClusterStatus { get; init; } = "open";

    public DateOnly WindowStart { get; init; }

    public DateOnly WindowEnd { get; init; }

    public string? Notes { get; init; }

    public DateTime CreatedDateUtc { get; init; }

    public string? OwningTenantName { get; init; }

    public int MemberCount { get; init; }

    public int TotalCount { get; init; }
}

public sealed class ClusterRow
{
    public Guid ClusterId { get; init; }

    public string ClusterType { get; init; } = string.Empty;

    public Guid SharedKey { get; init; }

    public string Title { get; init; } = string.Empty;

    public string ClusterStatus { get; init; } = "open";

    public DateOnly WindowStart { get; init; }

    public DateOnly WindowEnd { get; init; }

    public string? Notes { get; init; }

    public DateTime CreatedDateUtc { get; init; }

    public string? OwningTenantName { get; init; }

    public byte[] RowVersion { get; init; } = [];
}

public sealed class ClusterMemberRow
{
    public Guid CaseClusterId { get; init; }

    public Guid ExposureCaseId { get; init; }

    public string CaseNumber { get; init; } = string.Empty;

    public Guid PersonId { get; init; }

    public string LastName { get; init; } = string.Empty;

    public string? FirstName { get; init; }

    public string AnalyteCode { get; init; } = string.Empty;

    public string Population { get; init; } = "unknown";

    public string Classification { get; init; } = string.Empty;

    public string CaseStatus { get; init; } = string.Empty;

    public DateOnly OpenedDate { get; init; }

    public decimal? HighestValue { get; init; }

    public string? HighestUnit { get; init; }

    public DateOnly JoinedDate { get; init; }

    public DateOnly? LeftDate { get; init; }

    public Guid? LocationId { get; init; }

    public string? AddressLine1 { get; init; }

    public string? Unit { get; init; }

    public string? City { get; init; }

    public decimal? Latitude { get; init; }

    public decimal? Longitude { get; init; }
}

public sealed class ClusterTaskRow
{
    public Guid WorkTaskId { get; init; }

    public string QueueCode { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string? Detail { get; init; }

    public DateOnly? DueDate { get; init; }

    public bool IsUrgent { get; init; }

    public string? AssignedToName { get; init; }
}

public sealed record ClusterDetail(ClusterRow Cluster, IReadOnlyList<ClusterMemberRow> Members, IReadOnlyList<ClusterTaskRow> Tasks);

public sealed class CaseMapRow
{
    public Guid ExposureCaseId { get; init; }

    public string CaseNumber { get; init; } = string.Empty;

    public Guid PersonId { get; init; }

    public string LastName { get; init; } = string.Empty;

    public string? FirstName { get; init; }

    public string AnalyteCode { get; init; } = string.Empty;

    public string Population { get; init; } = "unknown";

    public string Classification { get; init; } = string.Empty;

    public string CaseStatus { get; init; } = string.Empty;

    public DateOnly OpenedDate { get; init; }

    public decimal? HighestValue { get; init; }

    public string? HighestUnit { get; init; }

    public Guid? LocationId { get; init; }

    public string? AddressLine1 { get; init; }

    public string? City { get; init; }

    public string? CountyFips { get; init; }

    public decimal? Latitude { get; init; }

    public decimal? Longitude { get; init; }

    public string? GeocodeQuality { get; init; }

    public int OpenClusterCount { get; init; }
}

/// <summary>A report's rows as name/value pairs, column order kept, as the procedure returned them.</summary>
public sealed record ReportData(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows);
