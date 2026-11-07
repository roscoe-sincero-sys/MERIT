using Merit.Application.Data;
using Merit.Application.Security;

namespace Merit.Application.Services;

public sealed class ScreenUsageService(IScreenUsageData data, ICurrentSession session, AuditService audit)
{
    public async Task<ScreenUsageReport> ReportAsync(int days, CancellationToken ct = default)
    {
        session.Require(Permissions.UsageView);
        var result = await data.ReportAsync(days, ct).ConfigureAwait(false);
        await audit.ListedAsync("screenUsage", result.Screens.Count, ct).ConfigureAwait(false);
        return result;
    }

    public async Task<ScreenSessionReport> SessionAsync(Guid reference, int days, int page, CancellationToken ct = default)
    {
        session.Require(Permissions.UsageView);
        var result = await data.SessionAsync(reference, days, page, ct).ConfigureAwait(false);
        // Never copy a retainable usage session reference into the immutable audit ledger.
        await audit.ListedAsync("screenUsage", result.Visits.Count, ct).ConfigureAwait(false);
        return result;
    }
}
