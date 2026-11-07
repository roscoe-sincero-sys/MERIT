using Dapper;
using Merit.Application.Data;

namespace Merit.Data.SqlServer;

public sealed class ScreenUsageData(Db db) : IScreenUsageData
{
    public Task<bool> RecordAsync(ScreenVisitInput v, CancellationToken ct = default)
        => db.QuerySingleAsync<bool>("logs.uspRecordScreenVisit", new { screenVisitId = v.VisitId, v.ScreenCode, v.TabId, v.PreviousVisitId, v.NavigationType }, ct);

    public Task DurationAsync(Guid visitId, int durationMs, CancellationToken ct = default)
        => db.ExecuteAsync("logs.uspSetScreenDuration", new { screenVisitId = visitId, durationMs }, ct);

    public Task ContinueSessionAsync(Guid newSessionScopeId, CancellationToken ct = default)
        => db.ExecuteAsync("auth.uspContinueUsageSession", new { newSessionScopeId }, ct);

    public Task<ScreenUsageReport> ReportAsync(int days, CancellationToken ct = default)
        => db.MultipleAsync("logs.uspGetScreenUsage", new { days }, async g => new ScreenUsageReport(
            (await g.ReadAsync<ScreenCountRow>().ConfigureAwait(false)).AsList(),
            (await g.ReadAsync<ScreenPathRow>().ConfigureAwait(false)).AsList(),
            await g.ReadSingleAsync<ScreenFlowRow>().ConfigureAwait(false),
            (await g.ReadAsync<ScreenSessionRow>().ConfigureAwait(false)).AsList()), ct);

    public Task<ScreenSessionReport> SessionAsync(Guid sessionReference, int days, int page, CancellationToken ct = default)
        => db.MultipleAsync("logs.uspGetScreenSession", new { sessionReference, days, pageNumber = page }, async g => new ScreenSessionReport(
            (await g.ReadAsync<ScreenVisitRow>().ConfigureAwait(false)).AsList(),
            (await g.ReadAsync<ScreenSessionEventRow>().ConfigureAwait(false)).AsList()), ct);

    public Task<int> PurgeAsync(int retentionDays, CancellationToken ct = default)
        => db.QuerySingleAsync<int>("logs.uspPurgeScreenVisits", new { retentionDays }, ct)!;
}
