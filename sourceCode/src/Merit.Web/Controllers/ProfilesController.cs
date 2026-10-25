using System.Globalization;
using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Security;
using Merit.Application.Services;
using Merit.Web.Infrastructure;
using Merit.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Merit.Web.Controllers;

/// <summary>
/// "My profiles": the master profile (read-only here), the person's own extra profiles (create, change, deactivate),
/// switching between them and the working tenant, and, for administrators, viewing MERIT as someone whose access they
/// manage. Every switch creates a new database session scope and ends the old one.
/// </summary>
[Route("account")]
public sealed class ProfilesController(ProfileService profiles, AdminService admin, AccountService accounts, ICurrentSession session)
    : Controller
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    [HttpGet("profiles")]
    [Authorize(Policy = Permissions.SignedIn)]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var mine = await profiles.MineAsync(ct);
        var viewAs = session.Has(Permissions.TenantAdminister) ? await admin.ViewAsCandidatesAsync(ct) : [];
        var tenants = await admin.TenantsAsync(ct);
        return View(new ProfilesPageModel(mine, session.ProfileName ?? "Master profile", (session as WebCurrentSession)?.ProfileId,
            session.ViewAsDisplayName, session.IsPlatformAdmin, session.ActingTenantId, tenants, viewAs, TempData["profileMessage"] as string,
            TempData["profileError"] as string));
    }

    [HttpGet("profiles/new")]
    [HttpGet("profiles/{id:guid}/edit")]
    [Authorize(Policy = Permissions.SignedIn)]
    public async Task<IActionResult> Edit(Guid? id, CancellationToken ct)
    {
        if (session.IsPlatformAdmin)
        {
            return RedirectToAction(nameof(Index));
        }

        var mine = await profiles.MineAsync(ct);
        var form = new ProfileFormModel { IsActive = true };
        if (id is Guid pid)
        {
            var p = mine.Profiles.FirstOrDefault(x => x.UserProfileId == pid && !x.IsMaster) ?? throw new NotFoundException();
            form = new ProfileFormModel
            {
                UserProfileId = pid, ProfileName = p.ProfileName, ProfileDescription = p.ProfileDescription, IsActive = p.IsActive,
                Grants = mine.Grants.Where(g => g.UserProfileId == pid)
                    .Select(g => new ProfileGrantForm { TenantId = g.TenantId, RoleId = g.RoleId, IncludeDescendants = g.IncludeDescendants, AccessLevel = g.AccessLevel })
                    .ToList(),
            };
        }

        return View(await FormAsync(form, mine, ct));
    }

    [HttpPost("profiles")]
    [Authorize(Policy = Permissions.SignedIn)]
    public async Task<IActionResult> Save(ProfileFormModel form, CancellationToken ct)
    {
        var grants = form.Grants.Where(g => g.TenantId is not null && g.RoleId is not null)
            .Select(g => new ProfileGrantInput(g.TenantId!.Value, g.RoleId!.Value, g.IncludeDescendants, g.AccessLevel ?? "read")).ToList();
        try
        {
            await profiles.SaveAsync(form.UserProfileId, form.ProfileName, form.ProfileDescription, form.IsActive, grants, ct);
            TempData["profileMessage"] = $"Profile \"{form.ProfileName}\" saved.";
            return RedirectToAction(nameof(Index));
        }
        catch (ValidationException ex)
        {
            foreach (var (k, v) in ex.Errors)
            {
                ModelState.AddModelError(k, string.Join(" ", v));
            }

            return View("Edit", await FormAsync(form, await profiles.MineAsync(ct), ct));
        }
    }

    /// <summary>Switches to one of the person's profiles (empty = master) and, optionally, a working tenant.</summary>
    [HttpPost("profiles/switch")]
    [Authorize(Policy = Permissions.SignedIn)]
    public Task<IActionResult> Switch(Guid? userProfileId, Guid? actingTenantId, CancellationToken ct)
        => NewScopeAsync(userProfileId, null, actingTenantId, "/", ct);

    /// <summary>An administrator sees MERIT with another person's access, limited to what the administrator manages.</summary>
    [HttpPost("view-as/{userId:guid}")]
    [Authorize(Policy = Permissions.TenantAdminister)]
    public Task<IActionResult> ViewAs(Guid userId, CancellationToken ct) => NewScopeAsync(null, userId, null, "/", ct);

    [HttpPost("view-as/end")]
    [Authorize(Policy = Permissions.SignedIn)]
    public Task<IActionResult> EndViewAs(CancellationToken ct) => NewScopeAsync(null, null, null, "/account/profiles", ct);

    private async Task<IActionResult> NewScopeAsync(Guid? userProfileId, Guid? viewAsUserId, Guid? actingTenantId, string next, CancellationToken ct)
    {
        var me = session.RequireUserId();
        try
        {
            var scope = await accounts.CreateSessionAsync(me, SessionLifetime, userProfileId, viewAsUserId, actingTenantId, ct);
            if (session.ScopeKey is Guid old)
            {
                await accounts.SignOutAsync(old, ct);
            }

            await SessionCookie.SignInAsync(HttpContext, scope.Scope);
            return LocalRedirect(next);
        }
        catch (MeritException ex)
        {
            TempData["profileError"] = ex.Message;
            return RedirectToAction(nameof(Index));
        }
    }

    private async Task<ProfileFormModel> FormAsync(ProfileFormModel form, MyProfiles mine, CancellationToken ct)
    {
        // Choices come from the master profile: its roles, and its tenants and everything below them.
        var master = mine.Profiles.FirstOrDefault(p => p.IsMaster);
        var masterGrants = mine.Grants.Where(g => g.UserProfileId == master?.UserProfileId).ToList();
        form.MasterGrants = masterGrants;
        form.Roles = masterGrants.DistinctBy(g => g.RoleId).Select(g => new SelectOption(g.RoleId.ToString(), g.RoleName)).ToList();
        var tenants = await admin.TenantsAsync(ct);
        form.Tenants = tenants.Select(t => new SelectOption(t.TenantId.ToString(), new string(' ', Math.Max(0, t.Depth - 1) * 3) + t.TenantName))
            .ToList();
        int rows = Math.Max(3, form.Grants.Count(g => g.TenantId is not null) + 1);
        while (form.Grants.Count < rows)
        {
            form.Grants.Add(new ProfileGrantForm { AccessLevel = "read" });
        }

        return form;
    }
}

public sealed record ProfilesPageModel(
    MyProfiles Mine,
    string CurrentProfileName,
    Guid? CurrentProfileId,
    string? ViewingAs,
    bool IsPlatformAdmin,
    Guid? ActingTenantId,
    IReadOnlyList<TenantListRow> Tenants,
    IReadOnlyList<PersonSummaryRow> ViewAsCandidates,
    string? Message,
    string? Error)
{
    public static string AccessText(string level) => level switch
    {
        "full" => "Full (read, write and delete)",
        "write" => "Read and write (no delete)",
        _ => "Read only",
    };

    public static string Scope(bool includeDescendants) => includeDescendants ? "and everything under it" : "only";

    public static string Id(Guid? id) => id?.ToString() ?? string.Empty;

    public static string Invariant(int n) => n.ToString(CultureInfo.InvariantCulture);
}

public sealed class ProfileFormModel
{
    public Guid? UserProfileId { get; set; }

    public string? ProfileName { get; set; }

    public string? ProfileDescription { get; set; }

    public bool IsActive { get; set; } = true;

    public List<ProfileGrantForm> Grants { get; set; } = [];

    public IReadOnlyList<MyProfileGrantRow> MasterGrants { get; set; } = [];

    public IReadOnlyList<SelectOption> Roles { get; set; } = [];

    public IReadOnlyList<SelectOption> Tenants { get; set; } = [];
}

public sealed class ProfileGrantForm
{
    public Guid? TenantId { get; set; }

    public Guid? RoleId { get; set; }

    public bool IncludeDescendants { get; set; } = true;

    public string? AccessLevel { get; set; } = "read";
}
