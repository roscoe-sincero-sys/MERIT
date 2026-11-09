namespace Merit.Application.Data;

public sealed class ContactCorrectionInput
{
    public Guid ContactCorrectionId { get; set; }
    public Guid PersonId { get; set; }
    public Guid CaregiverId { get; set; }
    public string ContactType { get; set; } = "phone";
    public string ProposedValue { get; set; } = "";
    public string SourceNote { get; set; } = "";
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Zip5 { get; set; }
}
public sealed class ContactCorrectionRow
{
    public Guid ContactCorrectionId { get; init; }
    public Guid PersonId { get; init; }
    public Guid CaregiverId { get; init; }
    public string PatientName { get; init; } = "";
    public string CaregiverName { get; init; } = "";
    public string ContactType { get; init; } = "";
    public string ProposedValue { get; init; } = "";
    public string? City { get; init; }
    public string? State { get; init; }
    public string? Zip5 { get; init; }
    public string SourceNote { get; init; } = "";
    public string RequestStatus { get; init; } = "";
    public string ResponsibleAgency { get; init; } = "";
    public string? ReviewNote { get; init; }
    public bool CanReview { get; init; }
    public byte[] RowVersion { get; init; } = [];
}
public interface IContactCorrectionData
{
    Task<IReadOnlyList<ContactCorrectionRow>> ListAsync(Guid? personId, CancellationToken ct);
    Task RequestAsync(ContactCorrectionInput request, CancellationToken ct);
    Task ReviewAsync(Guid contactCorrectionId, string requestStatus, string reviewNote, byte[]? rowVersion, CancellationToken ct);
}
