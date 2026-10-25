using System.Text.Json;
using Dapper;
using Merit.Application.Data;

namespace Merit.Data.SqlServer;

public sealed class ReviewData(Db db) : IReviewData
{
    private static readonly HashSet<string> ReportProcedures = new(StringComparer.Ordinal)
    {
        "dbo.uspReportTestingCounts", "dbo.uspReportElevatedCases", "dbo.uspReportCaseTimeliness",
        "dbo.uspReportInvestigationOutcomes", "dbo.uspReportReporterTimeliness",
    };

    // Procedures whose only filter is the period.
    private static readonly HashSet<string> PeriodOnly = new(StringComparer.Ordinal) { "dbo.uspReportInvestigationOutcomes" };

    public Task<IReadOnlyList<MatchReviewRow>> ListMatchReviewRowsAsync(bool escalatedOnly, Guid? stagingRowId, int pageNumber, int pageSize,
                                                                        CancellationToken ct = default)
        => db.QueryAsync<MatchReviewRow>("dbo.uspListMatchReviewRows", new { escalatedOnly, stagingRowId, pageNumber, pageSize }, ct);

    public Task<CandidateDetails> GetCandidateDetailsAsync(IReadOnlyList<Guid> personIds, CancellationToken ct = default)
        => db.MultipleAsync("dbo.uspGetMatchCandidateDetails", new { personIdsJson = JsonSerializer.Serialize(personIds) }, async g =>
        {
            var persons = (await g.ReadAsync<CandidatePersonRow>().ConfigureAwait(false)).AsList();
            var places = (await g.ReadAsync<CandidatePlaceRow>().ConfigureAwait(false)).AsList();
            var results = (await g.ReadAsync<CandidateResultRow>().ConfigureAwait(false)).AsList();
            return new CandidateDetails(persons, places, results);
        }, ct);

    public Task RecordMatchDecisionAsync(Guid stagingRowId, string decision, Guid? personId, string? incomingKey, string? note,
                                         CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspRecordMatchDecision", new { stagingRowId, decision, personId, incomingKey, note }, ct);

    public Task<IReadOnlyList<ClusterListRow>> ListClustersAsync(string? clusterStatus, int pageNumber, int pageSize, CancellationToken ct = default)
        => db.QueryAsync<ClusterListRow>("dbo.uspListClusters", new { clusterStatus, pageNumber, pageSize }, ct);

    public Task<ClusterDetail?> GetClusterAsync(Guid clusterId, CancellationToken ct = default)
        => db.MultipleAsync("dbo.uspGetCluster", new { clusterId }, async g =>
        {
            var cluster = await g.ReadFirstOrDefaultAsync<ClusterRow>().ConfigureAwait(false);
            var members = (await g.ReadAsync<ClusterMemberRow>().ConfigureAwait(false)).AsList();
            var tasks = (await g.ReadAsync<ClusterTaskRow>().ConfigureAwait(false)).AsList();
            return cluster is null ? null : new ClusterDetail(cluster, members, tasks);
        }, ct);

    public Task UpdateClusterAsync(Guid clusterId, string clusterStatus, string? notes, byte[] rowVersion, CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspUpdateCluster", new { clusterId, clusterStatus, notes, rowVersion }, ct);

    public Task SetCaseClusterMemberAsync(Guid clusterId, string caseNumber, bool isMember, CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspSetCaseClusterMember", new { clusterId, caseNumber, isMember }, ct);

    public Task<IReadOnlyList<CaseMapRow>> GetCaseMapAsync(Guid? analyteId, DateOnly? openedFrom, DateOnly? openedTo, bool openOnly,
                                                           CancellationToken ct = default)
        => db.QueryAsync<CaseMapRow>("dbo.uspGetCaseMap", new { analyteId, openedFrom, openedTo, openOnly }, ct);

    public Task SetLocationCoordinatesAsync(Guid locationId, decimal latitude, decimal longitude, string geocodeQuality,
                                            CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspSetLocationCoordinates", new { locationId, latitude, longitude, geocodeQuality }, ct);

    public async Task<ReportData> RunReportAsync(string procedure, DateOnly fromDate, DateOnly toDate, Guid? analyteId,
                                                 CancellationToken ct = default)
    {
        if (!ReportProcedures.Contains(procedure))
        {
            throw new ArgumentException($"{procedure} is not a report procedure.", nameof(procedure));
        }

        object parameters = PeriodOnly.Contains(procedure) ? new { fromDate, toDate } : new { fromDate, toDate, analyteId };
        var rows = await db.QueryAsync<dynamic>(procedure, parameters, ct).ConfigureAwait(false);
        var dictionaries = rows.Select(r => (IDictionary<string, object?>)r).ToList();
        var columns = dictionaries.Count == 0 ? [] : dictionaries[0].Keys.ToList();
        return new ReportData(columns, dictionaries.Select(d => (IReadOnlyList<object?>)d.Values.Select(Normalize).ToList()).ToList());
    }

    private static object? Normalize(object? value) => value is DateTime d ? DateOnly.FromDateTime(d) : value;
}
