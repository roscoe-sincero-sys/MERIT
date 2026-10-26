using Merit.Application.Errors;
using Merit.Application.Security;
using Merit.Application.Services;
using Merit.Domain.Tenants;
using Merit.Web.Infrastructure;
using Merit.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Merit.Web.Controllers;

/// <summary>
/// The tenant tree and the Add tenant form (htmx). Platform administrators can add tenants anywhere; other administrators
/// need tenant.create and add under the tenants they administer. The database repeats every check.
/// </summary>
[Route("admin/tenants")]
public sealed class TenantsController(AdminService admin, ConfigService config, AccountService accounts, ICurrentSession session) : Controller
{
    [HttpGet("")]
    [Authorize(Policy = Permissions.TenantCreate)]
    public async Task<IActionResult> Index(Guid? parentTenantId, string? created, CancellationToken ct)
    {
        var form = await FormAsync(new TenantFormModel { ParentTenantId = parentTenantId }, ct);
        if (Request.IsPartial())
        {
            return PartialView("_TypeFields", form);
        }

        ViewData["created"] = created;
        return View(form);
    }

    [HttpPost("")]
    [Authorize(Policy = Permissions.TenantCreate)]
    public async Task<IActionResult> Create(TenantFormModel form, CancellationToken ct)
    {
        try
        {
            await admin.CreateTenantAsync(new TenantInput(form.ParentTenantId, form.TenantCode, form.TenantName, form.TenantType,
                form.StatePostalCode, form.StateFips, form.CountyFipsList, form.NewCounties), ct);
        }
        catch (ValidationException ex)
        {
            foreach (var (k, v) in ex.Errors)
            {
                ModelState.AddModelError(k, string.Join(" ", v));
            }

            return View("Index", await FormAsync(form, ct));
        }
        catch (MeritException ex) when (ex is BusinessRuleException or PermissionDeniedException or NotFoundException)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return View("Index", await FormAsync(form, ct));
        }

        // The session's tenant lists were resolved at sign-in: a fresh scope with the same profile includes the new tenant.
        await RefreshScopeAsync(ct);
        return RedirectToAction(nameof(Index), new { created = form.TenantCode?.Trim().ToUpperInvariant() });
    }

    private async Task RefreshScopeAsync(CancellationToken ct)
    {
        static Guid? ClaimGuid(System.Security.Claims.ClaimsPrincipal user, string type)
            => Guid.TryParse(user.FindFirst(type)?.Value, out var g) ? g : null;

        var me = session.RequireUserId();
        try
        {
            var scope = await accounts.CreateSessionAsync(me, ProfilesController.SessionLifetime, ClaimGuid(User, MeritClaims.ProfileId),
                ClaimGuid(User, MeritClaims.ViewAsUserId), session.ActingTenantId, ct);
            if (session.ScopeKey is Guid old)
            {
                await accounts.SignOutAsync(old, ct);
            }

            await SessionCookie.SignInAsync(HttpContext, scope.Scope);
        }
        catch (MeritException)
        {
            // The tenant exists; the current session simply keeps its old lists until the next sign-in.
        }
    }

    private async Task<TenantFormModel> FormAsync(TenantFormModel form, CancellationToken ct)
    {
        var tenants = await admin.TenantsAsync(ct);
        form.Tree = tenants;
        var parents = tenants.Where(t => t.CanAdminister && TenantRules.ChildTypes(t.TenantType).Count > 0).ToList();
        form.ParentTenantId ??= parents.FirstOrDefault(p => p.TenantType != "platform")?.TenantId ?? parents.FirstOrDefault()?.TenantId;
        var parent = parents.FirstOrDefault(p => p.TenantId == form.ParentTenantId);
        int minDepth = parents.Count == 0 ? 0 : parents.Min(p => p.Depth);
        form.Parents = parents
            .Select(p => new SelectOption(p.TenantId.ToString(), new string(' ', (p.Depth - minDepth) * 3) + $"{p.TenantName} ({p.TenantType})",
                p.TenantId == form.ParentTenantId))
            .ToList();
        form.ParentType = parent?.TenantType;
        form.ParentName = parent?.TenantName;
        form.TypeOptions = parent is null ? [] : TenantRules.ChildTypes(parent.TenantType);
        if (form.TenantType is null || !form.TypeOptions.Contains(form.TenantType))
        {
            form.TenantType = form.TypeOptions.FirstOrDefault();
        }

        // The counties the new tenant may serve: any county of the data root under the root itself, otherwise only the
        // parent's own (the database enforces the same rule). Shown when this session can read them; a platform
        // administrator sees no records.
        form.MayChooseAnyCounty = parent is null
            || TenantRules.MayChooseAnyCounty(parent.TenantId == parent.RootTenantId, parent.TenantType);
        if (parent is not null && !TenantRules.CreatesDataRoot(parent.TenantType) && parent.RootTenantId == session.RootTenantId)
        {
            var counties = await config.SubJurisdictionsAsync(ct);
            form.ExistingCounties = form.MayChooseAnyCounty ? counties : counties.Where(c => c.IsServedBy(parent.TenantId)).ToList();
        }

        return form;
    }
}
