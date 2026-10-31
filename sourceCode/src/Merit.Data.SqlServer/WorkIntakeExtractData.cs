using System.Text.Json;
using Dapper;
using Merit.Application.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Merit.Data.SqlServer;

public sealed class WorkData(Db db) : IWorkData
{
    public Task CreateTaskAsync(WorkTaskCreate t, CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspCreateWorkTask", new
        {
            t.WorkTaskId, t.QueueCode, t.Title, t.Detail, t.PersonId, t.ExposureCaseId, t.LabResultId, t.InvestigationId,
            t.StagingRowId, t.IsUrgent, t.DueDate, t.OwningTenantId,
        }, ct);

    public Task<IReadOnlyList<QueueCountRow>> GetQueueCountsAsync(CancellationToken ct = default)
        => db.QueryAsync<QueueCountRow>("dbo.uspGetQueueCounts", null, ct);

    public Task<DashboardData> GetDashboardAsync(CancellationToken ct = default)
        => db.MultipleAsync("dbo.uspGetDashboard", null, async g => new DashboardData(
            await g.ReadSingleAsync<DashboardCountsRow>().ConfigureAwait(false),
            (await g.ReadAsync<DashboardItemRow>().ConfigureAwait(false)).AsList(),
            (await g.ReadAsync<DashboardCaseRow>().ConfigureAwait(false)).AsList()), ct);

    public Task<IReadOnlyList<WorkTaskRow>> ListTasksAsync(WorkTaskQuery q, CancellationToken ct = default)
        => db.QueryAsync<WorkTaskRow>("dbo.uspListWorkTasks", new
        {
            q.QueueCode, q.TaskStatus, q.AssignedToMe, q.UrgentOnly, q.SortBy, q.SortDescending, q.PageNumber, q.PageSize, q.OverdueOnly,
        }, ct);

    public async Task<int> UpdateTasksAsync(IReadOnlyList<Guid> taskIds, string action, Guid? assignToUserId, string? note, CancellationToken ct = default)
        => await db.QuerySingleAsync<int>("dbo.uspUpdateWorkTasks",
            new { workTaskIdsJson = JsonSerializer.Serialize(taskIds), action, assignToUserId, note }, ct).ConfigureAwait(false);

    public Task EnqueueJobAsync(Guid jobId, string jobType, string payloadJson, CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspEnqueueJob", new { jobId, jobType, payloadJson }, ct);

    public Task<ClaimedJobRow?> ClaimNextJobAsync(string workerName, CancellationToken ct = default)
        => db.QuerySingleAsync<ClaimedJobRow>("dbo.uspClaimNextJob", new { workerName }, ct);

    public Task CompleteJobAsync(Guid jobId, string? resultJson, CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspCompleteJob", new { jobId, resultJson }, ct);

    public Task FailJobAsync(Guid jobId, string errorText, CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspFailJob", new { jobId, errorText }, ct);

    public Task<JobRow?> GetJobAsync(Guid jobId, CancellationToken ct = default)
        => db.QuerySingleAsync<JobRow>("dbo.uspGetJob", new { jobId }, ct);

    public Task<IReadOnlyList<QueueDepthRow>> GetQueueDepthAsync(CancellationToken ct = default)
        => db.QueryAsync<QueueDepthRow>("dbo.uspGetQueueDepth", null, ct);

    public async Task<int> DetectClustersAsync(int windowDays, int minCount, CancellationToken ct = default)
    {
        await db.ExecuteAsync("dbo.uspDetectClusters", new { windowDays, minCount }, ct).ConfigureAwait(false);
        return 0;
    }

    public Task RecordAuditAsync(string eventAction, string entityType, Guid? entityId, int? recordCount, string? clientAddress,
                                 CancellationToken ct = default)
        => db.ExecuteAsync("logs.uspRecordAuditEvent", new { eventAction, entityType, entityId, recordCount, clientAddress }, ct);

    public Task<IReadOnlyList<AuditEventRow>> SearchAuditAsync(AuditQuery query, CancellationToken ct = default)
        => db.QueryAsync<AuditEventRow>("logs.uspSearchAuditEvents", new
        {
            appUserId = query.AppUserId,
            entityId = query.EntityId,
            eventAction = query.EventAction,
            entityType = query.EntityType,
            fromUtc = query.FromUtc,
            toUtc = query.ToUtc,
            pageNumber = query.PageNumber,
            pageSize = query.PageSize,
        }, ct);

    public Task<IReadOnlyList<AuditUserRow>> ListAuditUsersAsync(CancellationToken ct = default)
        => db.QueryAsync<AuditUserRow>("logs.uspListAuditUsers", null, ct);
}

public sealed class IntakeData(Db db) : IIntakeData
{
    public Task<IReadOnlyList<IntakeProfileListRow>> ListProfilesAsync(CancellationToken ct = default)
        => db.QueryAsync<IntakeProfileListRow>("dbo.uspListIntakeProfiles", null, ct);

    public Task<IntakeProfileRow?> GetProfileAsync(Guid intakeProfileId, CancellationToken ct = default)
        => db.QuerySingleAsync<IntakeProfileRow>("dbo.uspGetIntakeProfile", new { intakeProfileId }, ct);

    public Task CreateBatchAsync(Guid intakeBatchId, Guid intakeProfileId, string fileName, string rawContent, string contentSha256, Guid jobId,
                                 CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspCreateIntakeBatch", new { intakeBatchId, intakeProfileId, fileName, rawContent, jobId, contentSha256 }, ct);

    public Task<ReceivedBatchRow?> FindBatchByContentAsync(Guid intakeProfileId, string contentSha256, CancellationToken ct = default)
        => db.QuerySingleAsync<ReceivedBatchRow>("dbo.uspFindIntakeBatchByContent", new { intakeProfileId, contentSha256 }, ct);

    public Task<IReadOnlyList<IntakeBatchRow>> ListBatchesAsync(int maxRows, CancellationToken ct = default)
        => db.QueryAsync<IntakeBatchRow>("dbo.uspListIntakeBatches", new { maxRows }, ct);

    public Task<IntakeBatchRow?> GetBatchAsync(Guid intakeBatchId, bool includeContent, CancellationToken ct = default)
        => db.QuerySingleAsync<IntakeBatchRow>("dbo.uspGetIntakeBatch", new { intakeBatchId, includeContent }, ct);

    public Task SetBatchStatusAsync(Guid intakeBatchId, string batchStatus, string? pauseReason, CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspSetIntakeBatchStatus", new { intakeBatchId, batchStatus, pauseReason }, ct);

    public Task SaveStagingRowsAsync(Guid intakeBatchId, IReadOnlyList<StagingRowNew> rows, CancellationToken ct = default)
    {
        // parsedJson is nested as a JSON object, read back with OPENJSON ... AS JSON.
        string json = "[" + string.Join(',', rows.Select(r =>
            $"{{\"stagingRowId\":\"{r.StagingRowId}\",\"rowNumber\":{r.RowNumber},\"parsedJson\":{r.ParsedJson}}}")) + "]";
        return db.ExecuteAsync("dbo.uspSaveStagingRows", new { intakeBatchId, rowsJson = json }, ct);
    }

    public Task<IReadOnlyList<StagingRowForProcessing>> GetRowsForProcessingAsync(Guid intakeBatchId, bool releasedOnly, CancellationToken ct = default)
        => db.QueryAsync<StagingRowForProcessing>("dbo.uspGetStagingRowsForProcessing", new { intakeBatchId, releasedOnly }, ct);

    public Task CompleteRowAsync(Guid stagingRowId, string rowStatus, Guid? labResultId, IReadOnlyList<HoldReason> reasons, bool isUrgent,
                                 CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspCompleteStagingRow", new
        {
            stagingRowId, rowStatus, labResultId, isUrgent,
            holdReasonsJson = JsonSerializer.Serialize(reasons.Select(r => new
            {
                reasonCode = r.ReasonCode, fieldName = r.FieldName, reasonMessage = r.ReasonMessage, reportedValue = r.ReportedValue,
            })),
        }, ct);

    public Task<IReadOnlyList<HeldGroupRow>> ListHeldGroupsAsync(Guid? intakeBatchId = null, CancellationToken ct = default)
        => db.QueryAsync<HeldGroupRow>("dbo.uspListHeldGroups", new { intakeBatchId }, ct);

    public Task<HeldOverview> GetHeldOverviewAsync(Guid? intakeBatchId, CancellationToken ct = default)
        => db.MultipleAsync("dbo.uspListHeldGroups", new { intakeBatchId, includeSummary = true }, async grid =>
        {
            var groups = (await grid.ReadAsync<HeldGroupRow>().ConfigureAwait(false)).ToList();
            var summary = await grid.ReadSingleAsync<HeldSummaryRow>().ConfigureAwait(false);
            return new HeldOverview(groups, summary);
        }, ct);

    public async Task<HeldSummaryRow> GetHeldSummaryAsync(Guid? intakeBatchId, CancellationToken ct = default)
        => await db.QuerySingleAsync<HeldSummaryRow>("dbo.uspGetHeldSummary", new { intakeBatchId }, ct).ConfigureAwait(false)
           ?? new HeldSummaryRow(0, null);

    public Task<IReadOnlyList<HeldRowRow>> ListHeldRowsAsync(string? reasonCode, string? reportedValue, Guid? senderOrganizationId, bool exactGroup,
                                                             Guid? intakeBatchId, int pageNumber, int pageSize, CancellationToken ct = default)
        => db.QueryAsync<HeldRowRow>("dbo.uspListHeldRows",
            new { reasonCode, reportedValue, senderOrganizationId, exactGroup, intakeBatchId, pageNumber, pageSize }, ct);

    public Task SaveCorrectionAsync(Guid stagingRowId, string correctedJson, CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspSaveStagingCorrection", new { stagingRowId, correctedJson }, ct);

    public Task<int> CorrectHeldGroupAsync(string reason, string? reportedValue, Guid? sender, Guid? batch, string field,
        string? value, IReadOnlyList<Guid>? selectedIds, CancellationToken ct = default)
        => db.QuerySingleAsync<int>("dbo.uspCorrectHeldGroup", new { reason, reportedValue, sender, batch, field, value,
            selectedIdsJson = selectedIds is null ? null : System.Text.Json.JsonSerializer.Serialize(selectedIds) }, ct);

    public async Task<int> ReleaseRowsAsync(IReadOnlyList<Guid> stagingRowIds, CancellationToken ct = default)
        => await db.QuerySingleAsync<int>("dbo.uspReleaseStagingRows",
            new { stagingRowIdsJson = JsonSerializer.Serialize(stagingRowIds) }, ct).ConfigureAwait(false);

    public async Task<int> RejectRowsAsync(IReadOnlyList<Guid> stagingRowIds, string reason, CancellationToken ct = default)
        => await db.QuerySingleAsync<int>("dbo.uspRejectStagingRows",
            new { stagingRowIdsJson = JsonSerializer.Serialize(stagingRowIds), rejectReason = reason }, ct).ConfigureAwait(false);
}

public sealed class ExtractData(Db db) : IExtractData
{
    public Task<IReadOnlyList<ExtractFindingPerson>> ResolveFindingPersonsAsync(Guid extractRunId, IReadOnlyList<string> recordIds, CancellationToken ct = default)
        => db.QueryAsync<ExtractFindingPerson>("dbo.uspResolveExtractFindingPersons", new { extractRunId, recordIdsJson = JsonSerializer.Serialize(recordIds) }, ct);

    public Task CreateRunAsync(Guid extractRunId, string extractType, DateOnly periodStart, DateOnly periodEnd, Guid jobId, CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspCreateExtractRun", new { extractRunId, extractType, periodStart, periodEnd, jobId }, ct);

    public Task<IReadOnlyList<ExtractRunRow>> ListRunsAsync(int maxRows, CancellationToken ct = default)
        => db.QueryAsync<ExtractRunRow>("dbo.uspListExtractRuns", new { maxRows }, ct);

    public Task<ExtractRunRow?> GetRunAsync(Guid extractRunId, bool includeFile, CancellationToken ct = default)
        => db.QuerySingleAsync<ExtractRunRow>("dbo.uspGetExtractRun", new { extractRunId, includeFile }, ct);

    public Task UpdateRunAsync(Guid extractRunId, string runStatus, int? recordCount, int? errorCount, string? validationReportJson,
                               string? fileName, string? fileContent, CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspUpdateExtractRun",
            new { extractRunId, runStatus, recordCount, errorCount, validationReportJson, fileName, fileContent }, ct);

    public Task MarkSubmittedAsync(Guid extractRunId, CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspMarkExtractSubmitted", new { extractRunId }, ct);

    public Task<IReadOnlyList<CblsSourceRow>> GetCblsRowsAsync(DateOnly periodStart, DateOnly periodEnd, CancellationToken ct = default)
        => db.QueryAsync<CblsSourceRow>("dbo.uspGetCblsExtractRows", new { periodStart, periodEnd }, ct);

    public Task<IReadOnlyList<AblesSourceRow>> GetAblesRowsAsync(int reportYear, CancellationToken ct = default)
        => db.QueryAsync<AblesSourceRow>("dbo.uspGetAblesExtractRows", new { reportYear }, ct);
}

public static class DataServiceCollectionExtensions
{
    /// <summary>The SQL Server implementation of the application's data access interfaces.</summary>
    public static IServiceCollection AddMeritSqlServer(this IServiceCollection services, Action<MeritDatabaseOptions> configure)
    {
        services.Configure(configure);
        services.AddScoped<Db>();
        services.AddScoped<IAccountData, AccountData>();
        services.AddScoped<IConfigData, ConfigData>();
        services.AddScoped<IPersonData, PersonData>();
        services.AddScoped<IPlaceData, PlaceData>();
        services.AddScoped<IResultData, ResultData>();
        services.AddScoped<ICaseData, CaseData>();
        services.AddScoped<IWorkData, WorkData>();
        services.AddScoped<IIntakeData, IntakeData>();
        services.AddScoped<IExtractData, ExtractData>();
        services.AddScoped<IReviewData, ReviewData>();
        return services;
    }
}
