using Merit.Application.Security;
using Merit.Application.Services;
using Merit.Web.Infrastructure;
using Merit.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Merit.Web.Controllers;

/// <summary>Home dashboard (htmx): server-rendered tiles whose counts refresh by polling a partial.</summary>
public sealed class HomeController(QueueService queues, ICurrentSession session) : Controller
{
    [HttpGet("/")]
    [Authorize(Policy = Permissions.SignedIn)]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        // A platform administrator (and anyone whose profile has no work queues) starts where their work is.
        if (!session.Has(Permissions.TaskView))
        {
            return Redirect(session.Has(Permissions.TenantAdminister) ? "/admin/users" : "/account/profiles");
        }

        var dashboard = await queues.DashboardAsync(ct);
        var tiles = new QueueTilesModel(await queues.CountsAsync(ct));
        return View(new DashboardModel(dashboard, tiles, session.DisplayName ?? string.Empty, session.ActingTenantName));
    }

    [HttpGet("/home/queues")]
    [Authorize(Policy = Permissions.TaskView)]
    public async Task<IActionResult> Queues(CancellationToken ct)
    {
        var tiles = new QueueTilesModel(await queues.CountsAsync(ct));
        return Request.IsPartial() ? PartialView("_QueueTiles", tiles) : RedirectToAction(nameof(Index));
    }

    [HttpGet("/home/error")]
    [AllowAnonymous]
    public IActionResult Error() => View("Error", new AlertModel("error", "Something went wrong",
        $"The request could not be completed. Reference {HttpContext.TraceIdentifier}."));
}

/// <summary>Global search by name, DOB, address, organization, accession or case number.</summary>
[Route("search")]
public sealed class SearchController(SearchService search) : Controller
{
    [HttpGet("")]
    [Authorize(Policy = Permissions.PersonView)]
    public async Task<IActionResult> Index(string? q, CancellationToken ct)
    {
        var model = new SearchModel(q, string.IsNullOrWhiteSpace(q) ? null : await search.SearchAsync(q, ct));
        return this.PageOrPartial("Index", "_Results", model);
    }
}

/// <summary>
/// The Razor host view for Angular screens under /app/: the shared header and navigation, the antiforgery cookie and the
/// CSP nonce, then Angular bootstraps into the main region. Users see one header whichever front end draws the page.
/// </summary>
[Route("app")]
public sealed class AppHostController(AngularAssets assets) : Controller
{
    [HttpGet("")]
    [HttpGet("{**path}")]
    [Authorize(Policy = Permissions.SignedIn)]
    public IActionResult Index() => View(new AngularHostModel(assets.Scripts, assets.Styles, assets.IsBuilt));
}

/// <summary>
/// Server-side type-ahead: a text input whose hx-get returns a USWDS-styled list of matches after the user pauses
/// typing (ARIA combobox pattern in merit.js); the chosen value posts as a hidden id.
/// </summary>
[Route("lookup")]
public sealed class LookupController(LookupService lookup, PlaceService places, SearchService search) : Controller
{
    [HttpGet("people")]
    [Authorize(Policy = Permissions.PersonView)]
    public async Task<IActionResult> People(string? q, string field, CancellationToken ct)
    {
        var results = string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2 ? null : await search.SearchAsync(q, ct);
        return PartialView("_Options", new LookupOptionsModel(field, (results?.Persons ?? []).Take(10)
            .Select(p => (p.PersonId, $"{p.LastName}, {p.FirstName}", (string?)(p.Dob is null ? "DOB unknown" : "b. " + Options.Date(p.Dob)))).ToList()));
    }

    [HttpGet("organizations")]
    [Authorize(Policy = Permissions.PersonView)]
    public async Task<IActionResult> Organizations(string? q, string? type, string field, CancellationToken ct)
    {
        var rows = await lookup.OrganizationsAsync(q, type, ct);
        return PartialView("_Options", new LookupOptionsModel(field,
            rows.Select(r => (r.OrganizationId, r.OrganizationName, (string?)string.Join(", ",
                new[] { r.SiteAddress, r.SiteCity, r.CliaNumber is null ? null : "CLIA " + r.CliaNumber }.Where(s => s is not null)))).ToList()));
    }

    [HttpGet("sites")]
    [Authorize(Policy = Permissions.PersonView)]
    public async Task<IActionResult> Sites(string? q, string? type, string field, CancellationToken ct)
    {
        var rows = await lookup.OrganizationsAsync(q, type, ct);
        return PartialView("_SiteOptions", new LookupOptionsModel(field,
            rows.Where(r => r.OrganizationSiteId is not null && r.SiteLocationId is not null)
                .Select(r => (r.OrganizationSiteId!.Value, r.OrganizationName, (string?)$"{r.SiteAddress}, {r.SiteCity}")).ToList(),
            string.Join(',', rows.Where(r => r.OrganizationSiteId is not null && r.SiteLocationId is not null)
                .Select(r => r.SiteLocationId!.Value.ToString()))));
    }

    [HttpGet("providers")]
    [Authorize(Policy = Permissions.PersonView)]
    public async Task<IActionResult> Providers(string? q, string field, CancellationToken ct)
    {
        var rows = await lookup.ProvidersAsync(q, ct);
        return PartialView("_Options", new LookupOptionsModel(field,
            rows.Select(r => (r.ProviderId, $"{r.LastName}, {r.FirstName} {r.Credential}".Trim(),
                (string?)$"NPI {r.Npi} {r.PracticeCity} {r.PracticeState}".Trim())).ToList()));
    }

    [HttpGet("locations")]
    [Authorize(Policy = Permissions.PersonView)]
    public async Task<IActionResult> Locations(string? q, string field, CancellationToken ct)
    {
        var rows = await places.FindLocationsAsync(q, ct);
        return PartialView("_Options", new LookupOptionsModel(field,
            rows.Select(r => (r.LocationId, $"{r.AddressLine1}{(string.IsNullOrEmpty(r.Unit) ? string.Empty : " " + r.Unit)}",
                (string?)$"{r.City}, {r.State} {r.Zip5}{(r.CountyName is null ? string.Empty : " - " + r.CountyName)}")).ToList()));
    }
}
