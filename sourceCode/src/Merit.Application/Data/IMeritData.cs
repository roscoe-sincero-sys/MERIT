namespace Merit.Application.Data;

// The data access interface (architecture: "Application's data access interface"). Merit.Data.SqlServer implements it
// with Dapper calls to stored procedures; a later PostgreSQL implementation would implement the same interfaces.
// Every call runs under the current session's SESSION_CONTEXT, so row-level security decides what each returns.

public interface IAccountData
{
    Task<SignInUserRow?> GetUserForSignInAsync(string userName, CancellationToken ct = default);

    Task RecordSignInResultAsync(Guid? appUserId, bool succeeded, int maxFailedAttempts, int lockoutMinutes, string? clientAddress, CancellationToken ct = default);

    /// <summary>Resolves a session from the master profile, one of the user's profiles, or (view as) another user's access.</summary>
    Task<SessionScopeResult> CreateSessionScopeAsync(Guid sessionScopeId, Guid appUserId, DateTime expiresUtc, Guid? userProfileId = null,
                                                     Guid? viewAsUserId = null, Guid? actingTenantId = null, CancellationToken ct = default);

    Task<SessionScopeCheckRow?> GetSessionScopeAsync(Guid sessionScopeId, CancellationToken ct = default);

    Task RevokeSessionScopeAsync(Guid sessionScopeId, CancellationToken ct = default);

    Task SaveMfaEnrollmentAsync(Guid appUserId, string mfaSecretProtected, IReadOnlyList<string> recoveryCodeHashes, CancellationToken ct = default);

    Task<IReadOnlyList<RecoveryCodeRow>> GetRecoveryCodesAsync(Guid appUserId, CancellationToken ct = default);

    Task UseRecoveryCodeAsync(Guid mfaRecoveryCodeId, CancellationToken ct = default);

    Task<IReadOnlyList<UserListRow>> ListUsersAsync(CancellationToken ct = default);

    Task<UserDetail?> GetUserAsync(Guid appUserId, CancellationToken ct = default);

    Task SaveUserAsync(Guid appUserId, string userName, string displayName, string? email, bool isActive, string? passwordHash, Guid? homeTenantId, CancellationToken ct = default);

    Task SaveUserAssignmentAsync(Guid userAssignmentId, Guid appUserId, Guid roleId, Guid tenantId, bool includeDescendants, string accessLevel, DateOnly startDate, DateOnly? endDate, CancellationToken ct = default);

    Task<PersonSummaryRow?> FindUserForGrantAsync(string userName, CancellationToken ct = default);

    Task<IReadOnlyList<PersonSummaryRow>> ListViewAsUsersAsync(CancellationToken ct = default);

    Task<MyProfiles> ListMyProfilesAsync(CancellationToken ct = default);

    Task SaveMyProfileAsync(Guid userProfileId, string profileName, string? profileDescription, bool isActive, IReadOnlyList<ProfileGrantInput> grants, CancellationToken ct = default);

    Task ResetUserMfaAsync(Guid appUserId, CancellationToken ct = default);

    Task<IReadOnlyList<RoleRow>> ListRolesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<TenantListRow>> ListTenantsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<AssignableUserRow>> ListAssignableUsersAsync(CancellationToken ct = default);

    /// <summary>A region, state, tribe or stand-alone jurisdiction; the database checks who may create it.</summary>
    Task<Guid> CreateRootTenantAsync(TenantCreate tenant, CancellationToken ct = default);

    /// <summary>A program or local jurisdiction under a tenant the caller administers.</summary>
    Task<Guid> CreateChildTenantAsync(TenantCreate tenant, CancellationToken ct = default);
}

public interface IConfigData
{
    Task<ReferenceDataSet> GetReferenceDataAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ThresholdRow>> GetThresholdsAsync(Guid? analyteSpecimenId, DateOnly? asOfDate, CancellationToken ct = default);

    Task SaveThresholdAsync(ThresholdSave threshold, CancellationToken ct = default);

    Task<IReadOnlyList<SettingRow>> GetSettingsAsync(DateOnly? asOfDate, bool includeHistory, CancellationToken ct = default);

    Task SaveSettingAsync(string settingKey, string settingValue, DateOnly effectiveFrom, CancellationToken ct = default);

    Task SaveAnalyteAsync(Guid analyteId, bool isActive, int sortOrder, CancellationToken ct = default);

    Task<IReadOnlyList<SubJurisdictionRow>> ListSubJurisdictionsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<SubJurisdictionBoundaryRow>> ListSubJurisdictionBoundariesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<EnvironmentalStandardRow>> GetEnvironmentalStandardsAsync(CancellationToken ct = default);

    Task<ExtractLayoutData?> GetExtractLayoutAsync(string extractType, CancellationToken ct = default);
}

public interface IPersonData
{
    Task<SearchResults> SearchAsync(string searchText, string? nameKey1, string? nameKey2, DateOnly? dob, string? addressKey, int maxRows, CancellationToken ct = default);

    Task<PersonHeader> GetPersonAsync(Guid personId, CancellationToken ct = default);

    Task<IReadOnlyList<RelationshipRow>> GetRelationshipsAsync(Guid personId, CancellationToken ct = default);

    Task<IReadOnlyList<PlaceListRow>> GetPlacesAsync(Guid personId, CancellationToken ct = default);

    Task<IReadOnlyList<EmploymentRow>> GetEmploymentAsync(Guid personId, CancellationToken ct = default);

    Task<IReadOnlyList<ExposureSourceRow>> GetExposureSourcesAsync(Guid personId, CancellationToken ct = default);

    Task<IReadOnlyList<PregnancyRow>> GetPregnanciesAsync(Guid personId, CancellationToken ct = default);

    Task<IReadOnlyList<PersonCaseRow>> GetCasesAsync(Guid personId, CancellationToken ct = default);

    Task<IReadOnlyList<PersonResultRow>> GetResultsAsync(Guid personId, Guid? analyteId, CancellationToken ct = default);

    Task<SavePersonResult> SavePersonAsync(PersonSave person, CancellationToken ct = default);

    Task SaveRacesAsync(Guid personId, IReadOnlyList<string> raceCodes, CancellationToken ct = default);

    Task SaveContactMethodAsync(Guid id, Guid personId, string contactType, string contactValue, bool isPreferred, bool isDeleted, CancellationToken ct = default);

    Task SaveIdentifierAsync(Guid id, Guid personId, string identifierType, string identifierValue, string? assigningFacility, string source, DateOnly reportedDate, bool isDeleted, CancellationToken ct = default);

    Task SaveAliasAsync(AliasSave alias, CancellationToken ct = default);

    Task SavePregnancyAsync(PregnancySave pregnancy, CancellationToken ct = default);

    Task SaveRelationshipAsync(RelationshipSave relationship, CancellationToken ct = default);

    Task<IReadOnlyList<PersonCandidateRow>> FindCandidatesAsync(string lastNameKey, string? firstNameKey, DateOnly? dob, int maxRows, string? incomingKey = null, CancellationToken ct = default);
}

public interface IPlaceData
{
    Task<IReadOnlyList<LookupSelectionRow>> GetLookupSelectionsAsync(IReadOnlyList<LookupSelectionRequest> requests, CancellationToken ct = default);

    Task<IReadOnlyList<LocationCandidateRow>> FindLocationsAsync(string addressKeyPrefix, string? zip5, int maxRows, CancellationToken ct = default);

    Task<SaveLocationResult> SaveLocationAsync(LocationSave location, CancellationToken ct = default);

    Task SavePersonLocationsAsync(Guid personId, string changesJson, CancellationToken ct = default);

    Task<IReadOnlyList<OrganizationSearchRow>> SearchOrganizationsAsync(string nameKey, string? organizationTypeCode, int maxRows, CancellationToken ct = default);

    Task<OrganizationDetail?> GetOrganizationAsync(Guid organizationId, CancellationToken ct = default);

    Task SaveOrganizationAsync(OrganizationSave organization, CancellationToken ct = default);

    Task SaveOrganizationSiteAsync(Guid organizationSiteId, Guid organizationId, Guid locationId, string? siteName, DateOnly? startDate, DateOnly? endDate, bool isDeleted, CancellationToken ct = default);

    Task SaveEmploymentAsync(EmploymentSave employment, CancellationToken ct = default);

    Task SaveExposureSourceAsync(ExposureSourceSave source, CancellationToken ct = default);

    Task<IReadOnlyList<ProductRow>> SearchProductsAsync(string text, int maxRows, CancellationToken ct = default);

    Task SaveProductAsync(Guid productId, string? brand, string productName, string? countryOfOrigin, string? productType, string? notes, CancellationToken ct = default);

    Task<IReadOnlyList<ProviderSearchRow>> SearchProvidersAsync(string text, int maxRows, CancellationToken ct = default);

    /// <summary>The provider with exactly this NPI, or null; an index seek, unlike the type-ahead search.</summary>
    Task<ProviderSearchRow?> GetProviderByNpiAsync(string npi, CancellationToken ct = default);

    Task SaveProviderAsync(Guid providerId, string? npi, string lastName, string? firstName, string? credential, Guid? practiceLocationId, CancellationToken ct = default);

    Task<OrganizationResolveRow?> ResolveOrganizationAsync(string? cliaNumber, string? npi, string? nameKey, CancellationToken ct = default);

    Task<IReadOnlyList<ReporterRow>> ListReportersAsync(string? text, string? kind, int maxRows, CancellationToken ct = default);

    /// <summary>Adds or updates checked registry rows, matched on CLIA number or NPI.</summary>
    Task<RegistryImportCounts> ImportReportersAsync(IReadOnlyList<ReporterSave> rows, CancellationToken ct = default);
}

public interface IResultData
{
    Task<ClassificationContext> GetClassificationContextAsync(Guid personId, Guid analyteId, CancellationToken ct = default);

    Task<DuplicateResultRow?> FindDuplicateAsync(Guid personId, Guid analyteSpecimenId, DateOnly collectedDate, Guid labOrganizationId, string? accessionNumber, decimal? valueReported, CancellationToken ct = default);

    Task<SaveLabResultResult> SaveLabResultAsync(LabResultSave result, CancellationToken ct = default);

    Task UpdateDerivedAsync(Guid labResultId, Guid? personId, int? ageAtCollectionMonths, string population, bool isIndeterminate, CancellationToken ct = default);

    Task SetRowStatusAsync(Guid labResultId, string resultRowStatus, byte[] rowVersion, CancellationToken ct = default);

    Task<LabResultDetailRow?> GetLabResultAsync(Guid labResultId, CancellationToken ct = default);

    Task<IReadOnlyList<ResultListRow>> ListResultsAsync(ResultListQuery query, CancellationToken ct = default);
}

public interface ICaseData
{
    Task<OpenCaseResult> OpenCaseAsync(Guid exposureCaseId, Guid personId, Guid analyteId, string population, string classification, Guid qualifyingLabResultId, string workflowTemplate, DateOnly openedDate, CancellationToken ct = default);

    Task<AttachResultOutcome> AttachResultAsync(Guid exposureCaseId, Guid labResultId, string computedClassification, bool isHighest, CancellationToken ct = default);

    Task<CaseDetail?> GetCaseAsync(Guid exposureCaseId, CancellationToken ct = default);

    Task<IReadOnlyList<CaseListRow>> ListCasesAsync(CaseListQuery query, CancellationToken ct = default);

    Task<IReadOnlyList<CaseOwnerRow>> ListCaseOwnersAsync(CancellationToken ct = default);

    Task UpdateCaseAsync(Guid exposureCaseId, Guid? assignedToUserId, string exposureCategory, string caseStatus, byte[] rowVersion, CancellationToken ct = default);

    Task OverrideClassificationAsync(Guid exposureCaseId, string classification, string population, string reason, byte[] rowVersion, CancellationToken ct = default);

    Task<int> SaveActivityAsync(Guid caseActivityId, Guid exposureCaseId, string activityType, DateOnly activityDate, Guid? contactPersonId, string? outcome, string? note, CancellationToken ct = default);

    Task CloseCaseAsync(Guid exposureCaseId, string closureReason, DateOnly closedDate, string? note, byte[] rowVersion, CancellationToken ct = default);

    Task CreateFollowUpTasksAsync(Guid exposureCaseId, int adultAgeCutoffYears, CancellationToken ct = default);

    Task<IReadOnlyList<OpenInvestigationRow>> FindOpenInvestigationsAsync(string investigationType, Guid? locationId, Guid? organizationId, Guid? worksiteLocationId, Guid? exposureSourceId, CancellationToken ct = default);

    Task<string> CreateInvestigationAsync(InvestigationCreate investigation, CancellationToken ct = default);

    Task LinkInvestigationAsync(Guid investigationId, Guid? exposureCaseId, Guid? employmentId, CancellationToken ct = default);

    Task<InvestigationDetail?> GetInvestigationAsync(Guid investigationId, CancellationToken ct = default);

    Task SaveInvestigationAsync(InvestigationUpdate investigation, CancellationToken ct = default);

    Task<bool?> SaveEnvironmentalSampleAsync(EnvironmentalSampleSave sample, CancellationToken ct = default);

    Task<IReadOnlyList<InvestigationListRow>> ListInvestigationsAsync(bool openOnly, bool assignedToMe, int pageNumber, int pageSize, CancellationToken ct = default);
}

public interface IWorkData
{
    Task CreateTaskAsync(WorkTaskCreate task, CancellationToken ct = default);

    Task<IReadOnlyList<QueueCountRow>> GetQueueCountsAsync(CancellationToken ct = default);

    Task<DashboardData> GetDashboardAsync(CancellationToken ct = default);

    Task<IReadOnlyList<WorkTaskRow>> ListTasksAsync(WorkTaskQuery query, CancellationToken ct = default);

    Task<int> UpdateTasksAsync(IReadOnlyList<Guid> taskIds, string action, Guid? assignToUserId, string? note, CancellationToken ct = default);

    Task EnqueueJobAsync(Guid jobId, string jobType, string payloadJson, CancellationToken ct = default);

    Task<ClaimedJobRow?> ClaimNextJobAsync(string workerName, CancellationToken ct = default);

    Task CompleteJobAsync(Guid jobId, string? resultJson, CancellationToken ct = default);

    Task FailJobAsync(Guid jobId, string errorText, CancellationToken ct = default);

    Task<JobRow?> GetJobAsync(Guid jobId, CancellationToken ct = default);

    Task<IReadOnlyList<QueueDepthRow>> GetQueueDepthAsync(CancellationToken ct = default);

    Task<int> DetectClustersAsync(int windowDays, int minCount, CancellationToken ct = default);

    Task RecordAuditAsync(string eventAction, string entityType, Guid? entityId, int? recordCount, string? clientAddress, CancellationToken ct = default);

    Task<IReadOnlyList<AuditEventRow>> SearchAuditAsync(AuditQuery query, CancellationToken ct = default);

    Task<IReadOnlyList<AuditUserRow>> ListAuditUsersAsync(CancellationToken ct = default);
}

public interface IIntakeData
{
    Task<IReadOnlyList<IntakeProfileListRow>> ListProfilesAsync(CancellationToken ct = default);

    Task<IntakeProfileRow?> GetProfileAsync(Guid intakeProfileId, CancellationToken ct = default);

    Task CreateBatchAsync(Guid intakeBatchId, Guid intakeProfileId, string fileName, string rawContent, string contentSha256, Guid jobId,
                          CancellationToken ct = default);

    /// <summary>The first batch already received for the profile with exactly this content, or null.</summary>
    Task<ReceivedBatchRow?> FindBatchByContentAsync(Guid intakeProfileId, string contentSha256, CancellationToken ct = default);

    Task<IReadOnlyList<IntakeBatchRow>> ListBatchesAsync(int maxRows, CancellationToken ct = default);

    Task<IntakeBatchRow?> GetBatchAsync(Guid intakeBatchId, bool includeContent, CancellationToken ct = default);

    Task SetBatchStatusAsync(Guid intakeBatchId, string batchStatus, string? pauseReason, CancellationToken ct = default);

    Task SaveStagingRowsAsync(Guid intakeBatchId, IReadOnlyList<StagingRowNew> rows, CancellationToken ct = default);

    Task<IReadOnlyList<StagingRowForProcessing>> GetRowsForProcessingAsync(Guid intakeBatchId, bool releasedOnly, CancellationToken ct = default);

    Task CompleteRowAsync(Guid stagingRowId, string rowStatus, Guid? labResultId, IReadOnlyList<HoldReason> reasons, bool isUrgent, CancellationToken ct = default);

    Task<IReadOnlyList<HeldGroupRow>> ListHeldGroupsAsync(Guid? intakeBatchId = null, CancellationToken ct = default);

    Task<HeldOverview> GetHeldOverviewAsync(Guid? intakeBatchId, CancellationToken ct = default);

    Task<HeldSummaryRow> GetHeldSummaryAsync(Guid? intakeBatchId, CancellationToken ct = default);

    /// <summary>
    /// One page of held rows. With <paramref name="exactGroup"/> the reason, value and sender pick exactly one held group,
    /// and a null value or sender means none; without it a null filter matches every row.
    /// </summary>
    Task<IReadOnlyList<HeldRowRow>> ListHeldRowsAsync(string? reasonCode, string? reportedValue, Guid? senderOrganizationId, bool exactGroup,
                                                      Guid? intakeBatchId, int pageNumber, int pageSize, CancellationToken ct = default);

    /// <summary>Merges a patch (JSON object) into a held row's corrections: null clears a field, missing fields are kept.</summary>
    Task SaveCorrectionAsync(Guid stagingRowId, string correctedJson, CancellationToken ct = default);

    Task<int> ReleaseRowsAsync(IReadOnlyList<Guid> stagingRowIds, CancellationToken ct = default);

    Task<int> RejectRowsAsync(IReadOnlyList<Guid> stagingRowIds, string reason, CancellationToken ct = default);
}

public interface IExtractData
{
    Task CreateRunAsync(Guid extractRunId, string extractType, DateOnly periodStart, DateOnly periodEnd, Guid jobId, CancellationToken ct = default);

    Task<IReadOnlyList<ExtractRunRow>> ListRunsAsync(int maxRows, CancellationToken ct = default);

    Task<ExtractRunRow?> GetRunAsync(Guid extractRunId, bool includeFile, CancellationToken ct = default);

    Task UpdateRunAsync(Guid extractRunId, string runStatus, int? recordCount, int? errorCount, string? validationReportJson, string? fileName, string? fileContent, CancellationToken ct = default);

    /// <summary>Records that a ready file was sent to CDC or NIOSH.</summary>
    Task MarkSubmittedAsync(Guid extractRunId, CancellationToken ct = default);

    Task<IReadOnlyList<CblsSourceRow>> GetCblsRowsAsync(DateOnly periodStart, DateOnly periodEnd, CancellationToken ct = default);

    Task<IReadOnlyList<AblesSourceRow>> GetAblesRowsAsync(int reportYear, CancellationToken ct = default);
}

public interface IReviewData
{
    Task<IReadOnlyList<MatchReviewRow>> ListMatchReviewRowsAsync(bool escalatedOnly, Guid? stagingRowId, int pageNumber, int pageSize, CancellationToken ct = default);

    Task<CandidateDetails> GetCandidateDetailsAsync(IReadOnlyList<Guid> personIds, CancellationToken ct = default);

    Task RecordMatchDecisionAsync(Guid stagingRowId, string decision, Guid? personId, string? incomingKey, string? note, CancellationToken ct = default);

    Task<IReadOnlyList<ClusterListRow>> ListClustersAsync(string? clusterStatus, int pageNumber, int pageSize, CancellationToken ct = default);

    Task<ClusterDetail?> GetClusterAsync(Guid clusterId, CancellationToken ct = default);

    Task UpdateClusterAsync(Guid clusterId, string clusterStatus, string? notes, byte[] rowVersion, CancellationToken ct = default);

    Task SetCaseClusterMemberAsync(Guid clusterId, string caseNumber, bool isMember, CancellationToken ct = default);

    Task<IReadOnlyList<CaseMapRow>> GetCaseMapAsync(Guid? analyteId, DateOnly? openedFrom, DateOnly? openedTo, bool openOnly, CancellationToken ct = default);

    Task SetLocationCoordinatesAsync(Guid locationId, decimal latitude, decimal longitude, string geocodeQuality, CancellationToken ct = default);

    /// <summary>Runs one of the report procedures (dbo.uspReport*) with a period and an optional analyte.</summary>
    Task<ReportData> RunReportAsync(string procedure, DateOnly fromDate, DateOnly toDate, Guid? analyteId, CancellationToken ct = default);
}
