namespace Merit.Application.Security;

/// <summary>
/// Who is acting, resolved at sign-in. The web application builds it from the session cookie; the worker builds it from
/// the user a job records. The data layer places ScopeKey, UserId, UserName and ActingTenantId into SQL Server's
/// SESSION_CONTEXT (read-only) on every connection, which is what row-level security filters on.
/// </summary>
public interface ICurrentSession
{
    bool IsAuthenticated { get; }

    Guid? UserId { get; }

    string? UserName { get; }

    string? DisplayName { get; }

    Guid? ScopeKey { get; }

    Guid? RootTenantId { get; }

    Guid? ActingTenantId { get; }

    string? ActingTenantName { get; }

    IReadOnlySet<string> Permissions { get; }

    bool Has(string permission) => Permissions.Contains(permission);

    /// <summary>The profile the session uses: "Master profile", a profile the user made, or "Viewing as ...".</summary>
    string? ProfileName => null;

    /// <summary>The time zone of the session's jurisdiction (Windows name); see <see cref="IClock"/>.</summary>
    string TimeZoneId => JurisdictionTimeZones.Default;

    /// <summary>The user's own profile the session uses; null for the master profile.</summary>
    Guid? ProfileId => null;

    /// <summary>The person whose access an administrator is viewing MERIT with; null otherwise.</summary>
    Guid? ViewAsUserId => null;

    /// <summary>The person whose access an administrator is viewing MERIT with; null otherwise.</summary>
    string? ViewAsDisplayName => null;

    /// <summary>A platform administrator manages accounts and roles only and sees no records.</summary>
    bool IsPlatformAdmin => false;

    /// <summary>Name stamped into audit columns: the real user, and whose view they are using.</summary>
    string? AuditName => ViewAsDisplayName is null ? UserName : $"{UserName} (viewing as {ViewAsDisplayName})";
}

public static class CurrentSessionExtensions
{
    /// <summary>Throws <see cref="Errors.PermissionDeniedException"/> unless the session holds the permission.</summary>
    public static void Require(this ICurrentSession session, string permission)
    {
        if (!session.IsAuthenticated || !session.Permissions.Contains(permission))
        {
            throw new Errors.PermissionDeniedException(permission);
        }
    }

    public static Guid RequireUserId(this ICurrentSession session)
        => session.UserId ?? throw new Errors.PermissionDeniedException(Security.Permissions.SignedIn);
}
