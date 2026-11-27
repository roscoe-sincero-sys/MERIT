using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Security;

namespace Merit.Application.Services;

/// <summary>
/// Scoped security monitoring, behavioral anomaly detection (#327), search-log reporting (#329),
/// unified per-user behavior summaries &amp; operational logs (#330), and row-change history (#332).
/// </summary>
public sealed class SecurityMonitoringService(ISecurityMonitoringData data, AuditService audit, ICurrentSession session)
{
    public async Task<IReadOnlyList<BehaviorAnomalyRow>> GetBehaviorAnomaliesAsync(BehaviorAnomalyQuery query, CancellationToken ct = default, bool auditView = true)
    {
        session.RequireAny(Permissions.SecurityReview, Permissions.TenantAdminister);
        var rows = await data.GetBehaviorAnomaliesAsync(query, ct).ConfigureAwait(false);
        if (auditView)
        {
            await audit.ListedAsync("securityAlert", rows.Count, ct, new { hours = query.Hours }).ConfigureAwait(false);
        }

        return rows;
    }

    public async Task<IReadOnlyList<SecurityFindingRow>> ListFindingsAsync(int days = 30, string? status = null, string? severity = null, Guid? appUserId = null, CancellationToken ct = default, bool auditView = true)
    {
        session.RequireAny(Permissions.SecurityReview, Permissions.TenantAdminister);
        int window = Math.Clamp(days, 1, 365);
        var rows = await data.ListSecurityFindingsAsync(window, status, severity, appUserId, 200, ct).ConfigureAwait(false);
        if (auditView)
        {
            await audit.ListedAsync("securityFinding", rows.Count, ct, new { days = window, status, severity, appUserId }).ConfigureAwait(false);
        }

        return rows;
    }

    public async Task UpdateFindingStatusAsync(Guid securityFindingId, string status, CancellationToken ct = default)
    {
        session.RequireAny(Permissions.SecurityReview, Permissions.TenantAdminister);
        await data.UpdateSecurityFindingStatusAsync(securityFindingId, status, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SearchLogSummaryRow>> GetSearchLogSummaryAsync(int days = 30, Guid? appUserId = null, CancellationToken ct = default)
    {
        session.RequireAny(Permissions.SecurityReview, Permissions.AuditView);
        int window = Math.Clamp(days, 1, 365);
        var rows = await data.GetSearchLogSummaryAsync(window, appUserId, ct).ConfigureAwait(false);
        await audit.ListedAsync("searchLog", rows.Count, ct, new { days = window, appUserId }).ConfigureAwait(false);
        return rows;
    }

    public async Task<IReadOnlyList<SearchLogEventRow>> ListSearchLogEventsAsync(DateTime? fromUtc, DateTime? toUtc, Guid? appUserId, bool includeSearchText = false, int top = 200, CancellationToken ct = default)
    {
        session.RequireAny(Permissions.SecurityReview, Permissions.AuditView);
        bool allowText = includeSearchText && session.Has(Permissions.SecurityInvestigate);
        var rows = await data.ListSearchLogEventsAsync(fromUtc, toUtc, appUserId, allowText, Math.Clamp(top, 1, 1000), ct).ConfigureAwait(false);
        await audit.ListedAsync("searchLog", rows.Count, ct, new { appUserId, includeSearchText = allowText }).ConfigureAwait(false);
        return rows;
    }

    public async Task<IReadOnlyList<AuditEventChangeRow>> GetAuditEventChangesAsync(long auditEventId, CancellationToken ct = default)
    {
        session.RequireAny(Permissions.AuditView, Permissions.SecurityReview);
        var rows = await data.GetAuditEventChangesAsync(auditEventId, ct).ConfigureAwait(false);
        await audit.ListedAsync("auditEventChange", rows.Count, ct, new { auditEventId }).ConfigureAwait(false);
        return rows;
    }

    public async Task<UserBehaviorSummary> GetUserBehaviorSummaryAsync(Guid appUserId, int days = 30, CancellationToken ct = default, bool auditView = true)
    {
        session.RequireAny(Permissions.SecurityReview, Permissions.AuditView, Permissions.TenantAdminister);
        int window = Math.Clamp(days, 1, 365);
        var summary = await data.GetUserBehaviorSummaryAsync(appUserId, window, ct).ConfigureAwait(false)
                      ?? throw new NotFoundException("The user was not found, or you do not have access to review this account.");
        if (auditView)
        {
            await audit.ViewedAsync("userBehavior", appUserId, ct).ConfigureAwait(false);
        }

        return summary;
    }

    public async Task<OperationalLogsReport> GetOperationalLogsAsync(int hours = 24, int top = 100, CancellationToken ct = default, bool auditView = true)
    {
        session.Require(Permissions.SecurityInvestigate);
        int window = Math.Clamp(hours, 1, 720);
        var report = await data.GetOperationalLogsAsync(window, Math.Clamp(top, 1, 500), ct).ConfigureAwait(false);
        if (auditView)
        {
            await audit.ListedAsync("operationalLog", report.ExecutionFailures.Count + report.DdlChanges.Count, ct, new { hours = window }).ConfigureAwait(false);
        }

        return report;
    }
}
