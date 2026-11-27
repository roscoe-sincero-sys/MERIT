using System.Security.Cryptography;
using System.Text;
using Merit.Application.Data;

namespace Merit.Worker;

/// <summary>
/// Periodically generates a SQL Server database ledger digest (<c>logs.uspVerifyAuditLedger</c>), persists it
/// write-once to <c>LedgerVerification:DigestDirectory</c> when configured (external WORM / append-only mount),
/// and verifies both the fresh digest and the most recent previously stored external digest against the database (#334).
/// </summary>
public sealed partial class AuditLedgerVerifier(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ILogger<AuditLedgerVerifier> logger,
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
        try
        {
            using var scope = scopes.CreateScope();
            var data = scope.ServiceProvider.GetRequiredService<ISecurityMonitoringData>();
            string? digestDir = configuration["LedgerVerification:DigestDirectory"];

            // 1. If an external digest directory is configured, check for archive shrinkage (deletion/rollback)
            //    and verify up to the last 3 archived digests against the database ledger (I20).
            if (!string.IsNullOrWhiteSpace(digestDir) && Directory.Exists(digestDir))
            {
                var priorFiles = Directory.EnumerateFiles(digestDir, "merit-ledger-digest-*.json")
                    .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                    .ToList();
                if (_lastDigestFileCount > 0 && priorFiles.Count < _lastDigestFileCount)
                {
                    DigestArchiveShrank(logger, _lastDigestFileCount, priorFiles.Count);
                }

                foreach (string priorFile in priorFiles.Take(3))
                {
                    string priorJson = await File.ReadAllTextAsync(priorFile, Encoding.UTF8, ct).ConfigureAwait(false);
                    var priorResult = await data.VerifyAuditLedgerAsync(priorJson, ct).ConfigureAwait(false);
                    if (!priorResult.Verified)
                    {
                        LedgerVerificationFailed(logger, Path.GetFileName(priorFile), priorResult.Detail);
                        return;
                    }
                }
            }

            // 2. Generate and verify a fresh digest, then archive it write-once if DigestDirectory is configured.
            var fresh = await data.VerifyAuditLedgerAsync(null, ct).ConfigureAwait(false);
            if (!fresh.Verified)
            {
                LedgerVerificationFailed(logger, "current", fresh.Detail);
                return;
            }

            if (!string.IsNullOrWhiteSpace(digestDir) && !string.IsNullOrWhiteSpace(fresh.DigestJson))
            {
                Directory.CreateDirectory(digestDir);
                byte[] utf8 = Encoding.UTF8.GetBytes(fresh.DigestJson);
                string hashPrefix = Convert.ToHexString(SHA256.HashData(utf8).AsSpan(0, 6)).ToLowerInvariant();
                string fileName = $"merit-ledger-digest-{fresh.VerifiedUtc:yyyyMMddTHHmmssfffZ}-block{fresh.LastVerifiedBlockId ?? 0}-{hashPrefix}.json";
                string fullPath = Path.Combine(digestDir, fileName);
                await using var fs = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await fs.WriteAsync(utf8, ct).ConfigureAwait(false);
                _lastDigestFileCount = Directory.EnumerateFiles(digestDir, "merit-ledger-digest-*.json").Count();
            }
            else if (!fresh.HasAutomaticDigestStorage
                     && (environment is null || (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))))
            {
                DigestStorageUnconfigured(logger);
            }

            LedgerVerified(logger, fresh.LastVerifiedBlockId ?? -1, fresh.Detail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LedgerVerificationError(logger, ex.GetType().Name);
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
}
