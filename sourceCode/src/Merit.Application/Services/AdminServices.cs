using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Reference;
using Merit.Application.Security;
using Merit.Domain.Ids;
using Merit.Domain.Tenants;
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

    /// <summary>County outlines for the maps (issue #48): reference geography, not records, so any signed-in user.</summary>
    public Task<IReadOnlyList<SubJurisdictionBoundaryRow>> BoundariesAsync(CancellationToken ct = default)
        => config.ListSubJurisdictionBoundariesAsync(ct);

    public Task<IReadOnlyList<EnvironmentalStandardRow>> StandardsAsync(CancellationToken ct = default)
        => config.GetEnvironmentalStandardsAsync(ct);
}

public sealed record UserInput(Guid? AppUserId, string? UserName, string? DisplayName, string? Email, bool IsActive, string? NewPassword,
                               Guid? HomeTenantId = null);

/// <summary>User administration: accounts, profile assignments at tenant nodes, MFA resets.</summary>
public sealed class AdminService(IClock clock, IAccountData accounts, AccountService accountService, ISettingsProvider settings, ICurrentSession session)
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
            assignment.IncludeDescendants, assignment.AccessLevel, assignment.StartDate, clock.Today, ct)
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

    /// <summary>
    /// Adds a tenant under one the caller administers. Under the platform or a region the new tenant is a data root
    /// (a platform administrator, or a region administrator adding a state or tribe); elsewhere it is a program or
    /// local jurisdiction sharing its parent's records. The database repeats every check.
    /// </summary>
    public async Task<Guid> CreateTenantAsync(TenantInput input, CancellationToken ct = default)
    {
        if (!session.IsPlatformAdmin)
        {
            session.Require(Permissions.TenantCreate);
        }

        var tenants = await accounts.ListTenantsAsync(ct).ConfigureAwait(false);
        var parent = tenants.FirstOrDefault(t => t.TenantId == input.ParentTenantId && t.CanAdminister)
                     ?? throw new ValidationException("ParentTenantId", "Choose the tenant the new one goes under.");

        string code = (input.TenantCode ?? string.Empty).Trim().ToUpperInvariant();
        string type = input.TenantType ?? string.Empty;
        string? postal = NullIfBlank(input.StatePostalCode)?.ToUpperInvariant();
        string? stateFips = NullIfBlank(input.StateFips);
        var countyFips = TenantRules.ParseFipsList(input.CountyFipsList);
        var newCounties = ParseNewCounties(input.NewCounties, out var badLines);
        var allowed = TenantRules.ChildTypes(parent.TenantType);

        var v = new Validator();
        v.When(!TenantRules.IsValidCode(code), "TenantCode", "Use capital letters, digits and hyphens, up to 30 characters (for example WV-JEFFERSON).");
        v.Required("TenantName", input.TenantName, "Name").MaxLength("TenantName", input.TenantName, 120, "Name");
        v.When(!allowed.Contains(type), "TenantType", $"Under a {parent.TenantType} you can add: {string.Join(", ", allowed)}.");
        v.When(TenantRules.NeedsPostalCode(type) && (postal is null || postal.Length != 2 || !postal.All(char.IsAsciiLetter)),
            "StatePostalCode", "Enter the two-letter postal code.");
        v.When(stateFips is not null && (stateFips.Length != 2 || !stateFips.All(char.IsAsciiDigit)), "StateFips", "A state FIPS code is two digits.");
        string? timeZone = NullIfBlank(input.TimeZone);
        v.When(TenantRules.CreatesDataRoot(parent.TenantType) && timeZone is not null && !JurisdictionTimeZones.IsKnown(timeZone),
            "TimeZone", "Choose the time zone from the list.");
        v.When(countyFips.Any(f => !TenantRules.IsValidCountyFips(f)), "CountyFipsList", "County FIPS codes are three digits, for example 037.");
        v.When(newCounties.Count > 0 && !TenantRules.MayChooseAnyCounty(parent.TenantId == parent.RootTenantId, parent.TenantType),
            "NewCounties", $"New counties are added under the state, tribe or other data root, not under {parent.TenantName}.");
        v.When(badLines.Count > 0, "NewCounties",
            $"Write each new county as its FIPS code then its name, for example \"037 Jefferson County\". Check: {string.Join("; ", badLines)}.");
        v.When(tenants.Any(t => string.Equals(t.TenantCode, code, StringComparison.OrdinalIgnoreCase) && t.ParentTenantId != parent.TenantId),
            "TenantCode", "That code is already used elsewhere in the tree.");
        v.ThrowIfInvalid();

        var create = new TenantCreate(code, input.TenantName!.Trim(), type, parent.TenantCode, postal, stateFips, countyFips, newCounties,
            TenantRules.CreatesDataRoot(parent.TenantType) ? timeZone ?? JurisdictionTimeZones.Default : null);
        return TenantRules.CreatesDataRoot(parent.TenantType)
            ? await accounts.CreateRootTenantAsync(create, ct).ConfigureAwait(false)
            : await accounts.CreateChildTenantAsync(create, ct).ConfigureAwait(false);
    }

    /// <summary>One county per line: "037 Jefferson County". Lines that do not fit are returned for the message.</summary>
    public static IReadOnlyList<NewCounty> ParseNewCounties(string? text, out IReadOnlyList<string> badLines)
    {
        var counties = new List<NewCounty>();
        var bad = new List<string>();
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            int gap = line.IndexOfAny([' ', '\t', ',']);
            string fips = gap < 0 ? line : line[..gap];
            string name = gap < 0 ? string.Empty : line[(gap + 1)..].Trim(' ', ',', '\t');
            if (TenantRules.IsValidCountyFips(fips) && name.Length is > 0 and <= 80)
            {
                counties.Add(new NewCounty(fips, name));
            }
            else
            {
                bad.Add(line);
            }
        }

        badLines = bad;
        return counties;
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>The Add tenant form. County FIPS codes are those the tenant serves; new counties are added to the data root.</summary>
public sealed record TenantInput(Guid? ParentTenantId, string? TenantCode, string? TenantName, string? TenantType, string? StatePostalCode,
                                 string? StateFips, string? CountyFipsList, string? NewCounties, string? TimeZone = null);

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
