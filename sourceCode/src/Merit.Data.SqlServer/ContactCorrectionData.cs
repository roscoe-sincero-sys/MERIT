using Merit.Application.Data;

namespace Merit.Data.SqlServer;

public sealed class ContactCorrectionData(Db db) : IContactCorrectionData
{
    public Task<IReadOnlyList<ContactCorrectionRow>> ListAsync(Guid? personId, CancellationToken ct)
        => db.QueryAsync<ContactCorrectionRow>("dbo.uspListContactCorrections", new { personId }, ct);
    public Task RequestAsync(ContactCorrectionInput request, CancellationToken ct)
        => db.ExecuteAsync("dbo.uspRequestContactCorrection", request, ct);
    public Task ReviewAsync(Guid contactCorrectionId, string requestStatus, string reviewNote, byte[]? rowVersion, CancellationToken ct)
        => db.ExecuteAsync("dbo.uspReviewContactCorrection", new { contactCorrectionId, requestStatus, reviewNote, rowVersion }, ct);
}
