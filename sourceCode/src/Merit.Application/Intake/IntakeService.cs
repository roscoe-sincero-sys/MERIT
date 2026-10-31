using System.Security.Cryptography;
using System.Text;
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

    /// <summary>
    /// Stores the file unchanged and queues the intake job; returns the batch id the screen polls. A file already received
    /// for the same profile is refused with <see cref="FileAlreadyReceivedException"/>, so a mistaken second upload does
    /// not hold every row twice (issue #20); <paramref name="uploadAgain"/> accepts a laboratory's genuine re-send.
    /// </summary>
    public async Task<Guid> UploadAsync(Guid intakeProfileId, string fileName, string content, bool uploadAgain = false,
                                        CancellationToken ct = default)
    {
        session.Require(Permissions.IntakeUpload);
        var profile = await intake.GetProfileAsync(intakeProfileId, ct).ConfigureAwait(false)
                      ?? throw new ValidationException("IntakeProfileId", "Choose the sender's intake profile.");
        var v = new Validator();
        v.When(string.IsNullOrWhiteSpace(content), "File", "The file is empty.");
        v.When(Encoding.UTF8.GetByteCount(content) > MaxFileBytes, "File", "The file is larger than 20 MB; split it or send it through the secure file drop.");
        v.When(profile.FileType is not ("csv" or "tsv" or "pipe"), "IntakeProfileId",
            $"This release parses delimited files; the profile is for {profile.FileType}.");
        v.ThrowIfInvalid();

        string hash = ContentHash(content);
        if (!uploadAgain && await intake.FindBatchByContentAsync(intakeProfileId, hash, ct).ConfigureAwait(false) is { } earlier)
        {
            throw new FileAlreadyReceivedException(earlier.IntakeBatchId, earlier.FileName, earlier.ReceivedUtc);
        }

        Guid batchId = TimeOrderedId.New();
        await intake.CreateBatchAsync(batchId, intakeProfileId, Path.GetFileName(fileName), content, hash, TimeOrderedId.New(), ct)
            .ConfigureAwait(false);
        return batchId;
    }

    /// <summary>The file's fingerprint: SHA-256 of its UTF-8 bytes, upper-case hex.</summary>
    public static string ContentHash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

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

    public async Task<IReadOnlyList<HeldGroupRow>> HeldGroupsAsync(Guid? batchId = null, CancellationToken ct = default)
    {
        session.Require(Permissions.ResultReleaseHold);
        return await intake.ListHeldGroupsAsync(batchId, ct).ConfigureAwait(false);
    }

    public async Task<HeldOverview> HeldOverviewAsync(Guid? batchId, CancellationToken ct = default)
    {
        session.Require(Permissions.ResultReleaseHold);
        return await intake.GetHeldOverviewAsync(batchId, ct).ConfigureAwait(false);
    }

    public async Task<HeldSummaryRow> HeldSummaryAsync(Guid? batchId, CancellationToken ct = default)
    {
        session.Require(Permissions.ResultReleaseHold);
        return await intake.GetHeldSummaryAsync(batchId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Held rows, for every cause or for one group. A group is a reason, reported value and sender together, so with
    /// <paramref name="exactGroup"/> a null value or sender means "none", and bulk actions on the page stay inside the group.
    /// </summary>
    public async Task<(IReadOnlyList<HeldRowRow> Rows, int Total, int Page)> HeldRowsAsync(string? reasonCode, string? reportedValue, Guid? senderOrganizationId,
                                                              bool exactGroup, Guid? batchId, int pageNumber, int pageSize,
                                                              CancellationToken ct = default)
    {
        session.Require(Permissions.ResultReleaseHold);
        if (exactGroup && string.IsNullOrWhiteSpace(reasonCode))
        {
            throw new ValidationException("reason", "Choose a held group: its reason is required.");
        }

        var result = await Paging.ReadAsync(pageNumber, pageSize,
            p => intake.ListHeldRowsAsync(reasonCode, reportedValue, senderOrganizationId, exactGroup, batchId, p, pageSize, ct),
            r => r.TotalCount).ConfigureAwait(false);
        await audit.ListedAsync("stagingRow", result.Rows.Count, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Edit in place: corrections are kept beside the reported values, which never change. The fields given are merged
    /// into what is stored; a field given as null is cleared, and fields left out are kept.
    /// </summary>
    public async Task CorrectAsync(Guid stagingRowId, IReadOnlyDictionary<string, string?> corrections, CancellationToken ct = default)
    {
        session.Require(Permissions.ResultReleaseHold);
        new Validator().When(corrections.ContainsKey("matchPersonId") || corrections.ContainsKey("matchNewPerson"),
            "Corrections", "Use Match review to choose an existing or new person.").ThrowIfInvalid();
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
