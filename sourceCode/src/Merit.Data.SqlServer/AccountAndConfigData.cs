using Dapper;
using System.Text.Json;
using Merit.Application.Data;

namespace Merit.Data.SqlServer;

public sealed class AccountData(Db db) : IAccountData
{
    public Task<SignInUserRow?> GetUserForSignInAsync(string userName, CancellationToken ct = default)
        => db.QuerySingleAsync<SignInUserRow>("auth.uspGetUserForSignIn", new { userName }, ct);

    public Task RecordSignInResultAsync(Guid? appUserId, bool succeeded, int maxFailedAttempts, int lockoutMinutes, string? clientAddress,
                                        CancellationToken ct = default)
        => db.ExecuteAsync("auth.uspRecordSignInResult", new { appUserId, succeeded, maxFailedAttempts, lockoutMinutes, clientAddress }, ct);

    public Task<SessionScopeResult> CreateSessionScopeAsync(Guid sessionScopeId, Guid appUserId, DateTime expiresUtc, Guid? userProfileId = null,
                                                            Guid? viewAsUserId = null, Guid? actingTenantId = null, CancellationToken ct = default)
        => db.MultipleAsync("auth.uspCreateSessionScope",
            new { sessionScopeId, appUserId, expiresUtc, userProfileId, viewAsUserId, actingTenantId }, async g =>
        {
            var summary = await g.ReadSingleAsync<SessionScopeSummaryRow>().ConfigureAwait(false);
            var permissions = (await g.ReadAsync<string>().ConfigureAwait(false)).AsList();
            var tenants = (await g.ReadAsync<TenantRow>().ConfigureAwait(false)).AsList();
            return new SessionScopeResult(summary, permissions, tenants);
        }, ct);

    public Task<SessionScopeCheckRow?> GetSessionScopeAsync(Guid sessionScopeId, CancellationToken ct = default)
        => db.QuerySingleAsync<SessionScopeCheckRow>("auth.uspGetSessionScope", new { sessionScopeId }, ct);

    public Task RevokeSessionScopeAsync(Guid sessionScopeId, CancellationToken ct = default)
        => db.ExecuteAsync("auth.uspRevokeSessionScope", new { sessionScopeId }, ct);

    public Task SaveMfaEnrollmentAsync(Guid appUserId, string mfaSecretProtected, IReadOnlyList<string> recoveryCodeHashes, CancellationToken ct = default)
        => db.ExecuteAsync("auth.uspSaveMfaEnrollment",
            new { appUserId, mfaSecretProtected, recoveryCodeHashesJson = JsonSerializer.Serialize(recoveryCodeHashes) }, ct);

    public Task<IReadOnlyList<RecoveryCodeRow>> GetRecoveryCodesAsync(Guid appUserId, CancellationToken ct = default)
        => db.QueryAsync<RecoveryCodeRow>("auth.uspGetRecoveryCodes", new { appUserId }, ct);

    public Task UseRecoveryCodeAsync(Guid mfaRecoveryCodeId, CancellationToken ct = default)
        => db.ExecuteAsync("auth.uspUseRecoveryCode", new { mfaRecoveryCodeId }, ct);

    public Task<IReadOnlyList<UserListRow>> ListUsersAsync(CancellationToken ct = default)
        => db.QueryAsync<UserListRow>("auth.uspListUsers", null, ct);

    public Task<UserDetail?> GetUserAsync(Guid appUserId, CancellationToken ct = default)
        => db.MultipleAsync("auth.uspGetUser", new { appUserId }, async g =>
        {
            var user = await g.ReadFirstOrDefaultAsync<UserListRow>().ConfigureAwait(false);
            var assignments = (await g.ReadAsync<UserAssignmentRow>().ConfigureAwait(false)).AsList();
            return user is null ? null : new UserDetail(user, assignments);
        }, ct);

    public Task SaveUserAsync(Guid appUserId, string userName, string displayName, string? email, bool isActive, string? passwordHash,
                              Guid? homeTenantId, CancellationToken ct = default)
        => db.ExecuteAsync("auth.uspSaveUser", new { appUserId, userName, displayName, email, isActive, passwordHash, homeTenantId }, ct);

    public Task SaveUserAssignmentAsync(Guid userAssignmentId, Guid appUserId, Guid roleId, Guid tenantId, bool includeDescendants,
                                        string accessLevel, DateOnly startDate, DateOnly? endDate, CancellationToken ct = default)
        => db.ExecuteAsync("auth.uspSaveUserAssignment",
            new { userAssignmentId, appUserId, roleId, tenantId, includeDescendants, accessLevel, startDate, endDate }, ct);

    public Task<PersonSummaryRow?> FindUserForGrantAsync(string userName, CancellationToken ct = default)
        => db.QuerySingleAsync<PersonSummaryRow>("auth.uspFindUserForGrant", new { userName }, ct);

    public Task<IReadOnlyList<PersonSummaryRow>> ListViewAsUsersAsync(CancellationToken ct = default)
        => db.QueryAsync<PersonSummaryRow>("auth.uspListViewAsUsers", null, ct);

    public Task<MyProfiles> ListMyProfilesAsync(CancellationToken ct = default)
        => db.MultipleAsync("auth.uspListMyProfiles", null, async g =>
        {
            var profiles = (await g.ReadAsync<MyProfileRow>().ConfigureAwait(false)).AsList();
            var grants = (await g.ReadAsync<MyProfileGrantRow>().ConfigureAwait(false)).AsList();
            return new MyProfiles(profiles, grants);
        }, ct);

    public Task SaveMyProfileAsync(Guid userProfileId, string profileName, string? profileDescription, bool isActive,
                                   IReadOnlyList<ProfileGrantInput> grants, CancellationToken ct = default)
        => db.ExecuteAsync("auth.uspSaveMyProfile", new
        {
            userProfileId, profileName, profileDescription, isActive,
            grantsJson = JsonSerializer.Serialize(grants.Select(x => new
            {
                tenantId = x.TenantId, roleId = x.RoleId, includeDescendants = x.IncludeDescendants, accessLevel = x.AccessLevel,
            })),
        }, ct);

    public Task ResetUserMfaAsync(Guid appUserId, CancellationToken ct = default)
        => db.ExecuteAsync("auth.uspResetUserMfa", new { appUserId }, ct);

    public Task<IReadOnlyList<RoleRow>> ListRolesAsync(CancellationToken ct = default)
        => db.QueryAsync<RoleRow>("auth.uspListRoles", null, ct);

    public Task<IReadOnlyList<TenantListRow>> ListTenantsAsync(CancellationToken ct = default)
        => db.QueryAsync<TenantListRow>("auth.uspListTenants", null, ct);

    public Task<IReadOnlyList<AssignableUserRow>> ListAssignableUsersAsync(CancellationToken ct = default)
        => db.QueryAsync<AssignableUserRow>("dbo.uspListAssignableUsers", null, ct);

    public async Task<Guid> CreateRootTenantAsync(TenantCreate t, CancellationToken ct = default)
        => await db.QuerySingleAsync<Guid>("config.uspCreateRootTenant", new
        {
            tenantCode = t.TenantCode, tenantName = t.TenantName, statePostalCode = t.StatePostalCode, stateFips = t.StateFips,
            tenantType = t.TenantType, parentTenantCode = t.ParentTenantCode, countiesJson = CountiesJson(t.NewCounties), timeZone = t.TimeZone,
        }, ct).ConfigureAwait(false);

    public async Task<Guid> CreateChildTenantAsync(TenantCreate t, CancellationToken ct = default)
        => await db.QuerySingleAsync<Guid>("config.uspCreateChildTenant", new
        {
            tenantCode = t.TenantCode, tenantName = t.TenantName, tenantType = t.TenantType, parentTenantCode = t.ParentTenantCode,
            countyFipsList = string.Join(',', t.CountyFips), countiesJson = CountiesJson(t.NewCounties),
        }, ct).ConfigureAwait(false);

    private static string CountiesJson(IReadOnlyList<NewCounty> counties)
        => JsonSerializer.Serialize(counties.Select(c => new { fips = c.Fips, name = c.Name, type = c.Type }));
}

public sealed class ConfigData(Db db) : IConfigData
{
    public Task<ReferenceDataSet> GetReferenceDataAsync(CancellationToken ct = default)
        => db.MultipleAsync("config.uspGetReferenceData", null, async g => new ReferenceDataSet(
            (await g.ReadAsync<AnalyteRow>().ConfigureAwait(false)).AsList(),
            (await g.ReadAsync<AnalyteSpecimenRow>().ConfigureAwait(false)).AsList(),
            (await g.ReadAsync<AcceptedUnitRow>().ConfigureAwait(false)).AsList(),
            (await g.ReadAsync<LoincRow>().ConfigureAwait(false)).AsList(),
            (await g.ReadAsync<ActionCodeRow>().ConfigureAwait(false)).AsList(),
            (await g.ReadAsync<LookupRow>().ConfigureAwait(false)).AsList()), ct);

    public Task<IReadOnlyList<ThresholdRow>> GetThresholdsAsync(Guid? analyteSpecimenId, DateOnly? asOfDate, CancellationToken ct = default)
        => db.QueryAsync<ThresholdRow>("config.uspGetThresholds", new { analyteSpecimenId, asOfDate }, ct);

    public Task SaveThresholdAsync(ThresholdSave t, CancellationToken ct = default)
        => db.ExecuteAsync("config.uspSaveThreshold", new
        {
            t.ThresholdId, t.AnalyteSpecimenId, t.Population, t.AgeMinMonths, t.AgeMaxMonths, t.Speciation, t.SampleType,
            t.ThresholdType, t.ActionCodeId, t.DueIntervalDays, t.LowerValue, t.UpperValue, t.Unit, t.Comparator,
            t.EffectiveFrom, t.EffectiveTo, t.Authority, t.IsActive, t.RowVersion,
        }, ct);

    public Task<IReadOnlyList<SettingRow>> GetSettingsAsync(DateOnly? asOfDate, bool includeHistory, CancellationToken ct = default)
        => db.QueryAsync<SettingRow>("config.uspGetSettings", new { asOfDate, includeHistory }, ct);

    public Task SaveSettingAsync(string settingKey, string settingValue, DateOnly effectiveFrom, CancellationToken ct = default)
        => db.ExecuteAsync("config.uspSaveSetting", new { settingKey, settingValue, effectiveFrom }, ct);

    public Task SaveAnalyteAsync(Guid analyteId, bool isActive, int sortOrder, CancellationToken ct = default)
        => db.ExecuteAsync("config.uspSaveAnalyte", new { analyteId, isActive, sortOrder }, ct);

    public Task<IReadOnlyList<SubJurisdictionRow>> ListSubJurisdictionsAsync(CancellationToken ct = default)
        => db.QueryAsync<SubJurisdictionRow>("config.uspListSubJurisdictions", null, ct);

    public Task<IReadOnlyList<EnvironmentalStandardRow>> GetEnvironmentalStandardsAsync(CancellationToken ct = default)
        => db.QueryAsync<EnvironmentalStandardRow>("config.uspGetEnvironmentalStandards", null, ct);

    public Task<ExtractLayoutData?> GetExtractLayoutAsync(string extractType, CancellationToken ct = default)
        => db.MultipleAsync("config.uspGetExtractLayout", new { extractType }, async g =>
        {
            var layout = await g.ReadFirstOrDefaultAsync<ExtractLayoutRow>().ConfigureAwait(false);
            var fields = (await g.ReadAsync<ExtractLayoutFieldRow>().ConfigureAwait(false)).AsList();
            return layout is null ? null : new ExtractLayoutData(layout, fields);
        }, ct);
}
