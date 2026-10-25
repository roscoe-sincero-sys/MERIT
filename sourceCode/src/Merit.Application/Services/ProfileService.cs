using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Security;
using Merit.Domain.Ids;

namespace Merit.Application.Services;

/// <summary>
/// The signed-in person's own profiles. The master profile is everything their administrator gave them and is read-only
/// here; other profiles are subsets for everyday work (read only, one county, no deleting). The database checks that a
/// profile never exceeds the master, and clips it again every time it is used. A platform administrator has only the
/// master profile.
/// </summary>
public sealed class ProfileService(IAccountData accounts, ICurrentSession session)
{
    public static readonly string[] AccessLevels = ["read", "write", "full"];

    public async Task<MyProfiles> MineAsync(CancellationToken ct = default)
    {
        session.RequireUserId();
        return await accounts.ListMyProfilesAsync(ct).ConfigureAwait(false);
    }

    public async Task<Guid> SaveAsync(Guid? userProfileId, string? profileName, string? description, bool isActive,
                                      IReadOnlyList<ProfileGrantInput> grants, CancellationToken ct = default)
    {
        session.RequireUserId();
        if (session.IsPlatformAdmin)
        {
            throw new PermissionDeniedException("platform administrators have only the master profile");
        }

        var v = new Validator();
        v.Required("ProfileName", profileName, "Profile name").MaxLength("ProfileName", profileName, 80, "Profile name");
        v.MaxLength("ProfileDescription", description, 300, "Description");
        v.When(isActive && grants.Count == 0, "Grants", "Add at least one grant: a role, a tenant and an access level.");
        v.When(grants.Any(g => !AccessLevels.Contains(g.AccessLevel)), "Grants", "Each grant needs read, write or full access.");
        v.ThrowIfInvalid();

        Guid id = userProfileId ?? TimeOrderedId.New();
        await accounts.SaveMyProfileAsync(id, profileName!.Trim(), string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            isActive, grants, ct).ConfigureAwait(false);
        return id;
    }
}
