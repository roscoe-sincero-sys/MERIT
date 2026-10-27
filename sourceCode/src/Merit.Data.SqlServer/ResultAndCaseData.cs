using Dapper;
using Merit.Application.Data;
using Merit.Application.Errors;

namespace Merit.Data.SqlServer;

public sealed class ResultData(Db db) : IResultData
{
    public Task<ClassificationContext> GetClassificationContextAsync(Guid personId, Guid analyteId, CancellationToken ct = default)
        => db.MultipleAsync("dbo.uspGetClassificationContext", new { personId, analyteId }, async g => new ClassificationContext(
            await g.ReadFirstOrDefaultAsync<ContextPersonRow>().ConfigureAwait(false),
            (await g.ReadAsync<ContextEpisodeRow>().ConfigureAwait(false)).AsList(),
            await g.ReadFirstOrDefaultAsync<ContextOpenCaseRow>().ConfigureAwait(false),
            (await g.ReadAsync<ContextPriorResultRow>().ConfigureAwait(false)).AsList()), ct);

    public Task<DuplicateResultRow?> FindDuplicateAsync(Guid personId, Guid analyteSpecimenId, DateOnly collectedDate, Guid labOrganizationId,
                                                        string? accessionNumber, decimal? valueReported, CancellationToken ct = default)
        => db.QuerySingleAsync<DuplicateResultRow>("dbo.uspFindDuplicateResult",
            new { personId, analyteSpecimenId, collectedDate, labOrganizationId, accessionNumber, valueReported }, ct);

    public async Task<SaveLabResultResult> SaveLabResultAsync(LabResultSave r, CancellationToken ct = default)
        => await db.QuerySingleAsync<SaveLabResultResult>("dbo.uspSaveLabResult", new
        {
            r.LabResultId, r.PersonId, r.AnalyteSpecimenId, r.CollectedDate, r.AnalyzedDate, r.ReportedDate, r.ReceivedUtc,
            r.ResultStatus, r.SupersedesLabResultId, r.ValueReported, r.ValueText, r.Operator, r.UnitReported, r.ValueCanonical,
            r.UnitCanonical, r.SampleType, r.TestMethod, r.DeviceModel, r.DeviceSerial, r.KitLot, r.Speciation, r.CreatinineValue,
            r.ValueCreatinineAdj, r.ReasonForTest, r.PayerType, r.LabOrganizationId, r.OrderingProviderId, r.OrderingFacilityId,
            r.AccessionNumber, r.LoincReported, r.ReportedSnapshotJson, r.ReportedLocationId, r.AgeAtCollectionMonths, r.Population,
            r.IsIndeterminate, r.ResultRowStatus, r.IntakeBatchId,
        }, ct).ConfigureAwait(false) ?? throw new NotFoundException();

    public Task UpdateDerivedAsync(Guid labResultId, Guid? personId, int? ageAtCollectionMonths, string population, bool isIndeterminate,
                                   CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspUpdateLabResultDerived", new { labResultId, personId, ageAtCollectionMonths, population, isIndeterminate }, ct);

    public Task SetRowStatusAsync(Guid labResultId, string resultRowStatus, byte[] rowVersion, CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspSetLabResultRowStatus", new { labResultId, resultRowStatus, rowVersion }, ct);

    public Task<LabResultDetailRow?> GetLabResultAsync(Guid labResultId, CancellationToken ct = default)
        => db.QuerySingleAsync<LabResultDetailRow>("dbo.uspGetLabResult", new { labResultId }, ct);

    public Task<IReadOnlyList<ResultListRow>> ListResultsAsync(ResultListQuery q, CancellationToken ct = default)
        => db.QueryAsync<ResultListRow>("dbo.uspListLabResults", new
        {
            q.AnalyteId, q.CollectedFrom, q.CollectedTo, q.Population, q.MinValue, q.LabOrganizationId, q.UnmatchedOnly, q.SortBy,
            q.SortDescending, q.PageNumber, q.PageSize,
        }, ct);
}

public sealed class CaseData(Db db) : ICaseData
{
    public async Task<OpenCaseResult> OpenCaseAsync(Guid exposureCaseId, Guid personId, Guid analyteId, string population, string classification,
                                                    Guid qualifyingLabResultId, string workflowTemplate, DateOnly openedDate,
                                                    CancellationToken ct = default)
        => await db.QuerySingleAsync<OpenCaseResult>("dbo.uspOpenExposureCase", new
        {
            exposureCaseId, personId, analyteId, population, classification, qualifyingLabResultId, workflowTemplate, openedDate,
        }, ct).ConfigureAwait(false) ?? throw new NotFoundException();

    public async Task<AttachResultOutcome> AttachResultAsync(Guid exposureCaseId, Guid labResultId, string computedClassification, bool isHighest,
                                                             CancellationToken ct = default)
        => await db.QuerySingleAsync<AttachResultOutcome>("dbo.uspAttachResultToCase",
            new { exposureCaseId, labResultId, computedClassification, isHighest }, ct).ConfigureAwait(false) ?? new AttachResultOutcome();

    public Task<CaseDetail?> GetCaseAsync(Guid exposureCaseId, CancellationToken ct = default)
        => db.MultipleAsync("dbo.uspGetExposureCase", new { exposureCaseId }, async g =>
        {
            var c = await g.ReadFirstOrDefaultAsync<CaseRow>().ConfigureAwait(false);
            var results = (await g.ReadAsync<CaseResultRow>().ConfigureAwait(false)).AsList();
            var activities = (await g.ReadAsync<CaseActivityRow>().ConfigureAwait(false)).AsList();
            var investigations = (await g.ReadAsync<CaseInvestigationRow>().ConfigureAwait(false)).AsList();
            var tasks = (await g.ReadAsync<CaseTaskRow>().ConfigureAwait(false)).AsList();
            var contact = await g.ReadFirstOrDefaultAsync<PrimaryContactRow>().ConfigureAwait(false);
            return c is null ? null : new CaseDetail(c, results, activities, investigations, tasks, contact);
        }, ct);

    public Task<IReadOnlyList<CaseListRow>> ListCasesAsync(CaseListQuery query, CancellationToken ct = default)
        => db.QueryAsync<CaseListRow>("dbo.uspListExposureCases", new
        {
            openOnly = query.OpenOnly,
            assignedToMe = query.Assigned == "me",
            unassignedOnly = query.Assigned == "unassigned",
            assignedToUserId = Guid.TryParse(query.Assigned, out var userId) ? userId : (Guid?)null,
            analyteId = query.AnalyteId,
            classification = query.Classification,
            population = query.Population,
            owningTenantId = query.OwningTenantId,
            search = query.Search,
            sort = query.Sort,
            descending = query.Descending,
            pageNumber = query.PageNumber,
            pageSize = query.PageSize,
        }, ct);

    public Task<IReadOnlyList<CaseOwnerRow>> ListCaseOwnersAsync(CancellationToken ct = default)
        => db.QueryAsync<CaseOwnerRow>("dbo.uspListCaseOwners", null, ct);

    public Task UpdateCaseAsync(Guid exposureCaseId, Guid? assignedToUserId, string exposureCategory, string caseStatus, byte[] rowVersion,
                                CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspUpdateExposureCase", new { exposureCaseId, assignedToUserId, exposureCategory, caseStatus, rowVersion }, ct);

    public Task OverrideClassificationAsync(Guid exposureCaseId, string classification, string population, string reason, byte[] rowVersion,
                                            CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspOverrideCaseClassification", new { exposureCaseId, classification, population, reason, rowVersion }, ct);

    public async Task<int> SaveActivityAsync(Guid caseActivityId, Guid exposureCaseId, string activityType, DateOnly activityDate,
                                             Guid? contactPersonId, string? outcome, string? note, CancellationToken ct = default)
        => await db.QuerySingleAsync<int>("dbo.uspSaveCaseActivity",
            new { caseActivityId, exposureCaseId, activityType, activityDate, contactPersonId, outcome, note }, ct).ConfigureAwait(false);

    public Task CloseCaseAsync(Guid exposureCaseId, string closureReason, DateOnly closedDate, string? note, byte[] rowVersion,
                               CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspCloseExposureCase", new { exposureCaseId, closureReason, closedDate, note, rowVersion }, ct);

    public Task CreateFollowUpTasksAsync(Guid exposureCaseId, int adultAgeCutoffYears, CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspCreateCaseFollowUpTasks", new { exposureCaseId, adultAgeCutoffYears }, ct);

    public Task<IReadOnlyList<OpenInvestigationRow>> FindOpenInvestigationsAsync(string investigationType, Guid? locationId, Guid? organizationId,
                                                                                Guid? worksiteLocationId, Guid? exposureSourceId,
                                                                                CancellationToken ct = default)
        => db.QueryAsync<OpenInvestigationRow>("dbo.uspFindOpenInvestigation",
            new { investigationType, locationId, organizationId, worksiteLocationId, exposureSourceId }, ct);

    public async Task<string> CreateInvestigationAsync(InvestigationCreate i, CancellationToken ct = default)
        => await db.QuerySingleAsync<string>("dbo.uspCreateInvestigation", new
        {
            i.InvestigationId, i.InvestigationType, i.LocationId, i.OrganizationId, i.WorksiteLocationId, i.ExposureSourceId,
            i.ExposureCaseId, i.EmploymentId, i.RequestedDate,
        }, ct).ConfigureAwait(false) ?? string.Empty;

    public Task LinkInvestigationAsync(Guid investigationId, Guid? exposureCaseId, Guid? employmentId, CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspLinkInvestigationCase", new { investigationId, exposureCaseId, employmentId }, ct);

    public Task<InvestigationDetail?> GetInvestigationAsync(Guid investigationId, CancellationToken ct = default)
        => db.MultipleAsync("dbo.uspGetInvestigation", new { investigationId }, async g =>
        {
            var inv = await g.ReadFirstOrDefaultAsync<InvestigationRow>().ConfigureAwait(false);
            var cases = (await g.ReadAsync<InvestigationCaseRow>().ConfigureAwait(false)).AsList();
            var samples = (await g.ReadAsync<EnvironmentalSampleRow>().ConfigureAwait(false)).AsList();
            return inv is null ? null : new InvestigationDetail(inv, cases, samples);
        }, ct);

    public Task SaveInvestigationAsync(InvestigationUpdate i, CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspSaveInvestigation", new
        {
            i.InvestigationId, i.InvestigationStatus, i.AssignedToUserId, i.ScheduledDate, i.CompletedDate, i.Findings,
            i.RegulatoryAction, i.ReferralDate, i.ReferralOutcome, i.RowVersion,
        }, ct);

    public Task<bool?> SaveEnvironmentalSampleAsync(EnvironmentalSampleSave s, CancellationToken ct = default)
        => db.QuerySingleAsync<bool?>("dbo.uspSaveEnvironmentalSample", new
        {
            s.EnvironmentalSampleId, s.InvestigationId, s.SampleType, s.RoomOrComponent, s.Surface, s.SampleValue, s.Unit,
            s.SampleDate, s.Notes, s.IsDeleted,
        }, ct);

    public Task<IReadOnlyList<InvestigationListRow>> ListInvestigationsAsync(bool openOnly, bool assignedToMe, int pageNumber, int pageSize,
                                                                            CancellationToken ct = default)
        => db.QueryAsync<InvestigationListRow>("dbo.uspListInvestigations", new { openOnly, assignedToMe, pageNumber, pageSize }, ct);
}
