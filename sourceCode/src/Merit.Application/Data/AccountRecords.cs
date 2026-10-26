namespace Merit.Application.Data;

// Rows returned by the auth.* stored procedures. Property names match the procedures' column names.

public sealed class SignInUserRow
{
    public Guid AppUserId { get; init; }

    public Guid RootTenantId { get; init; }

    public string UserName { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string PasswordHash { get; init; } = string.Empty;

    public Guid SecurityStamp { get; init; }

    public string? MfaSecretProtected { get; init; }

    public bool MfaEnabled { get; init; }

    public int FailedSignInCount { get; init; }

    public DateTime? LockoutEndUtc { get; init; }

    public bool IsActive { get; init; }

    public bool IsAdministrator { get; init; }
}

public sealed class SessionScopeSummaryRow
{
    public Guid SessionScopeId { get; init; }

    public Guid AppUserId { get; init; }

    public string UserName { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public Guid RootTenantId { get; init; }

    public Guid ActingTenantId { get; init; }

    public string? ActingTenantName { get; init; }

    public string StatePostalCode { get; init; } = string.Empty;

    public Guid SecurityStamp { get; init; }

    public DateTime ExpiresUtc { get; init; }

    /// <summary>The user-made profile in use; null for the master profile.</summary>
    public Guid? UserProfileId { get; init; }

    public string ProfileName { get; init; } = "Master profile";

    /// <summary>Set while an administrator views MERIT as another user.</summary>
    public Guid? ViewAsUserId { get; init; }

    public string? ViewAsDisplayName { get; init; }

    public bool IsPlatformAdmin { get; init; }
}

public sealed class TenantRow
{
    public Guid TenantId { get; init; }

    public string TenantName { get; init; } = string.Empty;

    public string TenantType { get; init; } = string.Empty;

    public string AccessLevel { get; init; } = "read";
}

public sealed record SessionScopeResult(SessionScopeSummaryRow Summary, IReadOnlyList<string> Permissions,
                                        IReadOnlyList<TenantRow> Tenants);

public sealed class SessionScopeCheckRow
{
    public Guid SessionScopeId { get; init; }

    public Guid AppUserId { get; init; }

    public Guid RootTenantId { get; init; }

    public DateTime ExpiresUtc { get; init; }

    public Guid SecurityStamp { get; init; }
}

public sealed class RecoveryCodeRow
{
    public Guid MfaRecoveryCodeId { get; init; }

    public string CodeHash { get; init; } = string.Empty;
}

public sealed class UserListRow
{
    public Guid AppUserId { get; init; }

    public string UserName { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string? Email { get; init; }

    public bool IsActive { get; init; }

    public bool MfaEnabled { get; init; }

    public DateTime? LastSignInUtc { get; init; }

    public DateTime? LockoutEndUtc { get; init; }

    public string? Assignments { get; init; }

    public Guid? HomeTenantId { get; init; }

    public string? HomeTenantName { get; init; }

    public string? HomeTenantType { get; init; }

    /// <summary>1 when the caller administers this person's home tenant and so may change the account itself.</summary>
    public bool CanManageAccount { get; init; }
}

public sealed class UserAssignmentRow
{
    public Guid UserAssignmentId { get; init; }

    public Guid RoleId { get; init; }

    public string RoleName { get; init; } = string.Empty;

    public Guid TenantId { get; init; }

    public string TenantName { get; init; } = string.Empty;

    public bool IncludeDescendants { get; init; }

    public string AccessLevel { get; init; } = "full";

    public DateOnly StartDate { get; init; }

    public DateOnly? EndDate { get; init; }

    /// <summary>1 when the grant is at a tenant the caller administers.</summary>
    public bool CanManage { get; init; }
}

public sealed record UserDetail(UserListRow User, IReadOnlyList<UserAssignmentRow> Assignments);

/// <summary>A role: a named bundle of permissions defined at a data root (or the platform).</summary>
public sealed class RoleRow
{
    public Guid RoleId { get; init; }

    public string RoleCode { get; init; } = string.Empty;

    public string RoleName { get; init; } = string.Empty;

    public bool IsAdministrator { get; init; }

    public bool IsDeidentified { get; init; }

    public Guid RootTenantId { get; init; }

    public string RootTenantName { get; init; } = string.Empty;

    public string? Permissions { get; init; }
}

/// <summary>A profile the signed-in user can switch to; the master profile is their assignments.</summary>
public sealed class MyProfileRow
{
    public Guid UserProfileId { get; init; }

    public string ProfileName { get; init; } = string.Empty;

    public string? ProfileDescription { get; init; }

    public bool IsMaster { get; init; }

    public bool IsActive { get; init; }
}

public sealed class MyProfileGrantRow
{
    public Guid UserProfileId { get; init; }

    public Guid RoleId { get; init; }

    public string RoleName { get; init; } = string.Empty;

    public Guid TenantId { get; init; }

    public string TenantName { get; init; } = string.Empty;

    public bool IncludeDescendants { get; init; }

    public string AccessLevel { get; init; } = "read";
}

public sealed record MyProfiles(IReadOnlyList<MyProfileRow> Profiles, IReadOnlyList<MyProfileGrantRow> Grants);

public sealed record ProfileGrantInput(Guid TenantId, Guid RoleId, bool IncludeDescendants, string AccessLevel);

public sealed class PersonSummaryRow
{
    public Guid AppUserId { get; init; }

    public string UserName { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string? HomeTenantName { get; init; }
}

public sealed class TenantListRow
{
    public Guid TenantId { get; init; }

    public Guid? ParentTenantId { get; init; }

    public string TenantCode { get; init; } = string.Empty;

    public string TenantName { get; init; } = string.Empty;

    public string TenantType { get; init; } = string.Empty;

    public Guid RootTenantId { get; init; }

    public int Depth { get; init; }

    /// <summary>The caller administers this tenant (users, grants, and new tenants under it).</summary>
    public bool CanAdminister { get; init; }
}

public sealed class AssignableUserRow
{
    public Guid AppUserId { get; init; }

    public string DisplayName { get; init; } = string.Empty;
}

public sealed class AuditEventRow
{
    public long AuditEventId { get; init; }

    public DateTime EventUtc { get; init; }

    public Guid? AppUserId { get; init; }

    public string? DisplayName { get; init; }

    public string EventAction { get; init; } = string.Empty;

    public string EntityType { get; init; } = string.Empty;

    public Guid? EntityId { get; init; }

    public int? RecordCount { get; init; }

    public string? ClientAddress { get; init; }
}

/// <summary>A county or other sub-jurisdiction new to a data root, added while creating a tenant that serves it.</summary>
public sealed record NewCounty(string Fips, string Name, string Type = "county");

/// <summary>What the database needs to create a tenant. A data root also gets its roles, settings and counties.</summary>
public sealed record TenantCreate(string TenantCode, string TenantName, string TenantType, string ParentTenantCode, string? StatePostalCode,
                                  string? StateFips, IReadOnlyList<string> CountyFips, IReadOnlyList<NewCounty> NewCounties);
