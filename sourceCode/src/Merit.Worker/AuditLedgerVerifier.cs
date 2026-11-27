using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Merit.Application.Data;

namespace Merit.Worker;

/// <summary>
/// Periodically verifies the SQL Server audit ledger against externally anchored digests (#334):
/// - When <c>LedgerVerification:DigestDirectory</c> is configured (pre-provisioned external WORM mount), verifies up
///   to the last 3 archived digests, fails visibly if the mount disappears or a file is empty/malformed, detects
///   archive shrinkage across restarts, and atomically publishes complete digests write-once (staging temp + move).
/// - When <c>DigestDirectory</c> is unset and SQL Server automatic digest storage is active, invokes
///   <c>sys.sp_verify_database_ledger_from_digest_storage</c>.
/// - Persists every run's outcome in <c>logs.ledgerVerificationRun</c> (surfaced in the operational review view) and
///   raises a critical <c>logs.securityFinding</c> on any SQL or archive failure.
/// </summary>
public sealed partial class AuditLedgerVerifier(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ILogger<AuditLedgerVerifier> logger,
    ISecurityAlertNotifier? notifier = null,
    IHostEnvironment? environment = null) : BackgroundService
{
    private int _lastDigestFileCount;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            int intervalHours = Math.Clamp(configuration.GetValue("LedgerVerification:IntervalHours", 24), 1, 168);
            await RunVerificationCycleAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromHours(intervalHours), stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task RunVerificationCycleAsync(CancellationToken ct)
    {
        string? digestDir = configuration["LedgerVerification:DigestDirectory"];
        bool hasDigestDir = !string.IsNullOrWhiteSpace(digestDir);
        string anchorSource = hasDigestDir ? "digestDirectory" : "unanchored";
        var attemptedAlertIds = new HashSet<Guid>();

        try
        {
            using var scope = scopes.CreateScope();
            var data = scope.ServiceProvider.GetRequiredService<ISecurityMonitoringData>();

            // Drain any undelivered ledger alerts persisted by prior failed cycles or interrupted runs (#354).
            await DrainPendingLedgerAlertsAsync(data, attemptedAlertIds, ct).ConfigureAwait(false);

            var state = await data.GetLedgerVerificationStateAsync(ct).ConfigureAwait(false);

            if (hasDigestDir)
            {
                await RunDigestDirectoryCycleAsync(data, digestDir!, state.LastGoodDigestFileCount, attemptedAlertIds, ct).ConfigureAwait(false);
                return;
            }

            // No DigestDirectory configured: select automatic storage directly from metadata without a redundant verify.
            if (state.HasAutomaticDigestStorage)
            {
                anchorSource = "automaticStorage";
                var autoResult = await data.VerifyAuditLedgerAsync(null, useAutomaticStorage: true, requireDigest: false, ct).ConfigureAwait(false);
                await data.RecordLedgerVerificationOutcomeAsync(anchorSource, autoResult.Verified, autoResult.LastVerifiedBlockId, null, autoResult.Detail, ct).ConfigureAwait(false);
                if (!autoResult.Verified)
                {
                    LedgerVerificationFailed(logger, anchorSource, autoResult.Detail);
                    await DrainPendingLedgerAlertsAsync(data, attemptedAlertIds, ct).ConfigureAwait(false);
                    return;
                }

                LedgerVerified(logger, autoResult.LastVerifiedBlockId ?? -1, autoResult.Detail);
                return;
            }

            // Unanchored (neither DigestDirectory nor active automatic digest storage configured).
            var unanchored = await data.VerifyAuditLedgerAsync(null, useAutomaticStorage: false, requireDigest: false, ct).ConfigureAwait(false);
            await data.RecordLedgerVerificationOutcomeAsync("unanchored", unanchored.Verified, unanchored.LastVerifiedBlockId, null, unanchored.Detail, ct).ConfigureAwait(false);
            if (!unanchored.Verified)
            {
                LedgerVerificationFailed(logger, "unanchored", unanchored.Detail);
                await DrainPendingLedgerAlertsAsync(data, attemptedAlertIds, ct).ConfigureAwait(false);
                return;
            }

            if (environment is null || (!environment.IsDevelopment() && !environment.IsEnvironment("Testing")))
            {
                DigestStorageUnconfigured(logger);
            }

            LedgerVerified(logger, unanchored.LastVerifiedBlockId ?? -1, unanchored.Detail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LedgerVerificationError(logger, ex.GetType().Name);
            await TryRecordFailureAndNotifyAsync(anchorSource, $"Verification threw {ex.GetType().Name}.", attemptedAlertIds, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task RunDigestDirectoryCycleAsync(
        ISecurityMonitoringData data,
        string digestDir,
        int? durableLastGoodCount,
        HashSet<Guid> attemptedAlertIds,
        CancellationToken ct)
    {
        // Never auto-create a missing provisioned mount — treat disappearance as a visible archive failure.
        if (!Directory.Exists(digestDir))
        {
            const string msg = "Configured ledger digest directory is missing or unmounted.";
            LedgerVerificationFailed(logger, "digestDirectory", msg);
            await data.RecordLedgerVerificationOutcomeAsync("digestDirectory", false, null, null, msg, ct).ConfigureAwait(false);
            await DrainPendingLedgerAlertsAsync(data, attemptedAlertIds, ct).ConfigureAwait(false);
            return;
        }

        var priorFiles = Directory.EnumerateFiles(digestDir, "merit-ledger-digest-*.json")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

        // Check durable last-known-good baseline (preserved across failures and restarts) BEFORE generating or publishing.
        int expectedMinCount = Math.Max(_lastDigestFileCount, durableLastGoodCount ?? 0);
        if (expectedMinCount > 0 && priorFiles.Count < expectedMinCount)
        {
            string msg = $"Digest archive shrank from {expectedMinCount} to {priorFiles.Count} file(s); refusing to publish replacement digest.";
            DigestArchiveShrank(logger, expectedMinCount, priorFiles.Count);
            await data.RecordLedgerVerificationOutcomeAsync("digestDirectory", false, null, priorFiles.Count, msg, ct).ConfigureAwait(false);
            await DrainPendingLedgerAlertsAsync(data, attemptedAlertIds, ct).ConfigureAwait(false);
            return;
        }

        foreach (string priorFile in priorFiles.Take(3))
        {
            string priorJson = await File.ReadAllTextAsync(priorFile, Encoding.UTF8, ct).ConfigureAwait(false);
            if (!IsValidDigestJson(priorJson))
            {
                string msg = $"Archived ledger digest '{Path.GetFileName(priorFile)}' is empty or malformed.";
                LedgerVerificationFailed(logger, Path.GetFileName(priorFile), msg);
                await data.RecordLedgerVerificationOutcomeAsync("digestDirectory", false, null, priorFiles.Count, msg, ct).ConfigureAwait(false);
                await DrainPendingLedgerAlertsAsync(data, attemptedAlertIds, ct).ConfigureAwait(false);
                return;
            }

            var priorResult = await data.VerifyAuditLedgerAsync(priorJson, useAutomaticStorage: false, requireDigest: true, ct).ConfigureAwait(false);
            if (!priorResult.Verified)
            {
                LedgerVerificationFailed(logger, Path.GetFileName(priorFile), priorResult.Detail);
                await data.RecordLedgerVerificationOutcomeAsync("digestDirectory", false, priorResult.LastVerifiedBlockId, priorFiles.Count, priorResult.Detail, ct).ConfigureAwait(false);
                await DrainPendingLedgerAlertsAsync(data, attemptedAlertIds, ct).ConfigureAwait(false);
                return;
            }
        }

        var fresh = await data.VerifyAuditLedgerAsync(null, useAutomaticStorage: false, requireDigest: false, ct).ConfigureAwait(false);
        if (!fresh.Verified || string.IsNullOrWhiteSpace(fresh.DigestJson))
        {
            string detail = string.IsNullOrWhiteSpace(fresh.Detail) ? "Fresh ledger digest generation failed." : fresh.Detail;
            LedgerVerificationFailed(logger, "current", detail);
            await data.RecordLedgerVerificationOutcomeAsync("digestDirectory", false, fresh.LastVerifiedBlockId, priorFiles.Count, detail, ct).ConfigureAwait(false);
            await DrainPendingLedgerAlertsAsync(data, attemptedAlertIds, ct).ConfigureAwait(false);
            return;
        }

        await PublishDigestAtomicallyAsync(digestDir, fresh, ct).ConfigureAwait(false);

        int newCount = Directory.EnumerateFiles(digestDir, "merit-ledger-digest-*.json").Count();
        _lastDigestFileCount = Math.Max(newCount, expectedMinCount);
        await data.RecordLedgerVerificationOutcomeAsync("digestDirectory", true, fresh.LastVerifiedBlockId, newCount, fresh.Detail, ct).ConfigureAwait(false);
        LedgerVerified(logger, fresh.LastVerifiedBlockId ?? -1, fresh.Detail);
    }

    private async Task DrainPendingLedgerAlertsAsync(ISecurityMonitoringData data, HashSet<Guid> attemptedAlertIds, CancellationToken ct)
    {
        if (notifier is null)
        {
            return;
        }

        try
        {
            var pending = await data.GetPendingLedgerAlertsAsync(ct).ConfigureAwait(false);
            foreach (var finding in pending)
            {
                if (!attemptedAlertIds.Add(finding.SecurityFindingId))
                {
                    continue;
                }

                bool delivered = await notifier.NotifyAsync([finding], ct).ConfigureAwait(false);
                if (delivered)
                {
                    await data.MarkSecurityFindingWebhookDeliveredAsync(finding.SecurityFindingId, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LedgerAlertOutboxError(logger, ex.GetType().Name);
        }
    }

    private async Task TryRecordFailureAndNotifyAsync(string anchorSource, string detail, HashSet<Guid> attemptedAlertIds, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var data = scope.ServiceProvider.GetRequiredService<ISecurityMonitoringData>();
            await data.RecordLedgerVerificationOutcomeAsync(anchorSource, false, null, null, detail, ct).ConfigureAwait(false);
            await DrainPendingLedgerAlertsAsync(data, attemptedAlertIds, ct).ConfigureAwait(false);
        }
        catch
        {
            // Already logged by caller.
        }
    }

    internal static bool IsValidDigestJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("database_name", out _)
                   && doc.RootElement.TryGetProperty("hash", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static async Task PublishDigestAtomicallyAsync(string digestDir, AuditLedgerVerificationRow fresh, CancellationToken ct)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(fresh.DigestJson!);
        string hashPrefix = Convert.ToHexString(SHA256.HashData(utf8).AsSpan(0, 6)).ToLowerInvariant();
        string fileName = $"merit-ledger-digest-{fresh.VerifiedUtc:yyyyMMddTHHmmssfffZ}-block{fresh.LastVerifiedBlockId ?? 0}-{hashPrefix}.json";
        string finalPath = Path.Combine(digestDir, fileName);
        string tempPath = Path.Combine(digestDir, $".tmp-{Guid.NewGuid():N}.part");

        try
        {
            await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await fs.WriteAsync(utf8, ct).ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }

            ct.ThrowIfCancellationRequested();
            File.Move(tempPath, finalPath, overwrite: false);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch
            {
                // Best-effort cleanup of incomplete staging file.
            }
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Audit ledger verified up to block {BlockId}: {Detail}")]
    private static partial void LedgerVerified(ILogger logger, long blockId, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "AUDIT LEDGER VERIFICATION FAILED (digest={DigestSource}): {Detail}")]
    private static partial void LedgerVerificationFailed(ILogger logger, string digestSource, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audit ledger digests are not anchored outside SQL Server: configure LedgerVerification:DigestDirectory on WORM storage or SQL Server automatic digest storage (#334).")]
    private static partial void DigestStorageUnconfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "AUDIT LEDGER DIGEST ARCHIVE SHRANK: expected at least {PreviousCount} file(s), found {CurrentCount}. Check WORM mount integrity.")]
    private static partial void DigestArchiveShrank(ILogger logger, int previousCount, int currentCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "Audit ledger verification threw an unexpected exception ({ExceptionType})")]
    private static partial void LedgerVerificationError(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audit ledger alert outbox drain failed ({ExceptionType}); pending alerts will retry on the next cycle.")]
    private static partial void LedgerAlertOutboxError(ILogger logger, string exceptionType);
}
