using System.Text.Json;
using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Security;
using Merit.Domain.Ids;

namespace Merit.Application.Intake;

/// <summary>
/// Intake from the web side: receive a file and queue it (long work runs in the worker), show batch status, and the
/// held-records queue built around fixing causes -- grouped by reason and reported value, released in bulk.
/// </summary>
public sealed class IntakeService(IIntakeData intake, IWorkData work, Services.AuditService audit, ICurrentSession session)
{
    public const int MaxFileBytes = 20 * 1024 * 1024;

    public async Task<IReadOnlyList<IntakeProfileListRow>> ProfilesAsync(CancellationToken ct = default)
    {
        session.Require(Permissions.IntakeUpload);
        return await intake.ListProfilesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Stores the file unchanged and queues the intake job; returns the batch id the screen polls.</summary>
    public async Task<Guid> UploadAsync(Guid intakeProfileId, string fileName, string content, CancellationToken ct = default)
    {
        session.Require(Permissions.IntakeUpload);
        var profile = await intake.GetProfileAsync(intakeProfileId, ct).ConfigureAwait(false)
                      ?? throw new ValidationException("IntakeProfileId", "Choose the sender's intake profile.");
        var v = new Validator();
        v.When(string.IsNullOrWhiteSpace(content), "File", "The file is empty.");
        v.When(content.Length > MaxFileBytes, "File", "The file is larger than 20 MB; split it or send it through the secure file drop.");
        v.When(profile.FileType is not ("csv" or "tsv" or "pipe"), "IntakeProfileId",
            $"This release parses delimited files; the profile is for {profile.FileType}.");
        v.ThrowIfInvalid();

        Guid batchId = TimeOrderedId.New();
        await intake.CreateBatchAsync(batchId, intakeProfileId, Path.GetFileName(fileName), content, TimeOrderedId.New(), ct)
            .ConfigureAwait(false);
        return batchId;
    }

    public async Task<IReadOnlyList<IntakeBatchRow>> BatchesAsync(CancellationToken ct = default)
    {
        session.Require(Permissions.IntakeUpload);
        return await intake.ListBatchesAsync(50, ct).ConfigureAwait(false);
    }

    public async Task<IntakeBatchRow> BatchAsync(Guid batchId, CancellationToken ct = default)
    {
        session.Require(Permissions.IntakeUpload);
        return await intake.GetBatchAsync(batchId, false, ct).ConfigureAwait(false) ?? throw new NotFoundException();
    }

    public async Task<IReadOnlyList<HeldGroupRow>> HeldGroupsAsync(CancellationToken ct = default)
    {
        session.Require(Permissions.ResultReleaseHold);
        return await intake.ListHeldGroupsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Held rows, for every cause or for one group. A group is a reason, reported value and sender together, so with
    /// <paramref name="exactGroup"/> a null value or sender means "none", and bulk actions on the page stay inside the group.
    /// </summary>
    public async Task<IReadOnlyList<HeldRowRow>> HeldRowsAsync(string? reasonCode, string? reportedValue, Guid? senderOrganizationId,
                                                              bool exactGroup, Guid? batchId, int pageNumber, int pageSize,
                                                              CancellationToken ct = default)
    {
        session.Require(Permissions.ResultReleaseHold);
        if (exactGroup && string.IsNullOrWhiteSpace(reasonCode))
        {
            throw new ValidationException("reason", "Choose a held group: its reason is required.");
        }

        var rows = await intake.ListHeldRowsAsync(reasonCode, reportedValue, senderOrganizationId, exactGroup, batchId, pageNumber, pageSize, ct)
            .ConfigureAwait(false);
        await audit.ListedAsync("stagingRow", rows.Count, ct).ConfigureAwait(false);
        return rows;
    }

    /// <summary>
    /// Edit in place: corrections are kept beside the reported values, which never change. The fields given are merged
    /// into what is stored; a field given as null is cleared, and fields left out are kept.
    /// </summary>
    public async Task CorrectAsync(Guid stagingRowId, IReadOnlyDictionary<string, string?> corrections, CancellationToken ct = default)
    {
        session.Require(Permissions.ResultReleaseHold);
        await intake.SaveCorrectionAsync(stagingRowId, JsonSerializer.Serialize(corrections), ct).ConfigureAwait(false);
    }

    /// <summary>Releasing reruns validation and matching in the worker.</summary>
    public async Task<int> ReleaseAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        session.Require(Permissions.ResultReleaseHold);
        new Validator().When(ids.Count == 0, "ids", "Select at least one held record.").ThrowIfInvalid();
        return await intake.ReleaseRowsAsync(ids, ct).ConfigureAwait(false);
    }

    /// <summary>Rejecting requires a reason and keeps the row.</summary>
    public async Task<int> RejectAsync(IReadOnlyList<Guid> ids, string? reason, CancellationToken ct = default)
    {
        session.Require(Permissions.ResultReleaseHold);
        new Validator().When(ids.Count == 0, "ids", "Select at least one held record.")
            .Required("reason", reason, "Reason").ThrowIfInvalid();
        int n = await intake.RejectRowsAsync(ids, reason!.Trim(), ct).ConfigureAwait(false);
        _ = work;
        return n;
    }
}
