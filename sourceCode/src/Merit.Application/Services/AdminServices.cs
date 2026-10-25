using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Reference;
using Merit.Application.Security;
using Merit.Domain.Ids;
using Merit.Domain.Text;

namespace Merit.Application.Services;

/// <summary>Work queues and the home dashboard.</summary>
public sealed class QueueService(IWorkData work, AuditService audit, ICurrentSession session)
{
    public static readonly IReadOnlyList<string> BulkActions = ["assignToMe", "assign", "complete", "dismiss", "reopen"];

    public async Task<IReadOnlyList<QueueCountRow>> CountsAsync(CancellationToken ct = default)
    {
        session.Require(Permissions.TaskView);
        return await work.GetQueueCountsAsync(ct).ConfigureAwait(false);
    }

    public async Task<DashboardData> DashboardAsync(CancellationToken ct = default)
    {
        session.Require(Permissions.TaskView);
        return await work.GetDashboardAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WorkTaskRow>> ListAsync(WorkTaskQuery query, CancellationToken ct = default)
    {
        session.Require(Permissions.TaskView);
        var rows = await work.ListTasksAsync(query, ct).ConfigureAwait(false);
        await audit.ListedAsync("workTask", rows.Count, ct).ConfigureAwait(false);
        return rows;
    }

    public async Task<int> BulkAsync(IReadOnlyList<Guid> taskIds, string action, Guid? assignTo, string? note, CancellationToken ct = default)
    {
        session.Require(Permissions.TaskEdit);
        var v = new Validator();
        v.When(taskIds.Count == 0, "taskIds", "Select at least one task.");
        v.When(!BulkActions.Contains(action), "action", "Unknown action.");
        v.When(action == "assign" && assignTo is null, "assignToUserId", "Choose who to assign to.");
        v.ThrowIfInvalid();
        return await work.UpdateTasksAsync(taskIds, action, assignTo, note, ct).ConfigureAwait(false);
    }
}

/// <summary>Threshold, settings and analyte configuration screens.</summary>
public sealed class ConfigService(IConfigData config, IReferenceDataProvider reference, IThresholdProvider thresholds,
                                  ISettingsProvider settings, ICurrentSession session)
{
    /// <summary>Configuration is visible to every user under the root tenant; changing it needs config.edit.</summary>
    public async Task<IReadOnlyList<ThresholdRow>> ThresholdsAsync(CancellationToken ct = default)
    {
        session.RequireUserId();
        return await config.GetThresholdsAsync(null, null, ct).ConfigureAwait(false);
    }

    public async Task SaveThresholdAsync(ThresholdSave t, CancellationToken ct = default)
    {
        session.Require(Permissions.ConfigEdit);
        var v = new Validator();
        v.When(t.Population is not ("child" or "adult" or "pregnant" or "all"), "Population", "Choose a population.");
        v.When(t.ThresholdType is not ("reportable" or "case" or "action"), "ThresholdType", "Choose a type.");
        v.When(t.ThresholdType == "action" && t.ActionCodeId is null, "ActionCodeId", "An action level needs the step it triggers.");
        v.When(t.LowerValue < 0, "LowerValue", "The level cannot be negative.");
        v.When(t.UpperValue is not null && t.UpperValue <= t.LowerValue, "UpperValue", "The upper bound must be above the level.");
        v.When(t.EffectiveTo is not null && t.EffectiveTo < t.EffectiveFrom, "EffectiveTo", "The end date is before the start date.");
        v.When(t.Comparator is not (">=" or ">"), "Comparator", "Choose >= or >.");
        v.ThrowIfInvalid();
        await config.SaveThresholdAsync(t, ct).ConfigureAwait(false);
        thresholds.Invalidate();
    }

    public async Task<IReadOnlyList<SettingRow>> SettingsAsync(bool includeHistory, CancellationToken ct = default)
    {
        session.RequireUserId();
        return await config.GetSettingsAsync(null, includeHistory, ct).ConfigureAwait(false);
    }

    public async Task SaveSettingAsync(string key, string? value, DateOnly effectiveFrom, CancellationToken ct = default)
    {
        session.Require(Permissions.ConfigEdit);
        new Validator().Required("SettingValue", value, "Value").MaxLength("SettingValue", value, 2000, "Value").ThrowIfInvalid();
        await config.SaveSettingAsync(key, value!.Trim(), effectiveFrom, ct).ConfigureAwait(false);
        settings.Invalidate();
    }

    public async Task SaveAnalyteAsync(Guid analyteId, bool isActive, int sortOrder, CancellationToken ct = default)
    {
        session.Require(Permissions.ConfigEdit);
        await config.SaveAnalyteAsync(analyteId, isActive, sortOrder, ct).ConfigureAwait(false);
        reference.Invalidate();
    }

    public Task<IReadOnlyList<SubJurisdictionRow>> SubJurisdictionsAsync(CancellationToken ct = default)
        => config.ListSubJurisdictionsAsync(ct);

    public Task<IReadOnlyList<EnvironmentalStandardRow>> StandardsAsync(CancellationToken ct = default)
        => config.GetEnvironmentalStandardsAsync(ct);
}

public sealed record UserInput(Guid? AppUserId, string? UserName, string? DisplayName, string? Email, bool IsActive, string? NewPassword,
                               Guid? HomeTenantId = null);

/// <summary>User administration: accounts, profile assignments at tenant nodes, MFA resets.</summary>
public sealed class AdminService(IAccountData accounts, AccountService accountService, ISettingsProvider settings, ICurrentSession session)
{
    public async Task<IReadOnlyList<UserListRow>> UsersAsync(CancellationToken ct = default)
    {
        session.Require(Permissions.TenantAdminister);
        return await accounts.ListUsersAsync(ct).ConfigureAwait(false);
    }

    public async Task<UserDetail> UserAsync(Guid id, CancellationToken ct = default)
    {
        session.Require(Permissions.TenantAdminister);
        return await accounts.GetUserAsync(id, ct).ConfigureAwait(false) ?? throw new NotFoundException();
    }

    public async Task<Guid> SaveUserAsync(UserInput input, CancellationToken ct = default)
    {
        session.Require(Permissions.TenantAdminister);
        var v = new Validator();
        v.Required("UserName", input.UserName, "User name").MaxLength("UserName", input.UserName, 100, "User name");
        v.Required("DisplayName", input.DisplayName, "Display name");
        v.When(input.AppUserId is null && string.IsNullOrWhiteSpace(input.NewPassword), "NewPassword", "A new account needs an initial password.");
        v.ThrowIfInvalid();
        string? hash = null;
        if (!string.IsNullOrWhiteSpace(input.NewPassword))
        {
            var config = await settings.GetAsync(ct).ConfigureAwait(false);
            accountService.ValidateNewPassword(input.NewPassword, config.PasswordMinLength);
            hash = accountService.HashPassword(input.NewPassword);
        }

        Guid id = input.AppUserId ?? TimeOrderedId.New();
        await accounts.SaveUserAsync(id, input.UserName!.Trim(), input.DisplayName!.Trim(), input.Email?.Trim(), input.IsActive, hash,
            input.HomeTenantId, ct).ConfigureAwait(false);
        return id;
    }

    /// <summary>Adds a grant to a user's master profile: a role at a tenant, with or without the tenants below, at an access level.</summary>
    public async Task AssignAsync(Guid appUserId, Guid roleId, Guid tenantId, bool includeDescendants, string accessLevel, DateOnly startDate,
                                  DateOnly? endDate, CancellationToken ct = default)
    {
        session.Require(Permissions.TenantAdminister);
        new Validator().When(accessLevel is not ("read" or "write" or "full"), "AccessLevel", "Choose read only, read and write, or full.")
            .ThrowIfInvalid();
        await accounts.SaveUserAssignmentAsync(TimeOrderedId.New(), appUserId, roleId, tenantId, includeDescendants, accessLevel, startDate,
            endDate, ct).ConfigureAwait(false);
    }

    public async Task EndAssignmentAsync(Guid appUserId, UserAssignmentRow assignment, CancellationToken ct = default)
    {
        session.Require(Permissions.TenantAdminister);
        await accounts.SaveUserAssignmentAsync(assignment.UserAssignmentId, appUserId, assignment.RoleId, assignment.TenantId,
            assignment.IncludeDescendants, assignment.AccessLevel, assignment.StartDate, DateOnly.FromDateTime(DateTime.UtcNow), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Finds a person by exact user name, to give someone from another tenant access where the caller administers.</summary>
    public async Task<PersonSummaryRow?> FindUserAsync(string? userName, CancellationToken ct = default)
    {
        session.Require(Permissions.TenantAdminister);
        return string.IsNullOrWhiteSpace(userName) ? null : await accounts.FindUserForGrantAsync(userName.Trim(), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PersonSummaryRow>> ViewAsCandidatesAsync(CancellationToken ct = default)
    {
        session.Require(Permissions.TenantAdminister);
        return session.IsPlatformAdmin ? [] : await accounts.ListViewAsUsersAsync(ct).ConfigureAwait(false);
    }

    public async Task ResetMfaAsync(Guid appUserId, CancellationToken ct = default)
    {
        session.Require(Permissions.TenantAdminister);
        await accounts.ResetUserMfaAsync(appUserId, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<RoleRow>> RolesAsync(CancellationToken ct = default) => accounts.ListRolesAsync(ct);

    public Task<IReadOnlyList<TenantListRow>> TenantsAsync(CancellationToken ct = default) => accounts.ListTenantsAsync(ct);
}

/// <summary>Server-side type-ahead for organizations, providers, locations and products.</summary>
public sealed class LookupService(IPlaceData places, ICurrentSession session)
{
    public async Task<IReadOnlyList<OrganizationSearchRow>> OrganizationsAsync(string? text, string? type, CancellationToken ct = default)
    {
        session.Require(Permissions.PersonView);
        string key = MatchKeys.NormalizeWords(text);
        return key.Length < 2 ? [] : await places.SearchOrganizationsAsync(key, type, 10, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProviderSearchRow>> ProvidersAsync(string? text, CancellationToken ct = default)
    {
        session.Require(Permissions.PersonView);
        return string.IsNullOrWhiteSpace(text) || text.Trim().Length < 2
            ? []
            : await places.SearchProvidersAsync(text.Trim(), 10, ct).ConfigureAwait(false);
    }

    public async Task<OrganizationDetail> OrganizationAsync(Guid id, CancellationToken ct = default)
    {
        session.Require(Permissions.PersonView);
        return await places.GetOrganizationAsync(id, ct).ConfigureAwait(false) ?? throw new NotFoundException();
    }
}
