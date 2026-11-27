using System.Data;
using Dapper;
using Merit.Application.Data;

namespace Merit.Data.SqlServer;

public sealed class SecurityMonitoringData(Db db) : ISecurityMonitoringData
{
    private static DynamicParameters ExportParams(DateTime fromUtc, DateTime toUtc, string cursorTimeName, DateTime? cursorTime, string cursorIdName, object? cursorId, int batchSize)
    {
        var p = new DynamicParameters();
        p.Add("fromUtc", fromUtc, DbType.DateTime2);
        p.Add("toUtc", toUtc, DbType.DateTime2);
        p.Add(cursorTimeName, cursorTime, DbType.DateTime2);
        p.Add(cursorIdName, cursorId);
        p.Add("batchSize", batchSize, DbType.Int32);
        return p;
    }

    public Task<int> PurgeSearchLogAsync(int retentionDays, CancellationToken ct = default)
        => db.QuerySingleAsync<int>("logs.uspPurgeSearchLog", new { retentionDays }, ct)!;

    public Task<IReadOnlyList<SearchLogSummaryRow>> GetSearchLogSummaryAsync(int days, Guid? appUserId = null, CancellationToken ct = default)
        => db.QueryAsync<SearchLogSummaryRow>("logs.uspGetSearchLogSummary", new { days, appUserId }, ct);

    public Task<IReadOnlyList<SearchLogEventRow>> ListSearchLogEventsAsync(DateTime? fromUtc, DateTime? toUtc, Guid? appUserId, bool includeSearchText, int top = 200, CancellationToken ct = default)
    {
        var p = new DynamicParameters();
        p.Add("fromUtc", fromUtc, DbType.DateTime2);
        p.Add("toUtc", toUtc, DbType.DateTime2);
        p.Add("appUserId", appUserId, DbType.Guid);
        p.Add("includeSearchText", includeSearchText, DbType.Boolean);
        p.Add("top", top, DbType.Int32);
        return db.QueryAsync<SearchLogEventRow>("logs.uspListSearchLogEvents", p, ct);
    }

    public Task<IReadOnlyList<AuditEventChangeRow>> GetAuditEventChangesAsync(long auditEventId, CancellationToken ct = default)
        => db.QueryAsync<AuditEventChangeRow>("logs.uspGetAuditEventChanges", new { auditEventId }, ct);

    public Task<int> PurgeAuditEventChangesAsync(int retentionDays, CancellationToken ct = default)
        => db.QuerySingleAsync<int>("logs.uspPurgeAuditEventChanges", new { retentionDays }, ct)!;

    public Task<IReadOnlyList<BehaviorAnomalyRow>> GetBehaviorAnomaliesAsync(BehaviorAnomalyQuery query, CancellationToken ct = default)
        => db.QueryAsync<BehaviorAnomalyRow>("logs.uspGetBehaviorAnomalies", new
        {
            hours = query.Hours,
            viewVelocityThreshold = query.ViewVelocityThreshold,
            exportSpikeThreshold = query.ExportSpikeThreshold,
            exportRowsThreshold = query.ExportRowsThreshold,
            probingThreshold = query.ProbingThreshold,
            searchVelocityThreshold = query.SearchVelocityThreshold,
            privBurstThreshold = query.PrivBurstThreshold,
            mfaAbuseThreshold = query.MfaAbuseThreshold,
            distributedFailThreshold = query.DistributedFailThreshold,
        }, ct);

    public Task<IReadOnlyList<NewSecurityFindingRow>> RunSecurityFindingScanAsync(int hours = 24, CancellationToken ct = default)
        => db.QueryAsync<NewSecurityFindingRow>("logs.uspRunSecurityFindingScan", new { hours }, ct);

    public Task<IReadOnlyList<SecurityFindingRow>> ListSecurityFindingsAsync(int days = 30, string? status = null, string? severity = null, Guid? appUserId = null, int top = 200, CancellationToken ct = default)
        => db.QueryAsync<SecurityFindingRow>("logs.uspListSecurityFindings", new { days, status, severity, appUserId, top }, ct);

    public Task UpdateSecurityFindingStatusAsync(Guid securityFindingId, string status, CancellationToken ct = default)
        => db.ExecuteAsync("logs.uspUpdateSecurityFindingStatus", new { securityFindingId, status }, ct);

    public Task<IReadOnlyList<AuditExportRow>> ExportAuditEventsAsync(DateTime fromUtc, DateTime toUtc, DateTime? afterEventUtc, long? afterAuditEventId, int batchSize = 2000, CancellationToken ct = default)
        => db.QueryAsync<AuditExportRow>("logs.uspExportAuditEvents", ExportParams(fromUtc, toUtc, "afterEventUtc", afterEventUtc, "afterAuditEventId", afterAuditEventId, batchSize), ct);

    public Task<IReadOnlyList<SignInExportRow>> ExportSignInEventsAsync(DateTime fromUtc, DateTime toUtc, DateTime? afterEventUtc, Guid? afterSignInEventId, int batchSize = 2000, CancellationToken ct = default)
        => db.QueryAsync<SignInExportRow>("logs.uspExportSignInEvents", ExportParams(fromUtc, toUtc, "afterEventUtc", afterEventUtc, "afterSignInEventId", afterSignInEventId, batchSize), ct);

    public Task<IReadOnlyList<ScreenVisitExportRow>> ExportScreenVisitsAsync(DateTime fromUtc, DateTime toUtc, DateTime? afterEventUtc, Guid? afterScreenVisitId, int batchSize = 2000, CancellationToken ct = default)
        => db.QueryAsync<ScreenVisitExportRow>("logs.uspExportScreenVisits", ExportParams(fromUtc, toUtc, "afterEventUtc", afterEventUtc, "afterScreenVisitId", afterScreenVisitId, batchSize), ct);

    public Task<IReadOnlyList<SearchLogExportRow>> ExportSearchLogsAsync(DateTime fromUtc, DateTime toUtc, DateTime? afterSearchedUtc, Guid? afterSearchLogId, int batchSize = 2000, CancellationToken ct = default)
        => db.QueryAsync<SearchLogExportRow>("logs.uspExportSearchLogs", ExportParams(fromUtc, toUtc, "afterSearchedUtc", afterSearchedUtc, "afterSearchLogId", afterSearchLogId, batchSize), ct);

    public Task<UserBehaviorSummary?> GetUserBehaviorSummaryAsync(Guid appUserId, int days = 30, CancellationToken ct = default)
        => db.MultipleAsync("logs.uspGetUserBehaviorSummary", new { appUserId, days }, async g =>
        {
            var overview = await g.ReadSingleOrDefaultAsync<UserBehaviorOverviewRow>().ConfigureAwait(false);
            var actions = (await g.ReadAsync<UserAuditActionRow>().ConfigureAwait(false)).AsList();
            var findings = (await g.ReadAsync<UserSecurityFindingRow>().ConfigureAwait(false)).AsList();
            var sessions = (await g.ReadAsync<UserUsageSessionRow>().ConfigureAwait(false)).AsList();
            return overview is null ? null : new UserBehaviorSummary(overview, actions, findings, sessions);
        }, ct);

    public Task<OperationalLogsReport> GetOperationalLogsAsync(int hours = 24, int top = 100, CancellationToken ct = default)
        => db.MultipleAsync("logs.uspGetOperationalLogs", new { hours, top }, async g =>
        {
            var failures = (await g.ReadAsync<ExecutionLogFailureRow>().ConfigureAwait(false)).AsList();
            var ddl = (await g.ReadAsync<DdlChangeRow>().ConfigureAwait(false)).AsList();
            var verify = (await g.ReadAsync<DdlAuditVerifyRow>().ConfigureAwait(false)).AsList();
            var ledger = await g.ReadSingleOrDefaultAsync<AuditLedgerStatusRow>().ConfigureAwait(false);
            return new OperationalLogsReport(failures, ddl, verify, ledger);
        }, ct);

    public Task<OperationalIntegrityCheck> CheckOperationalIntegrityAsync(int minutes = 15, CancellationToken ct = default)
        => db.MultipleAsync("logs.uspCheckOperationalIntegrity", new { minutes }, async g =>
        {
            var summary = await g.ReadSingleAsync<ExecutionFailureSpikeRow>().ConfigureAwait(false);
            var problems = (await g.ReadAsync<DdlAuditVerifyRow>().ConfigureAwait(false)).AsList();
            return new OperationalIntegrityCheck(summary, problems);
        }, ct);

    public Task<int> PurgeExecutionLogAsync(int retentionDays, CancellationToken ct = default)
        => db.QuerySingleAsync<int>("logs.uspPurgeExecutionLog", new { retentionDays }, ct)!;

    public Task<LedgerVerificationStateRow> GetLedgerVerificationStateAsync(CancellationToken ct = default)
        => db.QuerySingleAsync<LedgerVerificationStateRow>("logs.uspGetLedgerVerificationState", null, ct)!;

    public Task<AuditLedgerVerificationRow> VerifyAuditLedgerAsync(string? digestJson = null, bool useAutomaticStorage = false, bool requireDigest = false, CancellationToken ct = default)
        => db.MultipleAsync("logs.uspVerifyAuditLedger", new { digestJson, useAutomaticStorage, requireDigest }, async g =>
        {
            // sys.sp_verify_database_ledger(_from_digest_storage) emits a native result set containing
            // last_verified_block_id before our summary row. Carry the engine's native block id directly.
            long? nativeVerifiedBlockId = null;
            AuditLedgerVerificationRow? summary = null;
            while (!g.IsConsumed)
            {
                var row = await g.ReadFirstOrDefaultAsync<dynamic>().ConfigureAwait(false);
                if (row is IDictionary<string, object> dict)
                {
                    if (dict.TryGetValue("last_verified_block_id", out object? nativeObj) && nativeObj is not null)
                    {
                        nativeVerifiedBlockId = Convert.ToInt64(nativeObj);
                    }
                    else if (dict.ContainsKey("verified"))
                    {
                        summary = new AuditLedgerVerificationRow
                        {
                            Verified = Convert.ToBoolean(dict["verified"]),
                            LastVerifiedBlockId = nativeVerifiedBlockId,
                            DigestJson = dict.TryGetValue("digestJson", out object? dj) ? dj as string : null,
                            VerifiedUtc = dict.TryGetValue("verifiedUtc", out object? vu) && vu is not null ? Convert.ToDateTime(vu) : DateTime.UtcNow,
                            Detail = dict.TryGetValue("detail", out object? dt) ? dt as string ?? string.Empty : string.Empty,
                            HasAutomaticDigestStorage = dict.TryGetValue("hasAutomaticDigestStorage", out object? ha) && ha is not null && Convert.ToBoolean(ha),
                        };
                    }
                }
            }
            if (summary is null)
            {
                return new AuditLedgerVerificationRow { Verified = false, Detail = "No verification result returned." };
            }
            return summary.Verified && nativeVerifiedBlockId is not null
                ? new AuditLedgerVerificationRow
                {
                    Verified = summary.Verified,
                    LastVerifiedBlockId = nativeVerifiedBlockId,
                    DigestJson = summary.DigestJson,
                    VerifiedUtc = summary.VerifiedUtc,
                    Detail = $"{summary.Detail.TrimEnd('.')} (block {nativeVerifiedBlockId}).",
                    HasAutomaticDigestStorage = summary.HasAutomaticDigestStorage,
                }
                : summary;
        }, ct);

    public Task<LedgerVerificationOutcomeRow> RecordLedgerVerificationOutcomeAsync(string anchorSource, bool verified, long? lastVerifiedBlockId, int? digestFileCount, string detail, CancellationToken ct = default)
        => db.QuerySingleAsync<LedgerVerificationOutcomeRow>("logs.uspRecordLedgerVerificationOutcome",
            new { anchorSource, verified, lastVerifiedBlockId, digestFileCount, detail }, ct)!;

    public Task<IReadOnlyList<NewSecurityFindingRow>> GetPendingLedgerAlertsAsync(CancellationToken ct = default)
        => db.QueryAsync<NewSecurityFindingRow>("logs.uspGetPendingLedgerAlerts", null, ct);

    public Task MarkSecurityFindingWebhookDeliveredAsync(Guid securityFindingId, CancellationToken ct = default)
        => db.ExecuteAsync("logs.uspMarkSecurityFindingWebhookDelivered", new { securityFindingId }, ct);
}
